from pathlib import Path
import io
import json
import zipfile

import ezdxf
from ezdxf.lldxf.encoding import decode_dxf_unicode
import pymupdf as fitz
import pytest
from PIL import Image, ImageDraw

from pdf2office.service import ConversionRequest, convert


@pytest.fixture
def source(tmp_path):
    path = tmp_path / "Чертёж.pdf"
    font = Path(__file__).parents[1] / "assets/fonts/LiberationSans-Regular.ttf"
    with fitz.open() as document:
        page = document.new_page(width=595, height=842)
        page.insert_font(fontname="Custom", fontfile=str(font))
        page.insert_text((35, 45), "План здания 125 мм", fontname="Custom", fontsize=14)
        page.draw_rect(fitz.Rect(35, 100, 220, 210), width=1)
        page.draw_bezier((35, 240), (80, 200), (140, 280), (220, 240))
        document.save(path)
    return path


def test_real_dxf_docx_batch_is_editable_and_non_destructive(source, tmp_path):
    original = source.read_bytes()
    request = ConversionRequest([source], tmp_path / "Выход", ("dxf", "docx"), ocr_mode="off")
    result, = convert(request)
    assert result.error is None
    assert {p.suffix for p in result.outputs} == {".dxf", ".docx"}
    drawing = ezdxf.readfile(next(p for p in result.outputs if p.suffix == ".dxf"))
    assert not drawing.audit().has_errors
    assert "План здания" in " ".join(decode_dxf_unicode(e.plain_text()) for e in drawing.modelspace().query("TEXT"))
    assert len(drawing.modelspace().query("SPLINE")) == 1
    assert len(drawing.modelspace().query("LWPOLYLINE")) >= 1
    docx = next(p for p in result.outputs if p.suffix == ".docx")
    with zipfile.ZipFile(docx) as archive:
        xml = archive.read("word/document.xml").decode()
        assert "План здания" in xml and "txbxContent" in xml and "v:shape" in xml
    repeated, = convert(request)
    assert repeated.error is None
    assert {p.name for p in repeated.outputs} == {"Чертёж (2).dxf", "Чертёж (2).docx"}
    assert all(p.is_file() for p in result.outputs)
    assert source.read_bytes() == original
    assert not list(request.output_dir.glob(".pdf-master-*"))


def test_one_format_failure_does_not_hide_success(source, tmp_path, monkeypatch):
    from pdf2office import cad

    def failed(*args, **kwargs):
        raise RuntimeError("DWG backend unavailable for this test")

    monkeypatch.setattr(cad, "export_dwg", failed)
    result, = convert(ConversionRequest([source], tmp_path / "out", ("dwg", "docx"), ocr_mode="off"))
    assert len(result.outputs) == 1 and result.outputs[0].suffix == ".docx"
    assert "DWG" in result.error
    report = json.loads((tmp_path / "out/Чертёж.conversion.json").read_text())
    assert report["error"] == result.error
    assert not (tmp_path / "out/Чертёж.dwg").exists()


def test_raster_sidecars_survive_staging_with_unicode_filename(tmp_path):
    stream = io.BytesIO()
    Image.new("RGB", (100, 100), "red").save(stream, "PNG")
    source = tmp_path / "Растровая схема.pdf"
    with fitz.open() as document:
        page = document.new_page(width=200, height=200)
        page.insert_image(page.rect, stream=stream.getvalue())
        document.save(source)
    result, = convert(ConversionRequest([source], tmp_path / "out", ("dxf",), ocr_mode="off"))
    assert not result.error
    doc = ezdxf.readfile(result.outputs[0])
    raster, = doc.modelspace().query("IMAGE")
    reference = doc.entitydb[raster.dxf.image_def_handle].dxf.filename
    assert reference.isascii()
    assert (result.outputs[0].parent / reference).is_file()
    assert not list(result.outputs[0].parent.glob(".pdf-master-*"))


