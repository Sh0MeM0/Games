"""Local neural OCR: PaddleOCR PP-OCR models executed by ONNX Runtime on the CPU.

Pipeline
--------
1. A DB text-line detector (PP-OCRv6 small) finds text lines at any angle,
   including skewed scans and vertical dimension labels on drawings.
2. Each line is rectified by a perspective warp into a horizontal strip.
3. A 0°/180° direction classifier turns upside-down lines.
4. A CTC recogniser (PP-OCRv5 "eslav": Russian, Belarusian, Ukrainian and
   Latin, digits and engineering symbols such as ⌀ ± °) reads the strip.
5. Character positions from the CTC alignment and the ink of the strip give
   tight word/phrase quadrilaterals in image pixels.

No network access is used. Models are loaded from ``<root>/models/ocr`` or the
folder named by ``PDF2OFFICE_MODELS``. When a model or ONNX Runtime is missing
the caller falls back to Tesseract only.
"""
from __future__ import annotations

from dataclasses import dataclass, field
import math
import os
from pathlib import Path
import threading
from typing import Callable

import cv2
import numpy as np

from .runtime import resource_root

DET_MODEL = "PP-OCRv6_det_small.onnx"
CLS_MODEL = "ch_ppocr_mobile_v2.0_cls_mobile.onnx"
REC_MODEL = "eslav_PP-OCRv5_rec_mobile.onnx"

# Detection works on bounded tiles whose probabilities are stitched into one
# map, so long lines crossing tile borders are not cut into duplicates.
DET_TILE = 1536
DET_HALO = 160
DET_THRESHOLD = 0.3
DET_BOX_THRESHOLD = 0.5
DET_UNCLIP = 1.6
REC_HEIGHT = 48
REC_MAX_WIDTH = 3200
REC_BATCH = 8
CLS_SHAPE = (48, 192)


def models_root() -> Path:
    if value := os.environ.get("PDF2OFFICE_MODELS"):
        return Path(value).expanduser()
    return resource_root() / "models"


def ocr_models_dir() -> Path:
    return models_root() / "ocr"


def missing_models(directory: Path | None = None) -> list[str]:
    directory = directory or ocr_models_dir()
    return [name for name in (DET_MODEL, REC_MODEL) if not (directory / name).is_file()]


@dataclass
class OcrSegment:
    """A recognised phrase: one editable text entity.

    ``quad`` is (top-left, top-right, bottom-right, bottom-left) of the ink in
    reading order, in source-image pixels with the y axis pointing down.
    ``angle`` is the reading direction in degrees in the same axes.
    """

    text: str
    quad: np.ndarray
    confidence: float
    angle: float
    line_height: float
    words: list[tuple[str, np.ndarray, float]] = field(default_factory=list)


def _check(callback) -> None:
    if callback:
        callback()


def _onnx_session(path: Path, threads: int):
    import onnxruntime as ort

    options = ort.SessionOptions()
    options.log_severity_level = 3
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    if threads > 0:
        options.intra_op_num_threads = threads
    return ort.InferenceSession(str(path), options, providers=["CPUExecutionProvider"])


