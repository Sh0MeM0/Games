"""Offline machine translation of extracted text (Argos Translate models).

Models are Argos Translate packages (CTranslate2-format Transformer +
SentencePiece) unpacked into ``<root>/models/translate/<from>_<to>/``. They
are executed by :mod:`nmt` with NumPy only; nothing is sent over the network. Text is grouped into lines and paragraphs before translation,
so sentences broken across lines are translated as a whole, and the result is
redistributed over the original lines to keep the drawing/page layout.
"""
from __future__ import annotations

import copy
from dataclasses import dataclass
import json
from pathlib import Path
import re
import threading
from typing import Callable

from .models import PdfPage, TextSpan
from .neural import models_root

LANGUAGES = {"ru": "русский", "en": "английский"}


def translation_dir() -> Path:
    return models_root() / "translate"


@dataclass(frozen=True)
class Package:
    source: str
    target: str
    path: Path
    target_prefix: str = ""


def available_packages(directory: Path | None = None) -> list[Package]:
    directory = Path(directory or translation_dir())
    packages = []
    if not directory.is_dir():
        return packages
    for metadata in sorted(directory.glob("*/metadata.json")):
        try:
            info = json.loads(metadata.read_text(encoding="utf-8"))
            folder = metadata.parent
            if not ((folder / "model" / "model.bin").is_file() and (folder / "sentencepiece.model").is_file()):
                continue
            packages.append(Package(str(info["from_code"]), str(info["to_code"]), folder,
                                    str(info.get("target_prefix", ""))))
        except (OSError, ValueError, KeyError, TypeError):
            continue
    return packages


def detect_language(text: str) -> str | None:
    letters = [c for c in text if c.isalpha()]
    if not letters:
        return None
    cyrillic = sum(1 for c in letters if "Ѐ" <= c <= "ӿ")
    latin = sum(1 for c in letters if c.isascii())
    if cyrillic >= max(1, 0.6 * len(letters)):
        lowered = text.lower()
        if ("ў" in lowered or "і" in lowered) and not any(c in lowered for c in "иъщ"):
            return "be"
        return "ru"
    if latin >= max(1, 0.6 * len(letters)):
        return "en"
    return None


def _needs_translation(text: str) -> bool:
    # Dimensions, axis marks, codes ("3600", "R150", "Ø25", "А-А", "ЛМ-1")
    # are kept verbatim; machine translation would only damage them.
    letters = sum(1 for c in text if c.isalpha())
    words = re.findall(r"[^\W\d_]{2,}", text)
    return letters >= 3 and bool(words) and not re.fullmatch(r"[\W\d_]*[A-Za-zА-Яа-яЁё]{1,2}[\W\d_]*", text)


_NUMBER = re.compile(r"\d+(?:[.,:/×x]\d+)*")
_CODE = re.compile(r"(?<!\w)[A-ZА-ЯЁІЎ]{1,3}(?:-[A-ZА-ЯЁІЎ0-9]{1,3})*(?!\w)")
_TRANSLIT = dict(zip("АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЫЭЮЯІЎ",
                     ["A", "B", "V", "G", "D", "E", "E", "Zh", "Z", "I", "Y", "K", "L", "M", "N", "O", "P",
                      "R", "S", "T", "U", "F", "Kh", "Ts", "Ch", "Sh", "Shch", "Y", "E", "Yu", "Ya", "I", "U"]))
_TO_LATIN_LOOKALIKE = str.maketrans("АВСЕНКМОРТХ", "ABCEHKMOPTX")


def _code_variants(token: str) -> set[str]:
    return {token, token.translate(_TO_LATIN_LOOKALIKE), "".join(_TRANSLIT.get(c, c) for c in token)}


