"""Create reproducible demonstration inputs without third-party drawings."""
from pathlib import Path
import io
import random
import sys

import pymupdf as fitz
from PIL import Image, ImageDraw, ImageFont


def make_synthetic_facade():
    """Our own facade, with grey 1 px hatches and wall texture, not a user file."""
    image = Image.new("RGB", (900, 420), "white")
    draw = ImageDraw.Draw(image)
    # A stippled wall must not become thousands of stray CAD primitives.
    for y in range(200, 326, 5):
        for x in range(45, 856, 7):
            if not (150 <= x <= 270 or 345 <= x <= 465 or 568 <= x <= 622 or 698 <= x <= 752):
                draw.point((x, y), fill=(210, 210, 210))
    draw.rectangle((40, 195, 860, 330), outline="black", width=1)
    draw.rectangle((30, 150, 470, 195), outline="black", width=1)
    draw.rectangle((480, 65, 870, 195), outline="black", width=1)
    for x in range(484, 868, 4):
        draw.line((x, 66, x, 194), fill=(200, 200, 200), width=1)
    for x in (150, 345):
        draw.rectangle((x, 230, x + 120, 329), outline="black", width=1)
        draw.line((x, 230, x + 120, 329), fill="black", width=1)
        draw.line((x, 329, x + 120, 230), fill="black", width=1)
    for x in (570, 700):
        draw.rectangle((x, 235, x + 50, 290), outline=(172, 128, 94), width=1)
        draw.rectangle((x + 5, 240, x + 45, 285), outline="black", width=1)
        draw.line((x + 25, 240, x + 25, 285), fill="black", width=1)
    for x in range(35, 860, 60):
        draw.line((x, 25, x + 35, 25), fill="black", width=1)
    return image


def make_facade_examples(directory: Path):
    """Publish only our own labelled synthetic facade, in PNG and JPEG."""
    directory.mkdir(parents=True, exist_ok=True)
    image = make_synthetic_facade()
    font = ImageFont.truetype(str(Path(__file__).parents[1] / "assets/fonts/DejaVuSans.ttf"), 20)
    ImageDraw.Draw(image).text((55, 365), "ФАСАД — УЧЕБНЫЙ ПРИМЕР. 300 DPI", font=font, fill="black")
    for extension, options in [("png", {}), ("jpg", {"quality": 75, "subsampling": 0})]:
        destination = directory / ("Пример — фасад." + extension)
        image.save(destination, dpi=(300, 300), **options)
        print(destination)


def make_examples(directory: Path):
    directory.mkdir(parents=True, exist_ok=True)
    font = Path(__file__).parents[1] / "assets/fonts/LiberationSans-Regular.ttf"
    vector = directory / "Пример — векторный чертёж.pdf"
    with fitz.open() as pdf:
        page = pdf.new_page(width=842, height=595)
        page.insert_font(fontname="Liberation", fontfile=str(font))
        page.insert_text((45, 45), "План помещения • Проверка PDF Мастер", fontname="Liberation", fontsize=20)
        page.draw_rect(fitz.Rect(60, 90, 600, 430), width=2)
        page.draw_line((330, 90), (330, 280), width=2)
        page.draw_line((330, 350), (330, 430), width=2)
        page.draw_bezier((330, 280), (370, 280), (400, 310), (400, 350))
        page.draw_line((330, 350), (400, 350))
        page.draw_rect(fitz.Rect(80, 110, 210, 170), fill=(0.8, 0.9, 1), color=(0.2, 0.3, 0.7))
        page.insert_text((125, 250), "Комната 1", fontname="Liberation", fontsize=16)
        page.insert_text((430, 250), "Комната 2", fontname="Liberation", fontsize=16)
        page.insert_text((280, 460), "5400 мм", fontname="Liberation", fontsize=14)
        page.insert_text((60, 525), "Размеры и масштаб после конвертации необходимо проверить.", fontname="Liberation", fontsize=12)
        pdf.save(vector)
    image = Image.new("RGB", (1800, 1200), "white")
    draw = ImageDraw.Draw(image)
    text_font = ImageFont.truetype(str(font), 60)
    draw.text((100, 80), "Чертёж детали 125 мм", font=text_font, fill="black")
    draw.text((100, 190), "Drawing 125 mm", font=text_font, fill="black")
    draw.rectangle((120, 380, 1450, 850), outline="black", width=5)
    draw.line((700, 380, 700, 850), fill="black", width=5)
    draw.text((140, 950), "Сталь 20. Проверить размеры.", font=text_font, fill="black")
    data = io.BytesIO()
    image.save(data, format="PNG")
    scan = directory / "Пример — скан.pdf"
    with fitz.open() as pdf:
        page = pdf.new_page(width=900, height=600)
        page.insert_image(page.rect, stream=data.getvalue())
        pdf.save(scan)
    print(vector)
    print(scan)
    # Public synthetic TIFF for checking cleanup, deskew, OCR and curved paths.
    tiff = Image.new("L", (1800, 1400), 246)
    drawing = ImageDraw.Draw(tiff)
    for y in (100, 400, 1000, 1300):
        drawing.line((100, y, 1700, y), fill=0, width=4)
    drawing.rectangle((100, 100, 1700, 1300), outline=0, width=4)
    drawing.line((900, 400, 900, 1000), fill=0, width=4)
    drawing.arc((250, 500, 700, 950), 15, 300, fill=0, width=4)
    drawing.text((180, 200), "Чертёж детали 125 мм", font=text_font, fill=0)
    drawing.text((1000, 640), "Сталь 20", font=text_font, fill=0)
    drawing.text((180, 1100), "Проверка TIFF 400 DPI", font=text_font, fill=0)
    noise = random.Random(42)
    for _ in range(18000):
        drawing.point((noise.randrange(tiff.width), noise.randrange(tiff.height)), fill=0)
    tiff = tiff.rotate(-2.4, expand=True, fillcolor=246)
    tiff_path = directory / "Пример — шумный скан.tif"
    tiff.save(tiff_path, dpi=(400, 400), compression="tiff_lzw")
    print(tiff_path)
    make_facade_examples(directory)


if __name__ == "__main__":
    make_examples(Path(sys.argv[1]) if len(sys.argv) > 1 else Path("examples"))
