import fitz
import pytest

from pdf2office.extract import extract_pdf
from pdf2office.fonts import bundled_font_dir
from pdf2office.text_quality import fix_mixed_scripts, fix_mojibake, is_garbled

SAMPLE = "Привет, мир! Стены из кирпича"


@pytest.mark.parametrize("source, expected", [
    ("Осb A", "Ось А"),
    ("pаздел", "раздел"),
    ("Hello wоrld", "Hello world"),
    ("Section А-А", "Section A-A"),
    ("Section A-A", "Section A-A"),
    ("Класс С20/25", "Класс С20/25"),
    ("M 1:100", "M 1:100"),
    ("CTEHA", "CTEHA"),  # no Cyrillic evidence: left as recognised
])
def test_mixed_script_words_use_one_alphabet(source, expected):
    assert fix_mixed_scripts(source) == expected


@pytest.mark.parametrize("encode, decode", [
    ("cp1251", "cp1252"), ("utf-8", "cp1251"), ("utf-8", "cp1252"),
    ("koi8-r", "cp1251"), ("cp866", "cp1251"),
])
def test_mojibake_is_redecoded(encode, decode):
    broken = SAMPLE.encode(encode).decode(decode, errors="replace")
    assert fix_mojibake(broken) == SAMPLE


@pytest.mark.parametrize("text", [
    "Обычный текст", "Plain English text", "ПЛАН 1 ЭТАЖА", "ГОСТ 21.501-2018", "РЕЗЕРВ",
    "СЕРВЕР РЕСУРС", "Résumé café", "Fußgänger Straße", "Ё-моё", "РСУ",
])
def test_correct_text_is_not_redecoded(text):
    assert fix_mojibake(text) is None


def test_garbled_detection():
    assert is_garbled("")
    assert is_garbled("\x01\x02a")
    assert not is_garbled("abc")
    assert not is_garbled("Текст ⌀25 ±0,5")


def test_pdf_text_in_wrong_code_page_is_repaired(tmp_path):
    broken = SAMPLE.encode("cp1251").decode("cp1252")
    path = tmp_path / "mojibake.pdf"
    with fitz.open() as document:
        page = document.new_page(width=400, height=200)
        page.insert_font(fontname="LS", fontfile=str(bundled_font_dir() / "LiberationSans-Regular.ttf"))
        page.insert_text((30, 60), broken, fontname="LS", fontsize=12)
        document.save(path)
    page, = extract_pdf(path, ocr_mode="off")
    assert SAMPLE in " ".join(span.text for span in page.texts)
    assert any("Перекодирован" in warning for warning in page.warnings)
