import io
import shutil
from pathlib import Path
import subprocess

import numpy as np
from PIL import Image, ImageDraw, ImageFont, TiffImagePlugin
import pytest

from pdf2office.extract import PdfExtractionError
from pdf2office.scans import clean_scan, deskew_scan, estimate_skew, extract_tiff


def save_tiff(tmp_path, image, *, dpi=(300, 300), name="скан.tif", frames=None):
    path = tmp_path / name
    kwargs = dict(compression="tiff_lzw")
    if dpi is not None:
        kwargs["dpi"] = dpi
    if frames:
        kwargs.update(save_all=True, append_images=frames)
    image.save(path, **kwargs)
    return path


def test_tiff_physical_dimensions_source_unchanged_and_png_reference(tmp_path):
    image = Image.new("1", (800, 400), 1)
    ImageDraw.Draw(image).line((20, 20, 700, 20), fill=0)
    path = save_tiff(tmp_path, image, dpi=(400, 200))
    original = path.read_bytes()
    page, = extract_tiff(path, ocr_mode="off", vectorize_scans=False, deskew=False)
    assert path.read_bytes() == original
    assert (page.width, page.height) == pytest.approx((144, 144))
    assert not page.texts and not page.ocr_used
    assert page.images[0].bbox == (0, 0, 144, 144)
    with Image.open(io.BytesIO(page.images[0].data)) as raster:
        assert raster.size == image.size
        assert raster.getpixel((300, 20)) == 0


def test_multipage_tiff_selection_and_progress(tmp_path):
    path = save_tiff(tmp_path, Image.new("L", (300, 200), 255),
                     frames=[Image.new("L", (250, 350), 255), Image.new("L", (150, 100), 255)])
    messages = []
    pages = extract_tiff(path, pages="2-3", ocr_mode="off", vectorize_scans=False,
                         deskew=False, progress=messages.append)
    assert [page.number for page in pages] == [2, 3]
    assert [(page.width, page.height) for page in pages] == pytest.approx([(60, 84), (36, 24)])
    assert any("страница 2 из 3" in message for message in messages)


def test_missing_dpi_has_explicit_scale_warning_and_override(tmp_path):
    path = save_tiff(tmp_path, Image.new("L", (600, 300), 255), dpi=None)
    page, = extract_tiff(path, ocr_mode="off", vectorize_scans=False, deskew=False)
    assert page.width == 144
    assert any("300 dpi" in message for message in page.warnings)
    override, = extract_tiff(path, dpi=600, ocr_mode="off", vectorize_scans=False, deskew=False)
    assert override.width == 72
    assert any("600 dpi" in message for message in override.warnings)


@pytest.mark.parametrize("dpi", [True, -1, 71, 2401, float("nan"), float("inf"), "300"])
def test_invalid_dpi_is_actionable(tmp_path, dpi):
    with pytest.raises(PdfExtractionError, match="DPI"):
        extract_tiff(tmp_path / "any.tif", dpi=dpi)


def test_non_tiff_and_corrupted_input_rejected(tmp_path):
    png = tmp_path / "looks-like.tif"
    Image.new("L", (30, 30), 255).save(png, "PNG")
    with pytest.raises(PdfExtractionError, match="не является TIFF"):
        extract_tiff(png)
    png.write_bytes(b"broken")
    with pytest.raises(PdfExtractionError, match="повреждён"):
        extract_tiff(png)


def test_pixel_cap_never_silently_resizes(tmp_path, monkeypatch):
    import pdf2office.scans as scans
    path = save_tiff(tmp_path, Image.new("1", (100, 100), 1))
    monkeypatch.setattr(scans, "MAX_SCAN_PIXELS", 5000)
    with pytest.raises(PdfExtractionError, match="слишком велика"):
        extract_tiff(path, ocr_mode="off")


def test_cleanup_removes_speckles_preserves_thin_lines_and_punctuation():
    image = Image.new("L", (400, 240), 255)
    draw = ImageDraw.Draw(image)
    draw.line((20, 80, 350, 80), fill=0, width=1)
    draw.line((200, 30, 200, 210), fill=0, width=1)
    draw.rectangle((40, 140, 46, 160), fill=0)
    draw.rectangle((51, 158, 52, 159), fill=0)  # punctuation beside a character
    draw.rectangle((300, 160, 301, 161), fill=0)  # isolated scan speckle
    clean, removed = clean_scan(image, 300)
    assert removed == 4
    assert clean.getpixel((300, 160)) == 255
    assert clean.getpixel((51, 158)) == 0
    assert all(clean.getpixel((x, 80)) == 0 for x in range(20, 351))
    assert all(clean.getpixel((200, y)) == 0 for y in range(30, 211))


