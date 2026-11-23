"""Lettering drawn as filled outlines becomes text exactly once.

The outlines are made from a bundled font, as CAD programs print text to PDF:
every glyph a filled path with its counters as holes. No OCR model is needed:
without an engine the detector's reading is checked against the shapes.
"""
from __future__ import annotations

from pathlib import Path

import numpy as np
import pytest
from fontTools.pens.basePen import BasePen
from fontTools.ttLib import TTFont

from pdf2office.models import Drawing, PdfPage, Segment, TextSpan
from pdf2office.vector_text import VectorText

FONT = Path(__file__).parents[1] / "assets/fonts/LiberationSans-Regular.ttf"


class _SegmentPen(BasePen):
    def __init__(self, glyphs, transform):
        super().__init__(glyphs)
        self.transform = transform
        self.segments: list[Segment] = []
        self.start = self.current = None

    def _moveTo(self, point):
        self.start = self.current = self.transform(point)

    def _lineTo(self, point):
        end = self.transform(point)
        self.segments.append(Segment("line", (self.current, end)))
        self.current = end

    def _curveToOne(self, first, second, third):
        points = (self.current, self.transform(first), self.transform(second), self.transform(third))
        self.segments.append(Segment("cubic", points))
        self.current = points[-1]

    def _qCurveToOne(self, control, end):
        p0 = np.array(self._getCurrentPoint(), float)
        q = np.array(control, float)
        p3 = np.array(end, float)
        self._curveToOne(tuple(p0 + 2 / 3 * (q - p0)), tuple(p3 + 2 / 3 * (q - p3)), tuple(p3))

    def _closePath(self):
        if self.current != self.start:
            self.segments.append(Segment("line", (self.current, self.start)))
        self.current = self.start


_FONT = TTFont(FONT)
_CMAP = _FONT.getBestCmap()
_GLYPHS = _FONT.getGlyphSet()
_UNITS = _FONT["head"].unitsPerEm


def outline(text: str, x: float, baseline: float, size: float, gaps: dict[int, float] | None = None) -> list[Drawing]:
    """One filled Drawing per glyph, like a CAD export (y grows downwards)."""
    scale = size / _UNITS
    drawings = []
    advance = 0.0
    for index, character in enumerate(text):
        advance += (gaps or {}).get(index, 0.0)
        name = _CMAP[ord(character)]
        origin = x + advance

        def transform(point, origin=origin):
            return (origin + point[0] * scale, baseline - point[1] * scale)

        pen = _SegmentPen(_GLYPHS, transform)
        _GLYPHS[name].draw(pen)
        if pen.segments:
            drawings.append(Drawing(pen.segments, fill=(0, 0, 0), closed=True))
        advance += _FONT["hmtx"][name][0] * scale
    return drawings


def ink_box(drawings):
    points = np.array([point for drawing in drawings for segment in drawing.segments for point in segment.points])
    return points[:, 0].min(), points[:, 1].min(), points[:, 0].max(), points[:, 1].max()


def detected(text: str, box, confidence=0.96, angle=0.0) -> TextSpan:
    """A span as the text detector reports it: a slightly loose box."""
    x0, y0, x1, y1 = box
    x0, y0, x1, y1 = x0 - 1.0, y0 - 1.5, x1 + 1.0, y1 + 1.5
    quad = ((x0, y0), (x1, y0), (x1, y1), (x0, y1))
    if angle == 180:
        quad = (quad[2], quad[3], quad[0], quad[1])
    return TextSpan(text, (x0, y0, x1, y1), "Liberation Sans", y1 - y0, (0, 0, 0), from_ocr=True,
                    quad=quad, angle=angle, confidence=confidence, engine="neural")


def run(page: PdfPage, spans: list[TextSpan]) -> tuple[list[TextSpan], VectorText]:
    vector = VectorText(page)
    kept = vector.apply(spans, kinds=("fill",))
    vector.finish([])
    return kept, vector


