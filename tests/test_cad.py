from __future__ import annotations

import io
import json
from pathlib import Path
import shutil
import subprocess

import ezdxf
from ezdxf.lldxf.encoding import decode_dxf_unicode
from PIL import Image
import pytest
from fontTools.pens.boundsPen import BoundsPen
from fontTools.ttLib import TTFont

from pdf2office.cad import (
    CadConversionError,
    POINT_TO_MM,
    export_dwg,
    export_dxf,
    find_dwg_converter,
    image_directory_name,
    _conversion_directory,
)
from pdf2office.models import Drawing, PdfPage, RasterImage, Segment, TextSpan
from pdf2office.fonts import FontCatalog


def page(number=1):
    return PdfPage(
        number, 200, 300,
        [TextSpan("Размер {№1}: 25 ± 0,2 мм", (10, 10, 190, 25), "Liberation Sans", 12, (0, 0, 0))],
        [
            Drawing([Segment("line", ((0, 0), (72, 72)))], stroke=(0, 0, 0)),
            Drawing([
                Segment("line", ((10, 40), (40, 40))),
                Segment("line", ((40, 40), (40, 60))),
                Segment("line", ((40, 60), (10, 60))),
            ], stroke=(0, 0, 0), fill=(255, 0, 0), closed=True),
            Drawing([Segment("cubic", ((0, 100), (20, 80), (40, 80), (60, 100)))], stroke=(0, 0, 0)),
        ], [],
    )


def test_dxf_has_editable_entities_mm_and_cyrillic(tmp_path):
    output = tmp_path / "чертёж.dxf"
    export_dxf([page()], output, scale=2)
    doc = ezdxf.readfile(output)
    msp = doc.modelspace()
    assert doc.units == ezdxf.units.MM
    assert len(msp.query("LINE")) == 1
    line = msp.query("LINE")[0]
    assert line.dxf.end.x - line.dxf.start.x == pytest.approx(50.8)
    assert line.dxf.start.y - line.dxf.end.y == pytest.approx(50.8)
    assert msp.query("LWPOLYLINE")[0].closed
    assert len(msp.query("HATCH")) == 1
    curve = msp.query("SPLINE")[0]
    assert curve.dxf.degree == 3
    assert len(curve.control_points) == 4
    assert tuple(curve.control_points[1]) == pytest.approx((20 * POINT_TO_MM * 2, 220 * POINT_TO_MM * 2, 0))
    text = msp.query("TEXT")[0]
    assert decode_dxf_unicode(text.plain_text()) == page().texts[0].text
    assert text.dxf.layer == "PAGE_1_TEXT"
    assert not doc.audit().errors


def test_pages_are_separated_and_rotated_text_stays_editable(tmp_path):
    p = page(2)
    p.texts[0].angle = 90
    p.texts[0].origin = (50, 50)
    output = tmp_path / "multi.dxf"
    export_dxf([page(), p], output)
    doc = ezdxf.readfile(output)
    texts = list(doc.modelspace().query("TEXT"))
    assert texts[1].dxf.layer == "PAGE_2_TEXT"
    assert texts[1].dxf.rotation == pytest.approx(-90)
    assert texts[1].dxf.insert.x > 200 * POINT_TO_MM


def test_images_are_portable_references_and_ocr_reference_can_be_shown(tmp_path):
    png = io.BytesIO()
    Image.new("RGB", (20, 10), "white").save(png, "PNG")
    p = page()
    p.ocr_used = True
    p.images.append(RasterImage((10, 50, 110, 100), png.getvalue()))
    output = tmp_path / "Чертёж с пробелами.dxf"
    warnings = export_dxf([p], output)
    doc = ezdxf.readfile(output)
    image = doc.modelspace().query("IMAGE")[0]
    image_def = doc.entitydb[image.dxf.image_def_handle]
    assert image_def.dxf.filename.startswith(image_directory_name(output.stem) + "/")
    assert image_def.dxf.filename.isascii()
    assert (tmp_path / image_def.dxf.filename).is_file()
    assert doc.layers.get(image.dxf.layer).is_off()
    assert any("выключен" in warning for warning in warnings)
    assert any("не являются редактируемыми" in warning for warning in warnings)


