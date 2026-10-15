"""Discover licensed local fonts without copying fonts out of source PDFs."""

from __future__ import annotations

import os
import re
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

from fontTools.ttLib import TTFont

from .runtime import resource_root


@dataclass(frozen=True)
class FontMatch:
    family: str
    path: Path | None
    substituted: bool = False
    bold: bool = False
    italic: bool = False


def bundled_font_dir() -> Path:
    return resource_root() / "assets" / "fonts"


def _key(name: str) -> str:
    name = re.sub(r"^[A-Z]{6}\+", "", name)
    return re.sub(r"[^\w]", "", name.casefold())


def _family_key(name: str) -> str:
    value = _key(name)
    for suffix in ("bolditalic", "boldoblique", "semibolditalic", "semibold", "bold", "italic", "oblique", "regular", "roman", "psmt", "mt"):
        if value.endswith(suffix):
            value = value[:-len(suffix)]
    return value


class FontCatalog:
    """System, bundled and optional user TTF/OTF fonts; SHX is CAD-specific.

    No proprietary font files are copied or extracted. Substitution is explicit
    in ``FontMatch.substituted`` so exporters can report it to the user.
    """

    def __init__(self, extra_dirs: Iterable[Path] = ()) -> None:
        self.extra_dirs = [Path(path).expanduser() for path in extra_dirs]
        self.warnings: list[str] = []
        self._fonts: list[tuple[FontMatch, set[str]]] = []
        self.refresh()

    def _directories(self) -> list[Path]:
        roots = [*self.extra_dirs, bundled_font_dir()]
        if os.name == "nt":
            roots.extend([
                Path(os.environ.get("WINDIR", r"C:\Windows")) / "Fonts",
                Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData/Local")) / "Microsoft/Windows/Fonts",
            ])
        elif sys.platform == "darwin":
            roots.extend([Path("/System/Library/Fonts"), Path("/Library/Fonts"), Path.home() / "Library/Fonts"])
        else:
            roots.extend([Path("/usr/share/fonts"), Path("/usr/local/share/fonts"), Path.home() / ".fonts", Path.home() / ".local/share/fonts"])
        return roots

    def refresh(self) -> None:
        self._fonts.clear()
        self.warnings.clear()
        visited: set[Path] = set()
        for directory in self._directories():
            if not directory.is_dir():
                continue
            for path in sorted(directory.rglob("*")):
                if path.suffix.lower() not in {".ttf", ".otf", ".ttc"} or not path.is_file():
                    continue
                canonical = path.resolve()
                if canonical in visited:
                    continue
                visited.add(canonical)
                try:
                    with TTFont(path, fontNumber=0, lazy=True) as font:
                        names = font["name"]
                        family = names.getBestFamilyName() or path.stem
                        subfamily = names.getBestSubFamilyName() or "Regular"
                        aliases = {family, family + subfamily, path.stem}
                        for item in names.names:
                            if item.nameID in {1, 4, 6, 16}:
                                try:
                                    aliases.add(item.toUnicode())
                                except (UnicodeError, LookupError):
                                    pass
                        style = subfamily.casefold()
                        selection = getattr(font.get("OS/2"), "fsSelection", 0)
                        bold = "bold" in style or bool(selection & (1 << 5))
                        italic = any(word in style for word in ("italic", "oblique")) or bool(selection & 1)
                        self._fonts.append((FontMatch(family, path, False, bold, italic), {_key(alias) for alias in aliases}))
                except Exception:
                    self.warnings.append(f"Не удалось прочитать шрифт: {path.name}")

    def families(self) -> list[str]:
        return sorted({font.family for font, _ in self._fonts}, key=str.casefold)

    def resolve(self, pdf_font: str, bold: bool = False, italic: bool = False) -> FontMatch:
        requested = _key(pdf_font)
        inferred = requested.casefold()
        bold = bold or "bold" in inferred
        italic = italic or "italic" in inferred or "oblique" in inferred
        exact = [(font, aliases) for font, aliases in self._fonts if requested in aliases]
        family = _family_key(pdf_font)
        if not exact:
            exact = [(font, aliases) for font, aliases in self._fonts if _family_key(font.family) == family]
        substituted = not bool(exact)
        if not exact:
            if any(word in family for word in ("times", "serif", "roman")):
                fallback = "Liberation Serif"
            elif any(word in family for word in ("courier", "mono", "consolas")):
                fallback = "Liberation Mono"
            else:
                fallback = "Liberation Sans"
            exact = [(font, aliases) for font, aliases in self._fonts if font.family == fallback]
        if not exact:
            return FontMatch("Arial", None, True, bold, italic)
        font, _ = min(exact, key=lambda item: (item[0].bold != bold) + (item[0].italic != italic))
        style_changed = font.bold != bold or font.italic != italic
        return FontMatch(font.family, font.path, substituted or style_changed, font.bold, font.italic)
