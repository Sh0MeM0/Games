"""Text clean-up shared by OCR and PDF extraction.

* ``fix_mixed_scripts`` repairs words where OCR mixed Latin and Cyrillic
  look-alike letters (``Осb`` → ``Ось``, ``CTEHA`` stays Latin only when the
  word has no Cyrillic evidence).
* ``fix_mojibake`` re-decodes text that was stored in one code page and read
  in another (``Ïðèâåò`` cp1251-as-cp1252, ``РџСЂРёРІРµС‚`` UTF-8-as-cp1251,
  ``рТЙЧЕФ`` KOI8-R-as-cp1251). A fix is accepted only if the result reads
  measurably more like Russian/Belarusian/English text.
* ``is_garbled`` flags text that cannot be repaired by re-decoding: private
  use area glyph codes, replacement and control characters from PDFs without
  a usable ToUnicode map. Such text is re-read from the page image by OCR.
"""
from __future__ import annotations

import math
import re
import unicodedata

# Look-alike letters in common print faces. Lower-case "b" is the usual
# misreading of the Cyrillic soft sign.
_LATIN_LOOKALIKE = "AaBCcEeHKMOoPpTXxyb"
_CYRILLIC_LOOKALIKE = "АаВСсЕеНКМОоРрТХхуь"
_TO_CYRILLIC = str.maketrans(_LATIN_LOOKALIKE, _CYRILLIC_LOOKALIKE)
_TO_LATIN = str.maketrans(_CYRILLIC_LOOKALIKE, _LATIN_LOOKALIKE)
_AMBIGUOUS = set(_LATIN_LOOKALIKE + _CYRILLIC_LOOKALIKE)


def _is_cyrillic(character: str) -> bool:
    return "Ѐ" <= character <= "ӿ"


def _is_latin(character: str) -> bool:
    return ("a" <= character.lower() <= "z") and character.isascii()


def _evidence(word: str) -> tuple[int, int]:
    """Unambiguous Cyrillic and Latin letters in ``word``."""
    cyrillic = sum(1 for c in word if _is_cyrillic(c) and c not in _AMBIGUOUS)
    latin = sum(1 for c in word if _is_latin(c) and c not in _AMBIGUOUS)
    return cyrillic, latin


def _script_letters(word: str) -> tuple[int, int]:
    return (sum(1 for c in word if _is_cyrillic(c)), sum(1 for c in word if _is_latin(c)))


_CAPITAL_LOOKALIKES = set("АВЕКМНОРСТХABEKMHOPCTX")


def _cyrillic_capitals(token: str) -> str:
    """"PA3PE3" -> "РАЗРЕЗ": capitals mixed with 3/0 inside one word.

    In printed Russian capitals the letter З and the digit 3 (О and 0) look
    the same. A word of at least three capitals with such digits and no
    other digits, and with Cyrillic evidence or only look-alike letters, is
    a Russian word; codes such as "C3" or "M20" (fewer letters, or other
    digits) are left untouched.
    """
    letters = [c for c in token if c.isalpha()]
    digits = [c for c in token if c.isdigit()]
    if len(letters) < 3 or not digits or any(d not in "30" for d in digits):
        return token
    if not all(c.isupper() for c in letters):
        return token
    if not all(c in _CAPITAL_LOOKALIKES or "\u0400" <= c <= "\u04ff" for c in letters):
        return token
    core = token.strip(".,:;-()")
    if not core or not core[0].isalpha() and core[0] not in "30":
        return token
    return token.translate(_TO_CYRILLIC).replace("3", "З").replace("0", "О")


# Greek letters that readers put into Cyrillic or Latin words ("κПа", "Mπa").
_GREEK_LOOKALIKE = str.maketrans("αβεηικνοπρτυχΑΒΕΗΙΚΜΝΟΠΡΤΥΧ", "авенікvопртухАВЕНІКМNОПРТYХ")


def _is_greek(character: str) -> bool:
    return "\u0370" <= character <= "\u03ff"


def _degreek(token: str) -> str:
    """Greek look-alikes inside a Cyrillic or Latin word become Cyrillic."""
    if not any(_is_greek(c) for c in token):
        return token
    if not any(_is_cyrillic(c) or _is_latin(c) for c in token):
        return token  # a Greek symbol on its own (φ, γ, Δ) is kept
    return token.translate(_GREEK_LOOKALIKE)


