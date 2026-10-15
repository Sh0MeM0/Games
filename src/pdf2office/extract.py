"""Native PDF geometry and text extraction, offline OCR and scan line tracing."""

from __future__ import annotations

import csv
import io
import math
import os
import re
import shutil
import subprocess
from pathlib import Path
from typing import Callable

import pymupdf as fitz
from PIL import Image

from .fonts import resource_root
from .models import Drawing, PdfPage, RasterImage, Segment, TextSpan
from .process import run_process


class PdfExtractionError(ValueError):
    """Actionable, user-facing conversion input error."""


def parse_page_range(spec: str, page_count: int) -> list[int]:
    """Return sorted unique zero-based pages; reject malformed/out-of-bounds input."""
    if page_count < 1:
        raise PdfExtractionError("PDF не содержит страниц.")
    if not spec.strip():
        return list(range(page_count))
    if len(spec) > 10000:
        raise PdfExtractionError("Слишком длинный список страниц.")
    selected: set[int] = set()
    for part in spec.split(","):
        match = re.fullmatch(r"\s*([0-9]+)\s*(?:-\s*([0-9]+)\s*)?", part)
        if not match:
            raise PdfExtractionError("Страницы задаются числами и диапазонами: 1,3-5.")
        if len(match[1]) > 12 or (match[2] and len(match[2]) > 12):
            raise PdfExtractionError("Номер страницы слишком велик.")
        first, last = int(match[1]), int(match[2] or match[1])
        if not 1 <= first <= last <= page_count:
            raise PdfExtractionError(f"Диапазон страниц должен находиться между 1 и {page_count}.")
        selected.update(range(first - 1, last))
    return sorted(selected)


def _point(value, matrix: fitz.Matrix) -> tuple[float, float]:
    point = fitz.Point(value) * matrix
    return float(point.x), float(point.y)


def _bbox(value, matrix: fitz.Matrix) -> tuple[float, float, float, float]:
    return tuple(float(coordinate) for coordinate in fitz.Rect(value) * matrix)


def _color(value) -> tuple[int, int, int] | None:
    if value is None:
        return None
    if isinstance(value, int):
        return (value >> 16) & 255, (value >> 8) & 255, value & 255
    if len(value) == 1:
        value = value * 3
    if len(value) == 4:
        c, m, y, k = value
        value = (1 - min(1, c + k), 1 - min(1, m + k), 1 - min(1, y + k))
    return tuple(max(0, min(255, round(channel * 255))) for channel in value[:3])


def _native_text(page: fitz.Page, blocks: list[dict]) -> list[TextSpan]:
    result: list[TextSpan] = []
    matrix = page.rotation_matrix
    for block in blocks:
        if block.get("type") != 0:
            continue
        for line in block.get("lines", []):
            direction = line.get("dir", (1, 0))
            start = _point((0, 0), matrix)
            end = _point(direction, matrix)
            angle = math.degrees(math.atan2(end[1] - start[1], end[0] - start[0]))
            for span in line.get("spans", []):
                if not span.get("text", "").strip():
                    continue
                flags = span.get("flags", 0)
                quad = fitz.recover_quad(direction, span)
                advance = math.hypot(quad.ur.x - quad.ul.x, quad.ur.y - quad.ul.y)
                result.append(TextSpan(
                    text=span["text"], bbox=_bbox(span["bbox"], matrix),
                    font=span.get("font", "Helvetica"), size=float(span.get("size", 12)),
                    color=_color(span.get("color", 0)), bold=bool(flags & 16),
                    italic=bool(flags & 2), origin=_point(span["origin"], matrix), angle=angle,
                    advance=advance,
                ))
    return result


def _parse_dashes(value: str | None) -> tuple[tuple[float, ...], float]:
    """Normalize a PDF dash array; every entry and phase uses PDF points."""
    if not value:
        return (), 0.0
    match = re.fullmatch(r"\s*\[([^\]]*)\]\s*(\S+)\s*", value)
    if not match:
        raise ValueError("Malformed PDF dash pattern")
    dashes = tuple(float(token) for token in match[1].split())
    offset = float(match[2])
    if any(not math.isfinite(length) or length < 0 for length in dashes):
        raise ValueError("Invalid PDF dash length")
    if not math.isfinite(offset) or offset < 0:
        raise ValueError("Invalid PDF dash phase")
    if dashes and sum(dashes) <= 0:
        raise ValueError("PDF dash pattern must have positive length")
    if len(dashes) % 2:
        dashes *= 2
    return dashes, offset


