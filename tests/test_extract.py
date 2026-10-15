import io
import re
import shutil
import subprocess
from pathlib import Path

import pymupdf as fitz
import pytest
from PIL import Image, ImageDraw, ImageFont

from pdf2office.extract import PdfExtractionError, _parse_dashes, extract_pdf, parse_page_range


def make_pdf(tmp_path, rotation=0):
    path = tmp_path / "source.pdf"
    with fitz.open() as document:
        page = document.new_page(width=300, height=400)
        page.insert_text((30, 50), "Editable text", fontsize=12, color=(1, 0, 0))
        page.draw_line((20, 100), (180, 100), color=(0, 0, 1), width=2)
        page.draw_bezier((30, 150), (60, 120), (90, 180), (120, 150), color=(0, 0, 0))
        page.draw_rect(fitz.Rect(20, 200, 100, 250), color=(0, 0, 0), fill=(0.5, 0.5, 0.5))
        page.set_rotation(rotation)
        document.save(path)
    return path


def test_extract_native_geometry_text(tmp_path):
    page, = extract_pdf(make_pdf(tmp_path), ocr_mode="off")
    assert (page.width, page.height) == (300, 400)
    assert page.texts[0].text == "Editable text"
    assert page.texts[0].origin == pytest.approx((30, 50))
    assert page.texts[0].color == (255, 0, 0)
    assert page.texts[0].size == 12
    assert page.texts[0].advance == pytest.approx(fitz.get_text_length("Editable text", fontsize=12), abs=0.001)
    assert not page.ocr_used
    line = next(d for d in page.drawings if d.stroke == (0, 0, 255))
    assert line.width == 2
    assert line.segments[0].points == ((20, 100), (180, 100))
    assert any(s.kind == "cubic" and len(s.points) == 4 for d in page.drawings for s in d.segments)
    rectangle = next(d for d in page.drawings if d.fill)
    assert len(rectangle.segments) == 4


@pytest.mark.parametrize("rotation,size,origin,angle", [(90, (400, 300), (350, 30), 90), (180, (300, 400), (270, 350), 180), (270, (400, 300), (50, 270), -90)])
def test_rotation_applies_to_all_geometry(tmp_path, rotation, size, origin, angle):
    page, = extract_pdf(make_pdf(tmp_path, rotation), ocr_mode="off")
    assert (page.width, page.height) == size
    assert page.texts[0].origin == pytest.approx(origin)
    assert page.texts[0].angle == pytest.approx(angle)
    assert page.texts[0].advance == pytest.approx(fitz.get_text_length("Editable text", fontsize=12), abs=0.001)
    for drawing in page.drawings:
        for segment in drawing.segments:
            for x, y in segment.points:
                assert 0 <= x <= page.width and 0 <= y <= page.height


@pytest.mark.parametrize("page_rotation", [0, 90])
def test_angled_text_keeps_baseline_advance_not_axis_aligned_box(tmp_path, page_rotation):
    path = tmp_path / "angled.pdf"
    text = "Angled editable baseline"
    with fitz.open() as document:
        page = document.new_page(width=300, height=400)
        anchor = fitz.Point(50, 200)
        page.insert_text(anchor, text, fontsize=12, morph=(anchor, fitz.Matrix(27)))
        page.set_rotation(page_rotation)
        document.save(path)
    page, = extract_pdf(path, ocr_mode="off")
    span, = page.texts
    assert span.angle == pytest.approx(page_rotation - 27, abs=0.001)
    assert span.advance == pytest.approx(fitz.get_text_length(text, fontsize=12), abs=0.01)
    assert abs(span.advance - (span.bbox[2] - span.bbox[0])) > 1


@pytest.mark.parametrize("bad", ["0", "2-1", "1,,2", "-1", "1-99999999999", "3", "x", "1;2", "1-"])
def test_invalid_page_ranges(bad):
    with pytest.raises(PdfExtractionError):
        parse_page_range(bad, 2)


