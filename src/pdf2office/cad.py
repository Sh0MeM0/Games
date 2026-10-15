"""Editable CAD export in millimetres, with the bundled GNU LibreDWG writer.

The DWG target is AutoCAD 2000 (AC1015), supported by contemporary CAD tools.
PDF drawing paths are geometry, not reconstructed parametric CAD constraints.
"""

from __future__ import annotations

import hashlib
import io
import math
import os
from contextlib import contextmanager
from functools import lru_cache
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time

import ezdxf
from ezdxf.colors import DXF_DEFAULT_COLORS, int2rgb
from PIL import Image
from fontTools.pens.boundsPen import BoundsPen
from fontTools.ttLib import TTFont

from .fonts import FontCatalog
from .models import PdfPage
from .process import run_process
from .runtime import tool_path


POINT_TO_MM = 25.4 / 72.0
DWG_SIGNATURE = b"AC1015"


class CadConversionError(RuntimeError):
    """An actionable CAD export error suitable for showing to the user."""


def _color_index(color: tuple[int, int, int] | None) -> int:
    if color is None or max(color) < 20:
        return 7
    return min(
        range(1, 256),
        key=lambda index: sum(
            (a - b) ** 2 for a, b in zip(color, int2rgb(DXF_DEFAULT_COLORS[index]))
        ),
    )


class _CadFontMetrics:
    """Metrics of the font CAD will use, expressed in em units.

    Keep the font in memory: a cached user font must not leave its file locked
    on Windows. PDF sizes describe the em square; CAD TEXT height describes
    capital letters. Confusing the two changes both height and line length.
    """

    def __init__(self, path: Path):
        self.font = TTFont(io.BytesIO(path.read_bytes()), fontNumber=0)
        self.units = self.font["head"].unitsPerEm
        self.cmap = self.font.getBestCmap() or {}
        self.glyphs = self.font.getGlyphSet()
        self.bounds = {}
        cap = getattr(self.font.get("OS/2"), "sCapHeight", 0)
        if cap <= 0:
            bounds = self._glyph_bounds(self.cmap.get(ord("H"), ".notdef"))
            cap = bounds[3] if bounds else self.units * 0.7
        self.cap_height = cap / self.units

    def _glyph_bounds(self, name):
        if name not in self.bounds:
            pen = BoundsPen(self.glyphs)
            self.glyphs[name].draw(pen)
            self.bounds[name] = pen.bounds
        return self.bounds[name]

    def measure(self, text):
        advance = 0.0
        bounds = None
        for char in text:
            name = self.cmap.get(ord(char), ".notdef")
            box = self._glyph_bounds(name)
            if box is not None:
                x0, y0, x1, y1 = box
                box = (x0 + advance, y0, x1 + advance, y1)
                if bounds is None:
                    bounds = box
                else:
                    bounds = (min(bounds[0], box[0]), min(bounds[1], box[1]),
                              max(bounds[2], box[2]), max(bounds[3], box[3]))
            advance += self.font["hmtx"][name][0]
        normalized = tuple(value / self.units for value in bounds) if bounds else None
        return advance / self.units, normalized


@lru_cache(maxsize=64)
def _cad_font_metrics(path: Path | None):
    if path is None:
        return None
    try:
        return _CadFontMetrics(path)
    except (OSError, ValueError, KeyError):
        return None