def test_invalid_input_does_not_stop_other_files(source, tmp_path):
    broken = tmp_path / "broken.pdf"
    broken.write_bytes(b"broken document")
    results = convert(ConversionRequest([broken, source], tmp_path / "out", ("docx",), ocr_mode="off"))
    assert len(results) == 2
    assert results[0].error and not results[0].outputs
    assert results[1].error is None and results[1].outputs[0].exists()


def test_cancel_during_page_progress_leaves_no_partial_document(source, tmp_path):
    stop = False

    def update(message):
        nonlocal stop
        if message.startswith("Чтение страницы"):
            stop = True

    request = ConversionRequest([source, source], tmp_path / "out", ("docx",), ocr_mode="off")
    results = convert(request, progress=update, cancelled=lambda: stop)
    assert len(results) == 1
    assert "остановлена" in results[0].error
    assert not list(request.output_dir.glob("*.docx"))


def test_cancellation_reaches_running_dwg_and_skips_later_outputs(source, tmp_path, monkeypatch):
    stop = False
    messages = []

    def update(message):
        messages.append(message)

    def native_run(command, **kwargs):
        nonlocal stop
        assert kwargs.get("timeout") is None
        kwargs["heartbeat"](181.0)
        stop = True
        kwargs["check_cancel"]()
        raise AssertionError("Cancellation did not reach the DWG child")

    monkeypatch.setattr("pdf2office.cad.find_dwg_converter", lambda _: Path("native-converter"))
    monkeypatch.setattr("pdf2office.cad.run_process", native_run)
    request = ConversionRequest([source, source], tmp_path / "out", ("dwg", "docx"), ocr_mode="off")
    result, = convert(request, progress=update, cancelled=lambda: stop)
    assert "остановлена" in result.error
    assert not result.outputs
    assert any("181 с" in line for line in messages)
    assert not any("Ошибка DWG" in line for line in messages)
    assert not list(request.output_dir.glob("*.dwg"))
    assert not list(request.output_dir.glob("*.docx"))
    assert not list(request.output_dir.glob(".pdf-master-*"))


def test_report_never_contains_password(tmp_path):
    path = tmp_path / "protected.pdf"
    with fitz.open() as document:
        document.new_page().insert_text((20, 20), "Secret drawing")
        document.save(path, encryption=fitz.PDF_ENCRYPT_AES_256, owner_pw="owner", user_pw="test-password-123")
    result, = convert(ConversionRequest([path], tmp_path / "out", ("docx",), password="test-password-123", ocr_mode="off"))
    assert not result.error
    assert "test-password-123" not in (tmp_path / "out/protected.conversion.json").read_text()


def test_wildcard_font_override(source, tmp_path):
    result, = convert(ConversionRequest([source], tmp_path / "out", ("docx",),
                                      ocr_mode="off", font_overrides={"*": "osifont"}))
    assert result.error is None
    with zipfile.ZipFile(result.outputs[0]) as archive:
        assert 'w:ascii="osifont"' in archive.read("word/document.xml").decode()


def test_overwrite_never_replaces_another_input_in_the_same_batch(tmp_path):
    sources = []
    for folder, name, label in [("first", "Plan.pdf", "FIRST DOCUMENT"),
                                ("second", "plan.pdf", "SECOND DOCUMENT")]:
        source = tmp_path / folder / name
        source.parent.mkdir()
        with fitz.open() as document:
            document.new_page().insert_text((30, 30), label)
            document.save(source)
        sources.append(source)
    results = convert(ConversionRequest(sources, tmp_path / "out", ("docx",),
                                        ocr_mode="off", overwrite=True))
    assert all(not result.error for result in results)
    assert results[0].outputs[0].name == "Plan.docx"
    assert results[1].outputs[0].name == "plan (2).docx"
    for result, label in zip(results, ["FIRST DOCUMENT", "SECOND DOCUMENT"]):
        with zipfile.ZipFile(result.outputs[0]) as archive:
            assert label in archive.read("word/document.xml").decode()


@pytest.mark.parametrize("scale", [0, -1, float("nan"), float("inf")])
def test_invalid_scale_rejected_before_output(source, tmp_path, scale):
    with pytest.raises(ValueError, match="Масштаб"):
        convert(ConversionRequest([source], tmp_path / "out", scale=scale))
    assert not (tmp_path / "out").exists()


