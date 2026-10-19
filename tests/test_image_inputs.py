"""Decode real raster files through the same editable scan pipeline as TIFF."""
import io
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, features
import pytest

from pdf2office.extract import PdfExtractionError
from pdf2office.input_formats import IMAGE_EXTENSIONS, INPUT_EXTENSIONS
from pdf2office.scans import extract_image, extract_tiff


def read(path, **kwargs):
    options = dict(ocr_mode="off", vectorize_scans=False, preprocess=False, deskew=False)
    options.update(kwargs)
    return extract_image(path, **options)


def raster(page):
    with Image.open(io.BytesIO(page.images[0].data)) as image:
        return image.copy()


def drawing(mode="RGB", size=(360, 160)):
    image = Image.new(mode, size, "white")
    ImageDraw.Draw(image).line((20, 70, 330, 70), fill="black", width=2)
    return image


@pytest.mark.parametrize("suffix,format_name,mode", [
    (".JPG", "JPEG", "RGB"), (".jpeg", "JPEG", "RGB"),
    (".jpe", "JPEG", "RGB"), (".jfif", "JPEG", "RGB"),
    (".png", "PNG", "RGB"), (".bmp", "BMP", "RGB"),
    (".dib", "DIB", "RGB"), (".webp", "WEBP", "RGB"),
    (".avif", "AVIF", "RGB"), (".gif", "GIF", "P"),
    (".jp2", "JPEG2000", "RGB"), (".j2k", "JPEG2000", "RGB"),
    (".jpc", "JPEG2000", "RGB"), (".jpf", "JPEG2000", "RGB"),
    (".jpx", "JPEG2000", "RGB"), (".j2c", "JPEG2000", "RGB"),
    (".pcx", "PCX", "RGB"), (".tga", "TGA", "RGB"),
    (".ppm", "PPM", "RGB"), (".pgm", "PPM", "L"),
    (".pbm", "PPM", "1"), (".pnm", "PPM", "RGB"),
    (".tif", "TIFF", "RGB"), (".tiff", "TIFF", "RGB"),
])
def test_actual_codec_and_alias_keeps_native_pixels_and_source(tmp_path, suffix, format_name, mode):
    path = tmp_path / ("Чертёж" + suffix)
    image = drawing().convert(mode)
    kwargs = {"quality": 100} if format_name in {"JPEG", "WEBP", "AVIF"} else {}
    if suffix in {".j2k", ".jpc", ".j2c"}:
        kwargs["no_jp2"] = True
    image.save(path, format_name, **kwargs)
    original = path.read_bytes()
    page, = read(path)
    output = raster(page)
    assert output.size == image.size
    assert output.getpixel((150, 70)) < 20
    assert output.getpixel((150, 20)) > 240
    assert not page.ocr_used and not page.texts
    assert path.read_bytes() == original
    assert suffix.lower() in IMAGE_EXTENSIONS
    assert any("приблизительно" in warning for warning in page.warnings)


def test_pdf_is_routed_separately_from_raster():
    assert ".pdf" in INPUT_EXTENSIONS and ".pdf" not in IMAGE_EXTENSIONS
    assert ".heic" not in INPUT_EXTENSIONS and ".raw" not in INPUT_EXTENSIONS


@pytest.mark.parametrize("orientation", [2, 3, 4, 5, 6, 7, 8])
def test_jpeg_exif_orientation_and_asymmetric_physical_dpi(tmp_path, orientation):
    image = Image.new("L", (120, 80), 255)
    ImageDraw.Draw(image).rectangle((10, 10, 30, 30), fill=0)
    exif = Image.Exif()
    exif[274], exif[282], exif[283], exif[296] = orientation, 300, 150, 2
    path = tmp_path / "phone.jpg"
    image.save(path, quality=100, exif=exif)
    page, = read(path)
    method = {2: Image.Transpose.FLIP_LEFT_RIGHT, 3: Image.Transpose.ROTATE_180,
              4: Image.Transpose.FLIP_TOP_BOTTOM, 5: Image.Transpose.TRANSPOSE,
              6: Image.Transpose.ROTATE_270, 7: Image.Transpose.TRANSVERSE,
              8: Image.Transpose.ROTATE_90}[orientation]
    expected = image.transpose(method)
    output = raster(page)
    assert output.size == expected.size
    assert np.array_equal(np.asarray(output) < 128, np.asarray(expected) < 128)
    dpi = (150, 300) if orientation >= 5 else (300, 150)
    assert (page.width, page.height) == pytest.approx((output.width * 72 / dpi[0], output.height * 72 / dpi[1]))