def test_wildcard_font_override_is_honored(tmp_path):
    output = tmp_path / "fonts.dxf"
    export_dxf([page()], output, font_overrides={"*": "Liberation Serif"})
    doc = ezdxf.readfile(output)
    text = doc.modelspace().query("TEXT")[0]
    assert "Serif" in doc.styles.get(text.dxf.style).dxf.font


def test_unsuccessful_ocr_keeps_only_raster_visible(tmp_path):
    png = io.BytesIO()
    Image.new("L", (20, 10), 128).save(png, "PNG")
    scanned = PdfPage(1, 100, 50, [], [], [RasterImage((0, 0, 100, 50), png.getvalue())],
                      ocr_used=True, raster_source=True)
    output = tmp_path / "unrecognized.dxf"
    export_dxf([scanned], output)
    doc = ezdxf.readfile(output)
    raster, = doc.modelspace().query("IMAGE")
    assert not doc.layers.get(raster.dxf.layer).is_off()


@pytest.mark.parametrize("raster_source,hidden", [(True, True), (False, False)])
def test_vectorized_image_reference_is_hidden_but_pdf_photos_stay_visible(tmp_path, raster_source, hidden):
    png = io.BytesIO()
    Image.new("RGB", (20, 10), "white").save(png, "PNG")
    p = page()
    p.texts = []
    p.raster_source = raster_source
    p.images = [RasterImage((0, 0, 200, 300), png.getvalue())]
    output = tmp_path / "facade.dxf"
    export_dxf([p], output)
    doc = ezdxf.readfile(output)
    assert doc.modelspace().query("LINE LWPOLYLINE")
    raster, = doc.modelspace().query("IMAGE")
    assert doc.layers.get(raster.dxf.layer).is_off() is hidden
    assert (output.parent / doc.entitydb[raster.dxf.image_def_handle].dxf.filename).is_file()


@pytest.mark.parametrize("angle", [0, 37, 90, -90])
def test_positioned_text_keeps_baseline_cap_height_and_pdf_advance(tmp_path, angle):
    p = page()
    span = TextSpan("Текст в таблице", (10, 5, 180, 90), "Liberation Serif", 9.96,
                    (0, 0, 0), origin=(20, 75), angle=angle, advance=95.0)
    p.texts = [span]
    output = tmp_path / "baseline.dxf"
    catalog = FontCatalog()
    export_dxf([p], output, scale=2, font_catalog=catalog)
    document = ezdxf.readfile(output)
    entity = document.modelspace().query("TEXT")[0]
    assert not document.modelspace().query("MTEXT")
    assert entity.dxf.insert.isclose((20 * POINT_TO_MM * 2, 225 * POINT_TO_MM * 2, 0))
    assert entity.dxf.rotation == pytest.approx(-angle)
    assert entity.dxf.halign == entity.dxf.valign == 0  # left, baseline
    with TTFont(catalog.resolve(span.font).path) as font:
        units = font["head"].unitsPerEm
        cap = font["OS/2"].sCapHeight / units
        cmap = font.getBestCmap()
        advance = sum(font["hmtx"][cmap[ord(char)]][0] for char in span.text) / units
    assert entity.dxf.height == pytest.approx(9.96 * cap * POINT_TO_MM * 2)
    assert advance * 9.96 * entity.dxf.width == pytest.approx(95)


def test_ocr_text_fits_ink_box_including_descenders(tmp_path):
    p = page()
    p.texts = [TextSpan("Размер", (10, 20, 70, 30), "Liberation Sans", 10,
                        (0, 0, 0), origin=(10, 30), from_ocr=True, advance=60)]
    output = tmp_path / "ocr-box.dxf"
    catalog = FontCatalog()
    export_dxf([p], output, font_catalog=catalog)
    entity = ezdxf.readfile(output).modelspace().query("TEXT")[0]
    with TTFont(catalog.resolve("Liberation Sans").path) as font:
        glyphs = font.getGlyphSet()
        cmap = font.getBestCmap()
        offset = 0
        boxes = []
        for char in p.texts[0].text:
            name = cmap[ord(char)]
            pen = BoundsPen(glyphs)
            glyphs[name].draw(pen)
            x0, y0, x1, y1 = pen.bounds
            boxes.append((offset + x0, y0, offset + x1, y1))
            offset += font["hmtx"][name][0]
        cap = font["OS/2"].sCapHeight
    glyph_scale = entity.dxf.height / cap
    assert entity.dxf.insert.x + min(b[0] for b in boxes) * glyph_scale * entity.dxf.width == pytest.approx(10 * POINT_TO_MM)
    assert entity.dxf.insert.x + max(b[2] for b in boxes) * glyph_scale * entity.dxf.width == pytest.approx(70 * POINT_TO_MM)
    assert entity.dxf.insert.y + min(b[1] for b in boxes) * glyph_scale == pytest.approx(270 * POINT_TO_MM)
    assert entity.dxf.insert.y + max(b[3] for b in boxes) * glyph_scale == pytest.approx(280 * POINT_TO_MM)


