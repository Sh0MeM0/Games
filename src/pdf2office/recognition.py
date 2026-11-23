"""Combined OCR: neural line detection/recognition with a Tesseract second opinion.

The neural engine (see :mod:`neural`) finds every text line at its real angle
and reads it. Each recognised phrase is then rectified, normalised to a common
glyph height and stacked with the others into strip images that Tesseract
reads in one pass with its dictionary-backed LSTM. Per phrase, the reading
with stronger support is kept. Tesseract therefore never has to segment a
noisy page itself (its slowest and least reliable step on scanned drawings).
"""
from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
import csv
import io
import math
import os
import re
from typing import Callable

import cv2
import numpy as np
from PIL import Image

from .models import TextSpan
from .neural import NeuralOcr, OcrSegment
from .text_quality import fix_mixed_scripts

STRIP_GLYPH_HEIGHT = 40
STRIP_MAX_WIDTH = 5000
STRIP_MAX_HEIGHT = 7000
STRIP_GAP = 26


def _check(callback) -> None:
    if callback:
        callback()


def _snap_angle(angle: float) -> float:
    for target in (0.0, 90.0, -90.0, 180.0, -180.0):
        if abs(angle - target) < 0.35:
            return 0.0 if abs(target) == 0 else (180.0 if abs(target) == 180 else target)
    return angle


def harmonize_angles(segments: list[OcrSegment], tolerance: float = 3.0) -> None:
    """Align short phrases with the sheet's dominant text direction.

    The rotated box of a short word ("Спальня") gives a noisy angle, while long
    lines measure the page direction precisely. Short phrases within
    ``tolerance`` degrees of that direction (or of a right angle to it) are
    turned about their centre to match it; genuinely inclined labels keep
    their own angle.
    """
    def size(segment):
        quad = segment.quad
        return float(np.linalg.norm(quad[1] - quad[0])), float(np.linalg.norm(quad[3] - quad[0]))

    long_angles = [segment.angle for segment in segments
                   if size(segment)[0] >= 6 * max(size(segment)[1], 1e-6) and abs(segment.angle) <= 15]
    dominant = float(np.median(long_angles)) if len(long_angles) >= 2 else 0.0
    for segment in segments:
        length, height = size(segment)
        if length >= 8 * max(height, 1e-6):
            continue
        for base in (dominant, dominant + 90, dominant - 90, dominant + 180, dominant - 180):
            difference = (segment.angle - base + 180) % 360 - 180
            if abs(difference) <= tolerance and difference:
                radians = math.radians(base)
                u = np.array((math.cos(radians), math.sin(radians)))
                v = np.array((-u[1], u[0]))
                centre = segment.quad.mean(axis=0)
                half_u, half_v = u * length / 2, v * height / 2
                segment.quad = np.array([centre - half_u - half_v, centre + half_u - half_v,
                                         centre + half_u + half_v, centre - half_u + half_v])
                segment.angle = base
                break


def segment_span(text: str, quad: np.ndarray, confidence: float, engine: str,
                 to_page: Callable[[np.ndarray], np.ndarray]) -> TextSpan | None:
    """Convert a pixel quad (tl, tr, br, bl) into a positioned OCR TextSpan."""
    points = to_page(np.asarray(quad, dtype=np.float64))
    if not np.all(np.isfinite(points)):
        return None
    direction = points[1] - points[0]
    advance = float(np.hypot(*direction))
    height = float(np.hypot(*(points[3] - points[0])))
    if advance <= 0 or height <= 0:
        return None
    angle = _snap_angle(math.degrees(math.atan2(direction[1], direction[0])))
    bbox = (float(points[:, 0].min()), float(points[:, 1].min()),
            float(points[:, 0].max()), float(points[:, 1].max()))
    return TextSpan(text, bbox, "Liberation Sans", max(1.0, height), (0, 0, 0),
                    origin=(float(points[3][0]), float(points[3][1])), angle=angle,
                    from_ocr=True, advance=advance,
                    quad=tuple((float(x), float(y)) for x, y in points),
                    confidence=float(confidence), engine=engine)


