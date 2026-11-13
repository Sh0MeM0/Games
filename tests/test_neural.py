import math

import numpy as np
from PIL import Image, ImageDraw, ImageFont
import pytest

from pdf2office.fonts import bundled_font_dir
from pdf2office.neural import NeuralOcr, missing_models, shared_engine
from pdf2office.recognition import choose_reading, segment_span

needs_models = pytest.mark.skipif(bool(missing_models()), reason="neural OCR models are not installed")


def test_reading_quad_orders_horizontal_and_vertical_boxes():
    horizontal = NeuralOcr.reading_quad(((100, 50), (200, 20), 0))
    assert horizontal[0] == pytest.approx((0, 40))
    assert horizontal[1] == pytest.approx((200, 40))
    assert horizontal[3] == pytest.approx((0, 60))
    vertical = NeuralOcr.reading_quad(((50, 100), (20, 200), 0))
    direction = vertical[1] - vertical[0]
    # Vertical labels are read bottom-to-top first; the classifier/both-way
    # recognition corrects the opposite direction.
    assert direction[1] < 0 and abs(direction[0]) < 1e-6


def test_segment_span_maps_rotated_quad_to_page_points():
    quad = np.array([(100, 300), (100, 100), (130, 100), (130, 300)], float)  # bottom-to-top
    span = segment_span("2700", quad, 0.97, "neural", lambda points: points * 72 / 300)
    assert span.angle == -90.0
    assert span.advance == pytest.approx(200 * 72 / 300)
    assert span.size == pytest.approx(30 * 72 / 300)
    assert span.origin == pytest.approx((130 * 72 / 300, 300 * 72 / 300))
    assert span.from_ocr and span.engine == "neural" and span.quad is not None


def test_choose_reading_prefers_tesseract_spacing_but_not_weaker_text():
    assert choose_reading("12.Масштаб", 0.98, ("12. Масштаб", 0.94))[0] == "12. Масштаб"
    assert choose_reading("Ось А", 0.95, ("Ось Al", 0.68))[0] == "Ось А"
    assert choose_reading("Санузел", 0.6, ("Санузел", 0.96))[2] == "neural+tesseract"
    assert choose_reading("Слово", 0.7, None) == ("Слово", 0.7, "neural")
    assert choose_reading("Разре3 1-1", 0.86, ("Разрез 1-1", 0.955))[0] == "Разрез 1-1"
    assert choose_reading("R150", 0.99, ("В150|", 0.66))[0] == "R150"
    merged = choose_reading("плит толщинои 220", 0.95, ("плит толщиной 220", 0.93,
                                                        [("плит", 0.96), ("толщиной", 0.95), ("220", 0.96)]),
                            [("плит", 0.99), ("толщинои", 0.41), ("220", 0.99)])
    assert merged[0] == "плит толщиной 220"
    breve = choose_reading("плит толщинои 220 мм", 0.97, ("плит толщиной 220 MM", 0.81,
                                                          [("плит", 0.96), ("толщиной", 0.41), ("220", 0.96), ("MM", 0.96)]),
                           [("плит", 0.99), ("толщинои", 0.92), ("220", 0.98), ("мм", 0.99)])
    assert breve[0] == "плит толщиной 220 мм"
    kept = choose_reading("2700", 0.99, ("2100", 0.95, [("2100", 0.95)]), [("2700", 0.99)])
    assert kept[0] == "2700"


def _label_image():
    font = ImageFont.truetype(str(bundled_font_dir() / "LiberationSans-Regular.ttf"), 46)
    image = Image.new("L", (1400, 900), 255)
    draw = ImageDraw.Draw(image)
    draw.text((80, 60), "Стена из кирпича 380 мм", font=font, fill=0)
    draw.text((80, 640), "Перекрытие из плит толщиной 220 мм", font=font, fill=0)
    draw.text((80, 760), "Все размеры указаны в миллиметрах", font=font, fill=0)
    tile = Image.new("L", (520, 70), 255)
    ImageDraw.Draw(tile).text((8, 6), "Разрез 1-1", font=font, fill=0)
    image.paste(tile.rotate(90, expand=True, fillcolor=255), (1200, 250))
    tile = Image.new("L", (460, 70), 255)
    ImageDraw.Draw(tile).text((8, 6), "Section A-A", font=font, fill=0)
    image.paste(tile.rotate(25, expand=True, fillcolor=255), (300, 400))
    return image.rotate(2.5, expand=True, fillcolor=255, resample=Image.Resampling.BICUBIC)


@needs_models
def test_neural_engine_reads_skewed_vertical_and_inclined_text():
    engine = shared_engine()
    assert engine is not None
    segments = engine.read(np.asarray(_label_image()), 300)
    texts = {segment.text: segment for segment in segments}
    assert "Стена из кирпича 380 мм" in texts
    assert "Section A-A" in texts
    # "з" and "3" are near-identical in this face; the raw network may confuse
    # them, which the Tesseract cross-check in the full pipeline resolves.
    vertical = next(segment for segment in segments if segment.text.replace("3", "з") == "Разрез 1-1")
    assert abs(texts["Стена из кирпича 380 мм"].angle - (-2.5)) < 1.0
    # The whole sheet is turned by 2.5° counter-clockwise.
    assert abs(vertical.angle - (-92.5)) < 1.5
    assert abs(texts["Section A-A"].angle - (-27.5)) < 2.5


@needs_models
def test_image_extraction_uses_neural_ocr_and_keeps_rotation(tmp_path):
    from pdf2office.scans import extract_image

    path = tmp_path / "labels.png"
    _label_image().save(path, dpi=(300, 300))
    page, = extract_image(path, vectorize_scans=False)
    by_text = {span.text: span for span in page.texts}
    assert "Разрез 1-1" in by_text
    assert abs(abs(by_text["Разрез 1-1"].angle) - 90) < 2
    assert any(span.engine.startswith("neural") for span in page.texts)
    assert any("Нейросетевой OCR" in warning for warning in page.warnings)
    # The page was deskewed by the text lines (no ruled lines on the sheet).
    assert any("Перекос изображения исправлен" in warning for warning in page.warnings)
    assert by_text["Стена из кирпича 380 мм"].angle == pytest.approx(0, abs=0.5)
    assert all(math.isfinite(value) for span in page.texts for value in span.bbox)


def test_short_phrases_follow_dominant_direction_but_inclined_labels_keep_angle():
    from pdf2office.neural import OcrSegment
    from pdf2office.recognition import harmonize_angles

    def segment(angle, length, height=20.0):
        radians = math.radians(angle)
        u = np.array((math.cos(radians), math.sin(radians)))
        v = np.array((-u[1], u[0]))
        centre = np.array((500.0, 500.0))
        quad = np.array([centre - u * length / 2 - v * height / 2, centre + u * length / 2 - v * height / 2,
                         centre + u * length / 2 + v * height / 2, centre - u * length / 2 + v * height / 2])
        return OcrSegment("x", quad, 0.9, angle, height)

    items = [segment(0.2, 600), segment(-0.1, 500), segment(2.1, 120), segment(-88.0, 90), segment(30.0, 200)]
    harmonize_angles(items)
    assert items[2].angle == pytest.approx(0.05, abs=0.2)
    assert items[3].angle == pytest.approx(-89.95, abs=0.2)
    assert items[4].angle == 30.0
    assert np.linalg.norm(items[2].quad[1] - items[2].quad[0]) == pytest.approx(120)