def test_outline_text_is_replaced_once_with_its_exact_size():
    glyphs = outline("203,57", 100, 200, 12)
    page = PdfPage(1, 400, 400, [], list(glyphs), [])
    box = ink_box(glyphs)
    kept, vector = run(page, [detected("203,57", box)])
    assert [span.text for span in kept] == ["203,57"]
    assert not any(drawing.fill for drawing in page.drawings)   # no outline is left under the text
    span = kept[0]
    assert span.bbox[0] == pytest.approx(box[0], abs=0.05)
    assert span.bbox[2] == pytest.approx(box[2], abs=0.05)
    assert span.size == pytest.approx(box[3] - box[1], rel=0.02)
    assert vector.replaced == 1


def test_lettering_printed_twice_becomes_one_text_and_both_copies_go():
    glyphs = outline("Песок мелкий", 50, 100, 10)
    page = PdfPage(1, 400, 400, [], glyphs + outline("Песок мелкий", 50, 100, 10), [])
    kept, _ = run(page, [detected("Песок мелкий", ink_box(glyphs))])
    assert [span.text for span in kept] == ["Песок мелкий"]
    assert not page.drawings


def test_rings_of_a_hatch_pattern_are_not_read_as_zeros():
    rings = []
    rng = np.random.default_rng(3)
    for _ in range(30):
        cx, cy = rng.uniform(60, 180), rng.uniform(60, 180)
        rings.extend(outline("o", cx, cy, 9))
    page = PdfPage(1, 300, 300, [], list(rings), [])
    spans = [detected("0", ink_box([ring]), confidence=0.97) for ring in rings[:4]]
    kept, vector = run(page, spans)
    assert kept == []
    assert len(page.drawings) == len(rings)          # the hatch keeps every ring
    assert any(reason == "штриховка" for reason, _, _ in vector.rejections)


def test_stroked_circles_are_never_letter_outlines():
    circle = Drawing([Segment("cubic", ((10, 0), (10, 5.5), (5.5, 10), (0, 10))),
                      Segment("cubic", ((0, 10), (-5.5, 10), (-10, 5.5), (-10, 0)))],
                     stroke=(0, 0, 0), width=0.5)
    page = PdfPage(1, 100, 100, [], [circle] + outline("5", 30, 50, 10), [])
    vector = VectorText(page)
    letters = vector.outline_letters()
    assert all(page.drawings[vector.drawing[index]].fill is not None for index in letters)


def test_number_separators_come_from_the_drawing():
    glyphs = outline("0,6", 100, 100, 10)
    page = PdfPage(1, 300, 300, [], list(glyphs), [])
    kept, _ = run(page, [detected("06", ink_box(glyphs))])     # the reader lost the comma
    assert [span.text for span in kept] == ["0,6"]
    assert not page.drawings                                     # the comma is not left behind


def test_roman_numerals_are_counted_on_the_letters():
    glyphs = outline("РАЗРЕЗ III-III", 40, 100, 14)
    page = PdfPage(1, 400, 300, [], list(glyphs), [])
    kept, _ = run(page, [detected("РАЗРЕЗ II-III", ink_box(glyphs))])
    assert [span.text for span in kept] == ["РАЗРЕЗ III-III"]


def test_labels_far_apart_become_separate_texts():
    size = 8
    glyphs = outline("80 100 120", 40, 100, size, gaps={2: 20.0, 6: 20.0})
    page = PdfPage(1, 300, 300, [], list(glyphs), [])
    kept, _ = run(page, [detected("80 100 120", ink_box(glyphs))])
    assert sorted(span.text for span in kept) == ["100", "120", "80"]
    xs = sorted(span.bbox[0] for span in kept)
    assert xs[1] - xs[0] > 25 and xs[2] - xs[1] > 25          # each at its own place


def test_a_phrase_without_matching_letters_is_dropped_not_duplicated():
    glyphs = outline("ГРУНТ", 40, 100, 12)
    page = PdfPage(1, 300, 300, [], list(glyphs), [])
    kept, vector = run(page, [detected("ГРУНТ", (200, 200, 260, 212))])  # nothing drawn there
    assert kept == []
    assert len(page.drawings) == len(glyphs)
    assert vector.rejected == 1
