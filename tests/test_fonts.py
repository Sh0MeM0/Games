import hashlib
import json

from fontTools.ttLib import TTFont

from pdf2office.fonts import FontCatalog, bundled_font_dir


def test_bundled_font_resolution_and_substitution(monkeypatch):
    monkeypatch.setattr(FontCatalog, "_directories", lambda self: [bundled_font_dir()])
    catalog = FontCatalog()
    assert "osifont" in [family.casefold() for family in catalog.families()]
    native = catalog.resolve("ABCDEF+LiberationSans-Bold", bold=True)
    assert native.family == "Liberation Sans"
    assert native.bold and not native.substituted
    assert native.path.is_file()
    fallback = catalog.resolve("Unknown Proprietary Font")
    assert fallback.substituted and fallback.family == "Liberation Sans"
    assert catalog.resolve("CourierNewPSMT").family == "Liberation Mono"
    assert catalog.resolve("TimesNewRomanPSMT").family == "Liberation Serif"


def test_bad_user_font_is_reported(tmp_path, monkeypatch):
    (tmp_path / "broken.ttf").write_bytes(b"not a font")
    monkeypatch.setattr(FontCatalog, "_directories", lambda self: [tmp_path])
    catalog = FontCatalog([tmp_path])
    assert catalog.warnings and "broken.ttf" in catalog.warnings[0]
    assert catalog.resolve("Any font").substituted


def test_resource_root_honors_portable_override(tmp_path, monkeypatch):
    monkeypatch.setenv("PDF2OFFICE_HOME", str(tmp_path))
    assert bundled_font_dir() == tmp_path / "assets/fonts"


def test_added_fonts_resolve_without_substitution_and_include_cyrillic(monkeypatch):
    monkeypatch.setattr(FontCatalog, "_directories", lambda self: [bundled_font_dir()])
    catalog = FontCatalog()
    manifest = json.loads((bundled_font_dir() / "SOURCES.json").read_text(encoding="utf-8"))
    russian = set(map(ord, "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдеёжзийклмнопрстуфхцчшщъыьэюя"))
    symbol_families = {"Noto Sans Math", "Noto Sans Symbols", "Noto Sans Symbols2"}
    for item in manifest["fonts"]:
        style = item["style"].casefold()
        resolved = catalog.resolve(item["family"], "bold" in style, "italic" in style or "oblique" in style)
        assert not resolved.substituted, item["file"]
        assert resolved.path.name == item["file"]
        with TTFont(resolved.path) as font:
            if item["family"] not in symbol_families:
                assert russian <= font.getBestCmap().keys(), item["file"]
    assert not catalog.warnings


def test_engineering_symbols_have_bundled_glyphs():
    # These geometric-tolerance/math symbols were absent from the original set.
    expected = {
        "DejaVuSans.ttf": "⊥∥◯⊙",
        "NotoSansMath-Regular.ttf": "⊥∥∫∑∏√",
        "NotoSansSymbols-Regular.ttf": "⌢⌔⌮⏧⏦",
        "NotoSansSymbols2-Regular.ttf": "◯⊙",
    }
    for filename, text in expected.items():
        with TTFont(bundled_font_dir() / filename) as font:
            assert set(map(ord, text)) <= font.getBestCmap().keys(), filename


def test_added_font_and_license_files_match_pinned_sources():
    directory = bundled_font_dir()
    manifest = json.loads((directory / "SOURCES.json").read_text(encoding="utf-8"))
    for item in manifest["fonts"]:
        assert hashlib.sha256((directory / item["file"]).read_bytes()).hexdigest() == item["sha256"]
    for package in manifest["packages"]:
        assert hashlib.sha256((directory / package["license_file"]).read_bytes()).hexdigest() == package["license_sha256"]