def test_literal_cad_control_syntax_stays_literal_and_adjacent(tmp_path):
    p = page()
    literal = r"Параметр %%d, %%p, ^C и \U+0041 {текст} C:\Temp"
    p.texts = [TextSpan(literal, (10, 20, 190, 40), "Liberation Sans", 12,
                        (0, 0, 0), origin=(10, 35), advance=180)]
    output = tmp_path / "literal.dxf"
    export_dxf([p], output)
    entities = list(ezdxf.readfile(output).modelspace().query("TEXT"))
    assert "".join(decode_dxf_unicode(e.plain_text()) for e in entities) == literal
    assert all(e.dxf.insert.y == pytest.approx(265 * POINT_TO_MM) for e in entities)
    assert all(a.dxf.insert.x < b.dxf.insert.x for a, b in zip(entities, entities[1:]))


def test_dashes_and_dots_keep_lengths_and_odd_pattern_repetition(tmp_path):
    p = page()
    p.drawings[0].dashes = (4, 2, 1)
    p.drawings[0].dash_offset = 3
    p.drawings[1].dashes = (0, 2)
    output = tmp_path / "dashes.dxf"
    warnings = export_dxf([p], output, scale=2)
    doc = ezdxf.readfile(output)
    line = doc.modelspace().query("LINE")[0]
    pattern = doc.linetypes.get(line.dxf.linetype).pattern_tags.tags
    assert [tag.value for tag in pattern if tag.code == 49] == pytest.approx([value * POINT_TO_MM * 2 for value in (4, -2, 1, -4, 2, -1)])
    assert next(tag.value for tag in pattern if tag.code == 40) == pytest.approx(14 * POINT_TO_MM * 2)
    polyline = doc.modelspace().query("LWPOLYLINE")[0]
    assert polyline.dxf.flags & 128
    dotted = doc.linetypes.get(polyline.dxf.linetype).pattern_tags.tags
    assert [tag.value for tag in dotted if tag.code == 49] == pytest.approx([0, -2 * POINT_TO_MM * 2])
    assert any("начальное смещение" in warning for warning in warnings)


def test_transient_windows_scratch_lock_does_not_mask_result(monkeypatch):
    attempts = []
    original_rmtree = shutil.rmtree

    def locked(path):
        attempts.append(path)
        raise PermissionError("Windows child process temporarily retains its cwd")

    with monkeypatch.context() as patch:
        patch.setattr("pdf2office.cad.shutil.rmtree", locked)
        patch.setattr("pdf2office.cad.time.sleep", lambda _: None)
        with _conversion_directory() as scratch:
            (scratch / "verified.dwg").write_bytes(b"finished")
    assert len(attempts) == 5
    assert scratch.is_dir()
    original_rmtree(scratch)


@pytest.mark.parametrize("scale", [0, -1, float("inf"), float("nan")])
def test_invalid_scale_rejected(tmp_path, scale):
    with pytest.raises(ValueError, match="Масштаб"):
        export_dxf([page()], tmp_path / "bad.dxf", scale=scale)


def test_empty_selection_is_rejected(tmp_path):
    with pytest.raises(CadConversionError, match="Нет страниц"):
        export_dxf([], tmp_path / "empty.dxf")