def _restore(source: str, translated: str) -> str:
    """Keep numbers, axis letters and short codes the model dropped.

    Machine translation may silently drop a lone axis letter ("Ось А" →
    "Axis") or a section mark. Such tokens are appended unchanged; a
    transliterated or look-alike Latin form in the output counts as kept.
    """
    if re.search(r"[мm][²³]", source):
        translated = re.sub(r"\b[mм]([23])\b", lambda m: "m" + {"2": "²", "3": "³"}[m[1]], translated)
    digits_out = re.sub(r"\D", "", translated)
    missing: list[str] = []
    for token in _NUMBER.findall(source):
        if re.sub(r"\D", "", token) not in digits_out and token not in missing:
            missing.append(token)
    for token in _CODE.findall(source):
        letters = sum(1 for c in token if c.isalpha())
        if letters > 2 and "-" not in token:
            continue  # ordinary capitalised words are translated, not protected
        if any(re.search(r"(?<!\w)" + re.escape(variant) + r"(?!\w)", translated)
               for variant in _code_variants(token)):
            continue
        if token not in missing:
            missing.append(token)
    for token in missing:
        translated = f"{translated} {token}".strip()
    return _label_style(source, translated)


def _label_style(source: str, translated: str) -> str:
    """Keep the look of drawing labels: capitals, no invented article or full stop."""
    words = source.split()
    if len(words) <= 4:
        # Russian has no articles; a short label must not gain "The".
        translated = re.sub(r"^(?:The|A|An)\s+(?=[a-zа-я])", "", translated)
        translated = translated[:1].upper() + translated[1:]
    if not re.search(r"[.!?…]\s*$", source) and re.search(r"(?<![.!?…])\.$", translated):
        translated = translated[:-1]
    letters = [c for c in source if c.isalpha()]
    if len(letters) >= 3 and all(c.isupper() for c in letters):
        translated = translated.upper()
    return translated


_SENTENCE = re.compile(r"(?<=[.!?…])\s+(?=[\"«(]?[A-ZА-ЯЁ0-9])")


class Translator:
    """One translation direction; thread-safe, results cached per text."""

    def __init__(self, package: Package):
        import sentencepiece

        from .nmt import TransformerModel

        self.package = package
        self.model = TransformerModel(package.path / "model")
        self.tokenizer = sentencepiece.SentencePieceProcessor(model_file=str(package.path / "sentencepiece.model"))
        self.cache: dict[str, str] = {}
        self.lock = threading.Lock()

    def _decode(self, tokens: list[str]) -> str:
        text = self.tokenizer.decode_pieces(tokens).replace("\u2581", " ")
        prefix = self.package.target_prefix
        if prefix and text.startswith(prefix):
            text = text[len(prefix):]
        return text.strip()

    def translate(self, texts: list[str], check_cancel: Callable[[], None] | None = None) -> list[str]:
        with self.lock:
            pending = sorted({text for text in texts if text not in self.cache})
            sentences, owners = [], []
            for text in pending:
                parts = [part.strip() for part in _SENTENCE.split(text) if part.strip()] or [text]
                for part in parts:
                    sentences.append(part)
                    owners.append(text)
            translated: dict[str, list[str]] = {text: [] for text in pending}
            for start in range(0, len(sentences), 8):
                if check_cancel:
                    check_cancel()
                batch = sentences[start:start + 8]
                tokens = [self.tokenizer.encode(sentence, out_type=str)[:400] for sentence in batch]
                if self.package.target_prefix:
                    raise ValueError("Модели перевода с префиксом языка не поддерживаются.")
                results = self.model.translate_tokens(tokens, beam_size=4, length_penalty=0.2)
                for owner, result in zip(owners[start:start + 8], results):
                    translated[owner].append(self._decode(result))
            for text, parts in translated.items():
                self.cache[text] = " ".join(part for part in parts if part) or text
            return [self.cache.get(text, text) for text in texts]


_TRANSLATORS: dict[tuple[str, str], Translator] = {}
_TRANSLATORS_LOCK = threading.Lock()