def test_mixed_pdf_tiff_batch_preserves_resolution_geometry_and_source(source, tmp_path):
    scan = tmp_path / "Скан.TIF"
    image = Image.new("L", (800, 600), 255)
    ImageDraw.Draw(image).rectangle((100, 100, 700, 500), outline=0, width=5)
    image.save(scan, dpi=(400, 400))
    original = scan.read_bytes()
    results = convert(ConversionRequest([source, scan], tmp_path / "out", ("dxf", "docx"),
                                        ocr_mode="off", scan_deskew=False))
    assert all(not item.error and len(item.outputs) == 2 for item in results)
    drawing = ezdxf.readfile(results[1].outputs[0])
    assert not drawing.audit().has_errors
    assert len(drawing.modelspace().query("LINE LWPOLYLINE")) >= 1
    raster, = drawing.modelspace().query("IMAGE")
    assert raster.dxf.image_size.x == 800
    assert raster.dxf.u_pixel.x == pytest.approx(25.4 / 400)
    report = json.loads((tmp_path / "out/Скан.conversion.json").read_text())
    assert report["input_format"] == "tif"
    assert report["tiff_settings"] == {"cleanup": True, "deskew": False, "dpi": 0, "vectorize": True}
    assert scan.read_bytes() == original


def test_tiff_controls_reach_extractor(tmp_path, monkeypatch):
    from pdf2office.models import PdfPage
    captured = {}

    def extract(path, **kwargs):
        captured.update(kwargs)
        return [PdfPage(1, 72, 72, [], [], [])]

    monkeypatch.setattr("pdf2office.scans.extract_image", extract)
    result, = convert(ConversionRequest([tmp_path / "scan.tIfF"], tmp_path / "out", ("docx",),
                                        scan_cleanup=False, scan_deskew=False,
                                        scan_dpi=600, vectorize_tiff=False))
    assert not result.error
    assert captured["preprocess"] is False and captured["deskew"] is False
    assert captured["dpi"] == 600 and captured["vectorize_scans"] is False
    assert callable(captured["check_cancel"]) and callable(captured["progress"])


@pytest.mark.parametrize("dpi", [-1, 1, 71, 2401, float("nan"), 300.5, True])
def test_invalid_scan_dpi_rejected_before_output(source, tmp_path, dpi):
    with pytest.raises(ValueError, match="DPI"):
        convert(ConversionRequest([source], tmp_path / "out", scan_dpi=dpi))
    assert not (tmp_path / "out").exists()


def test_unsupported_extension_does_not_stop_batch(source, tmp_path):
    results = convert(ConversionRequest([tmp_path / "scan.heic", source], tmp_path / "out",
                                        ("docx",), ocr_mode="off"))
    assert ".png" in results[0].error
    assert results[1].error is None


def test_cancel_during_tiff_cleanup_leaves_no_partial_document(tmp_path):
    scan = tmp_path / "scan.tif"
    Image.new("1", (1600, 1600), 1).save(scan, dpi=(300, 300))
    stop = False

    def update(message):
        nonlocal stop
        if message.startswith("Очистка фона"):
            stop = True

    request = ConversionRequest([scan], tmp_path / "out", ("dxf",), ocr_mode="off")
    result, = convert(request, progress=update, cancelled=lambda: stop)
    assert "остановлена" in result.error
    assert not result.outputs and not list(request.output_dir.glob("*.dxf"))


