#!/usr/bin/env python3
"""Create authored, deterministic document fixtures; no external document images.

Run from any directory: python3 tests/generate-document.py
Requires Pillow, numpy, and DejaVu Sans fonts (Debian: fonts-dejavu-core).
"""
from __future__ import annotations

import copy
import json
import math
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont, ImageOps

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "tests" / "fixtures"
EXAMPLES = ROOT / "examples"
WIDTH, HEIGHT, SCALE = 1800, 1400, 3
SIZE = (WIDTH * SCALE, HEIGHT * SCALE)
FONT_ROOT = Path("/usr/share/fonts/truetype/dejavu")
BLACK = (42, 43, 45)
BLUE = (30, 65, 152)
RED = (169, 45, 52)


def font(size: int, bold: bool = False):
    return ImageFont.truetype(str(FONT_ROOT / ("DejaVuSans-Bold.ttf" if bold else "DejaVuSans.ttf")), size * SCALE)


def new_layer():
    return Image.new("RGBA", SIZE, (255, 255, 255, 0))


def downsample(im):
    return im.resize((WIDTH, HEIGHT), Image.Resampling.LANCZOS)


def scaled_box(box):
    return tuple(round(v * SCALE) for v in box)


def line(layer, points, color=BLACK, width=1):
    ImageDraw.Draw(layer).line([(round(x * SCALE), round(y * SCALE)) for x, y in points], fill=(*color, 255), width=max(1, round(width * SCALE)))


def centered_text(layer, center, text, size, color, bold=False):
    d = ImageDraw.Draw(layer)
    d.text((round(center[0] * SCALE), round(center[1] * SCALE)), text, font=font(size, bold), fill=(*color, 255), anchor="mm")


def arc_text(layer, center, radius, text, middle_degrees, size, color, bottom=False):
    """Place separate rotated glyphs on a circular baseline."""
    f = font(size)
    advances = [f.getlength(c) / SCALE + 1.5 for c in text]
    total = sum(advances)
    offset = -total / 2
    for character, advance in zip(text, advances):
        offset += advance / 2
        angle = math.radians(middle_degrees) + offset / radius * (-1 if bottom else 1)
        glyph = Image.new("RGBA", (size * SCALE * 3, size * SCALE * 3))
        gd = ImageDraw.Draw(glyph)
        gd.text((glyph.width / 2, glyph.height / 2), character, font=f, fill=(*color, 255), anchor="mm")
        rotation = 90 - math.degrees(angle) if bottom else -math.degrees(angle) - 90
        rotated = glyph.rotate(rotation, Image.Resampling.BICUBIC, expand=True)
        px = round((center[0] + radius * math.cos(angle)) * SCALE - rotated.width / 2)
        py = round((center[1] + radius * math.sin(angle)) * SCALE - rotated.height / 2)
        layer.alpha_composite(rotated, (px, py))
        offset += advance / 2


def star(layer, center, radius, color):
    points = []
    for i in range(10):
        angle = -math.pi / 2 + math.pi * i / 5
        r = radius if i % 2 == 0 else radius * .42
        points.append(((center[0] + r * math.cos(angle)) * SCALE, (center[1] + r * math.sin(angle)) * SCALE))
    ImageDraw.Draw(layer).polygon(points, fill=(*color, 255))


def seal(center, color, second=False):
    layer = new_layer()
    d = ImageDraw.Draw(layer)
    cx, cy = center
    for r, thickness in [(165, 3.3), (157, 1.8), (121, 1.9)]:
        d.ellipse(scaled_box((cx-r, cy-r, cx+r, cy+r)), outline=(*color, 245), width=round(thickness*SCALE))
    arc_text(layer, center, 140, "УЧЕБНЫЙ ОБРАЗЕЦ", -90, 20, color)
    arc_text(layer, center, 140, "TEST DOCUMENT ONLY", 90, 18, color, bottom=True)
    centered_text(layer, (cx, cy-34), "ОБРАЗЕЦ", 27, color, bold=True)
    centered_text(layer, (cx, cy+2), "SAMPLE 02" if second else "SAMPLE 01", 23, color)
    centered_text(layer, (cx, cy+39), "0000000000", 16, color)
    line(layer, [(cx-84, cy+65), (cx+84, cy+65)], color, 1.5)
    for x in [-140, 140]:
        star(layer, (cx+x, cy+8), 8, color)
    # Subtle authored ink variation; never invents an official organisation.
    rng = np.random.default_rng(402 if second else 401)
    for _ in range(140):
        x, y = rng.integers(cx-165, cx+166), rng.integers(cy-165, cy+166)
        r = float(rng.uniform(.25, .7))
        d.ellipse(scaled_box((x-r, y-r, x+r, y+r)), fill=(255, 255, 255, 0))
    return layer