@pytest.mark.parametrize("unit,expected", [(2, (100, 50)), (3, (254, 127))])
def test_exif_resolution_preserves_both_axes_and_converts_centimetres(tmp_path, unit, expected):
    exif = Image.Exif()
    exif[282], exif[283], exif[296] = 100, 50, unit
    path = tmp_path / "scale.jpg"
    drawing().save(path, exif=exif)
    page, = read(path)
    assert (page.width, page.height) == pytest.approx((360 * 72 / expected[0], 160 * 72 / expected[1]))


@pytest.mark.parametrize("exif", [None, {274: 6}, {282: 100, 283: 50, 296: 1},
                                  {282: 0, 283: 0, 296: 2}])
def test_jpeg_without_physical_resolution_never_uses_pillow_invented_72dpi(tmp_path, exif):
    path = tmp_path / "no-scale.jpg"
    tags = Image.Exif()
    tags.update(exif or {})
    drawing().save(path, exif=tags)
    page, = read(path)
    output = raster(page)
    assert (page.width, page.height) == pytest.approx((output.width * 72 / 300, output.height * 72 / 300))
    assert any("300 dpi" in warning for warning in page.warnings)


def test_jfif_dpi_and_manual_override(tmp_path):
    path = tmp_path / "scale.jpg"
    drawing().save(path, dpi=(600, 300))
    page, = read(path)
    assert (page.width, page.height) == pytest.approx((43.2, 38.4))
    override, = read(path, dpi=400)
    assert (override.width, override.height) == pytest.approx((64.8, 28.8))
    assert any("400 dpi" in warning for warning in override.warnings)


def test_png_physical_dpi_and_exif_orientation(tmp_path):
    exif = Image.Exif()
    exif[274] = 6
    path = tmp_path / "scale.png"
    drawing().save(path, dpi=(400, 200), exif=exif)
    page, = read(path)
    assert raster(page).size == (160, 360)
    assert (page.width, page.height) == pytest.approx((57.6, 64.8), rel=0.0001)


@pytest.mark.parametrize("mode", ["RGBA", "LA", "P"])
def test_transparent_background_composites_white_without_losing_ink(tmp_path, mode):
    image = Image.new("RGBA", (200, 100), (0, 0, 0, 0))
    ImageDraw.Draw(image).line((20, 30, 180, 30), fill=(0, 0, 0, 255), width=2)
    if mode == "P":
        image = image.convert("P")
    elif mode == "LA":
        image = image.convert("LA")
    path = tmp_path / "transparent.png"
    image.save(path)
    page, = read(path)
    output = raster(page)
    assert output.getpixel((100, 10)) == 255
    assert output.getpixel((100, 30)) == 0


def test_cmyk_jpeg_and_16bit_png_keep_dark_lines(tmp_path):
    path = tmp_path / "cmyk.jpg"
    drawing().convert("CMYK").save(path, quality=100)
    page, = read(path)
    assert raster(page).getpixel((150, 70)) < 10
    assert raster(page).getpixel((150, 20)) > 245
    pixels = np.full((100, 200), 60000, np.uint16)
    pixels[30:32, 20:180] = 1000
    path = tmp_path / "16bit.png"
    Image.fromarray(pixels).save(path)
    page, = read(path)
    assert raster(page).getpixel((100, 30)) < 10
    assert raster(page).getpixel((100, 10)) > 230


@pytest.mark.parametrize("suffix,format_name", [(".gif", "GIF"), (".apng", "PNG"),
                                                (".webp", "WEBP"), (".avifs", "AVIF")])
