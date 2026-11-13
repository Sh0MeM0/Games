import pytest

from pdf2office.models import PdfPage, TextSpan
from pdf2office.translate import (_distribute, _restore, available_packages, detect_language,
                                  group_blocks, group_lines, translate_pages)

needs_models = pytest.mark.skipif(
    not {("ru", "en"), ("en", "ru")} <= {(p.source, p.target) for p in available_packages()},
    reason="translation models are not installed",
)


def span(text, x, y, width, size=10.0, angle=0.0):
    return TextSpan(text, (x, y - size, x + width, y), "Arial", size, (0, 0, 0),
                    origin=(x, y), advance=width, angle=angle)


def test_language_detection():
    assert detect_language("Стена из кирпича") == "ru"
    assert detect_language("Steel beam") == "en"
    assert detect_language("Ўсход і захад") == "be"
    assert detect_language("3600") is None


def test_words_on_one_baseline_form_a_line_and_paragraphs_form_blocks():
    spans = [span("Все", 10, 100, 20), span("размеры", 33, 100, 50), span("указаны", 10, 114, 45),
             span("ПЛАН", 300, 300, 40, 14), span("2700", 500, 500, 30, 10, -90.0)]
    lines = group_lines(spans)
    assert [len(line) for line in lines] == [2, 1, 1, 1]
    blocks = group_blocks(lines)
    assert [len(block) for block in blocks] == [2, 1, 1]


def test_translation_is_distributed_over_original_lines():
    block = [[span("один два", 0, 10, 40)], [span("три четыре пять шесть", 0, 24, 100)]]
    assert _distribute("one two three four five six", block) == ["one two", "three four five six"]


@pytest.mark.parametrize("source, translated, expected", [
    ("Ось А", "Axis", "Axis А"),
    ("Кухня 12,4 м²", "Kitchen 12.4 m2", "Kitchen 12.4 m²"),
    ("Лестница ЛМ-1", "Ladder LM-1", "Ladder LM-1"),
    ("Разрез Б-Б", "Section", "Section Б-Б"),
    ("Steel beam, see detail 3", "Стальная балка, см. деталь", "Стальная балка, см. деталь 3"),
    ("ДОМ", "HOUSE", "HOUSE"),
])
def test_numbers_and_short_codes_survive_translation(source, translated, expected):
    assert _restore(source, translated) == expected


@needs_models
def test_pages_are_translated_offline_without_touching_originals():
    page = PdfPage(1, 600, 800, [
        span("Примечание: все размеры указаны", 50, 100, 200), span("в миллиметрах.", 50, 114, 90),
        span("3600", 100, 400, 30), span("Steel beam", 100, 600, 80),
    ], [], [])
    translated, stats = translate_pages([page], "en")
    assert stats.translated == 1 and stats.unchanged == 2
    text = " ".join(item.text for item in translated[0].texts)
    assert "millimeters" in text.lower() and "3600" in text and "Steel beam" in text
    assert page.texts[0].text == "Примечание: все размеры указаны"
    russian, _ = translate_pages([page], "ru")
    assert any("балка" in item.text.lower() for item in russian[0].texts)


def test_relative_and_sinusoidal_positions_follow_ctranslate2():
    from pdf2office.nmt import _relative_positions, sinusoidal_positions

    positions = _relative_positions(1, 5, 2)  # one decoder query at step 4
    assert positions.tolist() == [[0, 0, 0, 1, 2]]
    table = sinusoidal_positions(3, 8)
    assert table.shape == (3, 8)
    # Position 1 is the first row: sin(1) for the fastest timescale.
    assert table[0, 0] == pytest.approx(0.841471, abs=1e-6)
    assert table[0, 4] == pytest.approx(0.540302, abs=1e-6)


@needs_models
@pytest.mark.parametrize("direction, source, expected", [
    ("ru_en", "План первого этажа", "First floor plan"),
    ("ru_en", "Стена из кирпича толщиной 380 мм", "Brick wall 380 mm thick"),
    ("en_ru", "Ground floor plan", "План первого этажа"),
])
def test_numpy_translator_reproduces_reference_translations(direction, source, expected):
    from pdf2office.translate import get_translator, release_translators

    translator = get_translator(*direction.split("_"))
    try:
        assert translator.translate([source]) == [expected]
    finally:
        release_translators()


@needs_models
def test_numpy_transformer_matches_ctranslate2_greedy_decoding():
    ctranslate2 = pytest.importorskip("ctranslate2")  # development oracle only
    import sentencepiece
    from pdf2office.nmt import TransformerModel
    from pdf2office.translate import translation_dir

    for direction, texts in (("ru_en", ["Перекрытия выполнены из сборных железобетонных плит.", "Ось А"]),
                             ("en_ru", ["Install the window frames after the walls are plastered."])):
        folder = translation_dir() / direction
        tokenizer = sentencepiece.SentencePieceProcessor(model_file=str(folder / "sentencepiece.model"))
        tokens = [tokenizer.encode(text, out_type=str) for text in texts]
        reference = ctranslate2.Translator(str(folder / "model"), device="cpu", compute_type="float32")
        expected = [result.hypotheses[0] for result in reference.translate_batch(tokens, beam_size=1)]
        assert TransformerModel(folder / "model").translate_tokens(tokens, beam_size=1) == expected