class NeuralOcr:
    """Offline PP-OCR inference; one instance is safe to share between threads."""

    def __init__(self, directory: Path | None = None, threads: int = 0):
        directory = Path(directory or ocr_models_dir())
        absent = missing_models(directory)
        if absent:
            raise FileNotFoundError("Нет моделей нейросетевого OCR: " + ", ".join(absent))
        self.directory = directory
        self.det = _onnx_session(directory / DET_MODEL, threads)
        self.rec = _onnx_session(directory / REC_MODEL, threads)
        self.cls = _onnx_session(directory / CLS_MODEL, threads) if (directory / CLS_MODEL).is_file() else None
        meta = self.rec.get_modelmeta().custom_metadata_map
        if "character" not in meta:
            raise ValueError("Модель распознавания не содержит словарь символов.")
        characters = [item for item in meta["character"].split("\n") if item]
        classes = self.rec.get_outputs()[0].shape[-1]
        # PaddleOCR CTC: index 0 is the blank; the space is appended last.
        self.characters = ["", *characters, " "]
        if isinstance(classes, int) and classes != len(self.characters):
            if classes == len(characters) + 1:
                self.characters = ["", *characters]
            else:
                raise ValueError(f"Словарь модели ({len(characters)}) не соответствует выходу ({classes}).")
        self._lock = threading.Lock()

    # ------------------------------------------------------------------ detection
    def _probability(self, gray: np.ndarray) -> np.ndarray:
        height, width = gray.shape
        padded_h, padded_w = max(32, math.ceil(height / 32) * 32), max(32, math.ceil(width / 32) * 32)
        canvas = np.full((padded_h, padded_w), 255, np.uint8)
        canvas[:height, :width] = gray
        tensor = (canvas.astype(np.float32) / 255.0 - 0.5) / 0.5
        tensor = np.repeat(tensor[None, None], 3, axis=1)
        output = self.det.run(None, {self.det.get_inputs()[0].name: tensor})[0]
        return output[0, 0, :height, :width]

    def probability_map(self, gray: np.ndarray, *, check_cancel=None, progress=None) -> np.ndarray:
        """Return a uint8 text probability map of the same size as ``gray``."""
        height, width = gray.shape
        result = np.zeros((height, width), np.uint8)
        tiles = [(top, left) for top in range(0, height, DET_TILE) for left in range(0, width, DET_TILE)]
        for number, (top, left) in enumerate(tiles, start=1):
            _check(check_cancel)
            bottom, right = min(height, top + DET_TILE), min(width, left + DET_TILE)
            y0, x0 = max(0, top - DET_HALO), max(0, left - DET_HALO)
            y1, x1 = min(height, bottom + DET_HALO), min(width, right + DET_HALO)
            crop = gray[y0:y1, x0:x1]
            if int(crop.min()) > 215:
                continue
            if progress and len(tiles) > 1:
                progress(f"Нейросеть: поиск строк текста, участок {number} из {len(tiles)}")
            probability = self._probability(crop)
            core = probability[top - y0:bottom - y0, left - x0:right - x0]
            result[top:bottom, left:right] = np.clip(core * 255.0 + 0.5, 0, 255).astype(np.uint8)
        return result

    @staticmethod
    def boxes_from_probability(probability: np.ndarray, *, box_threshold: float = DET_BOX_THRESHOLD,
                               unclip: float = DET_UNCLIP) -> list[tuple[tuple[float, float], tuple[float, float], float, float]]:
        """DB post-processing: (centre, (w, h), angle, score) rotated rectangles."""
        bitmap = (probability > round(DET_THRESHOLD * 255)).astype(np.uint8)
        bitmap = cv2.dilate(bitmap, np.ones((2, 2), np.uint8))
        contours, _ = cv2.findContours(bitmap, cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)
        boxes = []
        for contour in contours:
            if len(contour) < 3:
                continue
            rect = cv2.minAreaRect(contour)
            (cx, cy), (w, h), angle = rect
            if min(w, h) < 3:
                continue
            x, y, bw, bh = cv2.boundingRect(contour)
            mask = np.zeros((bh, bw), np.uint8)
            cv2.fillPoly(mask, [contour.reshape(-1, 2) - (x, y)], 1)
            score = cv2.mean(probability[y:y + bh, x:x + bw], mask)[0] / 255.0
            if score < box_threshold:
                continue
            # Equivalent of PaddleOCR's polygon offset for a rectangle.
            distance = w * h * unclip / (2 * (w + h))
            w, h = w + 2 * distance, h + 2 * distance
            if min(w, h) < 5:
                continue
            boxes.append(((cx, cy), (w, h), angle, score))
        return boxes

    # --------------------------------------------------------------- geometry
    @staticmethod
    def reading_quad(rect) -> np.ndarray:
        """Order a rotated rectangle as (tl, tr, br, bl) in reading direction.

        Long boxes read along their long side. Near-horizontal text reads left
        to right; near-vertical text is assumed to read bottom-to-top, the
        usual orientation of vertical drawing labels. The 0/180 classifier
        corrects the opposite case.
        """
        (cx, cy), (w, h), angle = rect[:3]
        points = cv2.boxPoints(((cx, cy), (w, h), angle)).astype(np.float64)
        edges = [points[1] - points[0], points[2] - points[1]]
        lengths = [float(np.hypot(*edge)) for edge in edges]
        if max(lengths) < 1.25 * max(min(lengths), 1e-6):
            index = 0 if abs(edges[0][0]) / max(lengths[0], 1e-6) >= abs(edges[1][0]) / max(lengths[1], 1e-6) else 1
        else:
            index = 0 if lengths[0] >= lengths[1] else 1
        long_length, short_length = lengths[index], lengths[1 - index]
        u = edges[index] / max(long_length, 1e-6)
        if abs(u[0]) >= abs(u[1]):
            if u[0] < 0:
                u = -u
        elif u[1] > 0:
            u = -u
        v = np.array((-u[1], u[0]))
        centre = np.array((cx, cy))
        half_u, half_v = u * long_length / 2, v * short_length / 2
        return np.array([centre - half_u - half_v, centre + half_u - half_v,
                         centre + half_u + half_v, centre - half_u + half_v])

    @staticmethod
    def rectify(gray: np.ndarray, quad: np.ndarray) -> np.ndarray:
        width = max(2, int(round(np.linalg.norm(quad[1] - quad[0]))))
        height = max(2, int(round(np.linalg.norm(quad[3] - quad[0]))))
        target = np.array([(0, 0), (width, 0), (width, height), (0, height)], np.float32)
        matrix = cv2.getPerspectiveTransform(quad.astype(np.float32), target)
        return cv2.warpPerspective(gray, matrix, (width, height), flags=cv2.INTER_CUBIC,
                                   borderMode=cv2.BORDER_REPLICATE)

    # ------------------------------------------------------------- classifier
    def _flipped(self, crops: list[np.ndarray], quads: list[np.ndarray]) -> list[bool]:
        if self.cls is None:
            return [False] * len(crops)
        result = []
        name = self.cls.get_inputs()[0].name
        height, width = CLS_SHAPE
        for start in range(0, len(crops), 16):
            batch = np.zeros((min(16, len(crops) - start), 3, height, width), np.float32)
            for row, crop in enumerate(crops[start:start + 16]):
                resized_w = min(width, max(1, math.ceil(height * crop.shape[1] / max(crop.shape[0], 1))))
                resized = cv2.resize(crop, (resized_w, height)).astype(np.float32)
                batch[row, :, :, :resized_w] = (resized / 255.0 - 0.5) / 0.5
            probabilities = self.cls.run(None, {name: batch})[0]
            for offset, scores in enumerate(probabilities):
                quad = quads[start + offset]
                direction = quad[1] - quad[0]
                horizontal = abs(direction[0]) >= abs(direction[1])
                # Upside-down text is rare on a properly oriented page, so a
                # horizontal line needs stronger evidence than a vertical one.
                threshold = 0.97 if horizontal else 0.9
                result.append(int(np.argmax(scores)) == 1 and float(scores[1]) >= threshold)
        return result

    # ------------------------------------------------------------ recognition
    def _recognise(self, crops: list[np.ndarray]) -> list[tuple[str, float, list[tuple[str, float, float]]]]:
        """Return (text, confidence, [(char, centre_x_fraction, prob)]) per crop."""
        name = self.rec.get_inputs()[0].name
        order = sorted(range(len(crops)), key=lambda i: crops[i].shape[1] / max(crops[i].shape[0], 1))
        results: list = [None] * len(crops)
        for start in range(0, len(order), REC_BATCH):
            indices = order[start:start + REC_BATCH]
            widths = [min(REC_MAX_WIDTH, max(8, math.ceil(REC_HEIGHT * crops[i].shape[1] / max(crops[i].shape[0], 1))))
                      for i in indices]
            padded = max(max(widths), 32)
            batch = np.zeros((len(indices), 3, REC_HEIGHT, padded), np.float32)
            for row, (index, width) in enumerate(zip(indices, widths)):
                resized = cv2.resize(crops[index], (width, REC_HEIGHT), interpolation=cv2.INTER_CUBIC
                                     if crops[index].shape[0] < REC_HEIGHT else cv2.INTER_AREA)
                batch[row, :, :, :width] = (resized.astype(np.float32) / 255.0 - 0.5) / 0.5
            output = self.rec.run(None, {name: batch})[0]
            steps = output.shape[1]
            stride = padded / steps
            labels = output.argmax(axis=2)
            peaks = output.max(axis=2)
            for row, (index, width) in enumerate(zip(indices, widths)):
                characters, scores = [], []
                previous = -1
                for step in range(steps):
                    label = int(labels[row, step])
                    if label != previous and label != 0:
                        centre = min(1.0, (step + 0.5) * stride / width)
                        characters.append((self.characters[label], centre, float(peaks[row, step])))
                        scores.append(float(peaks[row, step]))
                    previous = label
                text = "".join(item[0] for item in characters)
                results[index] = (text, float(np.mean(scores)) if scores else 0.0, characters)
        return results

    # ------------------------------------------------------------- pipeline
    def detect(self, gray: np.ndarray, dpi: float, *, check_cancel=None, progress=None) -> list[np.ndarray]:
        """Return reading-order quads of text lines in ``gray`` pixel units."""
        scale = min(2.0, max(0.5, 300.0 / max(dpi, 1.0)))
        quads, heights = self._detect_at(gray, scale, check_cancel=check_cancel, progress=progress)
        if heights:
            median = float(np.median(heights)) * scale
            # Very small text (screenshots, low-DPI scans) is detected more
            # reliably after enlargement; bounded by a working-pixel budget.
            if median < 16:
                budget = math.sqrt(120_000_000 / max(gray.size, 1))
                larger = min(3.0, budget, scale * 26.0 / max(median, 4.0))
                if larger > scale * 1.3:
                    if progress:
                        progress("Нейросеть: мелкий текст, повторный поиск строк с увеличением")
                    quads, _ = self._detect_at(gray, larger, check_cancel=check_cancel, progress=progress)
        return quads

    def _detect_at(self, gray, scale, *, check_cancel=None, progress=None):
        if abs(scale - 1.0) > 0.02:
            interpolation = cv2.INTER_AREA if scale < 1 else cv2.INTER_CUBIC
            working = cv2.resize(gray, (max(32, round(gray.shape[1] * scale)), max(32, round(gray.shape[0] * scale))),
                                 interpolation=interpolation)
        else:
            scale, working = 1.0, gray
        probability = self.probability_map(working, check_cancel=check_cancel, progress=progress)
        _check(check_cancel)
        quads, heights = [], []
        for (cx, cy), (w, h), angle, _score in self.boxes_from_probability(probability):
            rect = ((cx / scale, cy / scale), (w / scale, h / scale), angle)
            quad = self.reading_quad(rect)
            quads.append(quad)
            heights.append(float(np.linalg.norm(quad[3] - quad[0])))
        quads.sort(key=lambda q: (round(float(q[:, 1].min()) / 8), float(q[:, 0].min())))
        return quads, heights

    def read(self, gray: np.ndarray, dpi: float = 300.0, *, min_confidence: float = 0.5,
             check_cancel: Callable[[], None] | None = None,
             progress: Callable[[str], None] | None = None) -> list[OcrSegment]:
        """Detect and recognise text in a grayscale uint8 image."""
        if gray.ndim != 2:
            raise ValueError("Ожидается одноканальное изображение.")
        with self._lock:
            quads = self.detect(gray, dpi, check_cancel=check_cancel, progress=progress)
            if not quads:
                return []
            crops = []
            for number, quad in enumerate(quads):
                if number % 64 == 0:
                    _check(check_cancel)
                crop = self.rectify(gray, quad)
                if crop.shape[0] > crop.shape[1] * 1.5:
                    # Short vertical box that reading_quad could not orient.
                    quad = np.roll(quad, -1, axis=0)
                    crop = self.rectify(gray, quad)
                quads[number] = quad
                crops.append(crop)
            if progress:
                progress(f"Нейросеть: распознавание {len(crops)} строк текста")
            for number, flipped in enumerate(self._flipped(crops, quads)):
                if flipped:
                    quads[number] = np.roll(quads[number], 2, axis=0)
                    crops[number] = cv2.rotate(crops[number], cv2.ROTATE_180)
            recognised = []
            for start in range(0, len(crops), 64):
                _check(check_cancel)
                recognised.extend(self._recognise(crops[start:start + 64]))
            # Vertical labels read either way on drawings, and short ones defeat
            # the direction classifier. Read them in both directions and keep
            # the more confident reading.
            vertical = [number for number, quad in enumerate(quads)
                        if abs((quad[1] - quad[0])[1]) > abs((quad[1] - quad[0])[0])]
            if vertical:
                _check(check_cancel)
                turned = self._recognise([cv2.rotate(crops[number], cv2.ROTATE_180) for number in vertical])
                for number, alternative in zip(vertical, turned):
                    if alternative[1] > recognised[number][1] + 0.02:
                        recognised[number] = alternative
                        quads[number] = np.roll(quads[number], 2, axis=0)
                        crops[number] = cv2.rotate(crops[number], cv2.ROTATE_180)
            segments = []
            for number, (text, confidence, characters) in enumerate(recognised):
                if not text.strip() or confidence < min_confidence:
                    continue
                segments.extend(_segments(text, confidence, characters, quads[number],
                                          crops[number], min_confidence))
            return segments