def test_animation_frames_selection_composition_and_explicit_warning(tmp_path, suffix, format_name):
    first = Image.new("RGB", (120, 80), "white")
    second = first.copy()
    ImageDraw.Draw(first).rectangle((10, 20, 30, 40), fill="black")
    ImageDraw.Draw(second).rectangle((80, 20, 100, 40), fill="black")
    path = tmp_path / ("frames" + suffix)
    options = dict(save_all=True, append_images=[second], duration=100, loop=0)
    if format_name in {"WEBP", "AVIF"}:
        options["quality"] = 100
    if format_name == "GIF":
        options["disposal"] = 2
    first.save(path, format_name, **options)
    pages = read(path)
    assert [page.number for page in pages] == [1, 2]
    assert raster(pages[0]).getpixel((20, 30)) < 10
    assert raster(pages[1]).getpixel((20, 30)) > 245
    assert raster(pages[1]).getpixel((90, 30)) < 10
    assert all(any("время показа" in warning for warning in page.warnings) for page in pages)
    selected, = read(path, pages="2")
    assert selected.number == 2
    assert raster(selected).tobytes() == raster(pages[1]).tobytes()


def test_apng_separate_default_image_is_explicit_page_one(tmp_path):
    path = tmp_path / "poster.png"
    default = Image.new("RGB", (80, 60), "white")
    frame = Image.new("RGB", (80, 60), "black")
    default.save(path, save_all=True, append_images=[frame], default_image=True, duration=100)
    first, second = read(path)
    assert raster(first).getpixel((20, 20)) == 255
    assert raster(second).getpixel((20, 20)) == 0
    assert all(any("кадры анимации начинаются со страницы 2" in warning for warning in page.warnings)
               for page in (first, second))


def test_supported_mislabeled_image_is_read_by_content_without_modification(tmp_path):
    path = tmp_path / "wrong.jpg"
    drawing().save(path, "PNG")
    original = path.read_bytes()
    page, = read(path)
    assert raster(page).size == drawing().size
    assert path.read_bytes() == original
    assert any("не соответствует" in warning and "PNG" in warning for warning in page.warnings)
    with pytest.raises(PdfExtractionError, match="не является TIFF"):
        extract_tiff(path)


def test_corrupt_and_unsupported_images_are_not_successes(tmp_path):
    path = tmp_path / "broken.jpg"
    path.write_bytes(b"broken image")
    with pytest.raises(PdfExtractionError, match="повреждён"):
        read(path)
    unsupported = tmp_path / "drawing.heic"
    drawing().save(unsupported, "PNG")
    with pytest.raises(PdfExtractionError, match="не поддерживается"):
        read(unsupported)
    truncated = tmp_path / "truncated.png"
    drawing().save(truncated)
    truncated.write_bytes(truncated.read_bytes()[:70])
    with pytest.raises(PdfExtractionError, match="повреждён"):
        read(truncated)


def test_unavailable_jpeg2000_decoder_has_actionable_error(tmp_path, monkeypatch):
    path = tmp_path / "drawing.jp2"
    drawing().save(path, "JPEG2000")
    monkeypatch.setattr(features, "check_codec", lambda codec: False)
    with pytest.raises(PdfExtractionError, match="OpenJPEG"):
        read(path)


def test_new_formats_preserve_pixel_cap_and_cancellation(tmp_path, monkeypatch):
    import pdf2office.scans as scans
    path = tmp_path / "drawing.png"
    drawing().save(path)
    monkeypatch.setattr(scans, "MAX_SCAN_PIXELS", 1000)
    with pytest.raises(PdfExtractionError, match="слишком велика"):
        read(path)
    class Stop(Exception):
        pass
    def cancel():
        raise Stop()
    with pytest.raises(Stop):
        read(path, check_cancel=cancel)


def test_png_default_cleanup_and_vectorization_produce_editable_geometry(tmp_path):
    path = tmp_path / "drawing.png"
    drawing().save(path, dpi=(300, 300))
    page, = extract_image(path, ocr_mode="off", deskew=False)
    assert page.drawings
    assert any("шумовые пиксели" in warning for warning in page.warnings)