def test_cleanup_has_no_tile_seams_or_lost_strokes(monkeypatch):
    import pdf2office.scans as scans
    monkeypatch.setattr(scans, "CLEAN_TILE_SIZE", 128)
    image = Image.new("L", (410, 290), 255)
    draw = ImageDraw.Draw(image)
    draw.line((10, 127, 399, 127), fill=0, width=1)
    draw.line((255, 10, 255, 279), fill=0, width=1)
    clean, _ = clean_scan(image, 300)
    assert np.array_equal(np.asarray(clean), np.asarray(image))


def test_coloured_shaded_background_is_whitened_without_resizing():
    ramp = np.tile(np.linspace(150, 240, 450, dtype=np.uint8), (240, 1))
    image = Image.fromarray(ramp).convert("RGB")
    ImageDraw.Draw(image).line((25, 100, 425, 100), fill=(20, 20, 20), width=2)
    clean, _ = clean_scan(image, 300)
    assert clean.size == image.size
    assert clean.getpixel((50, 50)) == 255
    assert clean.getpixel((400, 50)) == 255
    assert clean.getpixel((200, 100)) == 0


def test_deskew_preserves_content_and_corrects_consistent_ruling():
    source = Image.new("L", (1100, 800), 255)
    draw = ImageDraw.Draw(source)
    for y in [90, 180, 310, 430, 590, 710]:
        draw.line((20, y, 1080, y), fill=0, width=3)
    for x in [20, 300, 600, 1080]:
        draw.line((x, 90, x, 710), fill=0, width=3)
    skewed = source.rotate(-2.7, expand=True, fillcolor=255)
    assert estimate_skew(skewed) == pytest.approx(2.7, abs=0.2)
    corrected, angle = deskew_scan(skewed)
    assert angle == pytest.approx(2.7, abs=0.2)
    assert corrected.width >= skewed.width and corrected.height >= skewed.height
    assert estimate_skew(corrected) == pytest.approx(0, abs=0.2)
    assert np.count_nonzero(np.asarray(corrected) < 128) >= np.count_nonzero(np.asarray(source) < 128) * 0.95


def test_unruled_map_and_blank_page_are_not_arbitrarily_rotated():
    image = Image.new("L", (600, 600), 255)
    assert estimate_skew(image) == 0
    ImageDraw.Draw(image).line((30, 550, 570, 30), fill=0, width=3)
    assert estimate_skew(image) == 0


def test_cancel_propagates_during_cleanup_and_frame_read(tmp_path):
    class Stop(Exception):
        pass
    def cancel():
        raise Stop()
    with pytest.raises(Stop):
        clean_scan(Image.new("L", (100, 100)), 300, check_cancel=cancel)
    path = save_tiff(tmp_path, Image.new("L", (100, 100), 255))
    with pytest.raises(Stop):
        extract_tiff(path, check_cancel=cancel)


def test_tiff_orientation_tag_and_anisotropic_dpi(tmp_path):
    image = Image.new("L", (120, 80), 255)
    ImageDraw.Draw(image).rectangle((10, 10, 30, 30), fill=0)
    tags = TiffImagePlugin.ImageFileDirectory_v2()
    tags[274] = 6
    path = tmp_path / "oriented.tif"
    image.save(path, dpi=(300, 150), tiffinfo=tags)
    page, = extract_tiff(path, ocr_mode="off", vectorize_scans=False, preprocess=False, deskew=False)
    with Image.open(io.BytesIO(page.images[0].data)) as raster:
        assert raster.size == (80, 120)
        assert raster.getpixel((60, 20)) == 0
    assert (page.width, page.height) == pytest.approx((80 * 72 / 150, 120 * 72 / 300))


def test_overlapping_ocr_tiles_emit_one_complete_word_with_physical_bbox(monkeypatch):
    import pdf2office.scans as scans
    monkeypatch.setattr(scans, "OCR_TILE_SIZE", 100)
    monkeypatch.setattr(scans, "OCR_TILE_OVERLAP", 30)
    image = Image.new("L", (200, 60), 255)
    ImageDraw.Draw(image).rectangle((90, 20, 119, 29), fill=0)
    calls = []
    def fake(command, **kwargs):
        calls.append(kwargs)
        x = 90 if len(calls) == 1 else 20  # same word, different tile crop origin
        return subprocess.CompletedProcess(command, 0, f"level\tleft\ttop\twidth\theight\tconf\ttext\n5\t{x}\t20\t30\t10\t90\tWord\n", "")
    monkeypatch.setattr(scans, "_run_tesseract", fake)
    spans = scans._ocr_tiles(image, (300, 300), (Path("tesseract"), [], None, {"eng"}), "eng", [])
    assert len(spans) == 1
    assert spans[0].text == "Word"
    assert spans[0].bbox == pytest.approx((21.6, 4.8, 28.8, 7.2))
    assert all("timeout" not in call and call["input_data"].startswith(b"\x89PNG") for call in calls)