def _native_drawings(page: fitz.Page, warnings: list[str]) -> list[Drawing]:
    result = []
    matrix = page.rotation_matrix
    def warn_once(message: str) -> None:
        if message not in warnings:
            warnings.append(message)

    for path in page.get_drawings(extended=True):
        if path.get("type") == "clip":
            warn_once("В PDF есть обтравочные контуры: обрезка векторных объектов может отличаться после экспорта.")
            continue
        if path.get("type") == "group":
            warn_once("Группы прозрачности и режимы смешивания PDF не переносятся в редактируемые объекты.")
            continue
        if any(float(path.get(key, 1) or 0) < 1 for key in ("stroke_opacity", "fill_opacity") if path.get(key) is not None):
            warn_once("Прозрачность векторных объектов PDF не сохраняется; проверьте перекрывающиеся элементы.")
        try:
            dashes, dash_offset = _parse_dashes(path.get("dashes"))
        except (ValueError, TypeError):
            dashes, dash_offset = (), 0.0
            warn_once("Некорректный штриховой шаблон PDF заменён сплошной линией.")
        if dashes and dash_offset:
            warn_once("Начальное смещение штрихового шаблона PDF может отличаться после экспорта; проверьте начало штриховых линий.")
        segments = []
        for item in path.get("items", []):
            if item[0] == "l":
                segments.append(Segment("line", tuple(_point(point, matrix) for point in item[1:3])))
            elif item[0] == "c":
                segments.append(Segment("cubic", tuple(_point(point, matrix) for point in item[1:5])))
            elif item[0] in {"re", "qu"}:
                shape = item[1]
                points = [shape.tl, shape.tr, shape.br, shape.bl] if item[0] == "re" else [shape.ul, shape.ur, shape.lr, shape.ll]
                if item[0] == "re" and len(item) > 2 and item[2] < 0:
                    points.reverse()
                for first, second in zip(points, points[1:] + points[:1]):
                    segments.append(Segment("line", (_point(first, matrix), _point(second, matrix))))
        if segments:
            result.append(Drawing(segments=segments, stroke=_color(path.get("color")),
                                  fill=_color(path.get("fill")), width=float(path.get("width") or 0),
                                  closed=bool(path.get("closePath")),
                                  dashes=dashes, dash_offset=dash_offset))
    return result


def _native_images(page: fitz.Page, blocks: list[dict], warnings: list[str]) -> list[RasterImage]:
    images = []
    for block in blocks:
        if block.get("type") != 1:
            continue
        try:
            with Image.open(io.BytesIO(block["image"])) as source:
                source = source.convert("RGBA")
                # Normalized image coordinates map to PDF page coordinates.
                transform = fitz.Matrix(block["transform"]) * page.rotation_matrix
                bbox = fitz.Rect(0, 0, 1, 1) * transform
                inverse = ~transform
                # Bake rotation, reflection and skew because RasterImage has an
                # axis-aligned bounding box. Preserve the source's useful DPI.
                scale = min(4.166667, max(source.width / max(math.hypot(transform.a, transform.b), 1), source.height / max(math.hypot(transform.c, transform.d), 1)))
                width = max(1, min(12000, round(bbox.width * scale)))
                height = max(1, min(12000, round(bbox.height * scale)))
                if width * height > 50_000_000:
                    shrink = math.sqrt(50_000_000 / (width * height))
                    width, height = max(1, int(width * shrink)), max(1, int(height * shrink))
                pixel_to_page = fitz.Matrix(bbox.width / width, 0, 0, bbox.height / height, bbox.x0, bbox.y0)
                mapping = pixel_to_page * inverse
                affine = (mapping.a * source.width, mapping.c * source.width, mapping.e * source.width,
                          mapping.b * source.height, mapping.d * source.height, mapping.f * source.height)
                output = source.transform((width, height), Image.Transform.AFFINE, affine, Image.Resampling.BICUBIC)
                if block.get("mask"):
                    with Image.open(io.BytesIO(block["mask"])) as mask:
                        mask = mask.convert("L").resize(source.size)
                        alpha = mask.transform((width, height), Image.Transform.AFFINE, affine, Image.Resampling.BICUBIC)
                        output.putalpha(alpha)
                stream = io.BytesIO()
                output.save(stream, format="PNG")
                images.append(RasterImage(tuple(float(value) for value in bbox), stream.getvalue()))
        except Exception as error:
            warnings.append(f"Не удалось извлечь растровое изображение: {type(error).__name__}.")
    return images


def _find_tesseract(explicit: Path | None) -> Path:
    if explicit is not None:
        if not Path(explicit).is_file():
            raise PdfExtractionError(f"Не найден Tesseract: {explicit}")
        return Path(explicit)
    candidates = [resource_root() / "vendor/tesseract/tesseract.exe", resource_root() / "vendor/tesseract/tesseract"]
    try:
        from .runtime import tool_path
        resolved = tool_path("tesseract")
        if resolved:
            candidates.insert(0, Path(resolved))
    except (ImportError, FileNotFoundError):
        pass
    found = shutil.which("tesseract")
    if found:
        candidates.append(Path(found))
    for path in candidates:
        if path.is_file():
            return path
    raise PdfExtractionError("Для распознавания сканов нужен Tesseract OCR. Используйте полную сборку программы или укажите путь к tesseract.exe.")