def _text_geometry(span, metrics):
    """Return the PDF-space baseline, CAD capital height and width factor.

    A native span is already a positioned line, so a baseline-aligned TEXT
    avoids wrapping altogether. OCR instead supplies the ink rectangle; fit
    the chosen font's glyph bounds into that rectangle, including descenders.
    """
    angle = math.radians(span.angle)
    direction = (math.cos(angle), math.sin(angle))
    up = (math.sin(angle), -math.cos(angle))
    cap = metrics.cap_height if metrics else 0.7
    advance, ink = metrics.measure(span.text) if metrics else (len(span.text) * 0.6, None)
    target_advance = span.advance
    if target_advance is None:
        # Older callers may only have an axis-aligned rectangle. This fallback
        # is exact for horizontal and vertical text; extraction supplies the
        # actual baseline advance for arbitrary angles.
        target_advance = ((span.bbox[2] - span.bbox[0]) * abs(direction[0])
                          + (span.bbox[3] - span.bbox[1]) * abs(direction[1]))
    size = max(float(span.size), 0.01)
    anchor = span.origin
    if span.from_ocr and ink and ink[3] > ink[1] and ink[2] > ink[0]:
        # OCR size is measured ink height, rather than a PDF font size.
        size /= ink[3] - ink[1]
        width = target_advance / ((ink[2] - ink[0]) * size)
        if anchor is None:
            anchor = (span.bbox[0], span.bbox[3])
        anchor = (anchor[0] - ink[0] * size * width * direction[0] - ink[1] * size * up[0],
                  anchor[1] - ink[0] * size * width * direction[1] - ink[1] * size * up[1])
    else:
        width = target_advance / (advance * size) if advance > 0 else 1.0
        if anchor is None:
            anchor = (span.bbox[0] - cap * size * up[0],
                      span.bbox[1] - cap * size * up[1])
    if not math.isfinite(width) or width <= 0:
        width = 1.0
    return anchor, size * cap, width


def _text_fragments(text: str) -> list[str]:
    # TEXT interprets %%d/%%p and Unicode escape sequences.
    # A PDF may contain those characters literally (for example a CAD manual).
    # Separate only these exceptional runs into adjacent editable entities;
    # ordinary text remains one entity per PDF span.
    if re.search(r"%%[cdpkou]|\\U\+[0-9a-f]{4}", text, re.IGNORECASE):
        return [part for part in re.split(r"([%\\])", text) if part]
    return [text]


def _font_name(font: str, overrides: dict[str, str]) -> str:
    cleaned = re.sub(r"^[A-Z]{6}\+", "", font).strip() or "Arial"
    selected = overrides.get(font, overrides.get(cleaned, overrides.get("*", cleaned)))
    # CAD style references must not contain a machine-specific absolute path.
    return selected.replace("\\", "/").rsplit("/", 1)[-1]


def image_directory_name(stem: str) -> str:
    """ASCII resource references also work in legacy DWG codepages."""
    safe = re.sub(r"[^A-Za-z0-9_-]+", "_", stem).strip("_-")[:40] or "document"
    digest = hashlib.sha256(stem.encode("utf-8")).hexdigest()[:12]
    return f"{safe}_{digest}_images"


def _split_paths(segments):
    """A PDF drawing may contain multiple disconnected subpaths (including holes)."""
    paths = []
    current = []
    for segment in segments:
        expected = 4 if segment.kind == "cubic" else 2
        if segment.kind not in ("line", "cubic") or len(segment.points) != expected:
            raise CadConversionError("PDF содержит неподдерживаемый сегмент контура.")
        if current and not _same_point(current[-1].points[-1], segment.points[0]):
            paths.append(current)
            current = []
        current.append(segment)
    if current:
        paths.append(current)
    return paths


def _same_point(a, b) -> bool:
    return math.isclose(a[0], b[0], abs_tol=1e-6) and math.isclose(
        a[1], b[1], abs_tol=1e-6
    )


def _add_stroke(msp, path, transform, attributes, closed):
    if all(segment.kind == "line" for segment in path):
        points = [transform(path[0].points[0])]
        points.extend(transform(segment.points[1]) for segment in path)
        if len(path) == 1 and not closed:
            msp.add_line(points[0], points[1], dxfattribs=attributes)
        else:
            if _same_point(points[0], points[-1]):
                points.pop()
                closed = True
            polyline = msp.add_lwpolyline(points, close=closed, dxfattribs=attributes)
            if attributes.get("linetype"):
                # Continue the dash sequence across connected vertices.
                polyline.dxf.flags |= 128
        return
    for segment in path:
        points = [transform(point) for point in segment.points]
        if segment.kind == "line":
            msp.add_line(points[0], points[1], dxfattribs=attributes)
        else:
            spline = msp.add_spline(dxfattribs=attributes)
            # A cubic Bezier is exactly a degree-3 clamped B-spline with four
            # control points. Keep it analytic rather than flattening to lines.
            spline.set_open_uniform([(x, y, 0) for x, y in points], degree=3)
    if closed and not _same_point(path[0].points[0], path[-1].points[-1]):
        msp.add_line(
            transform(path[-1].points[-1]),
            transform(path[0].points[0]),
            dxfattribs=attributes,
        )


