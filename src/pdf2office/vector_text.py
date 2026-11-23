"""Replace lettering drawn as vector geometry with editable text, never twice.

CAD programs often print text to PDF as outlines (small filled polygons per
letter) or as single-line strokes (SHX fonts). Such a page has no text layer,
so PDF Master reads the lettering with OCR and then matches every recognised
phrase with the vector sub-paths that drew its letters:

* the matched sub-paths are removed and the phrase becomes one TEXT entity,
  placed on the exact ink extent of those letters (size, angle, colour);
* the phrase is re-read from a clean rasterisation of only its own letters,
  so crossing lines, circles and hatching cannot disturb the reading, and the
  letters are trimmed to the characters actually read (a neighbouring ▼
  marker or the line above is never taken along);
* the reading direction is checked against the letter shapes: digits stacked
  in a column are separate upright numbers, not a vertical word;
* outline lettering is read from an image of the filled letters alone. Lines,
  scale ticks and hatch symbols (strokes) are absent from that image, so they
  cannot be mistaken for digits;
* a phrase whose letters cannot be found is discarded: it would either
  duplicate lettering that stays as geometry, or invent text from hatching.

Lettering that is not recognised confidently stays as the original geometry.
The drawing therefore never shows the same text twice and never loses ink.
"""
from __future__ import annotations

from dataclasses import dataclass
import math
import re
import unicodedata

import cv2
import numpy as np

from .models import Drawing, PdfPage, TextSpan
from .text_quality import fix_mixed_scripts

CLEAN_HEIGHT = 44.0      # pixels of glyph ink height in the clean re-reading
# Pieces of one letter are printed one right after another; touching pieces
# printed further apart belong to different objects (a ▼ marker or the label
# above that happens to touch this letter).
LETTER_ORDER_GAP = 8
SNAP_DEGREES = 4.0       # vector lettering is almost always at multiples of 90°
LAYER_GLYPH_PIXELS = 17  # letter height for finding lines; the reading uses a sharper image
LAYER_BUDGET = 60_000_000


def _same(a, b) -> bool:
    return abs(a[0] - b[0]) <= 1e-6 and abs(a[1] - b[1]) <= 1e-6


def _flatten(segments) -> np.ndarray:
    points = [segments[0].points[0]]
    for segment in segments:
        if segment.kind == "cubic":
            p0, p1, p2, p3 = (np.asarray(point, float) for point in segment.points)
            for t in (0.125, 0.25, 0.375, 0.5, 0.625, 0.75, 0.875, 1.0):
                s = 1 - t
                points.append(tuple(s * s * s * p0 + 3 * s * s * t * p1 + 3 * s * t * t * p2 + t * t * t * p3))
        else:
            points.append(segment.points[-1])
    return np.asarray(points, float)


def _touching_groups(boxes: np.ndarray, tolerance: np.ndarray, order_index: np.ndarray | None = None) -> np.ndarray:
    """Group label per box; boxes that touch (directly or via others) share it.

    With ``order_index`` (drawing order), only boxes printed within
    LETTER_ORDER_GAP of each other are joined.
    """
    count = len(boxes)
    parent = np.arange(count)

    def root(index: int) -> int:
        while parent[index] != index:
            parent[index] = parent[parent[index]]
            index = parent[index]
        return index

    order = np.argsort(boxes[:, 0], kind="stable")
    starts = boxes[order, 0]
    for position, index in enumerate(order):
        box = boxes[index]
        end = int(np.searchsorted(starts, box[2] + tolerance[index], side="right"))
        if end <= position + 1:
            continue
        others = order[position + 1:end]
        reach = np.maximum(tolerance[index], tolerance[others])
        near = (boxes[others, 1] <= box[3] + reach) & (box[1] <= boxes[others, 3] + reach)
        if order_index is not None:
            near &= np.abs(order_index[others] - order_index[index]) <= LETTER_ORDER_GAP
        hits = others[near]
        for other in hits:
            first, second = root(int(index)), root(int(other))
            if first != second:
                parent[first] = second
    return np.array([root(index) for index in range(count)], int)


def _group_words(glyphs: list, seed_min: float) -> list[list[int]]:
    """Group upright letters into words: indices into ``glyphs`` per word.

    ``glyphs`` are (members, (x0, y0, x1, y1)) page boxes of single letters.
    Letters at least ``seed_min`` tall on one line (half their height in
    common, heights within 1:1.8) and closer than 0.6 of their height form a
    word. Smaller marks (commas, periods, subscripts, diacritics) are taken
    in reading order, so "2,1" stays one word although the comma widens the
    gap between "2" and "1"; a mark next to no word stays out.
    """
    order = sorted(range(len(glyphs)), key=lambda index: glyphs[index][1][0])
    words: list[dict] = []
    pending = []
    for index in order:
        x0, y0, x1, y1 = glyphs[index][1]
        h = y1 - y0
        target = None
        if h >= seed_min:
            for word in reversed(words):
                if word["x1"] < x0 - 3 * word["h"]:
                    continue
                gap = x0 - word["x1"]
                overlap = min(word["c1"], y1) - max(word["c0"], y0)
                if -0.2 * h <= gap <= 0.6 * max(h, word["h"]) and overlap >= 0.5 * min(h, word["c1"] - word["c0"]) \
                        and 0.55 <= h / word["h"] <= 1.8:
                    target = word
                    break
        else:
            # A mark over one of a word's letters is its diacritic; otherwise
            # it belongs to the line it hangs from (a comma's top is at its
            # own baseline), not to the line just below it.
            centre = (y0 + y1) / 2
            best = None
            for word in words:
                if word["x1"] < x0 - 3 * word["h"] or x0 - word["x1"] > 0.6 * word["h"]:
                    continue
                if not word["c0"] - 0.6 * word["h"] <= centre <= word["c1"] + 0.4 * word["h"]:
                    continue
                # A comma (taller than wide) always hangs from a baseline; a
                # breve or a dot right over a letter may be its diacritic.
                above = h <= 1.6 * max(x1 - x0, 1e-6) and any(
                    glyphs[item][1][0] - 0.1 * word["h"] <= (x0 + x1) / 2 <= glyphs[item][1][2] + 0.1 * word["h"]
                    and y1 <= glyphs[item][1][1] + 0.2 * word["h"] for item in word["items"])
                score = abs(y0 - word["c1"]) / word["h"]
                if above:
                    score = min(score, 0.3)
                if best is None or score < best[0]:
                    best = (score, word)
            if best is not None:
                target = best[1]
        if target is None:
            if h >= seed_min:
                words.append({"items": [index], "x0": x0, "x1": x1, "c0": y0, "c1": y1, "h": h})
            else:
                pending.append(index)
            continue
        target["items"].append(index)
        target["x0"], target["x1"] = min(target["x0"], x0), max(target["x1"], x1)
        if h >= seed_min:
            target["c0"], target["c1"] = min(target["c0"], y0), max(target["c1"], y1)
            target["h"] = max(target["h"], h)
    for index in pending:
        x0, y0, x1, y1 = glyphs[index][1]
        cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
        best, distance = None, None
        for word in words:
            h = word["h"]
            if word["x0"] - 0.6 * h <= cx <= word["x1"] + 0.5 * h and word["c0"] - 0.6 * h <= cy <= word["c1"] + 0.4 * h:
                d = abs(cx - (word["x0"] + word["x1"]) / 2) + abs(cy - (word["c0"] + word["c1"]) / 2)
                if distance is None or d < distance:
                    best, distance = word, d
        if best is not None:
            best["items"].append(index)
    return [word["items"] for word in words]


def _dotted(text: str) -> bool:
    """True if the text has letters with separate marks (i, j, й, ё, accents)."""
    return any(c in "ijіїйёЙЁЇўЎ" or unicodedata.normalize("NFD", c) != c for c in text)


# Characters that rings, dots and loops of a hatch pattern are read as.
HATCH_LIKE = set("0OoОо8ВBQDCcСс°")


def _same_shape(a, b) -> bool:
    """Two shape signatures (size, holes, aspect, fill) of one pattern."""
    return (a[1] == b[1] and 0.6 <= a[0] / max(b[0], 1e-6) <= 1.6 and abs(a[2] - b[2]) <= 0.25
            and abs(a[3] - b[3]) <= 0.15)


def _hatch_like_text(text: str) -> bool:
    alnum = [c for c in text if c.isalnum()]
    return 1 <= len(alnum) <= 2 and all(c in HATCH_LIKE for c in alnum)


# Single characters that hatching, ticks and symbols imitate: circles, bars,
# crosses and chevrons. Never recovered from unmatched geometry on their own.
SYMBOL_LIKE = set("0OoОо1Il|іІ!xXхХ+vV<>^-–—_=.,:;'\"`/\\")


@dataclass
class _Frame:
    origin: np.ndarray
    u: np.ndarray
    v: np.ndarray
    length: float
    height: float
    angle: float

    def local(self, points: np.ndarray) -> np.ndarray:
        relative = points - self.origin
        return np.stack([relative @ self.u, relative @ self.v], axis=1)


def _frame_from(quad: np.ndarray, text: str) -> _Frame | None:
    direction = quad[1] - quad[0]
    length = float(np.hypot(*direction))
    height = float(np.hypot(*(quad[3] - quad[0])))
    if length <= 0 or height <= 0:
        return None
    angle = math.degrees(math.atan2(direction[1], direction[0]))
    # Lettering in drawings is horizontal or vertical unless clearly
    # inclined; short phrases give noisy angles and snap more readily.
    nearest = round(angle / 90) * 90
    alnum = sum(c.isalnum() for c in text)
    # One or two characters have no measurable direction of their own (the
    # diagonal of a "4" tilts its box); longer phrases snap when nearly square.
    limit = 45.0 if alnum <= 2 else SNAP_DEGREES * (2 if len(text.strip()) <= 4 else 1)
    if abs(angle - nearest) <= limit:
        angle = float(nearest)
    radians = math.radians(angle)
    u = np.array((math.cos(radians), math.sin(radians)))
    v = np.array((-u[1], u[0]))
    centre = quad.mean(axis=0)
    origin = centre - u * length / 2 - v * height / 2
    return _Frame(origin, u, v, length, height, angle)


