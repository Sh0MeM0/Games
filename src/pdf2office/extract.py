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


def _trace_scan(image: Image.Image, page: PdfPage) -> list[Drawing]:
    try:
        import cv2
        import numpy as np
    except ImportError:
        page.warnings.append("Векторизация скана недоступна: отсутствует OpenCV. Растр сохранён.")
        return []
    working = image.copy()
    working.thumbnail((2800, 2800))
    gray = np.array(working.convert("L"))
    sx, sy = page.width / working.width, page.height / working.height
    _, mask = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY_INV | cv2.THRESH_OTSU)
    for span in page.texts:
        x0, y0, x1, y1 = span.bbox
        cv2.rectangle(mask, (max(0, int(x0 / sx) - 2), max(0, int(y0 / sy) - 2)),
                      (int(x1 / sx) + 2, int(y1 / sy) + 2), 0, -1)
    edges = cv2.Canny(mask, 50, 150)
    lines = cv2.HoughLinesP(edges, 1, math.pi / 1800, threshold=35,
                            minLineLength=max(25, round(12 / sx)), maxLineGap=7)
    if lines is None:
        page.warnings.append("На скане не обнаружены прямые отрезки для векторизации.")
        return []
    result = []
    seen = set()
    for x0, y0, x1, y1 in lines[:, 0][:5000]:
        points = ((float(x0 * sx), float(y0 * sy)), (float(x1 * sx), float(y1 * sy)))
        signature = tuple(sorted((round(x / 2), round(y / 2)) for x, y in points))
        if signature in seen:
            continue
        seen.add(signature)
        result.append(Drawing([Segment("line", points)], stroke=(0, 0, 0), width=0.5))
    page.warnings.append("Векторизация скана приблизительная: восстановлены прямые отрезки; возможны двойные линии. Дуги, размеры, штриховки и масштаб требуют проверки; исходный растр сохранён.")
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
                page.drawings.extend(_trace_scan(rendered, page))
            if images:
                warnings.append("Растровые изображения сохранены; их пиксели не становятся редактируемыми объектами автоматически.")
            if any("\ufffd" in span.text for span in page.texts):
                warnings.append("В PDF есть символы без корректной Unicode-кодировки; проверьте текст или выберите режим OCR «всегда».")
            result.append(page)
    return result
