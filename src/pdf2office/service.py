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
    """Process each PDF independently and report partial failures explicitly."""
    from .cad import export_dwg, export_dxf, image_directory_name
    from .extract import extract_pdf
    from .fonts import FontCatalog
    from .word import export_docx

    if not request.inputs:
        raise ValueError("Выберите хотя бы один PDF-файл.")
    formats = tuple(dict.fromkeys(s.lower() for s in request.formats))
    if not formats or any(s not in {"dwg", "dxf", "docx"} for s in formats):
        raise ValueError("Выберите формат DWG, DXF или DOCX.")
    if not math.isfinite(request.scale) or request.scale <= 0:
        raise ValueError("Масштаб CAD должен быть положительным конечным числом.")
    if request.ocr_mode not in {"auto", "always", "off"}:
        raise ValueError("Неизвестный режим OCR.")
    if request.docx_mode not in {"layout", "flow"}:
        raise ValueError("Неизвестный режим DOCX.")
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
        try:
            notify(f"Чтение: {source.name}")
            pages = extract_pdf(
                source, pages=request.pages, password=request.password,
                ocr_mode=request.ocr_mode, ocr_language=request.ocr_language,
                tesseract_path=tool_path("tesseract"),
                vectorize_scans=request.vectorize_scans, progress=notify,
            )
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
            # Each target has its own staging directory. A failed format cannot
            # publish a half-written output or prevent the other formats.
            for fmt in formats:
                check_cancel()
                try:
                    notify(f"Запись {fmt.upper()}: {source.name}")
                    with TemporaryDirectory(prefix=".pdf-master-", dir=directory) as staging:
                        staged = Path(staging) / f"{stem}.{fmt}"
                        if fmt == "docx":
                            warnings = export_docx(pages, staged, mode=request.docx_mode,
                                                   font_overrides=overrides, font_catalog=fonts)
                        elif fmt == "dwg":
                            warnings = export_dwg(pages, staged, converter_path=tool_path("dxf2dwg"),
                                                  scale=request.scale, font_overrides=overrides,
                                                  font_catalog=fonts)
                        else:
                            warnings = export_dxf(pages, staged, scale=request.scale,
                                                  font_overrides=overrides, font_catalog=fonts)
                        result.warnings.extend(warnings)
                        check_cancel()
                        if not staged.is_file() or staged.stat().st_size == 0:
                            raise RuntimeError("Конвертер не создал выходной файл.")
                        images = Path(staging) / image_directory_name(stem)
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
            "application": "PDF Master 0.1.0",
            "created_utc": datetime.now(timezone.utc).isoformat(),
            "input": str(source),
            "outputs": [str(p) for p in result.outputs],
            "formats_requested": formats,
            "pages": request.pages or "all",
            "ocr_mode": request.ocr_mode,
            "ocr_language": request.ocr_language,
            "vectorize_scans": request.vectorize_scans,
            "cad_scale": request.scale,
            "docx_mode": request.docx_mode,
            "warnings": result.warnings,
            "error": result.error,
        }
        try:
            report_path = directory / f"{stem}.conversion.json"
            with report_path.open("w" if request.overwrite else "x", encoding="utf-8") as stream:
                json.dump(report, stream, ensure_ascii=False, indent=2)
        except OSError as exc:
            result.warnings.append(f"Не удалось записать отчёт: {exc}")
        if cancelled and cancelled():
            break
    return results