def _turn(frame: _Frame) -> _Frame:
    """The same box read the other way round (turned by 180 degrees)."""
    origin = frame.origin + frame.u * frame.length + frame.v * frame.height
    angle = frame.angle - 180 if frame.angle > 0 else frame.angle + 180
    return _Frame(origin, -frame.u, -frame.v, frame.length, frame.height, float(angle))


def _frame(span: TextSpan) -> _Frame | None:
    if span.quad is not None:
        quad = np.asarray(span.quad, float)
    else:
        x0, y0, x1, y1 = span.bbox
        quad = np.array([(x0, y0), (x1, y0), (x1, y1), (x0, y1)], float)
    return _frame_from(quad, span.text)


def plausible_text(text: str, kind: str, confidence: float, agreed: bool) -> bool:
    """Reject readings that are typical of hatching, ticks and symbols."""
    compact = re.sub(r"\s+", "", text)
    alnum = [c for c in compact if c.isalnum()]
    if not alnum:
        return False
    letters = [c for c in compact if c.isalpha()]
    # Rows of identical marks are scale ticks or hatching ("шшш", "ZZZZ").
    if len(compact) >= 3 and len(set(compact)) == 1 and not compact.isdigit():
        return False
    if len(letters) >= 3 and len(set(c.lower() for c in letters)) == 1:
        return False
    if kind == "stroke":
        # Single-line lettering looks like hatching; demand strong evidence.
        return len(alnum) >= 2 and confidence >= (0.85 if agreed else 0.97)
    if len(alnum) == 1:
        character = alnum[0]
        if agreed and character.isalpha() and character not in SYMBOL_LIKE and character not in HATCH_LIKE:
            # A letter no symbol imitates ("П", "Г"), read alike by both readers.
            return confidence >= 0.5
        return confidence >= (0.6 if agreed else 0.9)
    return confidence >= 0.5


@dataclass
class _Match:
    span: TextSpan | None
    members: np.ndarray
    frame: _Frame
    kind: str
    bounds: tuple


