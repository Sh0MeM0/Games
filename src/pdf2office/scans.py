"""Offline raster-image cleaning and OCR using the existing page model.

Scans are processed at their native resolution. Cleaning and OCR use overlapping
bounded tiles so a large engineering sheet is not silently downsampled. This is
visual reconstruction: stains, lost strokes and handwriting cannot be recovered
reliably from a damaged original.
"""
from __future__ import annotations

import csv
from functools import lru_cache
import io
import math
from pathlib import Path
import re
import warnings as python_warnings
from typing import Callable

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont, ImageOps, UnidentifiedImageError, features

from .extract import (PdfExtractionError, _find_tesseract, _run_tesseract,
                      _tesseract_options, _trace_scan, parse_page_range)
from .models import PdfPage, RasterImage, TextSpan
from .input_formats import EXTENSION_FORMATS, IMAGE_FORMATS

MAX_SCAN_PIXELS = 160_000_000
CLEAN_TILE_SIZE = 1536
OCR_TILE_SIZE = 2800
OCR_TILE_OVERLAP = 180


def _check(callback) -> None:
    if callback:
        callback()


def _resolution(source: Image.Image, override: int, messages: list[str]) -> tuple[float, float]:
    if override:
        messages.append(f"Масштаб изображения рассчитан по заданному разрешению {override} dpi; проверьте размеры чертежа.")
        return float(override), float(override)

    def physical_pair(value, factor=1.0):
        try:
            x, y = float(value[0]) * factor, float(value[1]) * factor
            if all(math.isfinite(v) and 36 <= v <= 4800 for v in (x, y)):
                return x, y
        except (TypeError, ValueError, IndexError, OverflowError, ZeroDivisionError):
            pass
        return None

    # Read both EXIF axes: Pillow's JPEG fallback copies XResolution to Y and
    # invents 72 dpi when EXIF has orientation but no physical resolution.
    # Unit 1 is only an aspect ratio and must not set a CAD drawing's scale.
    exif = source.getexif()
    unit = exif.get(296)
    if unit in (2, 3):
        pair = physical_pair((exif.get(282), exif.get(283)), 2.54 if unit == 3 else 1.0)
        if pair:
            return pair
    if source.format == "JPEG":
        unit = source.info.get("jfif_unit")
        pair = physical_pair(source.info.get("jfif_density"), 2.54 if unit == 2 else 1.0) if unit in (1, 2) else None
    else:
        # Pillow converts TIFF centimetres, PNG pixels/metre and BMP metadata.
        pair = physical_pair(source.info.get("dpi"))
    if pair:
        return pair
    messages.append("В изображении нет корректного разрешения DPI. Принято 300 dpi; задайте DPI скана вручную для правильного масштаба CAD.")
    return 300.0, 300.0