def _strip_crop(gray: np.ndarray, segment: OcrSegment) -> np.ndarray:
    quad = segment.quad
    length = float(np.linalg.norm(quad[1] - quad[0]))
    height = float(np.linalg.norm(quad[3] - quad[0]))
    u = (quad[1] - quad[0]) / max(length, 1e-6)
    v = (quad[3] - quad[0]) / max(height, 1e-6)
    pad_v, pad_u = height * 0.22 + 1, height * 0.35 + 2
    padded = np.array([quad[0] - u * pad_u - v * pad_v, quad[1] + u * pad_u - v * pad_v,
                       quad[2] + u * pad_u + v * pad_v, quad[3] - u * pad_u + v * pad_v])
    crop = NeuralOcr.rectify(gray, padded)
    factor = STRIP_GLYPH_HEIGHT / max(height, 1.0)
    factor = min(factor, STRIP_MAX_WIDTH / max(crop.shape[1], 1))
    size = (max(1, round(crop.shape[1] * factor)), max(1, round(crop.shape[0] * factor)))
    interpolation = cv2.INTER_CUBIC if factor > 1 else cv2.INTER_AREA
    return cv2.resize(crop, size, interpolation=interpolation)


def parallel_map(function, items):
    """Run independent Tesseract jobs on several cores; yield in input order.

    Each Tesseract process uses one OpenMP thread (see extract.tesseract_environment),
    so separate processes are the efficient way to use a multi-core CPU.
    """
    items = list(items)
    workers = max(1, min(4, os.cpu_count() or 1, len(items)))
    if workers == 1:
        for item in items:
            yield function(item)
        return
    with ThreadPoolExecutor(max_workers=workers, thread_name_prefix="pdf-master-ocr") as pool:
        futures = [pool.submit(function, item) for item in items]
        try:
            for future in futures:
                yield future.result()
        finally:
            for future in futures:
                future.cancel()


def tesseract_readings(gray: np.ndarray, segments: list[OcrSegment], environment, language: str, *,
                       check_cancel=None, progress=None) -> list[tuple | None]:
    """Read every phrase with Tesseract, batching phrases into strip images."""
    crops = [_strip_crop(gray, segment) for segment in segments]
    return tesseract_read_crops(crops, environment, language, check_cancel=check_cancel, progress=progress)


def tesseract_read_crops(crops: list[np.ndarray], environment, language: str, *,
                         check_cancel=None, progress=None) -> list[tuple | None]:
    """Tesseract readings (text, confidence, [(word, confidence)]) of upright crops."""
    from .extract import PdfExtractionError, _run_tesseract

    executable, options, cwd, _ = environment
    readings: list[tuple[str, float] | None] = [None] * len(crops)
    batches, current, height = [], [], 0
    for index, crop in enumerate(crops):
        if current and height + crop.shape[0] + STRIP_GAP > STRIP_MAX_HEIGHT:
            batches.append(current)
            current, height = [], 0
        current.append(index)
        height += crop.shape[0] + STRIP_GAP
    if current:
        batches.append(current)
    def run_strip(batch):
        width = max(crops[index].shape[1] for index in batch) + 2 * STRIP_GAP
        total = sum(crops[index].shape[0] + STRIP_GAP for index in batch) + STRIP_GAP
        strip = np.full((total, width), 255, np.uint8)
        rows = []
        y = STRIP_GAP
        for index in batch:
            crop = crops[index]
            strip[y:y + crop.shape[0], STRIP_GAP:STRIP_GAP + crop.shape[1]] = crop
            rows.append((y, y + crop.shape[0], index))
            y += crop.shape[0] + STRIP_GAP
        stream = io.BytesIO()
        # A transient image: fast compression costs nothing in quality.
        Image.fromarray(strip).save(stream, format="PNG", compress_level=1)
        run = _run_tesseract([str(executable), "stdin", "stdout", *options, "-l", language,
                              "--dpi", "300", "--psm", "6", "-c", "tessedit_create_tsv=1"],
                             input_data=stream.getvalue(), cwd=cwd, check_cancel=check_cancel)
        if run.returncode:
            raise PdfExtractionError("Ошибка распознавания Tesseract: " + run.stderr.strip()[-1200:])
        return rows, run.stdout

    finished = 0
    for rows, output in parallel_map(run_strip, batches):
        finished += 1
        _check(check_cancel)
        if progress and len(batches) > 1:
            progress(f"Tesseract: проверка распознанных строк, пакет {finished} из {len(batches)}")
        words: dict[int, list[tuple[float, str, float]]] = {}
        for record in csv.DictReader(io.StringIO(output), delimiter="\t", quoting=csv.QUOTE_NONE):
            text = (record.get("text") or "").strip()
            if record.get("level") != "5" or not text:
                continue
            try:
                confidence = float(record["conf"])
                left, top, word_height = float(record["left"]), float(record["top"]), float(record["height"])
            except (KeyError, TypeError, ValueError):
                continue
            centre = top + word_height / 2
            for start, end, index in rows:
                if start - STRIP_GAP / 2 <= centre < end + STRIP_GAP / 2:
                    words.setdefault(index, []).append((left, text, max(0.0, confidence) / 100))
                    break
        for index, items in words.items():
            items.sort()
            text = " ".join(item[1] for item in items)
            weights = [max(1, len(item[1])) for item in items]
            confidence = float(np.average([item[2] for item in items], weights=weights))
            readings[index] = (text, confidence, [(item[1], item[2]) for item in items])
    return readings


