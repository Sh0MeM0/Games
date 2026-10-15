"""Create reproducible demonstration inputs without third-party drawings."""
from pathlib import Path
import io
import random
import sys

import pymupdf as fitz
from PIL import Image, ImageDraw, ImageFont


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


if __name__ == "__main__":
    make_examples(Path(sys.argv[1]) if len(sys.argv) > 1 else Path("examples"))