def _ink_extent(crop: np.ndarray) -> tuple[np.ndarray, int, int]:
    """Binary ink mask and the vertical range holding 98% of the ink."""
    if crop.size == 0:
        return np.zeros_like(crop, dtype=bool), 0, crop.shape[0]
    _, binary = cv2.threshold(crop, 0, 255, cv2.THRESH_BINARY_INV | cv2.THRESH_OTSU)
    ink = binary > 0
    # Pale pages: Otsu may split paper texture. Require real contrast.
    if crop.max() - crop.min() < 40:
        ink = crop < 128
    rows = ink.sum(axis=1).astype(np.float64)
    total = rows.sum()
    if total <= 0:
        return ink, 0, crop.shape[0]
    cumulative = np.cumsum(rows) / total
    top = int(np.searchsorted(cumulative, 0.01))
    bottom = int(np.searchsorted(cumulative, 0.99)) + 1
    return ink, max(0, top), min(crop.shape[0], max(top + 1, bottom))


def _longest_internal_gap(values: np.ndarray) -> int:
    """Longest blank run bounded by ink on both sides.

    CTC peaks may fall in the margin before the first glyph; a blank run that
    touches the interval boundary is margin, not a gap between words.
    """
    ink = np.flatnonzero(values)
    if ink.size < 2:
        return 0
    steps = np.diff(ink) - 1
    return int(steps.max()) if steps.size else 0