def get_translator(source: str, target: str) -> Translator | None:
    with _TRANSLATORS_LOCK:
        key = (source, target)
        if key not in _TRANSLATORS:
            package = next((p for p in available_packages() if (p.source, p.target) == key), None)
            if package is None:
                return None
            _TRANSLATORS[key] = Translator(package)
        return _TRANSLATORS[key]


def release_translators() -> None:
    """Free model memory (0.2-0.6 GB per direction) after a document."""
    with _TRANSLATORS_LOCK:
        _TRANSLATORS.clear()


# --------------------------------------------------------------- layout groups
def _baseline(span: TextSpan) -> float:
    return span.origin[1] if span.origin else span.bbox[3]


def _left(span: TextSpan) -> float:
    return span.origin[0] if span.origin else span.bbox[0]


def _right(span: TextSpan) -> float:
    if span.advance is not None and span.origin is not None and not span.angle:
        return span.origin[0] + span.advance
    return span.bbox[2]


def group_lines(spans: list[TextSpan]) -> list[list[TextSpan]]:
    """Horizontal spans on one baseline with word-sized gaps form a line.

    Rotated spans (vertical/inclined labels) are kept as separate lines.
    """
    lines: list[list[TextSpan]] = []
    horizontal = sorted((span for span in spans if not span.angle and span.text.strip()),
                        key=lambda span: (_baseline(span), _left(span)))
    for span in horizontal:
        size = max(span.size, 0.1)
        best = None
        for line in lines[-12:]:
            last = line[-1]
            if (abs(_baseline(last) - _baseline(span)) <= 0.35 * size
                    and 0.66 <= last.size / size <= 1.5
                    and -0.3 * size <= _left(span) - _right(last) <= 1.6 * size):
                best = line
        if best is None:
            lines.append([span])
        else:
            best.append(span)
    lines.extend([span] for span in spans if span.angle and span.text.strip())
    return lines


def group_blocks(lines: list[list[TextSpan]]) -> list[list[list[TextSpan]]]:
    """Consecutive aligned lines of similar size form a paragraph."""
    blocks: list[list[list[TextSpan]]] = []
    for line in sorted((line for line in lines if not line[0].angle), key=lambda line: _baseline(line[0])):
        size = max(sum(span.size for span in line) / len(line), 0.1)
        left, right = min(_left(span) for span in line), max(_right(span) for span in line)
        placed = False
        for block in reversed(blocks[-6:]):
            previous = block[-1]
            previous_size = max(sum(span.size for span in previous) / len(previous), 0.1)
            previous_left = min(_left(span) for span in previous)
            previous_right = max(_right(span) for span in previous)
            distance = _baseline(line[0]) - _baseline(previous[0])
            overlap = min(right, previous_right) - max(left, previous_left)
            if (0 < distance <= 1.9 * max(size, previous_size)
                    and 0.8 <= size / previous_size <= 1.25
                    and (overlap >= 0.5 * min(right - left, previous_right - previous_left)
                         or abs(left - previous_left) <= 2 * size)):
                block.append(line)
                placed = True
                break
        if not placed:
            blocks.append([line])
    blocks.extend([line] for line in lines if line[0].angle)
    return blocks


def _line_text(line: list[TextSpan]) -> str:
    text = ""
    for span in line:
        piece = span.text.strip()
        if text and not text.endswith((" ", "-")):
            text += " "
        text += piece
    return text.strip()


def _block_text(block: list[list[TextSpan]]) -> str:
    result = ""
    for line in block:
        text = _line_text(line)
        if result.endswith("-") and text[:1].islower():
            result = result[:-1] + text  # hyphenated word across lines
        else:
            result = (result + " " + text).strip()
    return result