def bezier(layer, points, origin, color, width=2.8, phase=0):
    """A cubic pen stroke with deterministic pressure, drawn as short segments."""
    p = np.asarray(points, dtype=float)
    ts = np.linspace(0, 1, max(60, int(np.linalg.norm(p[3]-p[0]) * 1.4)))
    values = (1-ts[:,None])**3*p[0] + 3*(1-ts[:,None])**2*ts[:,None]*p[1] + 3*(1-ts[:,None])*ts[:,None]**2*p[2] + ts[:,None]**3*p[3]
    values += origin
    d = ImageDraw.Draw(layer)
    for i in range(len(values)-1):
        pressure = .78 + .25*math.sin(ts[i]*math.pi*2+phase) + .18*math.sin(ts[i]*math.pi)
        a, b = values[i:i+2] * SCALE
        d.line([tuple(a), tuple(b)], fill=(*color, 247), width=max(2, round(width*pressure*SCALE)))


def signature_one(origin):
    layer = new_layer()
    paths = [
        [(10,92),(75,-14),(124,-9),(84,81)],
        [(84,81),(52,155),(16,97),(81,29)],
        [(81,29),(111,-3),(129,10),(108,77)],
        [(108,77),(91,134),(67,154),(104,111)],
        [(53,77),(120,65),(153,60),(178,53)],
        [(113,88),(139,22),(170,49),(142,89)],
        [(142,89),(167,18),(184,24),(178,81)],
        [(178,81),(190,41),(213,25),(203,78)],
        [(203,78),(232,37),(240,39),(226,82)],
        [(226,82),(202,108),(216,105),(251,62)],
        [(251,62),(285,13),(269,40),(254,74)],
        [(254,74),(275,84),(306,7),(300,57)],
        [(300,57),(285,112),(334,25),(335,57)],
        [(335,57),(319,89),(356,78),(371,44)],
        [(371,44),(384,37),(387,54),(369,79)],
        [(369,79),(396,37),(411,32),(420,45)],
        [(37,117),(218,91),(371,83),(438,88)],
        [(438,88),(482,92),(376,121),(260,119)],
    ]
    for i, points in enumerate(paths):
        bezier(layer, points, origin, BLUE, 3.1 if i<6 else 2.7, i*.55)
    return layer


def signature_two(origin):
    layer = new_layer()
    paths = [
        [(12,93),(39,18),(108,-2),(95,48)],
        [(95,48),(77,83),(36,67),(40,33)],
        [(40,33),(46,7),(73,9),(65,81)],
        [(65,81),(58,145),(76,158),(107,117)],
        [(44,79),(118,64),(175,44),(197,45)],
        [(111,82),(124,29),(153,29),(137,79)],
        [(137,79),(150,50),(174,34),(169,78)],
        [(169,78),(194,13),(211,36),(194,79)],
        [(194,79),(174,111),(191,112),(222,73)],
        [(222,73),(246,38),(260,44),(241,80)],
        [(241,80),(271,60),(289,20),(285,55)],
        [(285,55),(275,83),(294,76),(311,47)],
        [(311,47),(327,13),(349,18),(335,51)],
        [(335,51),(306,98),(367,34),(367,60)],
        [(367,60),(350,83),(387,62),(406,48)],
        [(406,48),(445,28),(424,61),(411,71)],
        [(30,130),(137,98),(357,88),(447,104)],
        [(447,104),(504,115),(430,157),(374,136)],
    ]
    for i, points in enumerate(paths):
        bezier(layer, points, origin, (38,39,43), 3.0 if i<5 else 2.55, i*.83)
    return layer