def _segments(text: str, confidence: float, characters, quad: np.ndarray, crop: np.ndarray,
              min_confidence: float) -> list[OcrSegment]:
    """Split a recognised line into phrases at wide gaps; tighten to the ink."""
    length = float(np.linalg.norm(quad[1] - quad[0]))
    height = float(np.linalg.norm(quad[3] - quad[0]))
    if length <= 0 or height <= 0:
        return []
    u = (quad[1] - quad[0]) / length
    v = (quad[3] - quad[0]) / height
    ink, top, bottom = _ink_extent(crop)
    scale_x = crop.shape[1] / length
    # Words from the CTC alignment. A missed space is restored where the
    # strip has a blank gap clearly wider than this line's letter spacing.
    # Gaps use a permissive ink threshold: thin anti-aliased strokes (the bar
    # of "П" at low DPI) must not look like a blank gap inside a word.
    otsu, _ = cv2.threshold(crop, 0, 255, cv2.THRESH_BINARY | cv2.THRESH_OTSU)
    loose = crop < min(250.0, otsu + (float(np.median(crop)) - otsu) * 0.6)
    columns_with_ink = (loose[top:bottom].any(axis=0) if bottom > top
                        else np.zeros(crop.shape[1], bool))
    positions = [(character, centre * length, probability) for character, centre, probability in characters]
    gaps = []
    for (first, x0, _), (second, x1, _) in zip(positions, positions[1:]):
        if first == " " or second == " ":
            gaps.append(None)
            continue
        between = columns_with_ink[int(x0 * scale_x):int(math.ceil(x1 * scale_x))]
        gaps.append(_longest_internal_gap(between) if between.size else 0)
    measured = [gap for gap in gaps if gap is not None]
    letter_gap = float(np.median(measured)) if measured else 0.0
    space_width = max(3.0, 0.3 * (bottom - top), 2.5 * letter_gap + 1)
    words: list[list] = []
    for number, (character, x, probability) in enumerate(positions):
        if character == " ":
            if words and words[-1]:
                words.append([])
            continue
        if number and words and words[-1] and gaps[number - 1] is not None and gaps[number - 1] >= space_width:
            words.append([])
        if not words:
            words.append([])
        words[-1].append((character, x, probability))
    words = [word for word in words if word]
    if not words:
        return []
    centres = [item[1] for word in words for item in word]
    gaps = np.diff(centres) if len(centres) > 1 else np.array([])
    pitch = float(np.median(gaps)) if gaps.size else min(length, height * 0.6)
    pitch = min(max(pitch, height * 0.25), height * 1.2)
    boxes = []
    for word in words:
        start = max(0.0, word[0][1] - pitch * 0.6)
        end = min(length, word[-1][1] + pitch * 0.6)
        # Snap to the actual ink columns inside the estimated interval.
        x0, x1 = int(start * scale_x), max(int(start * scale_x) + 1, int(math.ceil(end * scale_x)))
        columns = np.flatnonzero(ink[top:bottom, x0:x1].any(axis=0)) if x1 > x0 else np.array([])
        if columns.size:
            start, end = (x0 + columns[0]) / scale_x, (x0 + columns[-1] + 1) / scale_x
        boxes.append((start, end, "".join(item[0] for item in word),
                      float(np.mean([item[2] for item in word])),
                      float(min(item[2] for item in word))))
    # Phrases: break where the blank gap exceeds two line heights, which
    # separates independent drawing labels detected as one long line.
    ink_height = max(1.0, (bottom - top) / max(crop.shape[0] / height, 1e-6))
    phrases: list[list] = [[boxes[0]]]
    for box in boxes[1:]:
        if box[0] - phrases[-1][-1][1] > max(2.0 * ink_height, 1.5 * height):
            phrases.append([box])
        else:
            phrases[-1].append(box)
    top_offset = top / max(crop.shape[0] / height, 1e-6)
    bottom_offset = bottom / max(crop.shape[0] / height, 1e-6)
    angle = math.degrees(math.atan2(u[1], u[0]))
    result = []
    for phrase in phrases:
        phrase_text = " ".join(item[2] for item in phrase)
        phrase_conf = float(np.mean([item[3] for item in phrase]))
        if phrase_conf < min_confidence or not any(character.isalnum() for character in phrase_text):
            continue
        start, end = phrase[0][0], phrase[-1][1]
        corner = quad[0]
        phrase_quad = np.array([
            corner + u * start + v * top_offset, corner + u * end + v * top_offset,
            corner + u * end + v * bottom_offset, corner + u * start + v * bottom_offset,
        ])
        # Per word: text, quad and the probability of its least certain
        # character, which pinpoints a single misread glyph.
        word_quads = [(item[2], np.array([
            corner + u * item[0] + v * top_offset, corner + u * item[1] + v * top_offset,
            corner + u * item[1] + v * bottom_offset, corner + u * item[0] + v * bottom_offset,
        ]), item[4]) for item in phrase]
        result.append(OcrSegment(phrase_text, phrase_quad, phrase_conf, angle, height, word_quads))
    return result


_INSTANCE: NeuralOcr | None = None
_INSTANCE_ERROR: str | None = None
_INSTANCE_LOCK = threading.Lock()


def shared_engine() -> NeuralOcr | None:
    """Return a cached engine, or None when models/runtime are unavailable."""
    global _INSTANCE, _INSTANCE_ERROR
    with _INSTANCE_LOCK:
        if _INSTANCE is None and _INSTANCE_ERROR is None:
            try:
                _INSTANCE = NeuralOcr()
            except (ImportError, OSError, ValueError, RuntimeError) as error:
                _INSTANCE_ERROR = str(error) or type(error).__name__
        return _INSTANCE


def unavailable_reason() -> str | None:
    shared_engine()
    return _INSTANCE_ERROR


def reset_shared_engine() -> None:
    """Forget the cached engine (tests, changed model folder)."""
    global _INSTANCE, _INSTANCE_ERROR
    with _INSTANCE_LOCK:
        _INSTANCE, _INSTANCE_ERROR = None, None
