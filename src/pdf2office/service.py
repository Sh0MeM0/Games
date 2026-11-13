"""Batch conversions shared by the desktop app and command line."""
from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timezone
import json
import math
from pathlib import Path
import shutil
from tempfile import TemporaryDirectory
from typing import Callable

from . import __version__
from .input_formats import IMAGE_EXTENSIONS
from .runtime import setup_runtime, tool_path


@dataclass
class ConversionRequest:
    inputs: list[Path]
    output_dir: Path
    formats: tuple[str, ...] = ("dwg", "docx")
    pages: str = ""
    password: str = ""
    ocr_mode: str = "auto"
    ocr_language: str = "rus+eng"
    vectorize_scans: bool = False
    docx_mode: str = "layout"
    scale: float = 1.0
    font_overrides: dict[str, str] = field(default_factory=dict)
    font_dirs: list[Path] = field(default_factory=list)
    overwrite: bool = False
    scan_cleanup: bool = True
    scan_deskew: bool = True
    scan_dpi: int = 0
    vectorize_tiff: bool = True
    # None preserves callers/settings from versions that named this TIFF-only.
    vectorize_images: bool | None = None
    # Local neural OCR (PP-OCR via ONNX Runtime) with a Tesseract cross-check.
    ai_ocr: bool = True
    # "" = no translation; "en"/"ru" = additionally write translated copies.
    translate_to: str = ""


@dataclass
class ConversionResult:
    input: Path
    outputs: list[Path] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)
    error: str | None = None


class ConversionCancelled(Exception):
    pass


def _unique_stem(directory: Path, stem: str, formats: tuple[str, ...], overwrite: bool,
                 reserved: set[str]) -> str:
    from .cad import image_directory_name

    stem = stem.rstrip(" .") or "document"
    candidate, index = stem, 2
    while candidate.casefold() in reserved or (not overwrite and (
        any((directory / f"{candidate}.{suffix}").exists()
            for suffix in (*formats, "conversion.json"))
        or (directory / image_directory_name(candidate)).exists()
    )):
        candidate = f"{stem} ({index})"
        index += 1
    reserved.add(candidate.casefold())
    return candidate


def _engine_counts(pages) -> dict[str, int]:
    counts: dict[str, int] = {}
    for page in pages:
        for span in page.texts:
            if span.from_ocr:
                key = span.engine or "tesseract"
                counts[key] = counts.get(key, 0) + 1
    return counts


def _publish_file(source: Path, destination: Path, overwrite: bool) -> None:
    if overwrite:
        source.replace(destination)
        return
    # Exclusive creation also protects files created after the initial name check.
    created = False
    try:
        with destination.open("xb") as target:
            created = True
            with source.open("rb") as content:
                shutil.copyfileobj(content, target)
    except BaseException:
        if created:
            destination.unlink(missing_ok=True)
        raise