def test_failed_converter_does_not_replace_existing_output(tmp_path, monkeypatch):
    output = tmp_path / "existing.dwg"
    output.write_bytes(b"original drawing")
    converter = tmp_path / "dxf2dwg"
    converter.touch()

    def fail(args, **kwargs):
        assert args[-3:] == ["-o", "drawing.dwg", "drawing.dxf"]
        assert "shell" not in kwargs
        assert kwargs.get("timeout") is None
        return subprocess.CompletedProcess(args, 7, b"Conversion failed")

    monkeypatch.setattr("pdf2office.cad.run_process", fail)
    with pytest.raises(CadConversionError, match="ошибкой 7"):
        export_dwg([page()], output, converter_path=converter)
    assert output.read_bytes() == b"original drawing"
    assert not list(tmp_path.glob("pdf2office-*"))


def test_dxf_disguised_as_dwg_is_rejected(tmp_path, monkeypatch):
    converter = tmp_path / "dxf2dwg"
    converter.touch()

    def fake(args, **kwargs):
        (Path(kwargs["cwd"]) / "drawing.dwg").write_bytes(b"  0\nSECTION\n" + b" " * 1024)
        return subprocess.CompletedProcess(args, 0, b"")

    monkeypatch.setattr("pdf2office.cad.run_process", fake)
    with pytest.raises(CadConversionError, match="не является DWG"):
        export_dwg([page()], tmp_path / "invalid.dwg", converter_path=converter)
    assert not (tmp_path / "invalid.dwg").exists()


def test_actual_libredwg_roundtrip_preserves_geometry_and_cyrillic(tmp_path):
    try:
        converter = find_dwg_converter()
    except CadConversionError:
        pytest.skip("Set PDF2OFFICE_DXF2DWG or install bundled native tools for DWG integration")
    reader = converter.with_name("dwg2dxf" + converter.suffix)
    if not reader.is_file():
        pytest.skip("LibreDWG dwg2dxf required for independent readback")
    output = tmp_path / "Чертёж с пробелами.dwg"
    first = page()
    first.drawings[0].dashes = (4, 2)
    first.drawings[1].dashes = (0, 2)
    first.texts[0].origin = (12, 25)
    first.texts[0].advance = 90
    second = page(2)
    second.texts[0].origin = (50, 60)
    second.texts[0].angle = 37
    second.texts[0].advance = 100
    export_dxf([first, second], tmp_path / "expected.dxf")
    export_dwg([first, second], output, converter_path=converter)
    assert output.read_bytes().startswith(b"AC1015")
    result = subprocess.run(
        [str(reader), "-y", "-o", "readback.dxf", output.name],
        cwd=tmp_path, capture_output=True, timeout=30,
    )
    assert result.returncode == 0, result.stderr.decode(errors="replace")
    doc = ezdxf.readfile(tmp_path / "readback.dxf")
    msp = doc.modelspace()
    assert len(msp.query("LINE")) == 2
    assert len(msp.query("LWPOLYLINE")) == 2
    assert len(msp.query("HATCH")) == 2
    assert len(msp.query("SPLINE")) == 2
    assert len(msp.query("TEXT")) == 2
    assert decode_dxf_unicode(msp.query("TEXT")[0].plain_text()) == page().texts[0].text
    expected = ezdxf.readfile(tmp_path / "expected.dxf")
    for actual, original in zip(msp.query("TEXT"), expected.modelspace().query("TEXT")):
        # A structurally valid DWG can still have broken text dimensions:
        # LibreDWG's MTEXT import used to set height=0 and width=height.
        assert actual.dxf.height > 0
        assert actual.dxf.height == pytest.approx(original.dxf.height)
        assert actual.dxf.width == pytest.approx(original.dxf.width)
        assert actual.dxf.insert.isclose(original.dxf.insert)
        assert actual.dxf.rotation == pytest.approx(original.dxf.rotation)
        assert actual.dxf.halign == original.dxf.halign == 0
        assert actual.dxf.valign == original.dxf.valign == 0
        assert actual.dxf.style == original.dxf.style
        assert doc.styles.get(actual.dxf.style).get_extended_font_data() == expected.styles.get(original.dxf.style).get_extended_font_data()
    assert len(msp.query("SPLINE")[0].control_points) == 4
    assert msp.query("HATCH")[0].dxf.layer == "PAGE_1_GEOMETRY"
    dashed = doc.linetypes.get(msp.query("LINE")[0].dxf.linetype)
    assert [tag.value for tag in dashed.pattern_tags.tags if tag.code == 49] == pytest.approx([4 * POINT_TO_MM, -2 * POINT_TO_MM])
    polyline = msp.query("LWPOLYLINE")[0]
    assert polyline.dxf.flags & 128
    dotted = doc.linetypes.get(polyline.dxf.linetype)
    assert [tag.value for tag in dotted.pattern_tags.tags if tag.code == 49] == pytest.approx([0, -2 * POINT_TO_MM])
    audit = doc.audit()
    assert not audit.errors
    assert not audit.fixes