def _tesseract_options(executable: Path) -> list[str]:
    folders = [executable.parent / "tessdata", resource_root() / "vendor/tesseract/tessdata"]
    if os.environ.get("TESSDATA_PREFIX"):
        folders.insert(0, Path(os.environ["TESSDATA_PREFIX"]))
    for folder in folders:
        if folder.is_dir():
            return ["--tessdata-dir", str(folder)]
    return []


def _run_tesseract(command: list[str], timeout: int | None = None, *,
                   input_data: bytes | None = None, cwd: Path | None = None,
                   check_cancel=None, heartbeat=None) -> subprocess.CompletedProcess:
    try:
        result = run_process(command, input_data=input_data, cwd=cwd,
                             timeout=timeout, check_cancel=check_cancel, heartbeat=heartbeat)
        return subprocess.CompletedProcess(result.args, result.returncode,
                                           result.stdout.decode("utf-8", errors="replace"),
                                           result.stderr.decode("utf-8", errors="replace"))
    except subprocess.TimeoutExpired as error:
        raise PdfExtractionError(f"Tesseract не ответил на служебный запрос за {timeout} секунд.") from error
    except OSError as error:
        raise PdfExtractionError(f"Не удалось запустить Tesseract OCR: {error}") from error


def _ocr(page: fitz.Page, native: list[TextSpan], executable: Path, language: str, warnings: list[str], *, check_cancel=None, progress=None) -> tuple[list[TextSpan], Image.Image]:
    if not re.fullmatch(r"[A-Za-z0-9_]+(?:\+[A-Za-z0-9_]+)*", language):
        raise PdfExtractionError("Некорректный язык OCR. Пример: rus+eng.")
    executable = executable.resolve()
    options = _tesseract_options(executable)
    # Windows Tesseract's file APIs may use the system code page. Python starts
    # the process/cwd through wide-character Win32 APIs; '.' and stdin avoid
    # passing a Unicode install, custom data or TEMP path to native file APIs.
    cwd = Path(options[1]).resolve() if options else None
    if cwd is not None:
        options = ["--tessdata-dir", "."]
    available = _run_tesseract([str(executable), "--list-langs", *options], timeout=30, cwd=cwd, check_cancel=check_cancel)
    languages = set(available.stdout.splitlines()[1:])
    missing = set(language.split("+")) - languages
    if available.returncode or missing:
        raise PdfExtractionError("Отсутствуют языковые данные Tesseract: " + ", ".join(sorted(missing or {language})) + ". Установите соответствующие .traineddata в каталог tessdata.")
    # Bound memory on large engineering sheets while retaining 300 dpi on A4/A3.
    dpi = min(300, max(72, int(72 * math.sqrt(40_000_000 / max(page.rect.width * page.rect.height, 1)))))
    if dpi < 300:
        warnings.append(f"Большая страница: OCR выполнен при {dpi} dpi для ограничения памяти.")
    pixmap = page.get_pixmap(dpi=dpi, colorspace=fitz.csRGB, alpha=False)
    image = Image.frombytes("RGB", (pixmap.width, pixmap.height), pixmap.samples)
    scale_x, scale_y = page.rect.width / image.width, page.rect.height / image.height
    rotation = 0
    mapping = fitz.Matrix(scale_x, scale_y)
    png_stream = io.BytesIO()
    image.save(png_stream, format="PNG")
    image_data = png_stream.getvalue()
    if "osd" in languages:
        try:
            orientation = _run_tesseract([str(executable), "stdin", "stdout", *options, "-l", "osd", "--dpi", str(dpi), "--psm", "0", "-c", "min_characters_to_try=15"], input_data=image_data, cwd=cwd, check_cancel=check_cancel,
                                         heartbeat=lambda elapsed: progress(f"Определение ориентации скана продолжается: {int(elapsed)} с.") if progress else None)
            rotate_match = re.search(r"Rotate:\s*(0|90|180|270)\b", orientation.stdout)
            confidence_match = re.search(r"Orientation confidence:\s*([0-9.]+)", orientation.stdout)
            if orientation.returncode == 0 and rotate_match and confidence_match and float(confidence_match[1]) >= 2:
                rotation = int(rotate_match[1])
        except PdfExtractionError:
            warnings.append("Не удалось определить ориентацию скана; проверьте повёрнутые надписи.")
    if rotation:
        png_stream = io.BytesIO()
        image.rotate(-rotation, expand=True).save(png_stream, format="PNG")
        image_data = png_stream.getvalue()
        transform = fitz.Matrix(rotation)
        extent = fitz.Rect(0, 0, image.width, image.height) * transform
        transform.e, transform.f = -extent.x0, -extent.y0
        mapping = (~transform) * mapping
    run = _run_tesseract([str(executable), "stdin", "stdout", *options, "-l", language, "--dpi", str(dpi), "--psm", "3", "-c", "tessedit_create_tsv=1"], input_data=image_data, cwd=cwd, check_cancel=check_cancel,
                         heartbeat=lambda elapsed: progress(f"Распознавание текста продолжается: {int(elapsed)} с; ограничение времени отключено.") if progress else None)
    if run.returncode:
        raise PdfExtractionError("Ошибка распознавания Tesseract: " + run.stderr.strip()[-1200:])
    result = []
    for row in csv.DictReader(io.StringIO(run.stdout), delimiter="\t", quoting=csv.QUOTE_NONE):
        text = (row.get("text") or "").strip()
        if row.get("level") != "5" or not text:
            continue
        try:
            confidence = float(row["conf"])
            if confidence < 15:
                continue
            x, y, width, height = (float(row[key]) for key in ("left", "top", "width", "height"))
        except (ValueError, KeyError, TypeError):
            continue
        bbox = _bbox((x, y, x + width, y + height), mapping)
        rect = fitz.Rect(bbox)
        # OCR is supplemental on mixed pages; overlapping native text always wins.
        if any((rect & fitz.Rect(span.bbox)).get_area() > rect.get_area() * 0.2 for span in native):
            continue
        result.append(TextSpan(text, bbox, "Liberation Sans", max(3.0, height * math.hypot(mapping.c, mapping.d)), (0, 0, 0),
                               origin=_point((x, y + height), mapping),
                               angle=math.degrees(math.atan2(mapping.b, mapping.a)), from_ocr=True,
                               advance=width * math.hypot(mapping.a, mapping.b)))
    warnings.append("Распознанный текст требует проверки: шрифт и начертание скана определяются приблизительно.")
    return result, image