class VectorText:
    """Matches recognised phrases with the vector letters of one page."""

    def __init__(self, page: PdfPage):
        self.page = page
        drawing_ids, starts, stops, boxes, filled, stroked, points = [], [], [], [], [], [], []
        for number, drawing in enumerate(page.drawings):
            segments = drawing.segments
            if not segments:
                continue
            start = 0
            for index in range(1, len(segments) + 1):
                if index < len(segments) and _same(segments[index - 1].points[-1], segments[index].points[0]):
                    continue
                flat = _flatten(segments[start:index])
                drawing_ids.append(number)
                starts.append(start)
                stops.append(index)
                boxes.append((flat[:, 0].min(), flat[:, 1].min(), flat[:, 0].max(), flat[:, 1].max()))
                filled.append(drawing.fill is not None)
                stroked.append(drawing.stroke is not None)
                points.append(flat)
                start = index
        self.drawing = np.asarray(drawing_ids, int)
        self.start = np.asarray(starts, int)
        self.stop = np.asarray(stops, int)
        self.box = np.asarray(boxes, float).reshape(-1, 4)
        self.filled = np.asarray(filled, bool)
        self.stroked = np.asarray(stroked, bool)
        self.points = points
        self.claimed = np.zeros(len(self.drawing), bool)
        # Some CAD exports print lettering twice: identical outlines on top
        # of each other. A copy ("shadow") is never matched on its own; it is
        # removed together with its first copy, so it can neither become a
        # second text nor stay behind as a filled hatch under the text.
        self.twin_of = np.arange(len(self.drawing))
        if len(self.drawing):
            counts = np.array([len(flat) for flat in points], float)
            key = np.concatenate([np.round(self.box, 2), counts[:, None], self.filled[:, None]], axis=1)
            _, inverse, sizes = np.unique(key, axis=0, return_inverse=True, return_counts=True)
            inverse = inverse.reshape(-1)
            first: dict[int, int] = {}
            for index in np.flatnonzero(sizes[inverse] > 1):
                group = int(inverse[index])
                primary = first.setdefault(group, int(index))
                if primary != index and np.allclose(points[primary], points[index], atol=0.01):
                    self.twin_of[index] = primary
        self.shadow = self.twin_of != np.arange(len(self.drawing))
        # Drawing order counted without the copies, so the pieces of one
        # text stay consecutive although each letter was printed twice.
        real = np.unique(self.drawing[~self.shadow]) if len(self.drawing) else np.zeros(0, int)
        self.order = np.searchsorted(real, self.drawing) if len(self.drawing) else np.zeros(0, int)
        self.widths = {number: drawing.width for number, drawing in enumerate(page.drawings)}
        self.colors = {number: (drawing.fill if drawing.fill is not None else drawing.stroke)
                       for number, drawing in enumerate(page.drawings)}
        self.replaced = 0
        self.rejected = 0
        # (reason, text, bbox) of every phrase left as geometry, for reports.
        self.rejections: list[tuple[str, str, tuple]] = []
        # Letter pieces of every accepted phrase (diagnostics and tests).
        self.accepted: list[tuple[TextSpan, np.ndarray]] = []

    # ------------------------------------------------------ outline lettering
    def outline_letters(self) -> np.ndarray:
        """Indices of small filled sub-paths: the letters of outline text.

        Filled bars, frames and large symbols are excluded by size; filled
        arrows or triangles may be included but are only ever replaced when
        the reading also passes the shape and plausibility checks.
        """
        if not len(self.drawing):
            return np.zeros(0, int)
        sizes = np.maximum(self.box[:, 2] - self.box[:, 0], self.box[:, 3] - self.box[:, 1])
        return np.flatnonzero(self.filled & (sizes > 0.3) & (sizes <= 60.0) & ~self.shadow)

    def render(self, members: np.ndarray) -> tuple[np.ndarray, float] | None:
        """Grayscale image of the given filled sub-paths alone, and its DPI."""
        if not len(members):
            return None
        heights = self.box[members, 3] - self.box[members, 1]
        # Letters are printed as several pieces; the upper decile of piece
        # heights is close to the full letter height.
        typical = float(np.percentile(heights, 90))
        dpi = LAYER_GLYPH_PIXELS * 72 / max(typical, 0.5)
        area = max(self.page.width * self.page.height, 1.0)
        dpi = min(400.0, max(100.0, dpi), 72 * math.sqrt(LAYER_BUDGET / area))
        scale = dpi / 72
        width = max(32, int(math.ceil(self.page.width * scale)))
        height = max(32, int(math.ceil(self.page.height * scale)))
        canvas = np.full((height, width), 255, np.uint8)
        by_drawing: dict[int, list[np.ndarray]] = {}
        for member in members:
            by_drawing.setdefault(int(self.drawing[member]), []).append(
                np.round(self.points[member] * scale * 16).astype(np.int32))
        for contours in by_drawing.values():
            cv2.fillPoly(canvas, contours, 0, lineType=cv2.LINE_AA, shift=4)
        return canvas, dpi

    # ---------------------------------------------------------------- matching
    def apply(self, spans: list[TextSpan], *, kinds=("fill", "stroke"), keep_boxes=(), engine=None,
              environment=None, language: str = "rus+eng", messages: list[str] | None = None,
              check_cancel=None, progress=None) -> list[TextSpan]:
        """Return the spans to keep and claim their letters for removal."""
        if not spans:
            return []
        keep_boxes = [tuple(box) for box in keep_boxes]

        def kept_area(span: TextSpan) -> bool:
            cx = (span.bbox[0] + span.bbox[2]) / 2
            cy = (span.bbox[1] + span.bbox[3]) / 2
            return any(x0 <= cx <= x1 and y0 <= cy <= y1 for x0, y0, x1, y1 in keep_boxes)

        confident = [span.size for span in spans
                     if (span.confidence or 0) >= 0.9 and sum(c.isalnum() for c in span.text) >= 2]
        typical = float(np.median(confident)) if confident else float(np.median([span.size for span in spans]))
        # Every phrase is matched and read independently first; letters are
        # then given to the best readings (no phrase can take letters away
        # from another before it has itself been verified), so a letter is
        # never part of two texts and a rejected phrase steals nothing.
        result: list[TextSpan] = []
        groups: list[tuple[list[list[_Match]], TextSpan]] = []
        for count, span in enumerate(spans):
            if check_cancel and count % 64 == 0:
                check_cancel()
            if kept_area(span):
                result.append(span)
                continue
            found = self._match(span, kinds, typical)
            if found is None:
                self.rejected += 1
                self.rejections.append(("нет букв", span.text, tuple(span.bbox)))
                continue
            angle = found.frame.angle
            square = abs(angle - round(angle / 90) * 90) < 1e-6
            if abs(abs(angle) - 90) < 1e-6 and self._sideways(found):
                # Upright letters stacked in a column (circled numbers one
                # above another) are separate horizontal labels.
                options = [self._regroup(found)]
            elif not square:
                # An inclined box may join two lines or a letter and its
                # subscript. Upright letters (their boxes are tighter along
                # the page axes than along the inclined line) are regrouped
                # into horizontal labels; truly inclined lettering is kept.
                tilt = self._tilt_evidence(found)
                if tilt > 1.05:
                    options = [self._regroup(found)]
                elif tilt < 0.95:
                    options = [[found]]
                else:
                    options = [[found], self._regroup(found)]
            elif abs(angle) < 1e-6 and self._lines(found) > 1:
                # Two labels one above the other read as one line ("0,2"
                # over "0,5"): compare with reading each line separately.
                options = [[found], self._regroup(found)]
            else:
                options = [[found]]
                runs = self._runs(found)
                if len(runs) > 1:
                    # Separately drawn objects next to each other ("8,1" and
                    # a circled "М", a ▼ marker): compare reading them apart.
                    options.append(runs)
            weights = [1.0] * len(options)
            original = _frame(span)
            if original is not None and abs(abs(original.angle) - 180) < 1e-6:
                # Read upside down by the detector: the upright reading above
                # is compared with the detector's orientation, which must be
                # clearly better to win, as text on drawings is rarely upside
                # down and digits turned over are digits again.
                turned = self._match(span, kinds, typical, upright=False)
                if turned is not None:
                    options.insert(0, [turned])
                    weights.insert(0, 0.8)
            kept_options = [(option, weight) for option, weight in zip(options, weights) if option]
            if not kept_options:
                self.rejected += 1
                self.rejections.append(("пересборка", span.text, tuple(span.bbox)))
                continue
            groups.append(([option for option, _ in kept_options], span, [weight for _, weight in kept_options]))
        if not groups:
            return result
        flat = [match for options, _, _ in groups for option in options for match in option]
        crops = [self._clean_image(match) for match in flat]
        readings: list = [None] * len(flat)
        if engine is not None:
            if progress:
                progress(f"Векторные надписи: уточнение {len(flat)} фраз по контурам букв")
            try:
                from .recognition import read_crops
                readings = read_crops(engine, crops, environment, language, check_cancel=check_cancel)
            except (ValueError, OSError) as error:  # Tesseract failure: keep the first reading
                if messages is not None:
                    messages.append(f"Уточнение векторных надписей пропущено: {error}")
        position = 0
        proposals = []
        for options, span, weights in groups:
            evaluated = []
            for option, weight in zip(options, weights):
                decided = []
                for match in option:
                    decided.append(self._decide(match, crops[position], readings[position]))
                    position += 1
                accepted = [item for item in decided if item is not None]
                score = weight * sum(self._quality(item[0]) for item in accepted)
                evaluated.append((score, len(evaluated), accepted))
            # Higher score wins; on a tie the later, axis-aligned option.
            _, _, accepted = max(evaluated, key=lambda item: (item[0], item[1]))
            if not accepted:
                self.rejected += 1
                self.rejections.append(("проверка", span.text, tuple(span.bbox)))
                continue
            proposals.extend(accepted)
        proposals.sort(key=lambda item: (-self._quality(item[0]), -len(item[1])))
        taken = []
        for proposal_span, members in proposals:
            if self.claimed[members].any():
                # The same letters are already part of a better reading.
                self.rejected += 1
                self.rejections.append(("перекрытие", proposal_span.text, tuple(proposal_span.bbox)))
                continue
            self.claimed[members] = True
            taken.append((proposal_span, members))
        for proposal_span, members in self._without_patterns(taken):
            result.append(proposal_span)
            self.accepted.append((proposal_span, members))
            self.replaced += 1
        return result

    def _without_patterns(self, taken: list) -> list:
        """Drop "0", "O" or "8" readings that are part of a hatch pattern.

        A ring of a gravel hatch reads as "0" or "O", two touching rings as
        "8". Such a short reading is kept only if it is not surrounded by
        similar unread shapes: printed labels stand among lines and other
        text, a hatch symbol among more of its kind.
        """
        suspicious = [index for index, (span, _) in enumerate(taken) if _hatch_like_text(span.text)]
        if not suspicious:
            return taken
        free = ~self.claimed
        for index in suspicious:
            free[taken[index][1]] = True
        dropped = set()
        for index in suspicious:
            span, members = taken[index]
            if self._similar_shapes(members, free) >= 4:
                dropped.add(index)
                self.claimed[members] = False
                self.rejected += 1
                self.rejections.append(("штриховка", span.text, tuple(span.bbox)))
        return [item for index, item in enumerate(taken) if index not in dropped]

    def _similar_shapes(self, members: np.ndarray, free: np.ndarray) -> int:
        """Unread filled shapes around ``members`` that look like its parts.

        Compared are the number of holes, the proportions and how much of
        its box the shape fills: the rings of a gravel hatch match each
        other, while a marker, a filled column or another letter does not.
        A closed ring printed as a path of its own is also compared alone,
        so two touching rings are two rings, not one "8".
        """
        b = self.box[members]
        x0, y0, x1, y1 = b[:, 0].min(), b[:, 1].min(), b[:, 2].max(), b[:, 3].max()
        size = max(x1 - x0, y1 - y0, 1e-6)
        own = self._shape_signatures(members, size)
        reach = 6 * size
        box = self.box
        near = np.flatnonzero((box[:, 2] >= x0 - reach) & (box[:, 0] <= x1 + reach) & (box[:, 3] >= y0 - reach)
                              & (box[:, 1] <= y1 + reach) & self.filled & ~self.shadow & free)
        near = near[~np.isin(near, members)]
        if not len(near):
            return 0
        sizes = np.maximum(box[near, 2] - box[near, 0], box[near, 3] - box[near, 1])
        near = near[sizes <= 2.0 * size]
        if not len(near):
            return 0
        labels = _touching_groups(box[near], np.full(len(near), 0.05 + 0.005 * size), self.order[near])
        count = 0
        for label in np.unique(labels):
            group = near[labels == label]
            theirs = self._shape_signatures(group, size)
            if any(_same_shape(a, b) for a in theirs for b in own):
                count += 1
        return count

    def _shape_signatures(self, members: np.ndarray, size: float) -> list:
        """Signatures of a shape: as a whole, per letter, per closed path."""
        signatures = [self._signature(members)]
        b = self.box[members]
        labels = _touching_groups(b, np.full(len(members), 0.05 + 0.005 * size), self.order[members])
        if len(np.unique(labels)) > 1:
            signatures += [self._signature(members[labels == label]) for label in np.unique(labels)]
        drawings = self.drawing[members]
        if len(np.unique(drawings)) > 1:
            for number in np.unique(drawings):
                signature = self._signature(members[drawings == number])
                if signature[1] >= 1:  # a closed ring of its own
                    signatures.append(signature)
        return signatures

    def _signature(self, members) -> tuple[float, int, float, float]:
        """(size, holes, width/height, filled share of the box) of a shape."""
        b = self.box[members]
        x0, y0, x1, y1 = b[:, 0].min(), b[:, 1].min(), b[:, 2].max(), b[:, 3].max()
        w, h = max(x1 - x0, 1e-6), max(y1 - y0, 1e-6)
        scale = 40.0 / max(w, h)
        canvas = np.zeros((int(math.ceil(h * scale)) + 4, int(math.ceil(w * scale)) + 4), np.uint8)
        by_drawing: dict[int, list[np.ndarray]] = {}
        for member in members:
            local = (self.points[member] - (x0, y0)) * scale + 2
            by_drawing.setdefault(int(self.drawing[member]), []).append(np.round(local * 16).astype(np.int32))
        for contours in by_drawing.values():
            layer = np.zeros_like(canvas)
            cv2.fillPoly(layer, contours, 255, shift=4)
            canvas |= layer
        contours, hierarchy = cv2.findContours(canvas, cv2.RETR_CCOMP, cv2.CHAIN_APPROX_SIMPLE)
        holes = 0 if hierarchy is None else int(np.sum(hierarchy[0][:, 3] >= 0))
        fill = float(np.count_nonzero(canvas)) / max(w * h * scale * scale, 1e-6)
        return max(w, h), holes, w / h, fill

    @staticmethod
    def _quality(span: TextSpan) -> float:
        agreed = 1.1 if "tesseract" in (span.engine or "") else 1.0
        return (span.confidence or 0) * agreed * max(1, sum(c.isalnum() for c in span.text))

    def _extents(self, members, frame: _Frame) -> np.ndarray:
        extents = np.empty((len(members), 4))
        for row, member in enumerate(members):
            local = frame.local(self.points[member])
            extents[row] = (local[:, 0].min(), local[:, 1].min(), local[:, 0].max(), local[:, 1].max())
        return extents

    def _select(self, frame: _Frame, kinds):
        """Letter pieces of one phrase frame: (members, bounds, kind) or None."""
        h, length = frame.height, frame.length
        corners = np.array([frame.origin, frame.origin + frame.u * length,
                            frame.origin + frame.u * length + frame.v * h, frame.origin + frame.v * h])
        reach = 0.45 * h + 1.5
        x0, y0 = corners.min(axis=0) - reach
        x1, y1 = corners.max(axis=0) + reach
        box = self.box
        candidates = np.flatnonzero((box[:, 0] >= x0) & (box[:, 2] <= x1) & (box[:, 1] >= y0)
                                    & (box[:, 3] <= y1) & ~self.claimed & ~self.shadow)
        if not len(candidates):
            return None
        extents = self._extents(candidates, frame)
        a0, b0, a1, b1 = extents.T
        centre_u, centre_v = (a0 + a1) / 2, (b0 + b1) / 2
        # A letter of this phrase has its centre on the phrase's own band; the
        # line above or below and a marker next to it do not.
        # Letters are centred on the band; a separate comma, period or quote
        # sits at its edge. Only an isolated small mark (touching no other
        # piece) may be centred a little outside: fragments of the letters
        # of the line above or below always touch their own siblings.
        in_band = (centre_v >= 0.05 * h) & (centre_v <= 0.95 * h)
        edge = ((b1 - b0) <= 0.45 * h) & ~in_band & (centre_v >= -0.15 * h) & (centre_v <= 1.2 * h)
        if edge.any():
            # Only a small mark as a whole (a comma, a breve of two pieces)
            # may sit at the edge; a piece of a bigger letter of the line
            # above or below touches the rest of that letter.
            boxes = self.box[candidates]
            labels = _touching_groups(boxes, np.full(len(candidates), 0.005 * h + 0.05), self.order[candidates])
            for index in np.flatnonzero(edge):
                group = extents[labels == labels[index]]
                if group[:, 3].max() - group[:, 1].min() > 0.45 * h or group[:, 2].max() - group[:, 0].min() > 1.2 * h:
                    edge[index] = False
        fits = ((centre_u >= -0.1 * h) & (centre_u <= length + 0.1 * h)
                & (in_band | edge)
                & (a0 >= -0.2 * h - 0.5) & (a1 <= length + 0.2 * h + 0.5)
                & (b0 >= -0.25 * h - 0.8) & (b1 <= 1.3 * h + 0.8)
                & (a1 - a0 <= 1.6 * h + 1.0) & (b1 - b0 <= 1.25 * h + 0.8))
        if not fits.any():
            return None
        filled = self.filled[candidates] & fits
        stroked = self.stroked[candidates] & ~self.filled[candidates] & fits
        kind = "fill" if filled.sum() >= stroked.sum() else "stroke"
        if kind not in kinds:
            return None
        chosen = filled if kind == "fill" else stroked
        if kind == "stroke":
            # Strokes get a tighter band: hatch marks touch stroke lettering.
            chosen &= (a0 >= -0.15 * h - 0.6) & (a1 <= length + 0.15 * h + 0.6) \
                & (b0 >= -0.1 * h - 0.6) & (b1 <= h + 0.1 * h + 0.6)
        # Pieces touching a selected letter belong to it (the tail of "у",
        # the descender of "р", the right half of a "0" printed in two
        # halves), even when they reach past the detector's approximate box.
        loose = ((centre_u >= -0.3 * h) & (centre_u <= length + 0.3 * h)
                 & (a0 >= -0.6 * h - 0.5) & (a1 <= length + 0.6 * h + 0.5)
                 & (b0 >= -0.35 * h - 1.0) & (b1 <= 1.4 * h + 1.0)
                 & (a1 - a0 <= 1.6 * h + 1.0) & (b1 - b0 <= 1.25 * h + 0.8))
        same_kind = self.filled[candidates] if kind == "fill" else (self.stroked[candidates] & ~self.filled[candidates])
        tolerance = 0.005 * h + 0.05
        for _ in range(2):
            extra = loose & same_kind & ~chosen
            if not extra.any() or not chosen.any():
                break
            picked = self.box[candidates[chosen]]
            added = False
            for index in np.flatnonzero(extra):
                b = self.box[candidates[index]]
                if np.any((picked[:, 0] <= b[2] + tolerance) & (b[0] <= picked[:, 2] + tolerance)
                          & (picked[:, 1] <= b[3] + tolerance) & (b[1] <= picked[:, 3] + tolerance)):
                    chosen[index] = True
                    added = True
            if not added:
                break
        members = candidates[chosen]
        if not len(members):
            return None
        sub = extents[chosen]
        bounds = (sub[:, 0].min(), sub[:, 1].min(), sub[:, 2].max(), sub[:, 3].max())
        return members, bounds, kind

    def _match(self, span: TextSpan, kinds, typical: float, *, upright: bool = True) -> _Match | None:
        frame = _frame(span)
        if frame is None or not len(self.drawing):
            return None
        if upright and abs(abs(frame.angle) - 180) < 1e-6:
            # Text on drawings is rarely upside down; digits read upside
            # down are digits again ("0,6" turned reads "9,0").
            frame = _turn(frame)
        selected = self._select(frame, kinds)
        if selected is None:
            return None
        # Re-anchor the frame on the letters found and select again: the
        # detector's box is only approximate, the letters themselves are exact.
        members, bounds, kind = selected
        a0, b0, a1, b1 = bounds
        refined = _Frame(frame.origin + frame.u * a0 + frame.v * b0, frame.u, frame.v,
                         max(a1 - a0, 1e-6), max(b1 - b0, 1e-6), frame.angle)
        again = self._select(refined, (kind,))
        if again is not None and len(again[0]) >= 0.8 * len(members):
            members, bounds, kind = again
            frame = refined
        members, bounds = self._continue_word(members, frame, kind, bounds)
        length, h = frame.length, frame.height
        alnum = sum(c.isalnum() for c in span.text)
        agreed = "tesseract" in (span.engine or "")
        original = _frame(span)
        if (bounds[2] - bounds[0]) < 0.7 * min(length, original.length) or (bounds[3] - bounds[1]) < 0.45 * h:
            return None
        if len(members) < max(1, math.ceil(0.5 * alnum)) or len(members) > 14 * max(1, len(span.text)):
            return None
        if h < 0.35 * typical and not ((span.confidence or 0) >= 0.95 and agreed):
            return None
        # Outline letters are re-read cleanly before the final decision, so
        # only obvious patterns are refused here; strokes need strong proof.
        if kind == "fill":
            if not plausible_text(span.text, kind, max(span.confidence or 0, 0.9), True):
                return None
        elif not plausible_text(span.text, kind, span.confidence or 0, agreed):
            return None
        # A tall detector box also covers the bottom of the line above (its
        # comma, the tip of a ▼ marker): keep only this line's letter band,
        # so those pieces are neither read nor removed with this phrase.
        refined = self._refine_band(_Match(span, members, frame, kind, bounds))
        return self._absorb_marks(refined) if refined is not None else None

    def _continue_word(self, members: np.ndarray, frame: _Frame, kind: str, bounds):
        """Add letters right at the phrase ends, closer than a word space.

        The detector's box sometimes stops short of the last letter (the
        bar of "ы", the right half of a "0"); a letter that close to the
        phrase, standing on the same baseline, belongs to it. The line's
        band is measured once on the letters already found, so the phrase
        can never grow into the line above or below.
        """
        probe = _Match(None, members, frame, kind, bounds)
        clusters = self._clusters(probe)
        heights = sorted(ext[3] - ext[1] for _, _, ext in clusters)
        if not heights:
            return members, bounds
        reference = heights[int(0.75 * (len(heights) - 1))]
        main = [ext for _, _, ext in clusters if 0.6 * reference <= ext[3] - ext[1] <= 1.6 * reference]
        if not main:
            return members, bounds
        baseline = float(np.median([ext[3] for ext in main]))
        top = float(np.median([ext[1] for ext in main]))
        band = max(baseline - top, 1e-6)
        same_kind = self.filled if kind == "fill" else (self.stroked & ~self.filled)
        reach = 0.3 * band
        for _ in range(4):
            a0, _, a1, _ = bounds
            corners = np.array([frame.origin + frame.u * u + frame.v * v
                                for u in (a0 - reach - 1.7 * band, a1 + reach + 1.7 * band)
                                for v in (top - 0.8 * band, baseline + 0.45 * band)])
            x0, y0 = corners.min(axis=0) - 0.5
            x1, y1 = corners.max(axis=0) + 0.5
            box = self.box
            near = np.flatnonzero((box[:, 0] >= x0) & (box[:, 2] <= x1) & (box[:, 1] >= y0) & (box[:, 3] <= y1)
                                  & same_kind & ~self.claimed & ~self.shadow)
            near = near[~np.isin(near, members)]
            if not len(near):
                break
            ext = self._extents(near, frame)
            centre_v = (ext[:, 1] + ext[:, 3]) / 2
            fits = ((centre_v >= top - 0.25 * band) & (centre_v <= baseline + 0.1 * band)
                    & (ext[:, 1] >= top - 0.8 * band) & (ext[:, 3] <= baseline + 0.45 * band)
                    & (ext[:, 2] - ext[:, 0] <= 1.7 * band))
            right = (ext[:, 0] >= a1 - 0.05 * band) & (ext[:, 0] - a1 <= reach)
            left = (ext[:, 2] <= a0 + 0.05 * band) & (a0 - ext[:, 2] <= reach)
            added = near[fits & (right | left)]
            if not len(added):
                break
            members = np.concatenate([members, added])
            ext = self._extents(members, frame)
            bounds = (ext[:, 0].min(), ext[:, 1].min(), ext[:, 2].max(), ext[:, 3].max())
        return members, bounds

    def _clusters(self, match: _Match) -> list[tuple[list[int], tuple, tuple]]:
        """Group letter pieces into characters: pieces that touch form one.

        Outline fonts are often printed as several adjacent filled pieces per
        letter (a "3" as three stacked arcs); touching pieces are one letter.
        Returns (member indices, page box, extent in the phrase frame).
        """
        members = [int(member) for member in match.members]
        extents = self._extents(members, match.frame)
        boxes = self.box[members]
        # Pieces of one letter share their edges exactly; neighbouring
        # letters of a tight font may be only ~0.3 pt apart.
        tolerance = 0.005 * match.frame.height + 0.05
        parent = list(range(len(members)))

        def root(index: int) -> int:
            while parent[index] != index:
                parent[index] = parent[parent[index]]
                index = parent[index]
            return index

        order = self.order[members]
        for first in range(len(members)):
            a = boxes[first]
            for second in range(first + 1, len(members)):
                b = boxes[second]
                if (a[0] <= b[2] + tolerance and b[0] <= a[2] + tolerance
                        and a[1] <= b[3] + tolerance and b[1] <= a[3] + tolerance
                        and abs(int(order[first]) - int(order[second])) <= LETTER_ORDER_GAP):
                    parent[root(first)] = root(second)
        groups: dict[int, list[int]] = {}
        for index in range(len(members)):
            groups.setdefault(root(index), []).append(index)
        result = []
        for indices in groups.values():
            box = (boxes[indices, 0].min(), boxes[indices, 1].min(), boxes[indices, 2].max(), boxes[indices, 3].max())
            extent = (extents[indices, 0].min(), extents[indices, 1].min(),
                      extents[indices, 2].max(), extents[indices, 3].max())
            result.append(([members[i] for i in indices], box, extent))
        return result

    def _runs(self, match: _Match) -> list[_Match]:
        """Split a match into the separately drawn objects it contains.

        Objects printed at different times (a label and a circled letter
        next to it, a ▼ marker) are split where they also stand apart on
        the drawing; letters of one word are never separated.
        """
        order = self.order[match.members]
        unique = np.unique(order)
        if len(unique) <= 1:
            return [match]
        groups, current = [], [unique[0]]
        for value in unique[1:]:
            if value - current[-1] <= 3:
                current.append(value)
            else:
                groups.append(current)
                current = [value]
        groups.append(current)
        if len(groups) == 1:
            return [match]
        parts = []
        for group in groups:
            members = match.members[np.isin(order, group)]
            extents = self._extents(members, match.frame)
            parts.append([members, (extents[:, 0].min(), extents[:, 1].min(), extents[:, 2].max(), extents[:, 3].max())])
        parts.sort(key=lambda item: item[1][0])
        h = max(match.bounds[3] - match.bounds[1], 1e-6)
        merged = [parts[0]]
        for members, bounds in parts[1:]:
            previous = merged[-1]
            if bounds[0] - previous[1][2] < 0.25 * h:
                previous[0] = np.concatenate([previous[0], members])
                previous[1] = (min(previous[1][0], bounds[0]), min(previous[1][1], bounds[1]),
                               max(previous[1][2], bounds[2]), max(previous[1][3], bounds[3]))
            else:
                merged.append([members, bounds])
        if len(merged) == 1:
            return [match]
        return [_Match(None, members, match.frame, match.kind, bounds) for members, bounds in merged]

    def _lines(self, match: _Match) -> int:
        """Number of separate text lines among the letters of a match."""
        extents = sorted((ext for _, _, ext in self._clusters(match)), key=lambda ext: ext[1])
        tallest = max((ext[3] - ext[1] for ext in extents), default=0.0)
        bands: list[list[float]] = []
        for a0, b0, a1, b1 in extents:
            if b1 - b0 < 0.4 * tallest:
                continue
            if bands and min(bands[-1][1], b1) - max(bands[-1][0], b0) >= 0.3 * min(bands[-1][1] - bands[-1][0], b1 - b0):
                bands[-1][0], bands[-1][1] = min(bands[-1][0], b0), max(bands[-1][1], b1)
            else:
                bands.append([b0, b1])
        return len(bands)

    def _tilt_evidence(self, match: _Match) -> float:
        """Ratio of letter box areas measured along the line vs the page axes.

        Above 1: the letters are upright on the page, so the inclined line is
        an artefact of joining neighbouring labels. Below 1: the letters
        themselves are inclined along the line.
        """
        along = 0.0
        page = 0.0
        for _, box, (a0, b0, a1, b1) in self._clusters(match):
            along += max(a1 - a0, 1e-6) * max(b1 - b0, 1e-6)
            page += max(box[2] - box[0], 1e-6) * max(box[3] - box[1], 1e-6)
        return along / page if page else 1.0

    def _sideways(self, match: _Match) -> bool:
        """True when the letters are upright on the page, not along the line."""
        h = match.frame.height
        ratios = [(a1 - a0) / max(b1 - b0, 1e-6) for _, _, (a0, b0, a1, b1) in self._clusters(match)
                  if b1 - b0 >= 0.4 * h]
        return bool(ratios) and float(np.median(ratios)) > 1.2

    def _regroup(self, match: _Match) -> list[_Match]:
        """Split letters into upright horizontal words (labels one above another)."""
        clusters = self._clusters(match)
        if not clusters:
            return []
        heights = sorted(box[3] - box[1] for _, box, _ in clusters)
        reference = heights[int(0.75 * (len(heights) - 1))]
        glyphs = [(members, box) for members, box, _ in clusters]
        return [item for item in (self._word_match([glyphs[i] for i in word], match.kind)
                                  for word in _group_words(glyphs, 0.5 * reference)) if item is not None]

    def _word_match(self, glyphs, kind: str) -> _Match | None:
        """An upright match of the given letters (members, page box)."""
        members = np.asarray([member for item in glyphs for member in item[0]], int)
        x0 = min(item[1][0] for item in glyphs)
        y0 = min(item[1][1] for item in glyphs)
        x1 = max(item[1][2] for item in glyphs)
        y1 = max(item[1][3] for item in glyphs)
        if y1 - y0 <= 0 or x1 - x0 <= 0:
            return None
        frame = _frame_from(np.array([(x0, y0), (x1, y0), (x1, y1), (x0, y1)], float), "")
        if frame is None:
            return None
        return self._absorb_marks(_Match(None, members, frame, kind, (0.0, 0.0, x1 - x0, y1 - y0)))

    def _band(self, match: _Match):
        """(top, baseline, height) of the usual letters of a one-line match."""
        clusters = self._clusters(match)
        heights = sorted(ext[3] - ext[1] for _, _, ext in clusters)
        if not heights:
            return None
        reference = heights[int(0.75 * (len(heights) - 1))]
        main = [ext for _, _, ext in clusters if 0.6 * reference <= ext[3] - ext[1] <= 1.6 * reference]
        if not main:
            return None
        top = float(np.median([ext[1] for ext in main]))
        baseline = float(np.median([ext[3] for ext in main]))
        return top, baseline, max(baseline - top, 1e-6)

    def _absorb_marks(self, match: _Match) -> _Match:
        """Add a comma or period lying between the phrase's letters.

        A detector box cut at the baseline, or a word put together from the
        letters of a neighbouring box, can miss the comma of "0,6"; left out,
        it would stay behind as a stray mark and the number would be wrong.
        """
        band = self._band(match)
        if band is None:
            return match
        top, baseline, height = band
        a0, _, a1, _ = match.bounds
        frame = match.frame
        corners = np.array([frame.origin + frame.u * u + frame.v * v
                            for u in (a0, a1) for v in (top, baseline + 0.5 * height)])
        x0, y0 = corners.min(axis=0)
        x1, y1 = corners.max(axis=0)
        same_kind = self.filled if match.kind == "fill" else (self.stroked & ~self.filled)
        box = self.box
        near = np.flatnonzero((box[:, 0] >= x0 - 0.5) & (box[:, 2] <= x1 + 0.5) & (box[:, 1] >= y0 - 0.5)
                              & (box[:, 3] <= y1 + 0.5) & same_kind & ~self.claimed & ~self.shadow)
        near = near[~np.isin(near, match.members)]
        if not len(near):
            return match
        ext = self._extents(near, frame)
        centre_u = (ext[:, 0] + ext[:, 2]) / 2
        centre_v = (ext[:, 1] + ext[:, 3]) / 2
        marks = ((ext[:, 3] - ext[:, 1] <= 0.45 * height) & (ext[:, 2] - ext[:, 0] <= 0.45 * height)
                 & (centre_u >= a0 + 0.1 * height) & (centre_u <= a1 - 0.1 * height)
                 & (centre_v >= top + 0.5 * height) & (ext[:, 3] <= baseline + 0.5 * height)
                 & (ext[:, 1] >= top + 0.3 * height))
        if not marks.any():
            return match
        members = np.concatenate([match.members, near[marks]])
        extents = self._extents(members, frame)
        return _Match(match.span, members, frame, match.kind,
                      (extents[:, 0].min(), extents[:, 1].min(), extents[:, 2].max(), extents[:, 3].max()))

    def _clean_image(self, match: _Match) -> np.ndarray:
        """Draw only the phrase's own letters, upright, at a reading size."""
        u0, v0, u1, v1 = match.bounds
        ink = max(v1 - v0, 1e-6)
        scale = CLEAN_HEIGHT / ink
        pad = 0.35 * ink
        width = int(math.ceil((u1 - u0 + 2 * pad) * scale))
        if width > 4000:
            scale *= 4000 / width
            width = 4000
        height = int(math.ceil((v1 - v0 + 2 * pad) * scale))
        canvas = np.full((max(height, 8), max(width, 8)), 255, np.uint8)
        offset = np.array((u0 - pad, v0 - pad))
        by_drawing: dict[int, list[np.ndarray]] = {}
        for member in match.members:
            local = (match.frame.local(self.points[member]) - offset) * scale
            by_drawing.setdefault(int(self.drawing[member]), []).append(np.round(local * 16).astype(np.int32))
        for drawing, contours in by_drawing.items():
            if match.kind == "fill":
                cv2.fillPoly(canvas, contours, 0, lineType=cv2.LINE_AA, shift=4)
            else:
                thickness = max(1, int(round(self.widths.get(drawing, 0.5) * scale)))
                cv2.polylines(canvas, contours, False, 0, thickness=thickness, lineType=cv2.LINE_AA, shift=4)
        return canvas

    def _decide(self, match: _Match, crop: np.ndarray, reading):
        """(span, letter pieces) for a match, or None to keep it as geometry."""
        choice = None
        extent = None
        positions = None
        if reading is not None and reading[0].strip():
            clean_text, clean_conf, clean_source, extent = reading[:4]
            if len(reading) > 4 and reading[4] and reading[4][0] == clean_text:
                positions = reading[4][1]
            if plausible_text(clean_text, match.kind, clean_conf, "tesseract" in clean_source):
                choice = (clean_text, clean_conf, clean_source)
        if match.span is not None:
            original = (match.span.text, match.span.confidence or 0.0, match.span.engine or "")
            if choice is None or (choice[1] < original[1] - 0.05 and "tesseract" not in choice[2]):
                if plausible_text(original[0], match.kind, original[1], "tesseract" in original[2]):
                    choice = original
        if choice is None:
            return None
        raw_text = choice[0]
        if positions is not None and match.kind == "fill":
            raw_text = self._spaces_from_geometry(match, raw_text, positions)
        text = fix_mixed_scripts(raw_text.strip())
        members, bounds = match.members, match.bounds
        if extent is not None:
            members, bounds = self._trim(match, extent)
            if not len(members):
                return None
        trimmed = _Match(match.span, members, match.frame, match.kind, bounds)
        trimmed = self._refine_band(trimmed, text)
        if trimmed is None:
            return None
        if not self._solid_shapes_ok(trimmed, text):
            # A filled triangle read as "V", a dot read as "0": keep geometry.
            return None
        if abs(abs(trimmed.frame.angle) - 90) < 1e-6 and self._sideways(trimmed):
            return None
        text, trimmed = self._number_from_geometry(trimmed, text)
        if not len(trimmed.members):
            return None
        if not self._numbers_consistent(trimmed, text):
            return None
        text = self._roman_from_geometry(trimmed, text)
        # Only the pieces that remain after trimming and band refinement are
        # this phrase's letters; everything else stays geometry.
        return self._span(text, choice[1], choice[2], trimmed), trimmed.members

    # Runs of bars standing apart from other letters ("I-I, II-II"), not the
    # "l" of a word or the "II" inside an index such as "gtIIsz".
    def _spaces_from_geometry(self, match: _Match, text: str, positions) -> str:
        """Put back word spaces the reader dropped ("механическихсвойств").

        ``positions`` are the network's character centres (fractions of the
        clean image width, one per character of ``text``). Where the letters
        on the drawing stand a word space apart and the characters on both
        sides of that gap are adjacent in the reading, a space is inserted.
        """
        if len(positions) != len(text):
            return text
        band = self._band(match)
        if band is None:
            return text
        height = band[2]
        clusters = sorted(self._clusters(match), key=lambda item: item[2][0])
        u0, v0, u1, v1 = match.bounds
        ink = max(v1 - v0, 1e-6)
        pad = 0.35 * ink
        width = u1 - u0 + 2 * pad
        centres = [u0 - pad + fraction * width for fraction in positions]
        gaps = []
        reach = clusters[0][2][2] if clusters else None
        for _, _, (a0, _b0, a1, _b1) in clusters[1:]:
            if reach is not None and a0 - reach > 0.5 * height:
                gaps.append((reach + a0) / 2)
            reach = a1 if reach is None else max(reach, a1)
        if not gaps:
            return text
        inserts = set()
        for gap in gaps:
            for index in range(len(text) - 1):
                if centres[index] < gap < centres[index + 1]:
                    # Only between two letters: digits ("11"), commas and
                    # colons have wide margins of their own.
                    if text[index].isalpha() and text[index + 1].isalpha():
                        inserts.add(index + 1)
                    break
        if not inserts:
            return text
        return "".join((" " if index in inserts else "") + character for index, character in enumerate(text))

    _ROMAN_TEXT = re.compile(r"(?<![^\W\d_])[IlІі|1]+(?:\s*[-–,.]?\s*[IlІі|1]+)*(?![^\W\d_])")

    def _roman_from_geometry(self, match: _Match, text: str) -> str:
        """Count the bars of Roman numerals ("III-III") on the letters.

        Readers often lose or add one of several identical bars. Each run of
        bars, dashes and commas on the drawing is written out exactly
        ("I" per bar) in place of the corresponding run in the reading, when
        the reading has the same number of such runs.
        """
        found = list(self._ROMAN_TEXT.finditer(text))
        # Pieces of one run the reader split ("1-1, II-, III") are one run.
        merged: list[list[int]] = []
        for m in found:
            if merged and re.fullmatch(r"[\s\-–,.]*", text[merged[-1][1]:m.start()]):
                merged[-1][1] = m.end()
            else:
                merged.append([m.start(), m.end()])
        segments = [(start, end) for start, end in merged if re.search(r"[IlІі|]", text[start:end])]
        if not segments:
            return text
        clusters = sorted(self._clusters(match), key=lambda item: item[2][0])
        if not clusters:
            return text
        heights = sorted(ext[3] - ext[1] for _, _, ext in clusters)
        band = heights[int(0.75 * (len(heights) - 1))]
        if band <= 0:
            return text
        top = float(np.median([ext[1] for _, _, ext in clusters if ext[3] - ext[1] >= 0.6 * band]))
        kinds = []
        for members, _, (a0, b0, a1, b1) in clusters:
            w, h = a1 - a0, b1 - b0
            centre = (b0 + b1) / 2 - top
            if h >= 0.75 * band and w <= 0.55 * h and self._bar_shape(members, match.frame, (a0, b0, a1, b1)):
                kinds.append(("I", a0, a1))
            elif h <= 0.3 * band and w >= 1.4 * h and 0.25 * band <= centre <= 0.75 * band:
                kinds.append(("-", a0, a1))
            elif h <= 0.45 * band and w <= 0.35 * band and centre >= 0.7 * band:
                kinds.append(("," if h > 1.6 * w or h > 0.2 * band else ".", a0, a1))
            else:
                kinds.append((None, a0, a1))
        runs, current, previous_letter = [], [], None
        for kind, a0, a1 in kinds + [(None, math.inf, math.inf)]:
            if kind is None:
                while current and current[-1][0] != "I":
                    current.pop()
                # A run touching a letter on either side is part of a word.
                apart = current and (previous_letter is None or current[0][1] - previous_letter > 0.25 * band) \
                    and a0 - current[-1][2] > 0.25 * band
                if apart and any(item[0] == "I" for item in current):
                    runs.append(current)
                current = []
                previous_letter = a1
            elif kind == "I" or current:
                current.append((kind, a0, a1))
        if len(runs) != len(segments):
            return text
        pieces, position = [], 0
        for (segment_start, segment_end), run in zip(segments, runs):
            written = run[0][0]
            for (_, _, previous_end), (kind, start, _) in zip(run, run[1:]):
                if start - previous_end > 0.35 * band and kind == "I":
                    written += " "
                written += kind
            written = re.sub(r"\s*([-–])\s*", r"\1", written)
            original = re.sub(r"\s+", "", text[segment_start:segment_end])
            compact = written.replace(" ", "")
            if abs(len(compact) - len(original)) > max(3, 0.6 * len(compact)):
                return text
            pieces.append(text[position:segment_start])
            pieces.append(written)
            position = segment_end
        pieces.append(text[position:])
        return "".join(pieces)

    _NUMBER_TEXT = re.compile(r"[\d\s.,:\-–=_~'\"‚]+")

    def _number_from_geometry(self, match: _Match, text: str) -> tuple[str, _Match]:
        """Write a number's separators from its drawn shapes.

        Readers lose or invent commas and periods ("0,2" read as "02") and
        take a neighbouring marker for "=" or "-". In a number every digit
        is a letter-size shape and every comma or period a small mark at the
        baseline, so the digits are taken from the reading in order and the
        separators, signs and spaces from the drawing. A mark outside the
        digits that is no separator (a ▼ marker next to the number) is left
        as geometry. Readings with other symbols ("±", "°", "/") are kept.
        """
        if not self._NUMBER_TEXT.fullmatch(text) or not any(c.isdigit() for c in text):
            return text, match
        digits = [c for c in text if c.isdigit()]
        clusters = sorted(self._split_marks(match, self._clusters(match)), key=lambda item: item[2][0])
        if not clusters:
            return text, match
        tallest = max(ext[3] - ext[1] for _, _, ext in clusters)
        big = [ext for _, _, ext in clusters if ext[3] - ext[1] >= 0.6 * tallest]
        if len(big) != len(digits):
            return text, match
        top = float(np.median([ext[1] for ext in big]))
        baseline = float(np.median([ext[3] for ext in big]))
        band = max(baseline - top, 1e-6)
        if any(abs(ext[3] - baseline) > 0.2 * band for ext in big):
            return text, match
        first, last = big[0][0], big[-1][2]
        written, keep, previous = [], [], None
        digit = iter(digits)
        for members, _, (a0, b0, a1, b1) in clusters:
            h, w, centre = b1 - b0, a1 - a0, (b0 + b1) / 2
            if h >= 0.6 * tallest:
                character = next(digit)
            elif h <= 0.45 * band and w <= 0.45 * band and centre >= top + 0.65 * band and a0 > first:
                character = "," if b1 > baseline + 0.12 * band or h > 1.6 * w else "."
            elif h <= 0.25 * band and w >= 1.2 * h and top + 0.3 * band <= centre <= top + 0.75 * band \
                    and a1 <= last + 0.2 * band:
                character = "-"
            elif a1 <= first + 0.05 * band or a0 >= last - 0.05 * band:
                continue  # beside the number, not part of it
            else:
                return text, match  # an unknown symbol between the digits
            if previous is not None and a0 - previous > 0.45 * band:
                written.append(" ")
            written.append(character)
            keep.extend(members)
            previous = a1
        rebuilt = "".join(written).strip()
        if len(keep) == len(match.members):
            return rebuilt, match
        members = match.members[np.isin(match.members, keep)]
        if not len(members):
            return rebuilt, _Match(match.span, members, match.frame, match.kind, match.bounds)
        kept = self._extents(members, match.frame)
        return rebuilt, _Match(match.span, members, match.frame, match.kind,
                               (kept[:, 0].min(), kept[:, 1].min(), kept[:, 2].max(), kept[:, 3].max()))

    def _bar_shape(self, members, frame: _Frame, extent) -> bool:
        """True for a straight stem with symmetric (or no) serifs: I, l, |.

        A "1" has its flag on one side at the top; an "I" is a plain bar or
        has equal serifs on both sides.
        """
        a0, b0, a1, b1 = extent
        height = max(b1 - b0, 1e-6)
        scale = 48.0 / height
        canvas = np.zeros((int(math.ceil(height * scale)) + 3, int(math.ceil((a1 - a0) * scale)) + 3), np.uint8)
        by_drawing: dict[int, list[np.ndarray]] = {}
        for member in members:
            local = (frame.local(self.points[member]) - (a0, b0)) * scale + 1
            by_drawing.setdefault(int(self.drawing[member]), []).append(np.round(local * 16).astype(np.int32))
        for contours in by_drawing.values():
            layer = np.zeros_like(canvas)
            cv2.fillPoly(layer, contours, 255, shift=4)
            canvas |= layer
        rows = canvas.shape[0]
        middle = canvas[int(0.4 * rows):int(0.6 * rows)]
        columns = np.flatnonzero(middle.any(axis=0))
        if not len(columns) or columns[-1] - columns[0] > 0.2 * 48:
            return False  # no single narrow stem at mid-height
        stem = (columns[0] + columns[-1]) / 2
        for part in (canvas[:int(0.18 * rows)], canvas[int(0.82 * rows):]):
            ink = np.flatnonzero(part.any(axis=0))
            if not len(ink):
                continue
            left, right = stem - ink[0], ink[-1] - stem
            if abs(left - right) > 0.12 * 48:
                return False
        return True

    def _split_marks(self, match: _Match, clusters: list) -> list:
        """Separate a comma or period printed touching its digit ("7,1").

        In small, tight lettering the comma can touch the digit before it and
        join its letter group. A small piece low on the line that sticks out
        at the side of a group is taken apart again.
        """
        heights = sorted(ext[3] - ext[1] for _, _, ext in clusters)
        if not heights:
            return clusters
        band = heights[int(0.75 * (len(heights) - 1))]
        top = float(np.median([ext[1] for _, _, ext in clusters if ext[3] - ext[1] >= 0.6 * band]))
        result = []
        for members, box, extent in clusters:
            if len(members) < 2:
                result.append((members, box, extent))
                continue
            pieces = self._extents(members, match.frame)
            marks = []
            for row, (a0, b0, a1, b1) in enumerate(pieces):
                others = np.delete(pieces, row, axis=0)
                small = b1 - b0 <= 0.45 * band and a1 - a0 <= 0.45 * band
                low = (b0 + b1) / 2 >= top + 0.65 * band
                side = a0 >= others[:, 2].max() - 0.15 * band or a1 <= others[:, 0].min() + 0.15 * band
                if small and low and side:
                    marks.append(row)
            if not marks or len(marks) == len(members):
                result.append((members, box, extent))
                continue
            rest = [member for row, member in enumerate(members) if row not in marks]
            for group in [rest] + [[members[row]] for row in marks]:
                ext = self._extents(group, match.frame)
                b = self.box[group]
                result.append((group, (b[:, 0].min(), b[:, 1].min(), b[:, 2].max(), b[:, 3].max()),
                               (ext[:, 0].min(), ext[:, 1].min(), ext[:, 2].max(), ext[:, 3].max())))
        return result

    def _numbers_consistent(self, match: _Match, text: str) -> bool:
        """A number must consist of separate digits of equal height.

        Rejects readings such as "15" for a letter with its subscript ("qs")
        or digits invented from symbols: each digit of a printed number is one
        letter shape, all of the same height.
        """
        if not re.fullmatch(r"[\d\s.,:;/\-+±°×()]+", text) or not any(c.isdigit() for c in text):
            return True
        digits = sum(c.isdigit() for c in text)
        extra = sum(c in "±/×()+" for c in text)
        heights = [b1 - b0 for _, _, (a0, b0, a1, b1) in self._clusters(match)]
        if not heights:
            return False
        tallest = max(heights)
        big = [height for height in heights if height >= 0.6 * tallest]
        if not digits <= len(big) <= digits + extra:
            return False
        return min(big) >= 0.75 * max(big)

    def _trim(self, match: _Match, extent):
        """The letters where characters were actually read, and their bounds.

        Decided per letter (touching pieces): the network's character
        positions are only approximate, and cutting through a letter would
        leave part of it (the right stem of a wide "М") behind as geometry
        under the new text.
        """
        u0, v0, u1, v1 = match.bounds
        ink = max(v1 - v0, 1e-6)
        pad = 0.35 * ink
        width = u1 - u0 + 2 * pad
        low, high, pitch = extent
        pitch_pt = pitch * width if pitch else 0.6 * ink
        margin = max(0.6 * pitch_pt, 0.35 * ink)
        lo = u0 - pad + low * width - margin
        hi = u0 - pad + high * width + margin
        keep_members = []
        for members, _, (a0, _b0, a1, _b1) in self._clusters(match):
            centre = (a0 + a1) / 2
            inside = max(0.0, min(a1, hi) - max(a0, lo))
            if lo <= centre <= hi or inside >= 0.5 * max(a1 - a0, 1e-6):
                keep_members.extend(members)
        if len(keep_members) == len(match.members):
            return match.members, match.bounds
        keep = np.isin(match.members, keep_members)
        members = match.members[keep]
        if not len(members):
            return members, match.bounds
        kept = self._extents(members, match.frame)
        return members, (kept[:, 0].min(), kept[:, 1].min(), kept[:, 2].max(), kept[:, 3].max())

    def _refine_band(self, match: _Match, text: str | None = None) -> _Match | None:
        """Keep only the letters of this line (not the line above or a marker).

        Decided per letter (touching pieces), so a separately printed
        descender stays with its letter. The band runs from the top of the
        tallest letters standing on this line's baseline (capitals, digits,
        ascenders) to just below the baseline (commas, descenders). Above it,
        a small mark right over one of the letters is a diacritic (the breve
        of "й", the dots of "ё") unless it lies on another line of lettering,
        like the comma of the label above.
        """
        clusters = self._clusters(match)
        heights = [ext[3] - ext[1] for _, _, ext in clusters]
        if not heights:
            return None
        # The usual letter height: robust against a few oversized clusters
        # (letters of two lines touching) and small marks.
        ordered = sorted(heights)
        reference = ordered[int(0.75 * (len(ordered) - 1))]
        main = [ext for (_, _, ext), height in zip(clusters, heights) if 0.6 * reference <= height <= 1.6 * reference]
        if len(main) < 2:
            return match
        bottoms = [ext[3] for ext in main]
        if max(bottoms) - min(bottoms) > 0.7 * float(np.median([ext[3] - ext[1] for ext in main])):
            # Letters on two baselines: two lines, which are split into
            # separate labels elsewhere; nothing here is foreign to them.
            return match
        baseline = float(np.median(bottoms))
        # Top of the usual letters (x-height in lower-case text, cap height in
        # capitals and numbers); taller letters are judged by their centre.
        top = float(np.median([ext[1] for ext in main]))
        height = max(baseline - top, 1e-6)
        keep_members = []
        for members, _, extent in clusters:
            if extent[3] - extent[1] > 1.7 * height and len(members) > 1:
                # Far taller than any letter of the line: pieces of two lines
                # printed together; each piece is judged alone.
                parts = [([member], tuple(self._extents([member], match.frame)[0])) for member in members]
            else:
                parts = [(members, extent)]
            for part, part_extent in parts:
                centre = (part_extent[1] + part_extent[3]) / 2
                small = part_extent[3] - part_extent[1] <= 0.45 * height
                # A small mark reaching above the line's top in its upper part
                # is no punctuation of this line (that hangs at the
                # baseline): a diacritic, or the comma of the line above.
                poking = small and part_extent[1] < top - 0.1 * height and centre < top + 0.3 * height
                if top - 0.15 * height <= centre <= baseline + 0.35 * height and not poking:
                    keep_members.extend(part)
                elif centre < top + 0.3 * height and self._diacritic(
                        match, part, part_extent, main, top, height,
                        check_lines=text is not None and not _dotted(text)):
                    keep_members.extend(part)
        if len(keep_members) == len(match.members):
            return match
        if not keep_members:
            return None
        keep = np.isin(match.members, keep_members)
        kept = self._extents(match.members[keep], match.frame)
        return _Match(match.span, match.members[keep], match.frame, match.kind,
                      (kept[:, 0].min(), kept[:, 1].min(), kept[:, 2].max(), kept[:, 3].max()))

    def _diacritic(self, match: _Match, members, extent, letters, top: float, height: float, *,
                   check_lines: bool = True) -> bool:
        """A small mark just above a letter of this line, on no other line.

        Before reading (``check_lines`` False) every such mark is kept, so
        the reader sees "й" or "ё" whole; afterwards a mark is kept without
        question when the reading has a dotted or accented letter, and
        otherwise only if no other line of lettering lies beside it.
        """
        a0, b0, a1, b1 = extent
        if b1 - b0 > 0.45 * height or a1 - a0 > 1.2 * height or b1 < top - 0.6 * height:
            return False
        centre = (a0 + a1) / 2
        if not any(ext[0] - 0.1 * height <= centre <= ext[2] + 0.1 * height and b1 <= ext[1] + 0.15 * height
                   for ext in letters):
            return False
        if not check_lines:
            return True
        # Lettering of another line beside the mark (the rest of the label
        # above, a marker) shows that the mark belongs to that line.
        region = np.array([[a0 - 1.2 * height, b0], [a1 + 1.2 * height, b1]])
        corners = np.array([match.frame.origin + match.frame.u * u + match.frame.v * v
                            for u in region[:, 0] for v in region[:, 1]])
        x0, y0 = corners.min(axis=0)
        x1, y1 = corners.max(axis=0)
        box = self.box
        near = np.flatnonzero((box[:, 0] <= x1) & (box[:, 2] >= x0) & (box[:, 1] <= y1) & (box[:, 3] >= y0)
                              & self.filled & ~self.shadow)
        near = near[~np.isin(near, match.members)]
        if not len(near):
            return True
        others = self._extents(near, match.frame)
        beside = (others[:, 1] <= b1) & (others[:, 3] >= b0) & (others[:, 3] < top + 0.3 * height)
        return not beside.any()

    def _solid_shapes_ok(self, match: _Match, text: str) -> bool:
        """Short readings must not come from solid blobs.

        Letters and digits have counters or open strokes; a filled triangle
        (a level marker), a dot or a square is solid. Such a shape read as
        "V", "0" or "O" stays geometry. Bar-like characters are exempt.
        """
        compact = re.sub(r"\s+", "", text)
        if sum(c.isalnum() for c in compact) > 2:
            return True
        bars = set("1Il|!ij.:;,-–—_'\"`/\\")
        candidates = [c for c in compact if c.isalnum() and c not in bars]
        if not candidates:
            return True
        for members, _, extent in self._clusters(match):
            if extent[3] - extent[1] < 0.6 * (match.bounds[3] - match.bounds[1]):
                continue
            # Ink area drawn the way the PDF fills it: the counters of
            # "6", "4" or "0" (inner outlines) are holes, not ink.
            scale = 48.0 / max(extent[3] - extent[1], extent[2] - extent[0], 1e-6)
            offset = np.array((extent[0], extent[1]))
            width = int(math.ceil((extent[2] - extent[0]) * scale)) + 3
            height = int(math.ceil((extent[3] - extent[1]) * scale)) + 3
            canvas = np.zeros((height, width), np.uint8)
            points = []
            by_drawing: dict[int, list[np.ndarray]] = {}
            for member in members:
                local = (match.frame.local(self.points[member]) - offset) * scale + 1
                points.append(local)
                by_drawing.setdefault(int(self.drawing[member]), []).append(np.round(local * 16).astype(np.int32))
            for contours in by_drawing.values():
                layer = np.zeros_like(canvas)
                cv2.fillPoly(layer, contours, 255, shift=4)
                canvas |= layer
            hull = cv2.convexHull(np.concatenate(points).astype(np.float32))
            area = abs(float(cv2.contourArea(hull)))
            filled = float(np.count_nonzero(canvas))
            if area > 0 and filled / area > 0.88:
                return False
        return True

    def _span(self, text, confidence, source, match: _Match) -> TextSpan:
        a0, b0, a1, b1 = match.bounds
        frame = match.frame
        origin, u, v = frame.origin, frame.u, frame.v
        quad = (origin + u * a0 + v * b0, origin + u * a1 + v * b0,
                origin + u * a1 + v * b1, origin + u * a0 + v * b1)
        xs = [point[0] for point in quad]
        ys = [point[1] for point in quad]
        color = self.colors.get(int(self.drawing[match.members[0]])) or (0, 0, 0)
        return TextSpan(
            text, (min(xs), min(ys), max(xs), max(ys)),
            "osifont" if match.kind == "stroke" else "Liberation Sans", max(0.5, b1 - b0), color,
            origin=(float(quad[3][0]), float(quad[3][1])), angle=frame.angle, from_ocr=True,
            advance=max(0.1, a1 - a0), quad=tuple((float(x), float(y)) for x, y in quad),
            confidence=float(confidence), engine=f"{source} (вектор)".strip())

    # --------------------------------------------------------------- recovery
    def recover(self, engine, *, environment=None, language: str = "rus+eng", messages: list[str] | None = None,
                check_cancel=None, progress=None) -> list[TextSpan]:
        """Read outline letters that no detected phrase covered.

        The text detector works on a reduced image: it can miss a lone letter
        in a circle or join two lines into one box that matches nothing. The
        letter shapes still left are grouped into words by their geometry
        (touching pieces form a letter, neighbouring letters of one height a
        word) and read like any other phrase, with stricter acceptance: a
        word of circles, bars or crosses only ("0", "II", "x") stays geometry,
        because hatching and symbols look exactly like that.
        """
        if engine is None or not self.accepted:
            return []
        sizes = np.array([span.size for span, _ in self.accepted], float)
        low = 0.45 * float(np.percentile(sizes, 5))
        high = 1.5 * float(sizes.max())
        letters = self.outline_letters()
        letters = letters[~self.claimed[letters]]
        if not len(letters):
            return []
        boxes = self.box[letters]
        tolerance = 0.05 + 0.005 * (boxes[:, 3] - boxes[:, 1])
        labels = _touching_groups(boxes, tolerance, self.order[letters])
        groups: dict[int, list[int]] = {}
        for position, label in enumerate(labels):
            groups.setdefault(int(label), []).append(int(letters[position]))
        seeds, marks = [], []
        for members in groups.values():
            b = self.box[members]
            x0, y0, x1, y1 = b[:, 0].min(), b[:, 1].min(), b[:, 2].max(), b[:, 3].max()
            h, w = y1 - y0, x1 - x0
            glyph = (np.asarray(members, int), (x0, y0, x1, y1))
            if low <= h <= high and w <= 2.5 * h:
                seeds.append(glyph)
            elif h < low and w < high:
                marks.append(glyph)
        if not seeds:
            return []
        glyphs = seeds + marks
        matches = []
        for word in _group_words(glyphs, low):
            match = self._word_match([glyphs[index] for index in word], "fill")
            if match is not None:
                match = self._refine_band(match)
            if match is not None:
                matches.append(match)
        if not matches:
            return []
        if progress:
            progress(f"Векторные надписи: дочитывание {len(matches)} фрагментов, пропущенных поиском строк")
        if check_cancel:
            check_cancel()
        crops = [self._clean_image(match) for match in matches]
        from .recognition import read_crops
        try:
            readings = read_crops(engine, crops, environment, language, check_cancel=check_cancel)
        except (ValueError, OSError) as error:
            if messages is not None:
                messages.append(f"Дочитывание векторных надписей пропущено: {error}")
            return []
        result, taken = [], []
        recovered = 0
        for match, crop, reading in zip(matches, crops, readings):
            decided = self._decide(match, crop, reading)
            if decided is None:
                continue
            span, members = decided
            compact = re.sub(r"\s+", "", span.text)
            meaningful = [c for c in compact if c not in SYMBOL_LIKE]
            agreed = "tesseract" in (span.engine or "")
            if not meaningful:
                continue  # circles, bars, crosses: hatching looks the same
            confidence = span.confidence or 0
            if sum(c.isalnum() for c in compact) <= 1:
                # A lone letter: both readers must agree on it, or the
                # network must be certain.
                if not ((agreed and confidence >= 0.5) or confidence >= 0.97):
                    continue
            elif confidence < 0.8:
                continue
            if self.claimed[members].any():
                continue
            self.claimed[members] = True
            taken.append((span, members))
        for span, members in self._without_patterns(taken):
            self.accepted.append((span, members))
            self.replaced += 1
            recovered += 1
            result.append(span)
        if recovered and messages is not None:
            messages.append(f"Дочитано {recovered} надписей, пропущенных при поиске строк (одиночные буквы в кружках, "
                            "подписи у близко расположенных строк).")
        return result

    # ----------------------------------------------------------------- finish
    def finish(self, messages: list[str] | None = None) -> None:
        """Remove every claimed letter sub-path from the page drawings."""
        # Identical copies of a removed letter go with it.
        self.claimed |= self.claimed[self.twin_of]
        removed = int(self.claimed.sum())
        if removed:
            removal: dict[int, list[tuple[int, int]]] = {}
            for index in np.flatnonzero(self.claimed):
                removal.setdefault(int(self.drawing[index]), []).append((int(self.start[index]), int(self.stop[index])))
            kept = []
            for number, drawing in enumerate(self.page.drawings):
                ranges = removal.get(number)
                if not ranges:
                    kept.append(drawing)
                    continue
                drop = np.zeros(len(drawing.segments), bool)
                for start, stop in ranges:
                    drop[start:stop] = True
                if drop.all():
                    continue
                kept.append(Drawing([segment for segment, remove in zip(drawing.segments, drop) if not remove],
                                    stroke=drawing.stroke, fill=drawing.fill, width=drawing.width,
                                    closed=drawing.closed, dashes=drawing.dashes, dash_offset=drawing.dash_offset))
            self.page.drawings[:] = kept
            self.claimed[:] = False
        if messages is not None:
            if self.replaced:
                messages.append(f"Надписи, нарисованные в PDF контурами и линиями, заменены редактируемым текстом: "
                                f"{self.replaced} фраз; исходные контуры этих букв удалены ({removed} фрагментов), "
                                "поэтому текст не дублируется. Размер, угол и положение взяты по контурам букв.")
            if self.rejected:
                messages.append(f"Не вставлено {self.rejected} сомнительных распознаваний (штриховка, деления шкал, "
                                "условные знаки или нечёткие надписи): эти места оставлены исходной геометрией "
                                "без дублирования.")
