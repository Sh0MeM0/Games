"""Geometric fidelity of raster centreline tracing, independent of OCR engines."""
from __future__ import annotations

import math

import numpy as np
import pytest
from PIL import Image, ImageDraw

from pdf2office.extract import _trace_scan
from pdf2office.models import PdfPage, TextSpan

pytest.importorskip("cv2")


def page_for(image, *, texts=None, width=None, height=None):
    return PdfPage(1, width or image.width, height or image.height, texts or [], [], [])


def test_thick_stroke_becomes_one_centerline_not_two_outlines():
    image = Image.new("L", (400, 180), 255)
    ImageDraw.Draw(image).line((30, 60, 350, 60), fill=0, width=9)
    before = image.tobytes()
    drawings = _trace_scan(image, page_for(image))
    assert len(drawings) == 1
    assert len(drawings[0].segments) == 1
    first, last = drawings[0].segments[0].points
    assert first[1] == last[1] == 60
    assert abs(first[0] - 30) <= 5 and abs(last[0] - 350) <= 5
    assert 8 <= drawings[0].width <= 11
    assert image.tobytes() == before


def test_closed_curve_keeps_editable_connected_contour():
    image = Image.new("L", (300, 300), 255)
    ImageDraw.Draw(image).ellipse((40, 40, 240, 240), outline=0, width=5)
    drawings = _trace_scan(image, page_for(image))
    assert len(drawings) == 1 and drawings[0].closed
    segments = drawings[0].segments
    assert 12 < len(segments) < 100
    assert segments[-1].points[-1] == segments[0].points[0]
    assert all(first.points[-1] == second.points[0] for first, second in zip(segments, segments[1:]))
    # Centre of the 5 px ring lies 98 px from (140, 140).
    for segment in segments:
        for x, y in segment.points:
            assert abs(math.hypot(x - 140, y - 140) - 98) < 2


def test_short_dashes_stay_separate_and_are_not_bridged():
    image = Image.new("L", (400, 180), 255)
    draw = ImageDraw.Draw(image)
    expected_starts = list(range(20, 300, 25))
    for x in expected_starts:
        draw.line((x, 80, x + 12, 80), fill=0, width=3)
    drawings = _trace_scan(image, page_for(image))
    assert len(drawings) == len(expected_starts)
    for drawing, expected in zip(sorted(drawings, key=lambda item: item.segments[0].points[0][0]), expected_starts):
        assert len(drawing.segments) == 1
        x0, y0 = drawing.segments[0].points[0]
        x1, y1 = drawing.segments[0].points[1]
        assert abs(x0 - expected) <= 2
        assert abs(x1 - (expected + 12)) <= 2
        assert y0 == y1 == 80


def test_crossing_lines_share_single_junction_without_duplicate_edges():
    image = Image.new("L", (400, 300), 255)
    draw = ImageDraw.Draw(image)
    draw.line((30, 60, 350, 60), fill=0, width=5)
    draw.line((130, 20, 130, 250), fill=0, width=5)
    drawings = _trace_scan(image, page_for(image))
    assert len(drawings) == 2
    assert all(len(drawing.segments) == 1 for drawing in drawings)
    edges = {tuple(sorted(drawing.segments[0].points)) for drawing in drawings}
    assert len(edges) == 2
    assert any(all(x == 130 for x, y in edge) for edge in edges)
    assert any(all(y == 60 for x, y in edge) for edge in edges)


@pytest.mark.parametrize("endpoints,box", [
    ((30, 60, 350, 60), (98, 50, 128, 67)),
    ((120, 20, 120, 260), (98, 50, 135, 73)),
    ((30, 30, 250, 250), (98, 98, 131, 131)),
])
def test_removing_ocr_text_preserves_supported_line_crossing_box(endpoints, box):
    image = Image.new("L", (400, 300), 255)
    draw = ImageDraw.Draw(image)
    draw.line(endpoints, fill=0, width=3)
    draw.text((box[0] + 2, box[1] + 1), "TEST", fill=0)
    text = TextSpan("TEST", box, "Liberation Sans", 10, (0, 0, 0), from_ocr=True)
    drawings = _trace_scan(image, page_for(image, texts=[text]))
    # A continuous stroke must extend across both sides of the erased label.
    assert len(drawings) == 1
    vertices = [point for segment in drawings[0].segments for point in segment.points]
    dx, dy = endpoints[2] - endpoints[0], endpoints[3] - endpoints[1]
    for x, y in vertices:
        distance = abs(dy * (x - endpoints[0]) - dx * (y - endpoints[1])) / math.hypot(dx, dy)
        assert distance <= 1.5
    assert math.dist(vertices[0], vertices[-1]) > math.hypot(dx, dy) - 5


def test_ocr_box_does_not_erase_nearby_line_or_leave_text_geometry():
    image = Image.new("L", (300, 180), 255)
    draw = ImageDraw.Draw(image)
    draw.text((80, 50), "LABEL", fill=0)
    draw.line((30, 68, 250, 68), fill=0, width=1)
    text = TextSpan("LABEL", (80, 50, 115, 66), "Liberation Sans", 10, (0, 0, 0), from_ocr=True)
    drawings = _trace_scan(image, page_for(image, texts=[text]))
    assert len(drawings) == 1 and len(drawings[0].segments) == 1
    assert all(y == 68 for _, y in drawings[0].segments[0].points)