def _trace_scan(image: Image.Image, page: PdfPage,
                check_cancel: Callable[[], None] | None = None,
                progress: Callable[[str], None] | None = None) -> list[Drawing]:
    """Trace the centre of scanned ink into connected, editable paths.

    Skeleton paths preserve short separate dashes and curved contours. Tracing
    the two Canny edges of a pen stroke instead would create two CAD lines.
    This recovers visible geometry, not dimension objects or CAD constraints.
    """
    try:
        import cv2
        import numpy as np
    except ImportError:
        page.warnings.append("Векторизация скана недоступна: отсутствует OpenCV. Растр сохранён.")
        return []

    import time

    last_update = time.monotonic()
    stage = "Векторизация скана"

    def checkpoint(message=None):
        nonlocal last_update, stage
        if check_cancel:
            check_cancel()
        now = time.monotonic()
        if message:
            stage = message
        if progress and (message or now - last_update >= 30):
            progress(message or f"{stage}: обработка продолжается; ограничение времени отключено.")
            last_update = now

    checkpoint("Векторизация: подготовка линий скана")
    # Keep native resolution for large engineering sheets (including 400 dpi
    # A1 scans). The exceptional 100 MP limit bounds working-array memory, not
    # processing time or the number of output objects.
    ratio = min(1.0, math.sqrt(100_000_000 / (image.width * image.height)))
    working = image.convert("L")
    if ratio < 1:
        working = working.resize((max(1, round(image.width * ratio)), max(1, round(image.height * ratio))), Image.Resampling.LANCZOS)
        page.warnings.append(f"Векторизация выполнена на копии {working.width} × {working.height} пикселей; очень мелкие детали проверьте по растровой подложке.")
    gray = np.array(working)
    sx, sy = page.width / working.width, page.height / working.height
    _, mask = cv2.threshold(gray, 0, 1, cv2.THRESH_BINARY_INV | cv2.THRESH_OTSU)
    del gray, working

    # Erase only recognized text boxes, with no generous padding into nearby
    # drawing geometry. Restore actual ink on lines that cross the whole box
    # and have evidence outside both sides. Never invent a line across a gap.
    for number, span in enumerate(page.texts):
        if number % 32 == 0:
            checkpoint()
        x0, y0, x1, y1 = span.bbox
        x0, x1 = max(0, math.floor(x0 / sx)), min(mask.shape[1], math.ceil(x1 / sx))
        y0, y1 = max(0, math.floor(y0 / sy)), min(mask.shape[0], math.ceil(y1 / sy))
        if x0 >= x1 or y0 >= y1:
            continue
        margin = max(6, min(40, max(x1 - x0, y1 - y0) // 2))
        left, top = max(0, x0 - margin), max(0, y0 - margin)
        right, bottom = min(mask.shape[1], x1 + margin), min(mask.shape[0], y1 + margin)
        region = mask[top:bottom, left:right].copy()
        bx0, by0, bx1, by1 = x0 - left, y0 - top, x1 - left, y1 - top
        preserve = np.zeros_like(region)
        candidates = cv2.HoughLinesP(region, 1, math.pi / 720, threshold=max(8, min(bx1 - bx0, by1 - by0)),
                                     minLineLength=max(12, min(bx1 - bx0, by1 - by0) + 6), maxLineGap=1)
        crossings = []
        if candidates is not None:
            for ax, ay, zx, zy in candidates[:, 0]:
                # Liang-Barsky interval of the candidate inside the text box.
                dx, dy = float(zx - ax), float(zy - ay)
                enter, leave = 0.0, 1.0
                intersects = True
                for p, q in ((-dx, ax - bx0), (dx, bx1 - ax), (-dy, ay - by0), (dy, by1 - ay)):
                    if p == 0:
                        if q < 0:
                            intersects = False
                            break
                    elif p < 0:
                        enter = max(enter, q / p)
                    else:
                        leave = min(leave, q / p)
                length = math.hypot(dx, dy)
                if intersects and enter < leave and enter * length >= 3 and (1 - leave) * length >= 3:
                    crossings.append((np.array((ax, ay), np.float32), np.array((zx, zy), np.float32)))
        # Hough may report several positions across a thick pen stroke. Fit
        # their common axis instead of restoring a broad band of nearby letters.
        groups = []
        for first, last in sorted(crossings, key=lambda pair: float(np.linalg.norm(pair[1] - pair[0])), reverse=True):
            direction = last - first
            direction /= np.linalg.norm(direction)
            for group in groups:
                group_direction = group[1] - group[0]
                group_direction /= np.linalg.norm(group_direction)
                cross = abs(float(direction[0] * group_direction[1] - direction[1] * group_direction[0]))
                delta = (first + last - group[0] - group[1]) / 2
                separation = abs(float(delta[0] * group_direction[1] - delta[1] * group_direction[0]))
                if cross < 0.025 and separation < 3:
                    group.extend((first, last))
                    break
            else:
                groups.append([first, last])
        for group in groups:
            vx, vy, cx, cy = cv2.fitLine(np.array(group), cv2.DIST_L2, 0, 0.01, 0.01).ravel()
            direction = np.array((vx, vy))
            center_point = np.array((cx, cy))
            projections = (np.array(group) - center_point) @ direction
            first = np.rint(center_point + projections.min() * direction).astype(int)
            last = np.rint(center_point + projections.max() * direction).astype(int)
            cv2.line(preserve, tuple(first), tuple(last), 1, 3)
        mask[y0:y1, x0:x1] = region[by0:by1, bx0:bx1] & preserve[by0:by1, bx0:bx1]

    checkpoint("Векторизация: удаление отдельных точек и выделение осей линий")
    # Measure the scanned pen, not the denoising filter: smoothing can fill
    # pinholes inside old-print stains and would inflate their stroke widths.
    widths = cv2.distanceTransform(mask, cv2.DIST_L2, 3)
    count, labels, stats, _ = cv2.connectedComponentsWithStats(mask, connectivity=8)
    extent = np.maximum(stats[:, cv2.CC_STAT_WIDTH], stats[:, cv2.CC_STAT_HEIGHT])
    minor = np.minimum(stats[:, cv2.CC_STAT_WIDTH], stats[:, cv2.CC_STAT_HEIGHT])
    area = stats[:, cv2.CC_STAT_AREA]
    compact = (area < 30) & (extent < 12) & (extent < 3 * np.maximum(minor, 1))
    noisy = np.count_nonzero(compact[1:]) > max(1000, mask.size // 5000)
    if noisy:
        checkpoint("Векторизация: подавление густого точечного шума с сохранением тонких линий")
        # This copy is exclusively for geometric tracing. Median filtering
        # suppresses blot edges, while monotone paths protect long thin
        # strokes and curves. Separate slender dashes and sparse components are
        # also retained so that they need not survive a median filter.
        thin = (area >= 5) & (extent >= 6) & ((area <= 1.5 * extent) | ((area <= 4 * extent) & (area < 0.15 * extent * minor)))
        thin[0] = False
        protected = thin[labels].astype(np.uint8)
        del labels, stats, thin, extent, minor, area, compact
        kernel_size = min(7, max(3, round(1.3 / max(sx, sy)) | 1))
        cleaned = cv2.medianBlur(mask, kernel_size) | protected
        del protected
        # A straight morphological kernel would fragment 1 px curves. Dynamic
        # paths may turn by one pixel per row, so both near-horizontal and
        # near-vertical curves survive without expanding the scanned ink.
        for rows, target in ((mask, cleaned), (mask.T, cleaned.T)):
            forward = np.empty(rows.shape, np.uint8)
            previous = np.zeros(rows.shape[1] + 2, np.uint8)
            for row_number in range(rows.shape[0]):
                if row_number % 128 == 0:
                    checkpoint()
                best = np.maximum(np.maximum(previous[:-2], previous[1:-1]), previous[2:])
                current = np.minimum(best + 1, 25) * rows[row_number]
                forward[row_number] = current
                previous[1:-1] = current
            previous.fill(0)
            for row_number in range(rows.shape[0] - 1, -1, -1):
                if row_number % 128 == 0:
                    checkpoint()
                best = np.maximum(np.maximum(previous[:-2], previous[1:-1]), previous[2:])
                current = np.minimum(best + 1, 25) * rows[row_number]
                target[row_number] |= ((forward[row_number] + current >= 26) & (forward[row_number] >= 6) & (current >= 6)).astype(np.uint8)
                previous[1:-1] = current
            del forward
        del rows, target, previous, current, best
        removed = int(np.count_nonzero(mask & (1 - cleaned)))
        mask = cleaned
        del cleaned
        count, labels, stats, _ = cv2.connectedComponentsWithStats(mask, connectivity=8)
        page.warnings.append(f"Для векторной геометрии дополнительно подавлен точечный шум ({max(0, removed)} пикселей); мелкие детали проверьте по сохранённой подложке.")
    # Discard isolated dirt but keep tiny long dashes / thin construction lines.
    keep = (stats[:, cv2.CC_STAT_AREA] >= 5) & (np.maximum(stats[:, cv2.CC_STAT_WIDTH], stats[:, cv2.CC_STAT_HEIGHT]) >= 4)
    keep[0] = False
    mask = keep[labels].astype(np.uint8)
    del labels, stats, keep
    if not mask.any():
        page.warnings.append("На скане не обнаружена геометрия для векторизации; растровая подложка сохранена.")
        return []

    # Zhang-Suen topology-preserving thinning. A small LUT avoids allocating
    # eight full-size neighbour arrays each iteration; OpenCV does the stencil
    # in native code. No arbitrary iteration/time cutoff is imposed.
    kernel = np.array([[128, 1, 2], [64, 0, 4], [32, 16, 8]], dtype=np.float32)
    codes = np.arange(256, dtype=np.uint16)
    bits = [(codes >> bit) & 1 for bit in range(8)]
    neighbours = sum(bits)
    transitions = sum((bits[bit] == 0) & (bits[(bit + 1) % 8] == 1) for bit in range(8))
    eligible = (neighbours >= 2) & (neighbours <= 6) & (transitions == 1)
    erase = [
        (eligible & ((bits[0] * bits[2] * bits[4]) == 0) & ((bits[2] * bits[4] * bits[6]) == 0)).astype(np.uint8),
        (eligible & ((bits[0] * bits[2] * bits[6]) == 0) & ((bits[0] * bits[4] * bits[6]) == 0)).astype(np.uint8),
    ]
    skeleton = np.pad(mask, 1)
    del mask
    iteration = 0
    while True:
        checkpoint()
        changed = False
        for lookup in erase:
            neighbourhood = cv2.filter2D(skeleton, cv2.CV_16U, kernel, borderType=cv2.BORDER_CONSTANT)
            removal = skeleton & lookup[neighbourhood]
            if removal.any():
                skeleton -= removal
                changed = True
        iteration += 1
        if not changed:
            break
        if iteration % 16 == 0:
            checkpoint(f"Векторизация: выделение осей линий, проход {iteration}")
    del neighbourhood, removal

    # Encode undirected pixel graph edges once. Drop diagonal shortcuts around
    # an existing orthogonal corner, avoiding triangles and duplicate paths.
    directions = ((-1, 0), (-1, 1), (0, 1), (1, 1), (1, 0), (1, -1), (0, -1), (-1, -1))
    adjacency = np.zeros(skeleton.shape, np.uint8)
    center = skeleton[1:-1, 1:-1]
    height, width = center.shape
    for bit, (dy, dx) in enumerate(directions):
        links = center & skeleton[1 + dy:1 + dy + height, 1 + dx:1 + dx + width]
        if dx and dy:
            links &= 1 - (skeleton[1:1 + height, 1 + dx:1 + dx + width] | skeleton[1 + dy:1 + dy + height, 1:1 + width])
        adjacency[1:-1, 1:-1] |= links << bit
    del skeleton, center, links
    stride = adjacency.shape[1]
    flat = adjacency.ravel()
    offsets = tuple(dy * stride + dx for dy, dx in directions)
    bit_counts = np.array([value.bit_count() for value in range(256)], np.uint8)
    low_bit = {1 << bit: bit for bit in range(8)}
    degree = bit_counts[flat]

    # Remove only short dead-end burrs attached to another stroke. Independent
    # short marks and longer terminal strokes are preserved.
    for _ in range(3):
        pruned = False
        for number, start in enumerate(np.flatnonzero(degree == 1)):
            if number % 10000 == 0:
                checkpoint()
            current, previous = int(start), -1
            path = []
            for _step in range(13 if noisy else 4):
                flags = int(flat[current])
                choices = []
                while flags:
                    edge = flags & -flags
                    bit = low_bit[edge]
                    flags ^= edge
                    following = current + offsets[bit]
                    if following != previous:
                        choices.append((bit, following))
                if len(choices) != 1:
                    break
                bit, following = choices[0]
                path.append((current, bit, following))
                previous, current = current, following
                if degree[current] != 2:
                    break
            radius = float(widths[current // stride - 1, current % stride - 1])
            spur_limit = min(12, max(4, round(radius * 2))) if noisy else 3
            if len(path) <= spur_limit and degree[current] >= 3:
                for current, bit, following in path:
                    flat[current] &= 255 ^ (1 << bit)
                    flat[following] &= 255 ^ (1 << ((bit + 4) % 8))
                pruned = True
        if not pruned:
            break
        degree = bit_counts[flat]
        checkpoint()

    checkpoint("Векторизация: построение редактируемых линий и полилиний")
    result = []
    visited_pixels = 0

    def trace(start, bit):
        nonlocal visited_pixels
        path = [start]
        junction_positions = {start: 0}
        current = start
        while True:
            following = current + offsets[bit]
            flat[current] &= 255 ^ (1 << bit)
            flat[following] &= 255 ^ (1 << ((bit + 4) % 8))
            path.append(following)
            current = following
            visited_pixels += 1
            if visited_pixels % 20000 == 0:
                checkpoint()
            if current == start:
                return path
            if degree[current] >= 3 and current in junction_positions:
                # Keep a loop as its own closed polyline when a stroke
                # leads into it tangentially.
                split = junction_positions[current]
                add_path(path[split:])
                return path[:split + 1]
            if not flat[current] or degree[current] == 1:
                return path
            if degree[current] >= 3:
                junction_positions[current] = len(path) - 1
                earlier = path[max(0, len(path) - 6)]
                incoming_x = current % stride - earlier % stride
                incoming_y = current // stride - earlier // stride
                incoming_length = math.hypot(incoming_x, incoming_y)
                choices = []
                flags = int(flat[current])
                while flags:
                    edge = flags & -flags
                    candidate = low_bit[edge]
                    flags ^= edge
                    next_node = current + offsets[candidate]
                    dx = next_node % stride - current % stride
                    dy = next_node // stride - current // stride
                    alignment = (incoming_x * dx + incoming_y * dy) / max(incoming_length * math.hypot(dx, dy), 1e-9)
                    choices.append((alignment, candidate))
                choices.sort(reverse=True)
                # Join only an unambiguous smooth continuation. All unchosen
                # branches remain in the graph and are traced separately.
                if not choices or choices[0][0] < 0.8 or (len(choices) > 1 and choices[0][0] - choices[1][0] < 0.1):
                    return path
                bit = choices[0][1]
                continue
            flags = int(flat[current])
            bit = low_bit[flags & -flags]

    def add_path(path):
        # A short edge between two junctions can be part of a long table rule.
        # Dropping it merely because it has few pixels punctures the backbone.
        if len(path) < 2 or (len(path) < 5 and not (degree[path[0]] >= 3 and degree[path[-1]] >= 3)):
            return
        coordinates = np.array([(index % stride - 1, index // stride - 1) for index in path], np.float32)
        closed = path[0] == path[-1]
        if closed and abs(cv2.contourArea(coordinates)) < 6:
            return
        # Subpixel simplification removes stair steps, while curved lines retain
        # enough vertices to remain editable rather than being replaced by chords.
        simplified = cv2.approxPolyDP(coordinates, 0.85, closed).reshape(-1, 2)
        if len(simplified) < (3 if closed else 2):
            return
        points = [(float(x * sx), float(y * sy)) for x, y in simplified]
        if closed:
            points.append(points[0])
        segments = [Segment("line", (first, last)) for first, last in zip(points, points[1:])]
        samples = coordinates[::max(1, len(coordinates) // 64)].astype(np.int32)
        radius = float(np.median(widths[samples[:, 1], samples[:, 0]]))
        # Distance runs between pixel centres; 2r would nearly double a 1 px
        # stroke. Subtract the shared centre pixel before applying page units.
        pen_width = max(1.0, 2 * radius - 1) * (sx + sy) / 2
        result.append(Drawing(segments, stroke=(0, 0, 0), width=pen_width, closed=closed))

    # Start at graph junctions and endpoints; the remaining edges form loops.
    for node_mask in (degree == 1, degree >= 3):
        for start in np.flatnonzero(node_mask):
            start = int(start)
            while flat[start]:
                flags = int(flat[start])
                add_path(trace(start, low_bit[flags & -flags]))
    for start in np.flatnonzero(flat):
        start = int(start)
        if flat[start]:
            flags = int(flat[start])
            add_path(trace(start, low_bit[flags & -flags]))
    checkpoint(f"Векторизация завершена: {len(result)} редактируемых линий и полилиний")
    page.warnings.append("Векторизация скана приблизительная: восстановлены оси линий и полилинии кривых; текст, мелкие детали, размеры и масштаб требуют проверки по сохранённой растровой подложке.")
    if len(result) > 20000:
        page.warnings.append("Скан содержит много мелких контуров: часть шума или нераспознанных букв могла попасть в векторную геометрию.")
    return result


def extract_pdf(path: Path, pages: str = "", password: str = "", ocr_mode: str = "auto",
                ocr_language: str = "rus+eng", tesseract_path: Path | None = None,
                vectorize_scans: bool = False, progress: Callable[[str], None] | None = None,
                check_cancel: Callable[[], None] | None = None) -> list[PdfPage]:
    """Extract editable primitives and optionally recognize the rendered page.

    ``ocr_mode``: auto supplements native text without duplicates; always uses
    OCR text instead of native text; off preserves native text only. Geometry
    and images remain intact in all modes. Each result uses the displayed PDF
    orientation. This is visual reconstruction, not recovery of CAD constraints,
    dimensions or original Word paragraph structures.
    """
    if ocr_mode not in {"auto", "always", "off"}:
        raise PdfExtractionError("Режим OCR должен быть auto, always или off.")
    path = Path(path)
    if not path.is_file():
        raise PdfExtractionError(f"PDF не найден: {path}")
    try:
        document = fitz.open(path)
    except Exception as error:
        raise PdfExtractionError("Не удалось открыть PDF: файл повреждён или имеет другой формат.") from error
    result = []
    with document:
        if not document.is_pdf:
            raise PdfExtractionError("Выбранный файл не является PDF.")
        if document.needs_pass and not document.authenticate(password):
            raise PdfExtractionError("PDF защищён паролем. Укажите правильный пароль.")
        indices = parse_page_range(pages, document.page_count)
        executable = None
        for index in indices:
            if check_cancel:
                check_cancel()
            if progress:
                progress(f"Чтение страницы {index + 1} из {document.page_count}")
            source = document[index]
            blocks = source.get_text("dict", flags=fitz.TEXTFLAGS_DICT)["blocks"]
            warnings: list[str] = []
            texts = _native_text(source, blocks)
            drawings = _native_drawings(source, warnings)
            images = _native_images(source, blocks, warnings)
            page = PdfPage(index + 1, float(source.rect.width), float(source.rect.height), texts, drawings, images, warnings=warnings)
            # Even small images can contain text on otherwise searchable pages.
            needs_ocr = ocr_mode == "always" or (ocr_mode == "auto" and (bool(images) or not texts))
            rendered = None
            if needs_ocr and (images or drawings or texts):
                if progress:
                    progress(f"Распознавание текста: страница {index + 1}")
                executable = executable or _find_tesseract(tesseract_path)
                recognized, rendered = _ocr(source, [] if ocr_mode == "always" else texts,
                                           executable, ocr_language, warnings,
                                           check_cancel=check_cancel, progress=progress)
                if ocr_mode == "always":
                    page.texts = recognized
                    if texts:
                        warnings.append("Режим OCR «всегда»: исходный текст заменён результатом распознавания; шрифты и начертания приблизительны.")
                else:
                    page.texts.extend(recognized)
                page.ocr_used = True
                if not page.texts:
                    warnings.append("На странице не удалось распознать текст.")
            if vectorize_scans and images:
                if rendered is None:
                    pixmap = source.get_pixmap(matrix=fitz.Matrix(min(3, 2800 / max(source.rect.width, source.rect.height)), min(3, 2800 / max(source.rect.width, source.rect.height))), colorspace=fitz.csRGB, alpha=False)
                    rendered = Image.frombytes("RGB", (pixmap.width, pixmap.height), pixmap.samples)
                page.drawings.extend(_trace_scan(rendered, page, check_cancel=check_cancel, progress=progress))
            if images:
                warnings.append("Растровые изображения сохранены; их пиксели не становятся редактируемыми объектами автоматически.")
            if any("\ufffd" in span.text for span in page.texts):
                warnings.append("В PDF есть символы без корректной Unicode-кодировки; проверьте текст или выберите режим OCR «всегда».")
            result.append(page)
    return result