@pytest.mark.skipif(shutil.which("tesseract") is None, reason="Tesseract unavailable")
def test_real_tiff_ocr_is_editable_at_native_resolution(tmp_path):
    from pdf2office.fonts import bundled_font_dir
    image = Image.new("L", (1600, 450), 255)
    font = ImageFont.truetype(str(bundled_font_dir() / "LiberationSans-Regular.ttf"), 62)
    ImageDraw.Draw(image).text((90, 150), "Engineering drawing 12345", fill=0, font=font)
    path = save_tiff(tmp_path, image, dpi=(400, 400))
    page, = extract_tiff(path, ocr_language="eng", vectorize_scans=False, deskew=False)
    assert page.ocr_used
    assert "Engineering" in [span.text for span in page.texts]
    assert all(span.from_ocr and span.advance and span.origin for span in page.texts)
    assert page.width == 288
    assert all(0 <= span.bbox[0] < span.bbox[2] <= page.width for span in page.texts)


def test_16bit_tiff_retains_dark_strokes_without_clipping(tmp_path):
    pixels = np.full((200, 300), 60000, dtype=np.uint16)
    pixels[90:94, 20:280] = 1000
    path = save_tiff(tmp_path, Image.fromarray(pixels))
    page, = extract_tiff(path, ocr_mode="off", vectorize_scans=False, deskew=False)
    with Image.open(io.BytesIO(page.images[0].data)) as image:
        assert image.getpixel((100, 91)) == 0
        assert image.getpixel((100, 50)) == 255


def test_transparent_tiff_background_becomes_white(tmp_path):
    image = Image.new("RGBA", (200, 100), (0, 0, 0, 0))
    ImageDraw.Draw(image).line((20, 30, 180, 30), fill=(0, 0, 0, 255), width=2)
    path = save_tiff(tmp_path, image)
    page, = extract_tiff(path, ocr_mode="off", vectorize_scans=False, deskew=False)
    with Image.open(io.BytesIO(page.images[0].data)) as output:
        assert output.getpixel((100, 10)) == 255
        assert output.getpixel((100, 30)) == 0


def test_ocr_denoising_only_activates_for_dense_noise_and_preserves_source():
    from pdf2office.scans import _prepare_ocr_tile
    image = Image.new("L", (500, 300), 255)
    ImageDraw.Draw(image).rectangle((100, 120, 400, 140), fill=0)
    original = np.array(image)
    clean, filtered = _prepare_ocr_tile(image, 400)
    assert not filtered
    rng = np.random.default_rng(12)
    noisy = original.copy()
    noisy[rng.random(noisy.shape) < 0.08] = 0
    noisy_image = Image.fromarray(noisy)
    output, filtered = _prepare_ocr_tile(noisy_image, 400)
    assert filtered
    assert np.array_equal(np.asarray(noisy_image), noisy)
    assert np.count_nonzero(np.asarray(output) < 128) < np.count_nonzero(noisy < 128) * 0.6
    assert output.getpixel((200, 130)) == 0


def test_no_alignment_means_no_ocr_auto_rotation(tmp_path, monkeypatch):
    import pdf2office.scans as scans
    path = save_tiff(tmp_path, Image.new("L", (100, 200), 255))
    monkeypatch.setattr(scans, "_find_tesseract", lambda p: Path("tesseract"))
    monkeypatch.setattr(scans, "_ocr_environment", lambda *args: None)
    monkeypatch.setattr(scans, "_ocr_tiles", lambda *args, **kwargs: [])
    def unexpected(*args, **kwargs):
        raise AssertionError("Orientation should be disabled")
    monkeypatch.setattr(scans, "_orient_scan", unexpected)
    page, = extract_tiff(path, deskew=False, vectorize_scans=False)
    assert page.width < page.height


def test_line_like_ocr_punctuation_does_not_replace_geometry(monkeypatch):
    import pdf2office.scans as scans
    image = Image.new("L", (100, 80), 255)
    ImageDraw.Draw(image).line((10, 30, 80, 30), fill=0)
    tsv = "level\tleft\ttop\twidth\theight\tconf\ttext\n"
    tsv += "5\t10\t20\t50\t10\t99\t—\n5\t10\t40\t50\t10\t99\t|\n"
    tsv += "5\t10\t60\t50\t10\t90\t+0.5\n"
    monkeypatch.setattr(scans, "_run_tesseract", lambda command, **kwargs: subprocess.CompletedProcess(command, 0, tsv, ""))
    spans = scans._ocr_tiles(image, (300, 300), (Path("tesseract"), [], None, {"eng"}), "eng", [])
    assert [span.text for span in spans] == ["+0.5"]


def test_multipage_tiff_uses_each_frames_own_dpi(tmp_path):
    path = tmp_path / "different-dpi.tif"
    with TiffImagePlugin.AppendingTiffWriter(path, new=True) as output:
        for dpi in [300, 600]:
            Image.new("L", (600, 300), 255).save(output, format="TIFF", dpi=(dpi, dpi))
            output.newFrame()
    pages = extract_tiff(path, ocr_mode="off", vectorize_scans=False, deskew=False)
    assert [page.width for page in pages] == [144, 72]