def test_mixed_pdf_png_jpeg_batch_preserves_sources_and_image_scale(source, tmp_path):
    image = Image.new("RGB", (500, 300), "white")
    draw = ImageDraw.Draw(image)
    draw.rectangle((50, 50, 450, 250), outline="black", width=2)
    draw.line((50, 50, 450, 250), fill="black", width=1)
    images = [tmp_path / "Фасад.PNG", tmp_path / "Фасад.JpG"]
    for path in images:
        image.save(path, dpi=(200, 200))
    originals = {path: path.read_bytes() for path in images}
    results = convert(ConversionRequest([source, *images], tmp_path / "out", ("dxf", "docx"),
                                        ocr_mode="off", scan_deskew=False))
    assert len(results) == 3 and all(item.error is None for item in results)
    assert results[1].outputs[0].stem == "Фасад"
    assert results[2].outputs[0].stem == "Фасад (2)"
    for item in results[1:]:
        cad_path = next(path for path in item.outputs if path.suffix == ".dxf")
        doc = ezdxf.readfile(cad_path)
        assert not doc.audit().has_errors
        assert len(doc.modelspace().query("LINE LWPOLYLINE")) >= 1
        raster, = doc.modelspace().query("IMAGE")
        assert tuple(raster.dxf.image_size) == (500, 300, 0)
        assert raster.dxf.u_pixel.x == pytest.approx(25.4 / 200, rel=0.001)
        assert doc.layers.get(raster.dxf.layer).is_off()
        assert (cad_path.parent / doc.entitydb[raster.dxf.image_def_handle].dxf.filename).is_file()
        report = json.loads(cad_path.with_suffix(".conversion.json").read_text())
        assert report["image_settings"] == {"cleanup": True, "deskew": False, "dpi": 0, "vectorize": True}
        assert originals[item.input] == item.input.read_bytes()
        with zipfile.ZipFile(next(path for path in item.outputs if path.suffix == ".docx")) as archive:
            assert b"v:shape" in archive.read("word/document.xml")
    assert not list((tmp_path / "out").glob(".pdf-master-*"))


@pytest.mark.parametrize("new,legacy,expected", [(None, False, False), (None, True, True),
                                                (False, True, False), (True, False, True)])
def test_image_vectorization_control_and_legacy_fallback(tmp_path, monkeypatch, new, legacy, expected):
    from pdf2office.models import PdfPage
    captured = {}

    def extract(path, **kwargs):
        captured.update(kwargs)
        return [PdfPage(1, 72, 72, [], [], [])]

    monkeypatch.setattr("pdf2office.scans.extract_image", extract)
    result, = convert(ConversionRequest([tmp_path / "drawing.PNG"], tmp_path / "out", ("docx",),
                                        vectorize_tiff=legacy, vectorize_images=new,
                                        scan_cleanup=False, scan_deskew=False, scan_dpi=600))
    assert result.error is None
    assert captured["vectorize_scans"] is expected
    assert captured["preprocess"] is False and captured["deskew"] is False and captured["dpi"] == 600
    assert callable(captured["check_cancel"]) and callable(captured["progress"])
    report = json.loads((tmp_path / "out/drawing.conversion.json").read_text())
    assert report["image_settings"]["vectorize"] is expected
    assert report["image_settings"] == report["tiff_settings"]


def test_corrupt_image_does_not_stop_valid_image_and_has_no_partial_outputs(tmp_path):
    bad = tmp_path / "bad.jpg"
    bad.write_bytes(b"not a JPEG")
    valid = tmp_path / "valid.png"
    Image.new("RGB", (50, 30), "white").save(valid)
    results = convert(ConversionRequest([bad, valid], tmp_path / "out", ("dxf",), ocr_mode="off"))
    assert results[0].error and not results[0].outputs
    assert not results[1].error and len(results[1].outputs) == 1
    assert not (tmp_path / "out/bad.dxf").exists()
    assert not list((tmp_path / "out").glob(".pdf-master-*"))


def test_cancel_image_cleanup_leaves_input_and_no_result(tmp_path):
    source = tmp_path / "large.png"
    Image.new("RGB", (1700, 1700), "white").save(source)
    original = source.read_bytes()
    stop = False

    def progress(message):
        nonlocal stop
        if message.startswith("Очистка фона"):
            stop = True

    result, = convert(ConversionRequest([source], tmp_path / "out", ("dxf",), ocr_mode="off"),
                      progress=progress, cancelled=lambda: stop)
    assert "остановлена" in result.error and not result.outputs
    assert source.read_bytes() == original
    assert not list((tmp_path / "out").glob("*.dxf"))