def test_actual_dwg_keeps_complete_long_unicode_text(tmp_path):
    try:
        converter = find_dwg_converter()
    except CadConversionError:
        pytest.skip("Set PDF2OFFICE_DXF2DWG or install bundled native tools for DWG integration")
    reader = converter.with_name("dwgread" + converter.suffix)
    if not reader.is_file():
        pytest.skip("LibreDWG dwgread required for direct DWG text validation")
    p = page()
    text = "Длинная редактируемая строка таблицы без потери начала и окончания. " * 3
    p.texts = [TextSpan(text, (10, 20, 190, 34), "Liberation Serif", 9.96,
                        (0, 0, 0), origin=(10, 30), advance=180)]
    export_dxf([p], tmp_path / "expected.dxf")
    export_dwg([p], tmp_path / "long-text.dwg", converter_path=converter)
    result = subprocess.run([str(reader), "-O", "JSON", "-o", "readback.json", "long-text.dwg"],
                            cwd=tmp_path, capture_output=True, timeout=30)
    assert result.returncode == 0, result.stderr.decode(errors="replace")
    data = json.loads((tmp_path / "readback.json").read_text(encoding="utf-8"))
    entities = [entity for entity in data["OBJECTS"] if entity.get("entity") == "TEXT"]
    assert len(entities) == 1
    assert entities[0]["text_value"] == text
    expected = ezdxf.readfile(tmp_path / "expected.dxf").modelspace().query("TEXT")[0]
    # dwg2dxf 0.14 wrongly splits long TEXT into MTEXT-style DXF groups 3+1.
    # Read the stored DWG data directly so that reader bug cannot hide lost
    # text or cause a correct DWG to be mistaken for a truncated conversion.
    assert len(expected.dxf.text.encode("cp1252")) > 250
    assert entities[0]["height"] == pytest.approx(expected.dxf.height)
    assert entities[0]["width_factor"] == pytest.approx(expected.dxf.width)
    assert entities[0]["ins_pt"] == pytest.approx(tuple(expected.dxf.insert)[:2])


def test_actual_libredwg_image_and_curved_hatch_roundtrip(tmp_path):
    try:
        converter = find_dwg_converter()
    except CadConversionError:
        pytest.skip("LibreDWG is not installed")
    reader = converter.with_name("dwg2dxf" + converter.suffix)
    if not reader.is_file():
        pytest.skip("LibreDWG dwg2dxf is not installed")
    p = page()
    p.drawings[-1].fill = (255, 0, 0)
    p.drawings[-1].closed = True
    image = io.BytesIO()
    Image.new("RGB", (20, 10), "white").save(image, "PNG")
    p.images.append(RasterImage((10, 50, 110, 100), image.getvalue()))
    p.ocr_used = True
    output = tmp_path / "Растровая подложка.dwg"
    export_dwg([p], output, converter_path=converter)
    result = subprocess.run([str(reader), "-y", "-o", "image.dxf", output.name], cwd=tmp_path, capture_output=True, timeout=30)
    assert result.returncode == 0
    doc = ezdxf.readfile(tmp_path / "image.dxf")
    raster = doc.modelspace().query("IMAGE")[0]
    path = doc.entitydb[raster.dxf.image_def_handle].dxf.filename
    assert path.isascii()
    assert (tmp_path / path).is_file()
    assert doc.layers.get(raster.dxf.layer).is_off()
    hatch = doc.modelspace().query("HATCH")[1]
    assert len(hatch.paths[0].edges[0].control_points) == 4
    assert not doc.audit().errors