def read_crops(engine: NeuralOcr, crops: list[np.ndarray], environment, language: str, *,
               check_cancel=None) -> list[tuple | None]:
    """Read clean upright phrase images with the network and Tesseract.

    Each result is (text, confidence, engine, extent, positions): ``extent``
    is (first, last, pitch) of the network's character centres as fractions
    of the image width, ``positions`` the network's own text with one centre
    per character (or None).
    """
    if not crops:
        return []
    neural = engine.recognise_crops(crops)
    tesseract: list = [None] * len(crops)
    if environment is not None:
        tesseract = tesseract_read_crops(crops, environment, language, check_cancel=check_cancel)
    result = []
    for (text, confidence, characters), reading in zip(neural, tesseract):
        words, current = [], []
        for character, _centre, probability in characters:
            if character == " ":
                if current:
                    words.append(current)
                current = []
            else:
                current.append((character, probability))
        if current:
            words.append(current)
        neural_words = [("".join(c for c, _ in word), min(p for _, p in word)) for word in words]
        if not text.strip() and reading is None:
            result.append(None)
            continue
        chosen = choose_reading(" ".join(word for word, _ in neural_words) or text, confidence,
                                reading, neural_words)
        # Where the network saw characters, as fractions of the crop width.
        centres = [centre for character, centre, _ in characters if character != " "]
        extent = None
        if centres:
            pitch = float(np.median(np.diff(centres))) if len(centres) > 1 else None
            extent = (min(centres), max(centres), pitch)
        # The network's own text with one position per character, so word
        # spaces it dropped can be restored from the drawing.
        positions = (text, [centre for _, centre, _ in characters]) if len(characters) == len(text) else None
        result.append((*chosen, extent, positions))
    return result


_LOOKALIKES = str.maketrans("AaBCcEeHKMOoPpTXxy", "АаВСсЕеНКМОоРрТХху")


def _normalized(text: str) -> str:
    # Latin and Cyrillic look-alikes ("C"/"С") are the same reading.
    return re.sub(r"\s+", "", text).replace("ё", "е").replace("Ё", "Е").translate(_LOOKALIKES)


# Latin letter pairs Tesseract's English model reads isolated Cyrillic
# capitals as ("П" -> "IT"); used only to compare short readings.
_TESSERACT_CYRILLIC = (("IT", "П"), ("II", "П"), ("TI", "П"), ("JI", "Л"), ("bI", "Ы"), ("bl", "Ы"),
                       ("IO", "Ю"), ("LU", "Ш"))


def _same_reading(neural_text: str, tess_text: str) -> bool:
    if _normalized(tess_text) == _normalized(neural_text):
        return True
    compact = re.sub(r"\s+", "", neural_text)
    if len(compact) > 3 or not any("\u0400" <= c <= "\u04ff" for c in compact):
        return False
    converted = tess_text
    for latin, cyrillic in _TESSERACT_CYRILLIC:
        converted = converted.replace(latin, cyrillic)
    return _normalized(converted) == _normalized(neural_text)