def test_isolated_speckle_does_not_become_cad_geometry():
    image = Image.new("L", (400, 300), 255)
    draw = ImageDraw.Draw(image)
    draw.line((30, 60, 350, 60), fill=0, width=3)
    for x in range(10, 390, 13):
        for y in range(90, 290, 13):
            draw.point((x, y), fill=0)
    drawings = _trace_scan(image, page_for(image))
    assert len(drawings) == 1 and len(drawings[0].segments) == 1


def test_drawing_coordinates_follow_physical_page_size():
    image = Image.new("L", (400, 200), 255)
    ImageDraw.Draw(image).line((20, 100, 380, 100), fill=0, width=1)
    drawings = _trace_scan(image, page_for(image, width=200, height=50))
    assert drawings[0].segments[0].points == ((10.0, 25.0), (190.0, 25.0))


def test_large_number_of_separate_features_is_not_silently_truncated():
    image = Image.new("L", (1000, 1000), 255)
    draw = ImageDraw.Draw(image)
    expected = 0
    for y in range(10, 990, 13):
        for x in range(10, 990, 14):
            draw.line((x, y, x + 7, y), fill=0, width=1)
            expected += 1
    assert expected > 5000
    drawings = _trace_scan(image, page_for(image))
    assert len(drawings) == expected


def test_vectorization_checks_cancellation_inside_work():
    image = Image.new("L", (400, 300), 255)
    ImageDraw.Draw(image).line((30, 60, 350, 60), fill=0, width=21)
    calls = 0

    def cancel():
        nonlocal calls
        calls += 1
        if calls >= 4:
            raise InterruptedError("cancelled")

    with pytest.raises(InterruptedError, match="cancelled"):
        _trace_scan(image, page_for(image), check_cancel=cancel)
    assert calls == 4


@pytest.mark.parametrize("attached", [False, True])
def test_dense_noise_cleanup_preserves_one_pixel_curve_and_short_dashes(attached):
    image = Image.new("L", (1000, 800), 255)
    draw = ImageDraw.Draw(image)
    draw.ellipse((50, 50, 250, 250), outline=0, width=1)
    if attached:
        # A thin curve attached to a thick object must survive as well as an
        # isolated thin component; rigid straight opening kernels fragment it.
        draw.line((50, 150, 50, 300), fill=0, width=5)
    draw.line((20, 400, 900, 400), fill=0, width=1)
    for x in (300, 400):
        draw.line((x, 350, x + 7, 350), fill=0, width=1)
    for y in range(500, 790, 7):
        for x in range(10, 990, 7):
            draw.point((x, y), fill=0)
    original = image.tobytes()
    page = page_for(image, width=180, height=144)  # 400 dpi geometry
    drawings = _trace_scan(image, page)
    assert len(drawings) == (5 if attached else 4)
    rings = [drawing for drawing in drawings if drawing.closed]
    assert len(rings) == 1
    assert len(rings[0].segments) >= 24
    for segment in rings[0].segments:
        for x, y in segment.points:
            # The tangent junction spans the 5 px attached stroke; elsewhere
            # the circle itself must remain accurate to the pixel grid.
            tolerance = 5 if attached and x / 0.18 < 60 and y / 0.18 > 150 else 3
            assert abs(math.hypot(x / 0.18 - 150, y / 0.18 - 150) - 100) < tolerance
    assert any("подавлен точечный шум" in warning for warning in page.warnings)
    assert image.tobytes() == original


def test_dense_junctions_do_not_puncture_long_table_rule():
    image = Image.new("L", (420, 180), 255)
    draw = ImageDraw.Draw(image)
    draw.line((20, 60, 400, 60), fill=0, width=1)
    # Neighbouring branches split the backbone into edges of just 3 pixels.
    # They are not dirt: discarding short paths lost most of the actual rule.
    for x in range(25, 395, 3):
        draw.line((x, 60, x, 70), fill=0, width=1)
    drawings = _trace_scan(image, page_for(image))
    rendered = Image.new("L", image.size, 0)
    paint = ImageDraw.Draw(rendered)
    for drawing in drawings:
        for segment in drawing.segments:
            paint.line(segment.points, fill=255, width=1)
    assert np.all(np.asarray(rendered)[60, 20:401])
    assert any(len(drawing.segments) == 1 and drawing.segments[0].points == ((20.0, 60.0), (400.0, 60.0)) for drawing in drawings)


def test_one_pixel_rule_retains_physical_stroke_width():
    image = Image.new("L", (400, 200), 255)
    ImageDraw.Draw(image).line((20, 100, 380, 100), fill=0, width=1)
    drawings = _trace_scan(image, page_for(image, width=72, height=36))
    assert len(drawings) == 1
    assert drawings[0].width == pytest.approx(72 / 400)
