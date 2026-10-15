from __future__ import annotations

import io
from pathlib import Path
import shutil
import subprocess

import ezdxf
from ezdxf.lldxf.encoding import decode_dxf_unicode
from PIL import Image
import pytest

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
    text = msp.query("MTEXT")[0]
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
    texts = list(doc.modelspace().query("MTEXT"))
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
    text = doc.modelspace().query("MTEXT")[0]
    assert "Serif" in doc.styles.get(text.dxf.style).dxf.font


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
        return subprocess.CompletedProcess(args, 7, b"Conversion failed")

    monkeypatch.setattr(subprocess, "run", fail)
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

    monkeypatch.setattr(subprocess, "run", fake)
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
    export_dwg([first, page(2)], output, converter_path=converter)
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
    assert len(msp.query("MTEXT")) == 2
    assert decode_dxf_unicode(msp.query("MTEXT")[0].plain_text()) == page().texts[0].text
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