def fix_mixed_scripts(text: str) -> str:
    """Make each word use one alphabet when the evidence is unambiguous."""
    if not text or not any(c.isalpha() for c in text):
        return text
    tokens = [_cyrillic_capitals(_degreek(token)) for token in re.split(r"(\s+)", text)]
    # Pass 1: words that mix alphabets follow their unambiguous letters, or
    # failing that, the clear majority of their letters ("Осb" -> "Ось").
    for index, token in enumerate(tokens):
        cyrillic_letters, latin_letters = _script_letters(token)
        if not (cyrillic_letters and latin_letters):
            continue
        cyrillic, latin = _evidence(token)
        if cyrillic and not latin:
            tokens[index] = token.translate(_TO_CYRILLIC)
        elif latin and not cyrillic:
            tokens[index] = token.translate(_TO_LATIN)
        elif not cyrillic and not latin:
            if cyrillic_letters > latin_letters:
                tokens[index] = token.translate(_TO_CYRILLIC)
            elif latin_letters > cyrillic_letters:
                tokens[index] = token.translate(_TO_LATIN)
    # Pass 2: words made only of look-alikes follow a phrase that is clearly
    # written in the other alphabet ("Ось A" -> "Ось А", "Section A-A" kept).
    total_cyrillic = sum(_script_letters(token)[0] for token in tokens)
    total_latin = sum(_script_letters(token)[1] for token in tokens)
    evidence_cyrillic = sum(_evidence(token)[0] for token in tokens)
    evidence_latin = sum(_evidence(token)[1] for token in tokens)
    for index, token in enumerate(tokens):
        cyrillic_letters, latin_letters = _script_letters(token)
        if _evidence(token) != (0, 0) or not (cyrillic_letters or latin_letters):
            continue
        if latin_letters and not evidence_latin and total_cyrillic >= 2 * max(1, total_latin):
            tokens[index] = token.translate(_TO_CYRILLIC)
        elif cyrillic_letters and not evidence_cyrillic and total_latin >= 2 * max(1, total_cyrillic):
            tokens[index] = token.translate(_TO_LATIN)
    return "".join(tokens)


def prefer_page_script(spans) -> int:
    """Lone look-alike letters follow the alphabet of their page.

    A single "M", "C" or "P" looks the same in Latin and Cyrillic, and the
    readers do not always agree on it. All lone readings of one such letter
    on a page are the same shape, so they get one alphabet: the one most of
    them were read in, where the clearly dominant alphabet of the page
    (unambiguous letters, two to one) counts as one more vote. "X" is left alone (it is also the
    Roman numeral ten), as are words with digits ("C3", "M20"). Only OCR
    spans are changed (``span.text`` in place); returns the number changed.
    """
    cyrillic = latin = 0
    votes: dict[str, list[int]] = {}
    lone = []
    for span in spans:
        found = _evidence(span.text)
        cyrillic += found[0]
        latin += found[1]
        core = span.text.strip().strip(".,:;()«»\"'")
        if len(core) == 1 and (core in _LATIN_LOOKALIKE or core in _CYRILLIC_LOOKALIKE) and core not in "XxХх":
            key = core.translate(_TO_CYRILLIC)
            tally = votes.setdefault(key, [0, 0])
            tally[0 if _is_cyrillic(core) else 1] += 1
            if getattr(span, "from_ocr", False):
                lone.append((span, core, key))
    page = "cyrillic" if cyrillic >= 2 * max(1, latin) else "latin" if latin >= 2 * max(1, cyrillic) else None
    changed = 0
    for span, core, key in lone:
        as_cyrillic, as_latin = votes[key]
        # The page's alphabet counts as one more vote.
        score = as_cyrillic - as_latin + (1 if page == "cyrillic" else -1 if page == "latin" else 0)
        if score == 0:
            continue
        script = "cyrillic" if score > 0 else "latin"
        target = core.translate(_TO_CYRILLIC) if script == "cyrillic" else core.translate(_TO_LATIN)
        if target != core:
            span.text = span.text.replace(core, target)
            changed += 1
    return changed


# Relative letter frequencies (per mille) of Russian and English prose; used
# only to rank re-decoding candidates, not to judge spelling.
_RUSSIAN = dict(zip("оеаинтсрвлкмдпуяыьгзбчйхжшюцщэфъё",
                    (110, 85, 80, 74, 67, 63, 55, 47, 45, 44, 35, 32, 30, 28, 26, 20, 19, 17, 17, 16,
                     16, 14, 12, 10, 9, 7, 6, 4, 3, 3, 3, 1, 1)))