def _distribute(text: str, block: list[list[TextSpan]]) -> list[str]:
    """Split ``text`` over the block's lines in proportion to their lengths."""
    if len(block) == 1:
        return [text]
    words = text.split()
    weights = [max(1, len(_line_text(line))) for line in block]
    total = sum(weights)
    result, start = [], 0
    for number, weight in enumerate(weights):
        if number == len(weights) - 1:
            result.append(" ".join(words[start:]))
            break
        target = round(len(words) * sum(weights[:number + 1]) / total)
        target = max(start + (1 if start < len(words) else 0), min(target, len(words)))
        result.append(" ".join(words[start:target]))
        start = target
    return result


def _line_span(line: list[TextSpan], text: str) -> TextSpan:
    first = line[0]
    span = copy.copy(first)
    span.text = text
    if len(line) > 1:
        span.bbox = (min(s.bbox[0] for s in line), min(s.bbox[1] for s in line),
                     max(s.bbox[2] for s in line), max(s.bbox[3] for s in line))
        span.size = sorted(s.size for s in line)[len(line) // 2]
        if first.origin is not None:
            span.advance = max(1.0, _right(line[-1]) - first.origin[0])
        span.quad = None
    original = span.advance if span.advance else (span.bbox[2] - span.bbox[0])
    if original and original > 0:
        # Keep the translated line near its original length so it stays in
        # place on a drawing, but avoid extreme condensing or stretching.
        natural = 0.52 * span.size * len(text)
        fitted = min(max(natural, original * 0.75), original * 1.35)
        span.advance = fitted
        if not span.angle:
            span.bbox = (span.bbox[0], span.bbox[1], span.bbox[0] + fitted, span.bbox[3])
    return span


@dataclass
class TranslationStats:
    blocks: int = 0
    translated: int = 0
    unchanged: int = 0
    unsupported: int = 0


def translate_pages(pages: list[PdfPage], target: str, *, check_cancel=None, progress=None,
                    warnings: list[str] | None = None) -> tuple[list[PdfPage], TranslationStats]:
    """Return translated copies of ``pages``; the originals are not modified."""
    if target not in LANGUAGES:
        raise ValueError("Перевод поддерживается на русский (ru) или английский (en).")
    stats = TranslationStats()
    plans = []
    requests: dict[str, list[str]] = {}
    for page in pages:
        blocks = group_blocks(group_lines(page.texts))
        page_plan = []
        for block in blocks:
            text = _block_text(block)
            source = detect_language(text) if _needs_translation(text) else None
            stats.blocks += 1
            if source is None or source == target:
                stats.unchanged += 1
                page_plan.append((block, None, None))
                continue
            requests.setdefault(source, []).append(text)
            page_plan.append((block, source, text))
        plans.append(page_plan)
    results: dict[tuple[str, str], str] = {}
    missing = set()
    for source, texts in requests.items():
        translator = get_translator(source, target)
        if translator is None:
            missing.add(source)
            continue
        if progress:
            progress(f"Перевод {source}→{target}: фрагментов {len(texts)}")
        try:
            translated_texts = translator.translate(texts, check_cancel=check_cancel)
        finally:
            release_translators()
        for text, translated in zip(texts, translated_texts):
            results[(source, text)] = _restore(text, translated)
    if missing and warnings is not None:
        names = {"be": "белорусский", "ru": "русский", "en": "английский"}
        warnings.append("Нет офлайн-модели перевода для: " + ", ".join(names.get(code, code) for code in sorted(missing))
                        + f" → {names.get(target, target)}; такой текст оставлен без перевода.")
    translated_pages = []
    for page, page_plan in zip(pages, plans):
        new_page = copy.copy(page)
        new_page.warnings = list(page.warnings)
        texts: list[TextSpan] = []
        for block, source, text in page_plan:
            if source is None or (source, text) not in results:
                if source is not None:
                    stats.unsupported += 1
                texts.extend(span for line in block for span in line)
                continue
            stats.translated += 1
            for line, line_text in zip(block, _distribute(results[(source, text)], block)):
                if line_text:
                    texts.append(_line_span(line, line_text))
        new_page.texts = texts
        translated_pages.append(new_page)
    return translated_pages, stats