def rectangle(mask):
    box = mask.point(lambda v: 255 if v>12 else 0).getbbox()
    if not box:
        return None
    return {"x":box[0],"y":box[1],"width":box[2]-box[0],"height":box[3]-box[1]}


def main():
    OUT.mkdir(parents=True,exist_ok=True)
    EXAMPLES.mkdir(parents=True,exist_ok=True)
    paper = Image.new("RGBA",SIZE,"white")
    printed = new_layer()
    words = []
    lines = []

    def text(x,y,value,size=26,bold=False,target=None):
        d = ImageDraw.Draw(printed if target is None else target)
        f = font(size,bold)
        d.text((x*SCALE,y*SCALE),value,font=f,fill=(*BLACK,255),anchor="lt")
        baseline=y*SCALE-f.getbbox(value,anchor="ls")[1]
        prefix = ""
        for word in value.split(" "):
            word_x = x + f.getlength(prefix)/SCALE
            # Share the line's actual baseline; lowercase-only words have a
            # different top bound from capitals/ascenders in the same line.
            bb = d.textbbox((word_x*SCALE,baseline),word,font=f,anchor="ls")
            words.append({"text":word,"confidence":99,"bbox":{"x0":round(bb[0]/SCALE),"y0":round(bb[1]/SCALE),"x1":round(bb[2]/SCALE),"y1":round(bb[3]/SCALE)}})
            prefix += word + " "
        bb = d.textbbox((x*SCALE,y*SCALE),value,font=f,anchor="lt")
        lines.append({"text":value,"bbox":[round(v/SCALE) for v in bb]})

    text(120,88,"УЧЕБНЫЙ ДОКУМЕНТ / TEST DOCUMENT",40,True)
    text(120,155,"Акт проверки извлечения печатей и подписей № 07",29,True)
    text(120,222,"Дата: 15 октября 2026 года. Все изображения созданы искусственно.",26)
    text(120,271,"Обычный печатный текст должен исчезнуть из сохраняемых изображений.",26)
    text(120,320,"Номер договора: 12345. Наименование: тестовая документация.",26)
    text(120,369,"Printed labels and paragraphs must not become signature files.",26)
    text(120,418,"Две круглые печати содержат собственные буквы: их необходимо сохранить.",26)
    text(120,470,"Левый образец — синий; правый образец — красный.",26)
    text(165,886,"Место для печати / Stamp",25)
    text(1110,886,"Вторая печать / Second stamp",25)
    text(120,965,"Ответственный:",25)
    text(1000,965,"Проверил:",25)
    text(120,1205,"Подпись 1 / Signature 1",25)
    text(1000,1205,"Подпись 2 / Signature 2",25)
    text(120,1280,"Образцы не являются подписями реальных людей или действующими печатями.",24)
    text(120,1325,"Текст вне объектов, фон и линии бланка следует убрать. Страница 1 из 1.",24)

    form = new_layer()
    line(form,[(120,1190),(790,1190)],BLACK,1)
    line(form,[(1000,1190),(1675,1190)],BLACK,1)

    objects = [
        ("seal-blue","stamp",seal((420,700),BLUE),"blue"),
        ("seal-red","stamp",seal((1340,700),RED,True),"red"),
        ("signature-blue","signature",signature_one((270,1000)),"blue"),
        ("signature-black","signature",signature_two((1100,1000)),"black"),
    ]
    document = paper.copy()
    document.alpha_composite(printed)
    document.alpha_composite(form)
    truth_objects = []
    combined = Image.new("L",(WIDTH,HEIGHT),0)
    for object_id,kind,layer,color in objects:
        document.alpha_composite(layer)
        mask = downsample(layer.getchannel("A"))
        mask.save(OUT / f"{object_id}.mask.png")
        full_white = paper.copy()
        full_white.alpha_composite(layer)
        bbox = rectangle(mask)
        crop_box=(bbox["x"]-16,bbox["y"]-16,bbox["x"]+bbox["width"]+16,bbox["y"]+bbox["height"]+16)
        downsample(full_white.convert("RGB")).crop(crop_box).save(OUT / f"{object_id}.expected.png")
        combined = Image.fromarray(np.maximum(np.asarray(combined),np.asarray(mask)))
        truth_objects.append({"id":object_id,"kind":kind,"color":color,"bbox":bbox,"mask":f"{object_id}.mask.png","clean_reference":f"{object_id}.expected.png","ink_pixels":int((np.asarray(mask)>32).sum())})
    combined.save(OUT/"objects.mask.png")
    printed_mask=downsample(printed.getchannel("A"))
    printed_mask.save(OUT/"printed-text.mask.png")
    downsample(form.getchannel("A")).save(OUT/"form-lines.mask.png")
    clean=downsample(document.convert("RGB"))
    clean.save(EXAMPLES/"document.png")
    ImageOps.grayscale(clean).convert("RGB").save(EXAMPLES/"document-gray.png")
    # A difficult but reproducible photo-like illumination, small sensor noise,
    # and real JPEG artefacts. No geometric warp: masks remain in source pixels.
    image=np.asarray(clean).astype(np.float32)
    yy,xx=np.mgrid[:HEIGHT,:WIDTH]
    illumination=.88+.105*(xx/WIDTH)-.13*(yy/HEIGHT)+.03*np.cos(xx/370)
    rng=np.random.default_rng(20261015)
    noise=rng.normal(0,1.35,(HEIGHT,WIDTH,1))
    photo=image*illumination[:,:,None]+noise
    photo[:,:,0]+=3.7
    photo[:,:,2]-=3.0
    Image.fromarray(np.clip(photo,0,255).astype(np.uint8)).save(EXAMPLES/"document-photo.jpg",quality=88,subsampling=0)
    negative=paper.copy()
    negative.alpha_composite(printed)
    negative.alpha_composite(form)
    downsample(negative.convert("RGB")).save(OUT/"document-text-only.png")
    blue_printed=Image.new("RGBA",SIZE,(*BLUE,0))
    blue_printed.putalpha(printed.getchannel("A"))
    blue_negative=paper.copy()
    blue_negative.alpha_composite(blue_printed)
    blue_negative.alpha_composite(form)
    downsample(blue_negative.convert("RGB")).save(OUT/"document-text-only-blue.png")
    metadata={
        "schema_version":1,"width":WIDTH,"height":HEIGHT,"seed":20261015,
        "source":"../../examples/document.png","photo":"../../examples/document-photo.jpg",
        "grayscale":"../../examples/document-gray.png",
        "objects":truth_objects,"printed_words":copy.deepcopy(words),"printed_lines":copy.deepcopy(lines),
        "negative":"document-text-only.png","negative_blue":"document-text-only-blue.png","expected_negative_objects":0,
        "masks":"8-bit grayscale, 0=background, 255=ink; antialiased edges retained; full source size",
        "notes":["Every seal, path, and document phrase is authored for this test; no real signature or official seal is used.","Photo differs only in illumination, sensor noise, and JPEG compression; coordinates are unchanged.","Printed word boxes are exact authored layout annotations, not claims about OCR accuracy.","This fixture does not establish accuracy on arbitrary real documents or overlapping same-colour strokes."]
    }
    (OUT/"document.json").write_text(json.dumps(metadata,ensure_ascii=False,indent=2)+"\n",encoding="utf8")

    # The overlay obscures source pixels. Visible-only truth deliberately does not
    # require reconstructing the covered ink, which cannot be known from a photo.
    overlap_layer=new_layer()
    text(267,686,"Номер 123456789",24,target=overlap_layer)
    text(292,1065,"Согласовано 15.10.2026",22,target=overlap_layer)
    overlap_document=document.copy()
    overlap_document.alpha_composite(overlap_layer)
    downsample(overlap_document.convert("RGB")).save(EXAMPLES/"document-overlap.png")
    overlap_mask=downsample(overlap_layer.getchannel("A"))
    overlap_mask.save(OUT/"overlap-text.mask.png")
    overlap_metadata=copy.deepcopy(metadata)
    overlap_metadata["source"]="../../examples/document-overlap.png"
    overlap_metadata.pop("photo",None)
    overlap_metadata.pop("grayscale",None)
    overlap_metadata["printed_words"]=copy.deepcopy(words)
    overlap_metadata["printed_lines"]=copy.deepcopy(lines)
    overlap_metadata["notes"].append("Black text overlaps the blue seal and blue signature. Visible-only masks exclude occluded pixels; reconstruction is not expected.")
    for obj in overlap_metadata["objects"]:
        object_mask=np.asarray(Image.open(OUT/obj["mask"])).astype(np.float32)
        visible=object_mask*(1-np.asarray(overlap_mask).astype(np.float32)/255)
        visible_mask=Image.fromarray(np.rint(visible).astype(np.uint8))
        obj["visible_mask"]=obj["id"]+".visible.mask.png"
        visible_mask.save(OUT/obj["visible_mask"])
    (OUT/"document-overlap.json").write_text(json.dumps(overlap_metadata,ensure_ascii=False,indent=2)+"\n",encoding="utf8")

    angle=3
    clean.rotate(angle,Image.Resampling.BICUBIC,fillcolor="white").save(EXAMPLES/"document-rotated.png")
    rotated_metadata=copy.deepcopy(metadata)
    rotated_metadata["source"]="../../examples/document-rotated.png"
    rotated_metadata.pop("photo",None)
    rotated_metadata.pop("grayscale",None)
    rotated_metadata["rotation_degrees"]=angle
    rotated_metadata["notes"].append("Source and ground-truth masks rotated 3 degrees anticlockwise without expanding the canvas.")
    for obj in rotated_metadata["objects"]:
        object_mask=Image.open(OUT/obj["mask"]).rotate(angle,Image.Resampling.BICUBIC,fillcolor=0)
        obj["mask"]=obj["id"]+".rotated.mask.png"
        obj["bbox"]=rectangle(object_mask)
        obj["ink_pixels"]=int((np.asarray(object_mask)>32).sum())
        object_mask.save(OUT/obj["mask"])
        object_layer=next(item[2] for item in objects if item[0]==obj["id"])
        object_on_white=paper.copy()
        object_on_white.alpha_composite(object_layer)
        object_on_white=downsample(object_on_white.convert("RGB")).rotate(angle,Image.Resampling.BICUBIC,fillcolor="white")
        bb=obj["bbox"]
        obj["clean_reference"]=obj["id"]+".rotated.expected.png"
        object_on_white.crop((bb["x"]-16,bb["y"]-16,bb["x"]+bb["width"]+16,bb["y"]+bb["height"]+16)).save(OUT/obj["clean_reference"])
    radians=math.radians(angle)
    def rotate_bbox(bbox):
        points=[]
        for x,y in [(bbox[0],bbox[1]),(bbox[2],bbox[1]),(bbox[2],bbox[3]),(bbox[0],bbox[3])]:
            dx,dy=x-WIDTH/2,y-HEIGHT/2
            points.append((WIDTH/2+dx*math.cos(radians)+dy*math.sin(radians),HEIGHT/2-dx*math.sin(radians)+dy*math.cos(radians)))
        return [math.floor(min(p[0] for p in points)),math.floor(min(p[1] for p in points)),math.ceil(max(p[0] for p in points)),math.ceil(max(p[1] for p in points))]
    for word in rotated_metadata["printed_words"]:
        bb=word["bbox"]
        rotated=rotate_bbox([bb["x0"],bb["y0"],bb["x1"],bb["y1"]])
        word["bbox"]=dict(zip(["x0","y0","x1","y1"],rotated))
    for entry in rotated_metadata["printed_lines"]:
        entry["bbox"]=rotate_bbox(entry["bbox"])
    (OUT/"document-rotated.json").write_text(json.dumps(rotated_metadata,ensure_ascii=False,indent=2)+"\n",encoding="utf8")
    print(json.dumps({"image":str(EXAMPLES/"document.png"),"objects":truth_objects},ensure_ascii=False,indent=2))


if __name__=="__main__":
    main()