def test_page_ranges():
    assert parse_page_range("3, 1-2,2", 3) == [0, 1, 2]
    assert parse_page_range("", 3) == [0, 1, 2]


def test_pdf_error_and_password(tmp_path):
    broken = tmp_path / "broken.pdf"
    broken.write_bytes(b"invalid PDF data")
    with pytest.raises(PdfExtractionError, match="повреждён"):
        extract_pdf(broken, ocr_mode="off")
    locked = tmp_path / "encrypted.pdf"
    with fitz.open() as document:
        document.new_page().insert_text((10, 30), "Protected")
        document.save(locked, encryption=fitz.PDF_ENCRYPT_AES_256, owner_pw="owner", user_pw="secret")
    with pytest.raises(PdfExtractionError, match="паролем"):
        extract_pdf(locked, password="wrong", ocr_mode="off")
    assert extract_pdf(locked, password="secret", ocr_mode="off")[0].texts[0].text == "Protected"


def test_rotated_raster_preserves_color_and_bbox(tmp_path):
    source = Image.new("RGB", (200, 100), "white")
    ImageDraw.Draw(source).rectangle((0, 0, 99, 99), fill="red")
    data = io.BytesIO()
    source.save(data, "PNG")
    path = tmp_path / "image.pdf"
    with fitz.open() as document:
        page = document.new_page(width=200, height=300)
        page.insert_image(fitz.Rect(0, 0, 200, 100), stream=data.getvalue())
        page.set_rotation(90)
        document.save(path)
    page, = extract_pdf(path, ocr_mode="off")
    assert page.images[0].bbox == pytest.approx((200, 0, 300, 200))
    decoded = Image.open(io.BytesIO(page.images[0].data))
    assert decoded.getpixel((decoded.width // 2, decoded.height // 4))[:3] == (255, 0, 0)
    assert decoded.getpixel((decoded.width // 2, decoded.height * 3 // 4))[:3] == (255, 255, 255)


@pytest.mark.skipif(shutil.which("tesseract") is None, reason="Tesseract is optional in developer checkouts")
def test_scan_ocr_and_native_text_not_duplicated(tmp_path):
    from pdf2office.fonts import bundled_font_dir
    image = Image.new("RGB", (1200, 500), "white")
    draw = ImageDraw.Draw(image)
    font = ImageFont.truetype(str(bundled_font_dir() / "LiberationSans-Regular.ttf"), 58)
    draw.text((80, 180), "Scanned engineering 12345", fill="black", font=font)
    stream = io.BytesIO()
    image.save(stream, "PNG")
    path = tmp_path / "scan.pdf"
    with fitz.open() as document:
        page = document.new_page(width=600, height=250)
        page.insert_image(page.rect, stream=stream.getvalue())
        page.insert_text((40, 40), "Native title", fontsize=16)
        document.save(path)
    page, = extract_pdf(path, ocr_language="eng")
    assert page.ocr_used
    assert "engineering" in " ".join(span.text for span in page.texts if span.from_ocr)
    assert sum("Native" in span.text for span in page.texts) == 1
    assert all(0 <= span.bbox[0] < span.bbox[2] <= page.width for span in page.texts)


@pytest.mark.skipif(shutil.which("tesseract") is None, reason="Tesseract unavailable")
def test_missing_language_is_actionable(tmp_path):
    with pytest.raises(PdfExtractionError, match="языковые данные"):
        extract_pdf(make_pdf(tmp_path), ocr_mode="always", ocr_language="missing_language")


def test_progress_and_page_selection(tmp_path):
    path = tmp_path / "multiple.pdf"
    with fitz.open() as document:
        for index in range(3):
            document.new_page().insert_text((30, 30), str(index + 1))
        document.save(path)
    updates = []
    page, = extract_pdf(path, pages="2", ocr_mode="off", progress=updates.append)
    assert page.number == 2 and page.texts[0].text == "2"
    assert updates


def test_scan_vectorization_produces_editable_segments(tmp_path):
    pytest.importorskip("cv2")
    image = Image.new("RGB", (1000, 800), "white")
    draw = ImageDraw.Draw(image)
    draw.line((120, 120, 850, 120), fill="black", width=3)
    draw.line((120, 120, 120, 650), fill="black", width=3)
    stream = io.BytesIO()
    image.save(stream, "PNG")
    path = tmp_path / "lines.pdf"
    with fitz.open() as document:
        page = document.new_page(width=500, height=400)
        page.insert_image(page.rect, stream=stream.getvalue())
        document.save(path)
    page, = extract_pdf(path, ocr_mode="off", vectorize_scans=True)
    segments = [segment for drawing in page.drawings for segment in drawing.segments]
    assert any(abs(segment.points[0][1] - 60) < 3 and abs(segment.points[1][1] - 60) < 3 for segment in segments)
    assert any(abs(segment.points[0][0] - 60) < 3 and abs(segment.points[1][0] - 60) < 3 for segment in segments)
    assert page.images
    assert any("приблизительная" in message for message in page.warnings)


def test_native_dash_pattern_preserved_and_opacity_reported(tmp_path):
    path = tmp_path / "styles.pdf"
    with fitz.open() as document:
        source = document.new_page()
        source.draw_line((10, 10), (100, 10), dashes="[4 2] 0", stroke_opacity=0.5)
        document.save(path)
    page, = extract_pdf(path, ocr_mode="off")
    assert page.drawings[0].dashes == (4.0, 2.0)
    assert page.drawings[0].dash_offset == 0.0
    assert not any("штрих" in warning.casefold() for warning in page.warnings)
    assert any("Прозрачность" in warning for warning in page.warnings)


def test_odd_dash_array_and_nonzero_phase(tmp_path):
    path = tmp_path / "odd-dashes.pdf"
    with fitz.open() as document:
        source = document.new_page()
        source.draw_line((10, 10), (100, 10), dashes="[4 2 1] 3")
        document.save(path)
    page, = extract_pdf(path, ocr_mode="off")
    assert page.drawings[0].dashes == (4.0, 2.0, 1.0, 4.0, 2.0, 1.0)
    assert page.drawings[0].dash_offset == 3.0
    assert any("смещение штрихового" in warning for warning in page.warnings)


@pytest.mark.parametrize("value", ["[0 0] 0", "[-1 2] 0", "[nan 2] 0", "[inf 2] 0", "[1 2] -1", "[1 2] nan", "[1 2] inf", "invalid"])
def test_invalid_dash_arrays_are_rejected(value):
    with pytest.raises(ValueError):
        _parse_dashes(value)


def test_dash_array_zero_is_valid_dot_and_empty_is_solid():
    assert _parse_dashes("[0 2] 0") == ((0.0, 2.0), 0.0)
    assert _parse_dashes("[] 0") == ((), 0.0)
    assert _parse_dashes(None) == ((), 0.0)


@pytest.mark.skipif(shutil.which("tesseract") is None, reason="Tesseract unavailable")
def test_rotated_scan_ocr_retains_text_orientation(tmp_path):
    from pdf2office.fonts import bundled_font_dir
    image = Image.new("RGB", (1300, 900), "white")
    draw = ImageDraw.Draw(image)
    font = ImageFont.truetype(str(bundled_font_dir() / "LiberationSans-Regular.ttf"), 44)
    for index in range(8):
        draw.text((80, 50 + index * 95), "Engineering drawings with editable text", fill="black", font=font)
    stream = io.BytesIO()
    image.save(stream, "PNG")
    path = tmp_path / "rotated-scan.pdf"
    with fitz.open() as document:
        source = document.new_page(width=650, height=450)
        source.insert_image(source.rect, stream=stream.getvalue())
        source.set_rotation(90)
        document.save(path)
    page, = extract_pdf(path, ocr_language="eng")
    assert any("Engineering" in span.text for span in page.texts)
    assert all(span.angle == pytest.approx(90) for span in page.texts)
    assert all(0 <= span.bbox[0] < span.bbox[2] <= page.width for span in page.texts)


@pytest.mark.skipif(shutil.which("tesseract") is None, reason="Tesseract unavailable")
def test_always_ocr_replaces_corrupted_native_text_with_real_recognition(tmp_path, monkeypatch):
    import pdf2office.extract as extraction
    path = make_pdf(tmp_path)
    original_native_text = extraction._native_text

    def corrupt_native_text(page, blocks):
        spans = original_native_text(page, blocks)
        for span in spans:
            span.text = "\ufffd\ufffd\ufffd"
        return spans

    monkeypatch.setattr(extraction, "_native_text", corrupt_native_text)
    native, = extract_pdf(path, ocr_mode="off")
    assert native.texts[0].text == "\ufffd\ufffd\ufffd"
    recovered, = extract_pdf(path, ocr_mode="always", ocr_language="eng")
    assert "Editable text" in " ".join(span.text for span in recovered.texts)
    assert all(span.from_ocr and "\ufffd" not in span.text for span in recovered.texts)
    assert recovered.ocr_used
    assert recovered.drawings == native.drawings
    assert recovered.images == native.images
    assert any("шрифты и начертания приблизительны" in warning for warning in recovered.warnings)
    assert not any("Unicode" in warning for warning in recovered.warnings)


@pytest.mark.skipif(shutil.which("tesseract") is None, reason="Tesseract unavailable")
def test_unicode_ocr_data_and_temp_paths_use_ascii_native_arguments(tmp_path, monkeypatch):
    import pdf2office.extract as extraction
    executable = Path(shutil.which("tesseract"))
    languages = subprocess.run([str(executable), "--list-langs"], capture_output=True,
                               text=True, encoding="utf-8", errors="replace", check=True)
    match = re.search(r'"([^"]+)"', languages.stdout)
    if not match or not (Path(match[1]) / "eng.traineddata").is_file():
        pytest.skip("Cannot locate English Tesseract data for Unicode path fixture")
    unicode_data = tmp_path / "Пользователь Данные OCR"
    unicode_data.mkdir()
    shutil.copy2(Path(match[1]) / "eng.traineddata", unicode_data / "eng.traineddata")
    unicode_temp = tmp_path / "Временные файлы"
    unicode_temp.mkdir()
    monkeypatch.setenv("TESSDATA_PREFIX", str(unicode_data))
    monkeypatch.setenv("TEMP", str(unicode_temp))
    monkeypatch.setenv("TMP", str(unicode_temp))
    monkeypatch.setenv("TMPDIR", str(unicode_temp))
    invocations = []
    original_run = extraction._run_tesseract

    def record_run(command, timeout=None, *, input_data=None, cwd=None, **kwargs):
        invocations.append((command, input_data, cwd))
        return original_run(command, timeout, input_data=input_data, cwd=cwd, **kwargs)

    monkeypatch.setattr(extraction, "_run_tesseract", record_run)
    page, = extract_pdf(make_pdf(tmp_path), ocr_mode="always", ocr_language="eng")
    assert "Editable text" in " ".join(span.text for span in page.texts)
    assert len(invocations) >= 2
    for command, input_data, cwd in invocations:
        assert cwd == unicode_data.resolve()
        assert command[command.index("--tessdata-dir") + 1] == "."
        assert all(argument.isascii() for argument in command[1:])
        if "--list-langs" not in command:
            assert command[1:3] == ["stdin", "stdout"]
            assert input_data.startswith(b"\x89PNG\r\n\x1a\n")
    assert not list(unicode_temp.iterdir())