def choose_reading(neural_text: str, neural_conf: float, tesseract, neural_words=None) -> tuple[str, float, str]:
    """Pick or merge two readings of one phrase: (text, confidence, engine).

    ``tesseract`` is (text, confidence[, [(word, confidence), ...]]) or None;
    ``neural_words`` lists (word, least-certain-character probability).
    """
    if tesseract is None or not tesseract[0].strip():
        return neural_text, neural_conf, "neural"
    tess_text, tess_conf = tesseract[0], tesseract[1]
    tess_words = tesseract[2] if len(tesseract) > 2 else None
    if _normalized(tess_text) == _normalized(neural_text):
        # Same characters: Tesseract's word spacing is the more reliable one;
        # between look-alike alphabets the Cyrillic reading is kept.
        cyrillic = lambda value: sum("\u0400" <= c <= "\u04ff" for c in value)
        text = tess_text if cyrillic(tess_text) >= cyrillic(neural_text) else neural_text
        return text, max(neural_conf, tess_conf), "neural+tesseract"
    if _same_reading(neural_text, tess_text):
        # The same short Cyrillic text, written by Tesseract in Latin pairs.
        return neural_text, max(neural_conf, tess_conf), "neural+tesseract"
    # Word by word first: where the network was unsure of a character, or
    # produced a word shape print never has, and Tesseract (with its
    # dictionary) offers a comparable word, take Tesseract's word.
    if neural_words and tess_words and len(neural_words) == len(tess_words):
        merged, changed = [], False
        for (neural_word, neural_min), (tess_word, tess_word_conf) in zip(neural_words, tess_words):
            # Same length, or a broken neural word ("Осb"): Tesseract often
            # appends a frame line as "l"/"|" ("A" -> "Al"), which is refused.
            broken = suspicious_words(neural_word) > suspicious_words(tess_word)
            comparable = (len(neural_word) == len(tess_word)
                          or (abs(len(neural_word) - len(tess_word)) <= 1 and broken))
            if neural_word != tess_word and (
                    _diacritic_variant(neural_word, tess_word)
                    or (comparable and broken and tess_word_conf >= 0.6)
                    or (comparable and neural_min < 0.85 and tess_word_conf >= 0.88)):
                merged.append(tess_word)
                changed = True
            else:
                merged.append(neural_word)
        if changed:
            return " ".join(merged), neural_conf, "neural+tesseract"
        return neural_text, neural_conf, "neural"
    neural_alnum = sum(c.isalnum() for c in neural_text)
    tess_alnum = sum(c.isalnum() for c in tess_text)
    # Different word counts: Tesseract replaces the whole reading only with
    # clearly stronger support and a comparable amount of text; a dropped or
    # invented word changes the length and is rejected.
    similar_length = abs(tess_alnum - neural_alnum) <= max(1, 0.15 * neural_alnum)
    if similar_length and tess_conf >= 0.90 and tess_conf > neural_conf + 0.12:
        return tess_text, tess_conf, "tesseract"
    # A word shape that print never has ("Разре3": a digit inside a long
    # word, or Latin and Cyrillic mixed) marks a misread character.
    if similar_length and tess_conf >= 0.80 and suspicious_words(tess_text) < suspicious_words(neural_text):
        return tess_text, tess_conf, "tesseract"
    return neural_text, neural_conf, "neural"


_DIACRITIC_PAIRS = {frozenset("ий"), frozenset("ИЙ"), frozenset("её"), frozenset("ЕЁ"),
                    frozenset("уў"), frozenset("УЎ")}


def _diacritic_variant(first: str, second: str) -> bool:
    """Words that differ only in a breve/diaeresis (толщинои / толщиной).

    The recogniser often drops these small marks; Tesseract checks the word
    against its dictionary, so its variant is preferred.
    """
    if len(first) != len(second) or first == second:
        return False
    differences = [(a, b) for a, b in zip(first, second) if a != b]
    return all(frozenset((a, b)) in _DIACRITIC_PAIRS for a, b in differences)


def suspicious_words(text: str) -> int:
    count = 0
    for word in re.findall(r"\w+", text):
        letters = [c for c in word if c.isalpha()]
        digits = sum(c.isdigit() for c in word)
        cyrillic = any("\u0400" <= c <= "\u04ff" for c in letters)
        latin = any(c.isascii() for c in letters)
        if (len(letters) >= 3 and digits) or (cyrillic and latin):
            count += 1
    return count


def recognise(gray: np.ndarray, dpi: float, to_page: Callable[[np.ndarray], np.ndarray], *,
              engine: NeuralOcr, environment=None, language: str = "rus+eng",
              messages: list[str] | None = None, check_cancel=None, progress=None) -> list[TextSpan]:
    """Neural OCR of a grayscale page image, optionally cross-checked by Tesseract."""
    segments = engine.read(gray, dpi, check_cancel=check_cancel, progress=progress)
    harmonize_angles(segments)
    readings: list = [None] * len(segments)
    if environment is not None and segments:
        try:
            readings = tesseract_readings(gray, segments, environment, language,
                                          check_cancel=check_cancel, progress=progress)
        except (ValueError, OSError) as error:  # PdfExtractionError is a ValueError
            if messages is not None:
                messages.append(f"Проверка строк Tesseract пропущена: {error}")
    spans = []
    replaced = 0
    for segment, reading in zip(segments, readings):
        text, confidence, source = choose_reading(segment.text, segment.confidence, reading,
                                                  [(word[0], word[2]) for word in segment.words])
        if source == "tesseract":
            replaced += 1
        text = fix_mixed_scripts(text)
        span = segment_span(text, segment.quad, confidence, source, to_page)
        if span is not None:
            spans.append(span)
    if messages is not None and segments:
        rotated = sum(1 for span in spans if span.angle not in (0.0,))
        message = (f"Нейросетевой OCR: распознано фраз {len(spans)}"
                   + (f", из них повёрнутых/наклонных {rotated}" if rotated else "")
                   + (f"; по словарю Tesseract уточнено {replaced}" if replaced else "") + ".")
        messages.append(message)
    return spans