_ENGLISH = dict(zip("etaoinshrdlcumwfgypbvkjxqz",
                    (127, 91, 82, 75, 70, 67, 63, 61, 60, 43, 40, 28, 28, 24, 24, 22, 20, 20, 19, 15,
                     10, 8, 2, 2, 1, 1)))
_BELARUSIAN_EXTRA = {"і": 50, "ў": 10}


def readability(text: str) -> float:
    """Mean log-probability per visible character; higher is more natural.

    Letters are scored by Russian (or English) letter frequency, digits and
    ordinary punctuation are neutral and any other symbol (box drawing,
    accented Latin in Cyrillic context, control codes) is heavily penalized.
    """
    visible = [c for c in text if not c.isspace()]
    if not visible:
        return 0.0
    letters = [c.lower() for c in visible if c.isalpha()]
    cyrillic = sum(1 for c in letters if _is_cyrillic(c))
    table = dict(_RUSSIAN, **_BELARUSIAN_EXTRA) if letters and cyrillic >= len(letters) / 2 else _ENGLISH
    total = sum(table.values())
    score = 0.0
    for c in visible:
        lower = c.lower()
        if c.isalpha() and (_is_cyrillic(c) or _is_latin(c)):
            score += math.log((table.get(lower, 0) + 0.05) / total)
        elif c.isdigit() or c in ".,:;!?()[]\"'-/%+=№«»–—":
            score += math.log(20 / 1000)
        else:
            score += -12.0
    return score / len(visible)


_CODEC_PAIRS = (
    ("cp1252", "cp1251"),   # Windows-1251 read as Windows-1252: Ïðèâåò
    ("latin-1", "cp1251"),
    ("cp1251", "utf-8"),    # UTF-8 read as Windows-1251: РџСЂРёРІРµС‚
    ("cp1252", "utf-8"),    # UTF-8 read as Windows-1252: ÐŸÑ€Ð¸Ð²ÐµÑ‚
    ("latin-1", "utf-8"),
    ("cp1251", "koi8-r"),   # KOI8-R read as Windows-1251: рТЙЧЕФ
    ("koi8-r", "cp1251"),
    ("cp1251", "cp866"),    # DOS 866 read as Windows-1251
    ("cp866", "cp1251"),
)


_CP1251_HIGH = set(bytes(range(0x80, 0xC0)).decode("cp1251", errors="ignore"))


def fix_mojibake(text: str) -> str | None:
    """Return a re-decoded string if ``text`` looks mis-decoded, else None."""
    letters = [c for c in text if c.isalpha()]
    if len(letters) < 3:
        return None
    suspicious = sum(1 for c in letters if "\u00c0" <= c <= "\u00ff") / len(letters)
    # UTF-8 Cyrillic read as cp1251 turns every letter into "Р"/"С" followed
    # by a character from cp1251's 0x80-0xBF block ("Рџ", "СЂ", "Рё").
    utf8_pairs = sum(1 for first, second in zip(text, text[1:])
                     if first in "РС" and second in _CP1251_HIGH)
    utf8_latin = len(re.findall(r"[ÐÑ][\u0080-\u00bf\u0152-\u2122]", text))
    koi_like = sum(1 for c in letters if _is_cyrillic(c) and c.isupper()) / len(letters)
    if (suspicious < 0.3 and utf8_pairs < max(2, len(letters) * 0.3)
            and utf8_latin < max(2, len(letters) * 0.3) and not (0.3 < koi_like < 0.95)):
        return None
    original = readability(text)
    best, best_score = None, original
    for wrong, right in _CODEC_PAIRS:
        try:
            candidate = text.encode(wrong).decode(right)
        except (UnicodeEncodeError, UnicodeDecodeError):
            continue
        if candidate == text or any(unicodedata.category(c) == "Cc" for c in candidate if c not in "\t\n\r"):
            continue
        score = readability(candidate)
        candidate_letters = sum(1 for c in candidate if c.isalpha())
        if candidate_letters < len(letters) * 0.45:
            continue
        if score > best_score:
            best, best_score = candidate, score
    if best is not None and best_score > original + 0.8:
        return best
    return None


_GARBAGE = re.compile("[-�\x00-\x08\x0b\x0c\x0e-\x1f\x7f-\x9f]")


def is_garbled(text: str) -> bool:
    """True if a native PDF string has no usable Unicode mapping."""
    visible = [c for c in text if not c.isspace()]
    if not visible:
        return False
    bad = len(_GARBAGE.findall(text))
    return bad >= max(1, len(visible) * 0.3)