def convert(
    request: ConversionRequest,
    progress: Callable[[str], None] | None = None,
    cancelled: Callable[[], bool] | None = None,
) -> list[ConversionResult]:
    """Process each PDF/image independently and report partial failures explicitly."""
    from .cad import export_dwg, export_dxf, image_directory_name
    from .extract import extract_pdf
    from .scans import extract_image
    from .fonts import FontCatalog
    from .word import export_docx

    if not request.inputs:
        raise ValueError("Выберите хотя бы один PDF или файл изображения.")
    formats = tuple(dict.fromkeys(s.lower() for s in request.formats))
    if not formats or any(s not in {"dwg", "dxf", "docx"} for s in formats):
        raise ValueError("Выберите формат DWG, DXF или DOCX.")
    if not math.isfinite(request.scale) or request.scale <= 0:
        raise ValueError("Масштаб CAD должен быть положительным конечным числом.")
    if request.ocr_mode not in {"auto", "always", "off"}:
        raise ValueError("Неизвестный режим OCR.")
    if request.docx_mode not in {"layout", "flow"}:
        raise ValueError("Неизвестный режим DOCX.")
    if (not isinstance(request.scan_dpi, int) or isinstance(request.scan_dpi, bool)
            or (request.scan_dpi != 0 and not 72 <= request.scan_dpi <= 2400)):
        raise ValueError("DPI скана: 0 для автоматического выбора или целое число от 72 до 2400.")
    if request.translate_to not in {"", "en", "ru"}:
        raise ValueError("Перевод: пустое значение, en или ru.")
    if request.vectorize_images is not None and not isinstance(request.vectorize_images, bool):
        raise ValueError("Восстановление линий изображений должно быть включено или выключено.")
    vectorize_images = request.vectorize_tiff if request.vectorize_images is None else request.vectorize_images
    directory = Path(request.output_dir).expanduser().resolve()
    directory.mkdir(parents=True, exist_ok=True)
    setup_runtime()
    fonts = FontCatalog(extra_dirs=request.font_dirs)

    def check_cancel() -> None:
        if cancelled and cancelled():
            raise ConversionCancelled("Конвертация остановлена пользователем.")

    def notify(message: str) -> None:
        check_cancel()
        if progress:
            progress(message)

    results: list[ConversionResult] = []
    reserved_stems: set[str] = set()
    for source in request.inputs:
        source = Path(source).expanduser().resolve()
        if cancelled and cancelled():
            break
        result = ConversionResult(input=source)
        result.warnings.extend(fonts.warnings)
        results.append(result)
        stem = _unique_stem(directory, source.stem, formats, request.overwrite, reserved_stems)
        errors: list[str] = []
        pages_for_report = []
        report_stem = stem
        translation_report = None
        try:
            notify(f"Чтение: {source.name}")
            options = dict(pages=request.pages, ocr_mode=request.ocr_mode,
                           ocr_language=request.ocr_language,
                           tesseract_path=tool_path("tesseract"),
                           progress=notify, check_cancel=check_cancel,
                           neural=request.ai_ocr)
            if source.suffix.lower() in IMAGE_EXTENSIONS:
                pages = extract_image(source, **options,
                                  vectorize_scans=vectorize_images,
                                  preprocess=request.scan_cleanup,
                                  deskew=request.scan_deskew, dpi=request.scan_dpi)
            elif source.suffix.lower() == ".pdf":
                pages = extract_pdf(source, **options, password=request.password,
                                    vectorize_scans=request.vectorize_scans)
            else:
                raise ValueError("Поддерживаются PDF и изображения: " + ", ".join(sorted(IMAGE_EXTENSIONS)) + ".")
            pages_for_report = pages
            for page in pages:
                result.warnings.extend(page.warnings)
            overrides = dict(request.font_overrides)
            for page in pages:
                for span in page.texts:
                    if (span.font not in overrides and "*" not in overrides
                            and span.font.split("+", 1)[-1] not in overrides):
                        match = fonts.resolve(span.font, span.bold, span.italic)
                        overrides[span.font] = match.family
                        if match.substituted:
                            result.warnings.append(
                                f"Шрифт «{span.font}» заменён на «{match.family}»; "
                                "проверьте размеры и переносы текста."
                            )
            variants = [(stem, pages, "")]
            if request.translate_to:
                from .translate import translate_pages
                notify(f"Офлайн-перевод текста: {source.name}")
                translated_pages, stats = translate_pages(pages, request.translate_to, check_cancel=check_cancel,
                                                          progress=notify, warnings=result.warnings)
                translation_report = {"target": request.translate_to, "blocks": stats.blocks,
                                      "translated": stats.translated, "unchanged": stats.unchanged,
                                      "without_model": stats.unsupported}
                if stats.translated:
                    suffix = request.translate_to.upper()
                    translated_stem = _unique_stem(directory, f"{stem} ({suffix})", formats,
                                                   request.overwrite, reserved_stems)
                    variants.append((translated_stem, translated_pages, suffix))
                    result.warnings.append(
                        f"Перевод ({suffix}) выполнен локальной моделью Argos Translate: фрагментов {stats.translated}. "
                        "Машинный перевод требует проверки; числа, обозначения осей и коды сохраняются. "
                        "Результат записан в отдельные файлы с суффиксом " + f"({suffix})."
                    )
                else:
                    result.warnings.append("Перевод не выполнен: текста на другом языке не найдено или нет модели перевода.")
            # Each target has its own staging directory. A failed format cannot
            # publish a half-written output or prevent the other formats.
            for (variant_stem, variant_pages, label), fmt in ((variant, fmt) for variant in variants for fmt in formats):
                check_cancel()
                try:
                    notify(f"Запись {fmt.upper()}{' ' + label if label else ''}: {source.name}")
                    with TemporaryDirectory(prefix=".pdf-master-", dir=directory) as staging:
                        staged = Path(staging) / f"{variant_stem}.{fmt}"
                        if fmt == "docx":
                            warnings = export_docx(variant_pages, staged, mode=request.docx_mode,
                                                   font_overrides=overrides, font_catalog=fonts)
                        elif fmt == "dwg":
                            warnings = export_dwg(variant_pages, staged, converter_path=tool_path("dxf2dwg"),
                                                  scale=request.scale, font_overrides=overrides,
                                                  font_catalog=fonts, check_cancel=check_cancel,
                                                  progress=notify)
                        else:
                            warnings = export_dxf(variant_pages, staged, scale=request.scale,
                                                  font_overrides=overrides, font_catalog=fonts)
                        result.warnings.extend(warnings)
                        check_cancel()
                        if not staged.is_file() or staged.stat().st_size == 0:
                            raise RuntimeError("Конвертер не создал выходной файл.")
                        images = Path(staging) / image_directory_name(variant_stem)
                        if images.is_dir():
                            target_images = directory / images.name
                            target_images.mkdir(exist_ok=True)
                            for image in images.iterdir():
                                target_image = target_images / image.name
                                # Identical content-addressed assets can be shared by DWG/DXF.
                                if target_image.is_file() and target_image.read_bytes() == image.read_bytes():
                                    continue
                                _publish_file(image, target_image, request.overwrite)
                        target = directory / staged.name
                        _publish_file(staged, target, request.overwrite)
                        result.outputs.append(target)
                        notify(f"Готово: {target.name}")
                except ConversionCancelled:
                    raise
                except Exception as exc:
                    errors.append(f"{fmt.upper()}: {exc}")
                    if progress:
                        progress(f"Ошибка {fmt.upper()}: {exc}")
        except ConversionCancelled as exc:
            errors.append(str(exc))
        except Exception as exc:
            errors.append(str(exc))
        result.warnings = list(dict.fromkeys(result.warnings))
        result.error = "\n".join(errors) or None
        # No PDF password is recorded or persisted.
        report = {
            "application": "PDF Master " + __version__,
            "created_utc": datetime.now(timezone.utc).isoformat(),
            "input": str(source),
            "input_format": source.suffix.lower().lstrip("."),
            "outputs": [str(p) for p in result.outputs],
            "formats_requested": formats,
            "pages": request.pages or "all",
            "ocr_mode": request.ocr_mode,
            "ocr_language": request.ocr_language,
            "vectorize_scans": request.vectorize_scans,
            "image_settings": {"cleanup": request.scan_cleanup,
                               "deskew": request.scan_deskew,
                               "dpi": request.scan_dpi,
                               "vectorize": vectorize_images},
            "tiff_settings": {"cleanup": request.scan_cleanup,
                              "deskew": request.scan_deskew,
                              "dpi": request.scan_dpi,
                              "vectorize": vectorize_images},
            "cad_scale": request.scale,
            "docx_mode": request.docx_mode,
            "ai_ocr": request.ai_ocr,
            "ocr_engines": _engine_counts(pages_for_report),
            "translation": translation_report,
            "warnings": result.warnings,
            "error": result.error,
        }
        try:
            report_path = directory / f"{report_stem}.conversion.json"
            with report_path.open("w" if request.overwrite else "x", encoding="utf-8") as stream:
                json.dump(report, stream, ensure_ascii=False, indent=2)
        except OSError as exc:
            result.warnings.append(f"Не удалось записать отчёт: {exc}")
        if cancelled and cancelled():
            break
    return results

