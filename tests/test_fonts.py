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