def _add_fill(msp, paths, transform, attributes):
    hatch = msp.add_hatch(color=attributes["color"], dxfattribs=attributes)
    for path in paths:
        if all(segment.kind == "line" for segment in path):
            points = [transform(path[0].points[0])]
            points.extend(transform(segment.points[1]) for segment in path)
            # Default rather than EXTERNAL flags: even/odd nesting preserves
            # holes in compound PDF paths.
            hatch.paths.add_polyline_path(points, is_closed=True, flags=0)
        else:
            edge_path = hatch.paths.add_edge_path(flags=0)
            for segment in path:
                points = [transform(point) for point in segment.points]
                if segment.kind == "line":
                    edge_path.add_line(points[0], points[1])
                else:
                    edge_path.add_spline(
                        control_points=points,
                        knot_values=[0, 0, 0, 0, 1, 1, 1, 1],
                        degree=3,
                    )
            if not _same_point(path[0].points[0], path[-1].points[-1]):
                edge_path.add_line(
                    transform(path[-1].points[-1]), transform(path[0].points[0])
                )


def _write_dxf(pages, output, scale, font_overrides, image_directory, font_catalog=None, for_libredwg=False):
    if not pages:
        raise CadConversionError("Нет страниц для сохранения чертежа.")
    if not math.isfinite(scale) or scale <= 0:
        raise ValueError("Масштаб должен быть конечным положительным числом.")
    output = Path(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    doc = ezdxf.new("R2000")
    doc.units = ezdxf.units.MM
    doc.header["$MEASUREMENT"] = 1
    doc.header["$LWDISPLAY"] = True
    doc.header["$DIMSTYLE"] = "Standard"
    msp = doc.modelspace()
    warnings = []
    overrides = font_overrides or {}
    catalog = font_catalog or FontCatalog()
    substitutions = set()
    factor = POINT_TO_MM * scale
    x_offset = 0.0
    styles = {}
    linetypes = {}
    for page in pages:
        layers = {kind: f"PAGE_{page.number}_{kind}" for kind in ("GEOMETRY", "TEXT", "RASTER_REFERENCE")}
        for layer in layers.values():
            doc.layers.new(layer)

        def transform(point):
            return x_offset + point[0] * factor, (page.height - point[1]) * factor

        for drawing in page.drawings:
            paths = _split_paths(drawing.segments)
            if not paths:
                continue
            if drawing.fill is not None:
                _add_fill(msp, paths, transform, {"layer": layers["GEOMETRY"], "color": _color_index(drawing.fill)})
            if drawing.stroke is not None:
                # DXF lineweights are hundredths of millimetres from a fixed set.
                from ezdxf.lldxf.const import VALID_DXF_LINEWEIGHTS

                allowed = [value for value in VALID_DXF_LINEWEIGHTS if value >= 0]
                lineweight = min(allowed, key=lambda value: abs(value - drawing.width * factor * 100))
                attributes = {"layer": layers["GEOMETRY"], "color": _color_index(drawing.stroke), "lineweight": lineweight}
                dashes = tuple(drawing.dashes)
                if dashes:
                    if len(dashes) % 2:
                        dashes *= 2  # PDF repeats odd-length dash arrays twice.
                    if len(dashes) > 255 or any(not math.isfinite(value) or value < 0 for value in dashes) or not any(dashes):
                        warnings.append(f"Страница {page.number}: неподдерживаемый шаблон штрихов заменён сплошной линией.")
                    else:
                        pattern = tuple(value * factor * (1 if index % 2 == 0 else -1) for index, value in enumerate(dashes))
                        if pattern not in linetypes:
                            name = "PDF_DASH_" + hashlib.sha256(repr(pattern).encode("ascii")).hexdigest()[:12]
                            doc.linetypes.add(name, [sum(dashes) * factor, *pattern], description="PDF dash pattern")
                            linetypes[pattern] = name
                        attributes["linetype"] = linetypes[pattern]
                        if drawing.dash_offset:
                            warnings.append(f"Страница {page.number}: длины штрихов сохранены; начальное смещение шаблона штрихов не перенесено в CAD.")
                        if any(len(path) > 1 and any(segment.kind == "cubic" for segment in path) for path in paths):
                            warnings.append(f"Страница {page.number}: шаблон штрихов начинается заново на каждом отдельном криволинейном CAD-сегменте.")
                for path in paths:
                    _add_stroke(msp, path, transform, attributes, drawing.closed)

        for span in page.texts:
            if not span.text:
                continue
            family = _font_name(span.font, overrides)
            lookup = Path(family).stem if Path(family).suffix.lower() in {".ttf", ".otf", ".ttc"} else family
            match = catalog.resolve(lookup, bold=span.bold, italic=span.italic)
            if match.substituted:
                substitutions.add((family, match.family))
            key = (match.family, match.bold, match.italic)
            if key not in styles:
                style_name = "PDF_" + hashlib.sha256(repr(key).encode("utf-8")).hexdigest()[:16]
                style = doc.styles.new(style_name, dxfattribs={"font": match.path.name if match.path else match.family})
                style.set_extended_font_data(match.family, italic=match.italic, bold=match.bold)
                styles[key] = style_name
            metrics = _cad_font_metrics(match.path)
            if metrics is None:
                warnings.append(f"CAD: не удалось прочитать метрики шрифта «{match.family}»; размеры текста приближённые.")
            anchor, height, width = _text_geometry(span, metrics)
            angle = float(span.angle)
            direction = (math.cos(math.radians(angle)), math.sin(math.radians(angle)))
            em_size = height / (metrics.cap_height if metrics else 0.7)
            offset = 0.0
            # Each extracted PDF span is a positioned line. TEXT has no line
            # wrapping and preserves its baseline and explicit width factor.
            # LibreDWG 0.14's MTEXT importer instead aliases height and width
            # (DXF 40), writing zero height and a narrow wrapping rectangle.
            for fragment in _text_fragments(span.text):
                position = (anchor[0] + offset * direction[0], anchor[1] + offset * direction[1])
                msp.add_text(
                    # In DXF caret notation, "^ " represents a literal ^.
                    fragment.replace("^", "^ "),
                    dxfattribs={
                        "layer": layers["TEXT"],
                        "insert": transform(position),
                        "height": max(height * factor, 0.01),
                        "width": width,
                        "rotation": -angle,
                        "style": styles[key],
                        "color": _color_index(span.color),
                    },
                )
                length = metrics.measure(fragment)[0] if metrics else len(fragment) * 0.6
                offset += length * em_size * width

        for index, raster in enumerate(page.images, start=1):
            try:
                with Image.open(io.BytesIO(raster.data)) as source:
                    image = source.convert("RGBA")
                    pixel_size = image.size
                    digest = hashlib.sha256(raster.data).hexdigest()[:16]
                    image_directory.mkdir(parents=True, exist_ok=True)
                    image_path = image_directory / f"page_{page.number}_{index}_{digest}.png"
                    if not image_path.exists():
                        image.save(image_path)
            except (OSError, ValueError) as exc:
                warnings.append(f"Страница {page.number}: не удалось сохранить растровое изображение: {exc}")
                continue
            # Relative filename survives moving a drawing together with its
            # adjacent image directory, including after the temporary DXF goes.
            relative = f"{image_directory.name}/{image_path.name}"
            image_def = doc.add_image_def(relative, size_in_pixel=pixel_size)
            x0, y0, x1, y1 = raster.bbox
            msp.add_image(
                image_def,
                insert=transform((x0, y1)),
                size_in_units=((x1 - x0) * factor, (y1 - y0) * factor),
                dxfattribs={"layer": layers["RASTER_REFERENCE"]},
            )
        if page.images:
            warnings.append(f"Страница {page.number}: растровые фрагменты сохранены как IMAGE со ссылками на папку {image_directory.name}; их пиксели не являются редактируемыми CAD-объектами.")
            if page.ocr_used and any(span.text for span in page.texts):
                doc.layers.get(layers["RASTER_REFERENCE"]).off()
                warnings.append(f"Страница {page.number}: слой {layers['RASTER_REFERENCE']} выключен, чтобы исходные растровые буквы не перекрывали редактируемый OCR-текст. Его можно включить в CAD для сверки; часть деталей может существовать только на растре.")
        x_offset += page.width * factor + 20 * scale
    # AC1015 predates true colour. An indexed CAD palette is necessary, not an
    # implicit promise that arbitrary PDF colours survived byte-for-byte.
    warnings.append("DWG/DXF AutoCAD 2000: цвета приближены к палитре CAD; размеры указаны в мм. Масштаб исходного PDF/TIFF необходимо проверить по известному размеру.")
    warnings.extend(f"CAD: шрифт «{source}» заменён на «{target}». Для правильного отображения установите используемые шрифты на компьютере с CAD." for source, target in sorted(substitutions))
    doc.saveas(output)
    if for_libredwg:
        _prepare_libredwg_dxf(doc, output)
    return list(dict.fromkeys(warnings))


def _prepare_libredwg_dxf(doc, output):
    """Normalize the private intermediary for LibreDWG 0.14's R2000 reader.

    LibreDWG reserves 1F for model space, 1 for its table and 12 for ACAD
    application data. ezdxf uses those handles for unrelated objects, producing
    duplicate handles in DWG. Remap *all* handle references together. Its modern
    default material/mleader objects are unused and unsupported by this target.
    The public DXF export remains untouched by this backend-specific operation.
    """
    from ezdxf.lldxf.types import HEX_HANDLE_CODES

    reserved = {
        doc.modelspace().block_record.dxf.handle: "1F",
        doc.tables.block_records.head.dxf.handle: "1",
        doc.appids.get("ACAD").dxf.handle: "12",
    }
    removed = {entity.dxf.handle for entity in doc.objects if entity.dxftype() in {"MATERIAL", "MLEADERSTYLE"}}
    for name in ("ACAD_MATERIAL", "ACAD_MLEADERSTYLE"):
        dictionary = doc.rootdict.get(name)
        if dictionary is not None:
            removed.add(dictionary.dxf.handle)
    lines = output.read_bytes().splitlines()
    tags = [(int(lines[i]), lines[i + 1]) for i in range(0, len(lines), 2)]
    records = []
    for tag in tags:
        if tag[0] == 0 or not records:
            records.append([])
        records[-1].append(tag)
    normalized = []
    for record in records:
        handle = next((value.decode("ascii").strip() for code, value in record if code in (5, 105)), "")
        if handle in removed:
            continue
        if record[0] == (0, b"CLASS") and any(b"MATERIAL" in value.upper() or b"MLEADERSTYLE" in value.upper() for code, value in record if code in (1, 2)):
            continue
        skip_next = False
        for index, (code, value) in enumerate(record):
            if skip_next:
                skip_next = False
                continue
            if code == 3 and value in (b"ACAD_MATERIAL", b"ACAD_MLEADERSTYLE"):
                skip_next = True
                continue
            if record[0] == (0, b"LWPOLYLINE") and code == 70:
                flags = int(value)
                if flags & 1 and flags & 128:
                    # LibreDWG 0.14 translates closed and PLINEGEN with an
                    # if/elif, so both flags together otherwise lose PLINEGEN.
                    # Pretranslate only that bit in this private intermediary.
                    value = str((flags & ~128) | 256).encode("ascii")
            if code in HEX_HANDLE_CODES:
                text = value.decode("ascii").strip()
                if text in removed:
                    continue
                if text != "0":
                    value = reserved.get(text, f"{int(text, 16) + 256:X}").encode("ascii")
            normalized.extend((str(code).encode("ascii"), value))
    output.write_bytes(b"\n".join(normalized) + b"\n")


def export_dxf(pages: list[PdfPage], output: Path, scale: float = 1.0, font_overrides: dict[str, str] | None = None, font_catalog: FontCatalog | None = None) -> list[str]:
    """Export editable DXF; raster sidecars stay alongside the output file."""
    output = Path(output)
    return _write_dxf(pages, output, scale, font_overrides, output.with_name(image_directory_name(output.stem)), font_catalog)


def find_dwg_converter(converter_path: Path | None = None) -> Path:
    """Find an explicit, bundled, configured or PATH LibreDWG executable."""
    if converter_path is not None:
        candidate = Path(converter_path).expanduser().resolve()
        if not candidate.is_file():
            raise CadConversionError(f"Конвертер LibreDWG не найден: {candidate}")
        return candidate
    executable = "dxf2dwg.exe" if os.name == "nt" else "dxf2dwg"
    roots = [Path(getattr(sys, "_MEIPASS", Path(__file__).resolve().parents[2]))]
    if getattr(sys, "frozen", False):
        roots.append(Path(sys.executable).resolve().parent)
    for root in roots:
        for candidate in (root / "vendor" / "libredwg" / executable, root / "vendor" / "libredwg" / "bin" / executable):
            if candidate.is_file():
                return candidate.resolve()
    configured = os.environ.get("PDF2OFFICE_DXF2DWG")
    found = configured or tool_path("dxf2dwg") or shutil.which(executable)
    if found and Path(found).is_file():
        return Path(found).resolve()
    raise CadConversionError("Встроенный LibreDWG (dxf2dwg) не найден. Используйте полную portable-сборку или установщик; для запуска из исходников задайте PDF2OFFICE_DXF2DWG. Доступен экспорт DXF.")


@contextmanager
def _conversion_directory():
    # Keep the native process cwd outside the output transaction. On Windows a
    # just-exited native process can briefly retain a directory handle; that
    # must not prevent publishing an otherwise valid conversion.
    directory = Path(tempfile.mkdtemp(prefix="pdf2office-"))
    try:
        yield directory
    finally:
        for delay in (0, 0.05, 0.1, 0.2, 0.4):
            if delay:
                time.sleep(delay)
            try:
                shutil.rmtree(directory)
                break
            except FileNotFoundError:
                break
            except OSError:
                # Best-effort cleanup applies only to our generated scratch
                # directory, never to source files or the user's output.
                continue


def _publish_dwg(source: Path, output: Path):
    # TEMP and destination may be on different Windows drives. Copy into an
    # exclusive sibling file, then use an atomic same-filesystem replacement.
    sibling = None
    try:
        with tempfile.NamedTemporaryFile(prefix=".pdf2office-", suffix=".tmp", dir=output.parent, delete=False) as target:
            sibling = Path(target.name)
            with source.open("rb") as original:
                shutil.copyfileobj(original, target)
        os.replace(sibling, output)
    finally:
        if sibling is not None:
            sibling.unlink(missing_ok=True)


def export_dwg(pages: list[PdfPage], output: Path, converter_path: Path | None = None, scale: float = 1.0, font_overrides: dict[str, str] | None = None, font_catalog: FontCatalog | None = None, *, check_cancel=None, progress=None) -> list[str]:
    """Write a real AC1015 DWG, atomically replacing output only on success."""
    converter = find_dwg_converter(converter_path)
    output = Path(output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    with _conversion_directory() as temporary:
        dxf = temporary / "drawing.dxf"
        dwg = temporary / "drawing.dwg"
        warnings = _write_dxf(pages, dxf, scale, font_overrides, output.with_name(image_directory_name(output.stem)), font_catalog, for_libredwg=True)
        def heartbeat(elapsed):
            if progress:
                progress(f"Запись DWG продолжается: {int(elapsed)} с; ограничение времени отключено.")

        try:
            result = run_process(
                [str(converter), "--as", "r2000", "-y", "-o", dwg.name, dxf.name],
                cwd=temporary,
                check_cancel=check_cancel,
                heartbeat=heartbeat,
            )
        except OSError as exc:
            raise CadConversionError(f"Не удалось запустить LibreDWG: {exc}") from exc
        message = ((result.stdout or b"") + (result.stderr or b"")).decode("utf-8", errors="replace")
        if result.returncode != 0:
            raise CadConversionError(f"LibreDWG завершился с ошибкой {result.returncode}: {message[-2000:]}")
        if not dwg.is_file() or dwg.stat().st_size < 128:
            raise CadConversionError("LibreDWG не создал полноценный DWG-файл.")
        with dwg.open("rb") as handle:
            if handle.read(6) != DWG_SIGNATURE:
                raise CadConversionError("Полученный файл не является DWG AutoCAD 2000 (AC1015).")
        if re.search(r"\b(?:warning|error)\b", message, flags=re.IGNORECASE):
            warnings.append("LibreDWG сообщил диагностические замечания: " + message[-1500:])
        _publish_dwg(dwg, output)
    return warnings