def _scan_grayscale(image: Image.Image) -> Image.Image:
    """Normalize sample depth and alpha without clipping dark 16-bit ink."""
    if image.mode.startswith("I;16"):
        samples = np.asarray(image)
        return Image.fromarray((samples.astype(np.uint32) // 257).astype(np.uint8))
    if image.mode in {"I", "F"}:
        samples = np.asarray(image, dtype=np.float64)
        finite = np.isfinite(samples)
        if not finite.any():
            raise PdfExtractionError("Изображение содержит некорректные числовые значения пикселей.")
        lower, upper = float(samples[finite].min()), float(samples[finite].max())
        if lower >= 0 and upper <= 1 and image.mode == "F":
            scaled = samples * 255
        elif lower >= 0 and upper <= 255 and image.mode == "I":
            scaled = samples
        elif lower >= 0 and upper <= 65535 and image.mode == "I":
            scaled = samples * (255 / 65535)
        elif upper > lower:
            scaled = (samples - lower) * (255 / (upper - lower))
        else:
            scaled = np.full(samples.shape, 255)
        return Image.fromarray(np.clip(np.nan_to_num(scaled, nan=255), 0, 255).astype(np.uint8))
    if "A" in image.getbands() or "transparency" in image.info:
        rgba = image.convert("RGBA")
        white = Image.new("RGBA", rgba.size, "white")
        return Image.alpha_composite(white, rgba).convert("L")
    return image.convert("L")


def _clean_tile(gray: np.ndarray, dpi: float) -> tuple[np.ndarray, int]:
    """Remove isolated compact speckles, retaining thin connected strokes.

    Tiny components close to substantial strokes are retained: this protects
    dots over letters, punctuation and short dimension dashes. No morphological
    opening is applied to ink, as that would erase thin engineering lines.
    """
    if np.all((gray == 0) | (gray == 255)):
        binary = gray.copy()
    else:
        radius = max(15, min(101, int(round(dpi * 0.10)) | 1))
        background = cv2.morphologyEx(gray, cv2.MORPH_CLOSE,
                                      cv2.getStructuringElement(cv2.MORPH_RECT, (radius, radius)))
        normalized = cv2.divide(gray, np.maximum(background, 1), scale=255)
        binary = cv2.adaptiveThreshold(normalized, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C,
                                       cv2.THRESH_BINARY, radius, 12)
        # Adaptive thresholding can clip a faint hatch immediately before it
        # meets a black frame. Restore the original supported grey stroke,
        # without filling white gaps or importing isolated background texture.
        from .extract import _supported_faint_strokes
        supported = _supported_faint_strokes(normalized, (normalized < 128).astype(np.uint8))
        binary[supported != 0] = 0
    ink = 255 - binary
    count, labels, stats, _ = cv2.connectedComponentsWithStats(ink, connectivity=8)
    if count <= 1:
        return binary, 0
    scale = dpi / 300
    area_limit = max(3, round(10 * scale * scale))
    extent_limit = max(3, round(6 * scale))
    small = ((stats[:, cv2.CC_STAT_AREA] <= area_limit)
             & (stats[:, cv2.CC_STAT_WIDTH] <= extent_limit)
             & (stats[:, cv2.CC_STAT_HEIGHT] <= extent_limit))
    small[0] = False
    substantial = (~small).astype(np.uint8) * 255
    substantial[0] = 0
    proximity = max(2, min(20, round(5 * scale)))
    support = cv2.dilate(substantial[labels], np.ones((2 * proximity + 1, 2 * proximity + 1), np.uint8))
    # If any pixel of a component touches support, preserve the entire component.
    protected = np.zeros(count, dtype=bool)
    protected[np.unique(labels[support > 0])] = True
    remove = small & ~protected
    removed = int(stats[remove, cv2.CC_STAT_AREA].sum())
    binary[remove[labels]] = 255
    return binary, removed


def clean_scan(image: Image.Image, dpi: float, *, check_cancel=None, progress=None) -> tuple[Image.Image, int]:
    """Whiten background and remove isolated speckles without resizing."""
    output = Image.new("L", image.size, 255)
    removed = 0
    # Halo includes background estimation and punctuation protection. Writing
    # only tile cores avoids seams and double-counting removed noise pixels.
    halo = max(64, min(128, round(dpi * 0.15)))
    rows = math.ceil(image.height / CLEAN_TILE_SIZE)
    for row, top in enumerate(range(0, image.height, CLEAN_TILE_SIZE)):
        _check(check_cancel)
        if progress:
            progress(f"Очистка фона и шума: полоса {row + 1} из {rows}")
        bottom = min(image.height, top + CLEAN_TILE_SIZE)
        for left in range(0, image.width, CLEAN_TILE_SIZE):
            _check(check_cancel)
            right = min(image.width, left + CLEAN_TILE_SIZE)
            box = (max(0, left - halo), max(0, top - halo),
                   min(image.width, right + halo), min(image.height, bottom + halo))
            gray = np.asarray(image.crop(box).convert("L"))
            cleaned, _ = _clean_tile(gray, dpi)
            x0, y0 = left - box[0], top - box[1]
            core = cleaned[y0:y0 + bottom - top, x0:x0 + right - left]
            original = gray[y0:y0 + bottom - top, x0:x0 + right - left]
            removed += int(np.count_nonzero((original < 128) & (core == 255)))
            output.paste(Image.fromarray(core), (left, top))
    return output, removed


def estimate_skew(image: Image.Image) -> float:
    """Estimate only a strongly supported small rotation from long ruled lines.

    A dominant diagonal road/contour is not evidence of page skew. Require
    several long near-horizontal/vertical segments and narrow angle consensus.
    """
    preview = image.copy()
    preview.thumbnail((2400, 2400))
    gray = np.asarray(preview.convert("L"))
    ink = np.uint8(gray < 128) * 255
    minimum = max(80, round(min(preview.size) * 0.13))
    lines = cv2.HoughLinesP(ink, 1, math.pi / 1800, threshold=max(60, minimum // 2),
                            minLineLength=minimum, maxLineGap=12)
    if lines is None:
        return 0.0
    angles, weights = [], []
    for x0, y0, x1, y1 in lines[:, 0]:
        angle = (math.degrees(math.atan2(float(y1 - y0), float(x1 - x0))) + 45) % 90 - 45
        if abs(angle) <= 7:
            angles.append(angle)
            weights.append(math.hypot(float(x1 - x0), float(y1 - y0)))
    if len(angles) < 3:
        return 0.0
    values, lengths = np.array(angles), np.array(weights)
    histogram, edges = np.histogram(values, bins=np.arange(-7.05, 7.15, 0.1), weights=lengths)
    centre = (edges[histogram.argmax()] + edges[histogram.argmax() + 1]) / 2
    selected = np.abs(values - centre) <= 0.55
    if selected.sum() < 3 or lengths[selected].sum() < lengths.sum() * 0.55:
        return 0.0
    selected_angles, selected_weights = values[selected], lengths[selected]
    order = np.argsort(selected_angles)
    cumulative = np.cumsum(selected_weights[order])
    median = float(selected_angles[order][np.searchsorted(cumulative, cumulative[-1] / 2)])
    dispersion = float(np.average(np.abs(selected_angles - median), weights=selected_weights))
    return median if abs(median) >= 0.12 and dispersion <= 0.30 else 0.0


def deskew_scan(image: Image.Image, *, check_cancel=None) -> tuple[Image.Image, float]:
    _check(check_cancel)
    angle = estimate_skew(image)
    if not angle:
        return image, 0.0
    # PIL positive rotation corrects clockwise slant in top-left image axes.
    # Expansion preserves content at sheet edges; no perspective warp/rescaling.
    radians = math.radians(angle)
    expanded_width = math.ceil(abs(image.width * math.cos(radians)) + abs(image.height * math.sin(radians))) + 2
    expanded_height = math.ceil(abs(image.height * math.cos(radians)) + abs(image.width * math.sin(radians))) + 2
    if expanded_width * expanded_height > MAX_SCAN_PIXELS:
        raise PdfExtractionError("После выравнивания изображение превышает предел памяти. Отключите выравнивание или разделите лист на части без уменьшения DPI.")
    result = image.rotate(angle, resample=Image.Resampling.BICUBIC, expand=True, fillcolor=255)
    _check(check_cancel)
    return result, angle


def _ocr_environment(executable: Path, language: str, check_cancel=None):
    if not re.fullmatch(r"[A-Za-z0-9_]+(?:\+[A-Za-z0-9_]+)*", language):
        raise PdfExtractionError("Некорректный язык OCR. Пример: rus+eng.")
    executable = executable.resolve()
    options = _tesseract_options(executable)
    cwd = Path(options[1]).resolve() if options else None
    if cwd:
        options = ["--tessdata-dir", "."]
    available = _run_tesseract([str(executable), "--list-langs", *options], timeout=30,
                                cwd=cwd, check_cancel=check_cancel)
    languages = set(available.stdout.splitlines()[1:])
    missing = set(language.split("+")) - languages
    if available.returncode or missing:
        raise PdfExtractionError("Отсутствуют языковые данные Tesseract: " + ", ".join(sorted(missing or {language})))
    return executable, options, cwd, languages


def _png(image: Image.Image) -> bytes:
    stream = io.BytesIO()
    image.save(stream, format="PNG")
    return stream.getvalue()


def _orient_scan(image: Image.Image, environment, dpi: tuple[float, float], *, check_cancel=None, progress=None):
    executable, options, cwd, languages = environment
    if "osd" not in languages:
        return image, dpi, 0
    _check(check_cancel)
    preview = image.copy()
    preview.thumbnail((2600, 2600))
    result = _run_tesseract([str(executable), "stdin", "stdout", *options, "-l", "osd", "--psm", "0",
                             "-c", "min_characters_to_try=30"], input_data=_png(preview), cwd=cwd,
                             check_cancel=check_cancel,
                             heartbeat=lambda elapsed: progress(f"Определение ориентации изображения: {int(elapsed)} с.") if progress else None)
    rotation = re.search(r"Rotate:\s*(0|90|180|270)\b", result.stdout)
    confidence = re.search(r"Orientation confidence:\s*([0-9.]+)", result.stdout)
    angle = int(rotation[1]) if result.returncode == 0 and rotation and confidence and float(confidence[1]) >= 8 else 0
    if angle:
        image = image.rotate(-angle, expand=True, fillcolor=255)
        if angle in (90, 270):
            dpi = dpi[::-1]
    return image, dpi, angle


def _prepare_ocr_tile(image: Image.Image, dpi: float) -> tuple[Image.Image, bool]:
    """Suppress dense halftone speckles only in OCR's private working copy.

    Geometry/reference cleaning intentionally preserves thin strokes and tiny
    marks. On noisy scans those protected speckles overwhelm text segmentation;
    stronger filtering is confined to OCR and activated only by measured noise.
    """
    gray = np.asarray(image.convert("L"))
    count, labels, stats, _ = cv2.connectedComponentsWithStats(np.uint8(gray < 128), connectivity=8)
    scale = dpi / 400
    small_count = int(np.count_nonzero(stats[1:, cv2.CC_STAT_AREA] <= max(3, round(12 * scale * scale))))
    if small_count < max(100, gray.size * 0.001):
        return image, False
    del labels, stats
    # A dark threshold keeps coherent strokes while removing the average gray
    # of high-frequency scanner stipple; do not apply this to vector tracing.
    blurred = cv2.GaussianBlur(gray, (5, 5), max(0.6, min(1.5, scale)))
    _, binary = cv2.threshold(blurred, 110, 255, cv2.THRESH_BINARY)
    count, labels, stats, _ = cv2.connectedComponentsWithStats(255 - binary, connectivity=8)
    keep = ((stats[:, cv2.CC_STAT_AREA] > max(6, round(25 * scale * scale)))
            | (np.maximum(stats[:, cv2.CC_STAT_WIDTH], stats[:, cv2.CC_STAT_HEIGHT]) > max(12, round(25 * scale))))
    keep[0] = False
    return Image.fromarray(np.where(keep[labels], 0, 255).astype(np.uint8)), True


@lru_cache(maxsize=1)
def _ocr_check_fonts():
    from .fonts import bundled_font_dir
    fonts = []
    for name in ("LiberationSans-Regular.ttf", "LiberationSans-Bold.ttf",
                 "LiberationSerif-Regular.ttf", "LiberationSerif-Bold.ttf"):
        try:
            fonts.append(ImageFont.truetype(str(bundled_font_dir() / name), 128))
        except (OSError, ValueError):
            pass
    return fonts


def _glyph_shape_agreement(mask: np.ndarray, text: str) -> float:
    fonts = _ocr_check_fonts()
    if not fonts:
        return 1.0  # Missing optional check fonts must not discard OCR text.
    target = cv2.resize(mask, (64, 64), interpolation=cv2.INTER_AREA) > 64
    kernel = np.ones((5, 5), np.uint8)
    target_near = cv2.dilate(target.astype(np.uint8), kernel) > 0
    best = 0.0
    for font in fonts:
        bounds = font.getbbox(text)
        glyph = Image.new("L", (max(1, bounds[2] - bounds[0]), max(1, bounds[3] - bounds[1])))
        ImageDraw.Draw(glyph).text((-bounds[0], -bounds[1]), text, font=font, fill=255)
        reference = cv2.resize(np.asarray(glyph), (64, 64), interpolation=cv2.INTER_AREA) > 64
        reference_near = cv2.dilate(reference.astype(np.uint8), kernel) > 0
        precision = np.count_nonzero(target & reference_near) / max(1, np.count_nonzero(target))
        recall = np.count_nonzero(reference & target_near) / max(1, np.count_nonzero(reference))
        best = max(best, 2 * precision * recall / max(0.001, precision + recall))
    return best


def _reject_ocr_geometry(image: Image.Image, dpi: tuple[float, float], spans: list[TextSpan],
                         messages: list[str], *, preprocess=True, check_cancel=None) -> list[TextSpan]:
    """Keep drawings/signatures misread as giant short words as geometry.

    Size alone never rejects text. Only anomalous short tokens are inspected;
    a genuine large heading/dimension with plausible glyphs is retained. Local
    shape agreement tolerates different face weights/serifs. Large handwriting
    that fails this conservative check remains in the reference and tracing.
    """
    if len(spans) < 8 or not _ocr_check_fonts():
        return spans
    ordinary_height = float(np.median([span.bbox[3] - span.bbox[1] for span in spans]))
    retained = []
    rejected = 0
    for span in spans:
        _check(check_cancel)
        height = span.bbox[3] - span.bbox[1]
        if height <= ordinary_height * 2.2 or sum(c.isalnum() for c in span.text) > 4:
            retained.append(span)
            continue
        x0, y0, x1, y1 = [round(value * dpi[index % 2] / 72) for index, value in enumerate(span.bbox)]
        padding = max(12, (y1 - y0) // 2)
        box = (max(0, x0 - padding), max(0, y0 - padding),
               min(image.width, x1 + padding), min(image.height, y1 + padding))
        crop = image.crop(box).convert("L")
        original_width, original_height = crop.size
        crop.thumbnail((1200, 1200))
        sx, sy = crop.width / original_width, crop.height / original_height
        if preprocess:
            crop, _ = _prepare_ocr_tile(crop, math.sqrt(dpi[0] * dpi[1]) * math.sqrt(sx * sy))
        mask = np.uint8(np.asarray(crop) < 128) * 255
        left, top = round((x0 - box[0]) * sx), round((y0 - box[1]) * sy)
        right, bottom = round((x1 - box[0]) * sx), round((y1 - box[1]) * sy)
        inside = mask[top:bottom, left:right]
        if not inside.size:
            retained.append(span)
            continue
        agreement = _glyph_shape_agreement(inside, span.text)
        count, labels, stats, _ = cv2.connectedComponentsWithStats(mask, connectivity=8)
        ink_counts = np.bincount(labels[top:bottom, left:right].ravel(), minlength=count)
        ink_counts[0] = 0
        margin = max(3, (bottom - top) * 0.1)
        crossing = (((stats[:, 0] < left - margin) & (stats[:, 0] + stats[:, 2] > right + margin))
                    | ((stats[:, 1] < top - margin) & (stats[:, 1] + stats[:, 3] > bottom + margin)))
        continued_ink = ink_counts[crossing].sum() / max(1, ink_counts.sum())
        if agreement < 0.78 or (agreement < 0.85 and continued_ink > 0.40):
            rejected += 1
        else:
            retained.append(span)
    if rejected:
        messages.append(f"Отклонено сомнительных крупных OCR-фрагментов: {rejected}. Контуры, подписи и повреждённые надписи не заменены предположительными буквами; они сохранены в подложке и доступной векторизации.")
    return retained


def _ocr_tiles(image: Image.Image, dpi: tuple[float, float], environment, language: str,
               messages: list[str], *, check_cancel=None, progress=None, preprocess=True) -> list[TextSpan]:
    executable, options, cwd, _ = environment
    sx, sy = 72 / dpi[0], 72 / dpi[1]
    result = []
    columns = math.ceil(image.width / OCR_TILE_SIZE)
    rows = math.ceil(image.height / OCR_TILE_SIZE)
    for row, top in enumerate(range(0, image.height, OCR_TILE_SIZE)):
        bottom = min(image.height, top + OCR_TILE_SIZE)
        for column, left in enumerate(range(0, image.width, OCR_TILE_SIZE)):
            _check(check_cancel)
            right = min(image.width, left + OCR_TILE_SIZE)
            box = (max(0, left - OCR_TILE_OVERLAP), max(0, top - OCR_TILE_OVERLAP),
                   min(image.width, right + OCR_TILE_OVERLAP), min(image.height, bottom + OCR_TILE_OVERLAP))
            tile = image.crop(box)
            if tile.convert("L").getextrema()[0] >= 245:
                continue
            if progress:
                progress(f"Распознавание изображения без уменьшения: участок {row * columns + column + 1} из {rows * columns}")
            if preprocess:
                tile, filtered = _prepare_ocr_tile(tile, math.sqrt(dpi[0] * dpi[1]))
                message = "Для OCR подавлен плотный точечный шум в отдельной копии; мелкие знаки и десятичные разделители требуют проверки. Геометрия и подложка этим фильтром не изменены."
                if filtered and message not in messages:
                    messages.append(message)
            run = _run_tesseract([str(executable), "stdin", "stdout", *options, "-l", language,
                                  "--dpi", str(round(math.sqrt(dpi[0] * dpi[1]))), "--psm", "11",
                                  "-c", "tessedit_create_tsv=1"], input_data=_png(tile), cwd=cwd,
                                  check_cancel=check_cancel,
                                  heartbeat=lambda elapsed: progress(f"Распознавание изображения продолжается: {int(elapsed)} с; ограничение времени отключено.") if progress else None)
            if run.returncode:
                raise PdfExtractionError("Ошибка распознавания Tesseract: " + run.stderr.strip()[-1200:])
            for record in csv.DictReader(io.StringIO(run.stdout), delimiter="\t", quoting=csv.QUOTE_NONE):
                text = (record.get("text") or "").strip()
                if record.get("level") != "5" or not text or not any(character.isalnum() for character in text):
                    continue
                try:
                    confidence = float(record["conf"])
                    x, y, width, height = (float(record[key]) for key in ("left", "top", "width", "height"))
                    if confidence < 55 or min(width, height) <= 0 or not all(math.isfinite(v) for v in (x, y, width, height, confidence)):
                        continue
                except (ValueError, KeyError, TypeError):
                    continue
                x += box[0]
                y += box[1]
                # Only the tile owning the word centre emits it. Overlap gives
                # OCR full glyphs at tile boundaries without duplicate entities.
                if not (left <= x + width / 2 < right and top <= y + height / 2 < bottom):
                    continue
                bbox = (max(0, x * sx), max(0, y * sy), min(image.width, x + width) * sx, min(image.height, y + height) * sy)
                if bbox[2] <= bbox[0] or bbox[3] <= bbox[1]:
                    continue
                result.append(TextSpan(text, bbox, "Liberation Sans", max(2, height * sy), (0, 0, 0),
                                       origin=(bbox[0], bbox[3]), from_ocr=True, advance=width * sx))
    result = _reject_ocr_geometry(image, dpi, result, messages, preprocess=preprocess, check_cancel=check_cancel)
    messages.append("OCR скана требует проверки: рукописные, повреждённые и наклонные надписи могут быть пропущены или распознаны неверно; шрифт определяется приблизительно.")
    if not result:
        messages.append("На изображении не удалось уверенно распознать текст; растровая подложка сохранена.")
    return result


def extract_tiff(path: Path, pages: str = "", password: str = "", ocr_mode: str = "auto",
                 ocr_language: str = "rus+eng", tesseract_path: Path | None = None,
                 vectorize_scans: bool = True, progress: Callable[[str], None] | None = None,
                 check_cancel: Callable[[], None] | None = None, *, preprocess: bool = True,
                 deskew: bool = True, dpi: int = 0) -> list[PdfPage]:
    """Compatibility entry point that accepts only actual TIFF files."""
    return _extract_image(path, pages, password, ocr_mode, ocr_language, tesseract_path,
                          vectorize_scans, progress, check_cancel, preprocess=preprocess,
                          deskew=deskew, dpi=dpi, tiff_only=True)


def extract_image(path: Path, pages: str = "", password: str = "", ocr_mode: str = "auto",
                  ocr_language: str = "rus+eng", tesseract_path: Path | None = None,
                  vectorize_scans: bool = True, progress: Callable[[str], None] | None = None,
                  check_cancel: Callable[[], None] | None = None, *, preprocess: bool = True,
                  deskew: bool = True, dpi: int = 0) -> list[PdfPage]:
    """Read selected raster frames at native resolution and physical scale.

    TIFF pages and displayed animation frames become separate pages. APNG's
    optional separate default image is page one. ``dpi=0`` uses physical image
    metadata or an explicit, warned 300 dpi fallback; a nonzero DPI overrides
    it. ``password`` is accepted for the common PDF/image service interface.
    """
    return _extract_image(path, pages, password, ocr_mode, ocr_language, tesseract_path,
                          vectorize_scans, progress, check_cancel, preprocess=preprocess,
                          deskew=deskew, dpi=dpi, tiff_only=False)


def _extract_image(path: Path, pages: str, password: str, ocr_mode: str,
                   ocr_language: str, tesseract_path: Path | None,
                   vectorize_scans: bool, progress: Callable[[str], None] | None,
                   check_cancel: Callable[[], None] | None, *, preprocess: bool,
                   deskew: bool, dpi: int, tiff_only: bool) -> list[PdfPage]:
    if ocr_mode not in {"auto", "always", "off"}:
        raise PdfExtractionError("Режим OCR должен быть auto, always или off.")
    if isinstance(dpi, bool) or not isinstance(dpi, (int, float)) or not math.isfinite(dpi) or (dpi != 0 and not 72 <= dpi <= 2400):
        raise PdfExtractionError("DPI скана должен быть 0 (из файла) или числом от 72 до 2400.")
    path = Path(path)
    if not path.is_file():
        raise PdfExtractionError(f"Изображение не найдено: {path}")
    expected = "TIFF" if tiff_only else EXTENSION_FORMATS.get(path.suffix.lower())
    if expected is None:
        raise PdfExtractionError(f"Формат изображения {path.suffix or '(без расширения)'} не поддерживается.")
    if expected == "JPEG2000" and not features.check_codec("jpg_2000"):
        raise PdfExtractionError("JPEG 2000 недоступен в этой сборке: отсутствует декодер OpenJPEG. Сохраните исходное изображение в PNG или TIFF без уменьшения разрешения.")
    result = []
    environment = None
    # Our explicit cap supports 92 MP engineering scans while keeping corrupt
    # dimensions bounded. Never disable Pillow's global protection.
    _check(check_cancel)
    try:
        with python_warnings.catch_warnings():
            python_warnings.simplefilter("ignore", Image.DecompressionBombWarning)
            source = Image.open(path)
    except Image.DecompressionBombError as error:
        raise PdfExtractionError(f"Изображение слишком велико: допускается до {MAX_SCAN_PIXELS // 1_000_000} Мп на страницу. Разделите лист на части без уменьшения DPI.") from error
    except (OSError, UnidentifiedImageError, ValueError, SyntaxError) as error:
        raise PdfExtractionError("Не удалось открыть изображение: файл повреждён или имеет неподдерживаемый формат.") from error
    with source:
        detected = source.format
        if tiff_only and detected != "TIFF":
            raise PdfExtractionError("Выбранный файл не является TIFF.")
        if detected not in IMAGE_FORMATS:
            raise PdfExtractionError(f"Фактический формат изображения {detected or 'неизвестен'} не поддерживается.")
        format_warning = (f"Расширение {path.suffix} не соответствует содержимому. Изображение прочитано по фактическому формату {detected}; исходный файл не изменён."
                          if detected != expected else None)
        try:
            with python_warnings.catch_warnings():
                python_warnings.simplefilter("ignore", Image.DecompressionBombWarning)
                frame_count = getattr(source, "n_frames", 1)
        except (Image.DecompressionBombError, OSError, ValueError, SyntaxError, EOFError) as error:
            raise PdfExtractionError("Не удалось прочитать страницы изображения: файл повреждён или одна из страниц слишком велика.") from error
        indices = parse_page_range(pages, frame_count)
        for index in indices:
            _check(check_cancel)
            if progress:
                progress(f"Чтение {detected}: страница {index + 1} из {frame_count}")
            try:
                with python_warnings.catch_warnings():
                    python_warnings.simplefilter("ignore", Image.DecompressionBombWarning)
                    source.seek(index)
                if source.width * source.height > MAX_SCAN_PIXELS or max(source.size) > 40000:
                    raise PdfExtractionError(f"Страница изображения {index + 1} слишком велика: предел {MAX_SCAN_PIXELS // 1_000_000} Мп и 40 000 пикселей по стороне. Разделите лист на части без уменьшения DPI.")
                messages = [format_warning] if format_warning else []
                if frame_count > 1 and detected != "TIFF":
                    messages.append(f"В {detected} содержится {frame_count} кадров. Выбранные кадры сохранены отдельными страницами в том виде, как они отображаются; время показа и анимация не переносятся. Для одного чертежа выберите нужную страницу.")
                    if detected == "PNG" and source.info.get("default_image"):
                        messages.append("APNG содержит отдельное изображение по умолчанию: это страница 1; кадры анимации начинаются со страницы 2.")
                resolution = _resolution(source, dpi, messages)
                orientation = source.getexif().get(274, 1)
                # Pillow 12.3 pre-swaps raw-TIFF dimensions, but its Python raw
                # decoder still needs stored dimensions before transposing.
                # libtiff performs its own correct allocation. Keep this fix
                # local to TIFF; other decoders do not expose these attributes.
                if detected == "TIFF" and orientation in (5, 6, 7, 8) and not source.use_load_libtiff:
                    source._size = source._tile_size
                with python_warnings.catch_warnings():
                    python_warnings.simplefilter("ignore", Image.DecompressionBombWarning)
                    image = _scan_grayscale(ImageOps.exif_transpose(source))
                if orientation in (5, 6, 7, 8):
                    resolution = resolution[::-1]
            except (OSError, ValueError, SyntaxError, EOFError, Image.DecompressionBombError) as error:
                if isinstance(error, PdfExtractionError):
                    raise
                raise PdfExtractionError(f"Не удалось прочитать страницу изображения {index + 1}: файл повреждён или слишком велик.") from error
            if progress:
                progress(f"{detected} {image.width}×{image.height}, DPI {resolution[0]:g}×{resolution[1]:g}; исходное разрешение сохранено.")
            if preprocess:
                image, removed = clean_scan(image, math.sqrt(resolution[0] * resolution[1]), check_cancel=check_cancel, progress=progress)
                messages.append(f"Фон изображения осветлён, удалены изолированные шумовые пиксели: {removed:,}. Мелкие условные знаки и повреждённые линии требуют сверки с оригиналом.")
            if ocr_mode != "off":
                if environment is None:
                    environment = _ocr_environment(_find_tesseract(tesseract_path), ocr_language, check_cancel)
                rotation = 0
                if deskew:
                    image, resolution, rotation = _orient_scan(image, environment, resolution, check_cancel=check_cancel, progress=progress)
                if rotation:
                    messages.append(f"Ориентация изображения исправлена по тексту: поворот на {rotation}° по часовой стрелке.")
            if deskew:
                if progress:
                    progress("Определение перекоса изображения по длинным линиям")
                image, angle = deskew_scan(image, check_cancel=check_cancel)
                if angle:
                    messages.append(f"Перекос изображения исправлен на {angle:+.2f}° без обрезки; размер листа расширен. Перспектива и деформация бумаги автоматически не исправляются.")
                elif progress:
                    progress("Достоверный перекос изображения не обнаружен; ориентация сохранена.")
            width, height = image.width * 72 / resolution[0], image.height * 72 / resolution[1]
            page = PdfPage(index + 1, width, height, [], [], [], warnings=messages, raster_source=True)
            if ocr_mode != "off":
                page.texts = _ocr_tiles(image, resolution, environment, ocr_language, messages, check_cancel=check_cancel, progress=progress, preprocess=preprocess)
                page.ocr_used = True
            if vectorize_scans:
                page.drawings = _trace_scan(image, page, check_cancel=check_cancel, progress=progress)
            _check(check_cancel)
            page.images.append(RasterImage((0, 0, width, height), _png(image)))
            messages.append("Изображение преобразовано приблизительно: растровая подложка сохранена, уверенно распознанные надписи и восстановленные линии редактируются. Утраченные детали, размерные зависимости и точный шрифт автоматически не восстанавливаются.")
            result.append(page)
    return result
