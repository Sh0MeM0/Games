#!/usr/bin/env python3
"""Synthetic document fragments with exact per-pixel ink labels.

Every element is drawn on its own antialiased alpha layer and composited onto
paper with multiplicative (ink-like) blending, so overlapping pixels can carry
several labels at once. Label bits:

    1  printed text          8  signature
    2  printed form line    16  stamp impression
    4  handwritten text

The generator is only used to train the bundled segmentation model. It renders
fictional names and organisations; nothing here reproduces a real seal.
"""
from __future__ import annotations

import math
import random
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

PRINT, LINE, HAND, SIGN, STAMP = 1, 2, 4, 8, 16
ROOT = Path(__file__).resolve().parent
FONT_CACHE = ROOT / 'fonts'

PRINT_FONTS = [
    '/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf', '/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf',
    '/usr/share/fonts/truetype/dejavu/DejaVuSerif.ttf', '/usr/share/fonts/truetype/dejavu/DejaVuSerif-Bold.ttf',
    '/usr/share/fonts/truetype/dejavu/DejaVuSansCondensed.ttf', '/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf',
    '/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf', '/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf',
    '/usr/share/fonts/truetype/liberation/LiberationSerif-Regular.ttf', '/usr/share/fonts/truetype/liberation/LiberationSerif-Bold.ttf',
    '/usr/share/fonts/truetype/liberation/LiberationSerif-Italic.ttf', '/usr/share/fonts/truetype/liberation/LiberationMono-Regular.ttf',
    '/usr/share/fonts/truetype/freefont/FreeSans.ttf', '/usr/share/fonts/truetype/freefont/FreeSerif.ttf',
    '/usr/share/fonts/truetype/freefont/FreeSansBold.ttf', '/usr/share/fonts/truetype/freefont/FreeMono.ttf',
    '/usr/share/fonts/truetype/crosextra/Carlito-Regular.ttf',
]
STAMP_FONTS = [
    '/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf', '/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf',
    '/usr/share/fonts/truetype/dejavu/DejaVuSansCondensed.ttf', '/usr/share/fonts/truetype/dejavu/DejaVuSansCondensed-Bold.ttf',
    '/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf', '/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf',
    '/usr/share/fonts/truetype/liberation/LiberationSerif-Bold.ttf', '/usr/share/fonts/truetype/freefont/FreeSansBold.ttf',
    '/usr/share/fonts/truetype/liberation/LiberationSansNarrow-Regular.ttf', '/usr/share/fonts/truetype/liberation/LiberationSansNarrow-Bold.ttf',
]

WORDS_RU = ('договор акт приёма передачи товара услуг работ сторона заказчик исполнитель поставщик покупатель '
            'директор бухгалтер главный руководитель организации общество ограниченной ответственностью '
            'настоящий составлен двух экземплярах имеющих одинаковую юридическую силу оплата произведена '
            'согласно счёту фактуре сумма рублей копеек без НДС в том числе основание приказ доверенность '
            'наименование количество цена единица измерения итого всего подпись расшифровка печать место '
            'дата номер года месяца число получил сдал проверил утверждаю согласовано начальник отдела '
            'кадров заявление прошу принять на работу должность инженер специалист ведущий период срок '
            'действия условия обязательства ответственность порядок расчётов реквизиты банк счёт адрес '
            'город улица дом корпус телефон электронная почта УНП ОКПО регистрационный справка выдана '
            'для предъявления по месту требования подтверждается что работает в качестве с по настоящее время '
            'протокол заседания комиссии решение принято единогласно председатель секретарь члены '
            'товарная накладная груз отпуск разрешил принял водитель экспедитор маршрут пункт погрузки').split()
WORDS_BY = ('дагавор акт прыёму перадачы тавару паслуг бок заказчык выканаўца дырэктар галоўны бухгалтар '
            'адказнасць таварыства абмежаванай подпіс пячатка дата нумар горад вуліца рашэнне').split()
WORDS_EN = ('agreement invoice total amount payment delivery terms parties signature date name title company '
            'director accountant approved received copy certified seal place of the and for by with').split()
SURNAMES = ('Иванов Петров Сидоров Кузнецов Смирнова Ковалёв Новик Лукашевич Шевченко Мельник Бондаренко '
            'Козлова Волков Морозов Лебедев Зайцев Павлова Соколов Михайлов Фёдорова Ткачук Жук Климович '
            'Ivanov Petrova Smith Brown Novak Kowalski Miller Wagner Taylor Klimovich').split()
ORGS = ('РОМАШКА ВЕКТОР ГОРИЗОНТ СТРОЙМОНТАЖ АЛЬФА-ТРЕЙД ТЕХНОСЕРВИС АГРОКОМБИНАТ ЛОГИСТИКА ПЛЮС '
        'МЕДЦЕНТР ЭНЕРГОСБЫТ КРЕДО ЮНИКОМ МАГИСТРАЛЬ ДОРСТРОЙ СЕВЕР ТРАНЗИТ ОПТИМА БЕЛСНАБ').split()
CITIES = 'МИНСК ГРОДНО БРЕСТ ГОМЕЛЬ МОГИЛЕВ ВИТЕБСК МОСКВА ПИНСК БОБРУЙСК ЛИДА'.split()
FORMS = ['ОБЩЕСТВО С ОГРАНИЧЕННОЙ ОТВЕТСТВЕННОСТЬЮ', 'ЧАСТНОЕ ПРОИЗВОДСТВЕННОЕ УНИТАРНОЕ ПРЕДПРИЯТИЕ',
         'ОТКРЫТОЕ АКЦИОНЕРНОЕ ОБЩЕСТВО', 'ИНДИВИДУАЛЬНЫЙ ПРЕДПРИНИМАТЕЛЬ', 'ГОСУДАРСТВЕННОЕ УЧРЕЖДЕНИЕ',
         'ТАВАРЫСТВА З АБМЕЖАВАНАЙ АДКАЗНАСЦЮ', 'LIMITED LIABILITY COMPANY', 'ЗАКРЫТОЕ АКЦИОНЕРНОЕ ОБЩЕСТВО']
RECT_TEXT = ['КОПИЯ ВЕРНА', 'ПОЛУЧЕНО', 'ОПЛАЧЕНО', 'УТВЕРЖДАЮ', 'СОГЛАСОВАНО', 'ДЛЯ ДОКУМЕНТОВ', 'ПРОВЕРЕНО',
             'ВХОДЯЩИЙ', 'ОТМЕНЕНО', 'АРХИВ', 'ОРИГИНАЛ', 'ПРИНЯТО', 'ЗАРЕГИСТРИРОВАНО', 'COPY', 'PAID', 'RECEIVED']
HAND_PHRASES = ['согласовано', 'получил', 'копия верна', 'ознакомлен', 'не возражаю', 'к оплате', 'в приказ',
                'принято', 'с условиями согласен', 'исп.', 'вх. №', 'утв.', 'отказать', 'в дело', 'срочно']

_font_cache: dict[tuple[str, int], ImageFont.FreeTypeFont] = {}


def font(path: str, size: int) -> ImageFont.FreeTypeFont:
    key = (path, max(4, int(size)))
    if key not in _font_cache:
        _font_cache[key] = ImageFont.truetype(path, key[1])
    return _font_cache[key]


def available(paths):
    return [p for p in paths if Path(p).exists()]


def hand_fonts(cyrillic_only=False):
    fonts = sorted(str(p) for p in (FONT_CACHE / 'hand').glob('*.ttf'))
    if not cyrillic_only:
        return fonts
    return [f for f in fonts if supports(f, 'ЖщЫёя')]


_support: dict[tuple[str, str], bool] = {}


def supports(path, chars):
    key = (path, chars)
    if key not in _support:
        try:
            from fontTools.ttLib import TTFont
            cmap = TTFont(path, lazy=True).getBestCmap()
            _support[key] = all(ord(c) in cmap for c in chars)
        except Exception:
            _support[key] = False
    return _support[key]


def phrase(rng: random.Random, count: int) -> str:
    pool = rng.choice([WORDS_RU, WORDS_RU, WORDS_RU, WORDS_BY, WORDS_EN])
    words = [rng.choice(pool) for _ in range(count)]
    for i in range(len(words)):
        r = rng.random()
        if r < .06:
            words[i] = str(rng.randint(1, 99999))
        elif r < .09:
            words[i] = f'{rng.randint(1, 28):02d}.{rng.randint(1, 12):02d}.20{rng.randint(10, 30)}'
        elif r < .14:
            words[i] = words[i].capitalize()
        if rng.random() < .08:
            words[i] += rng.choice([',', '.', ';', ':'])
    return ' '.join(words)


@dataclass
class Layer:
    """An alpha layer in canvas coordinates and its constant ink colour."""
    alpha: np.ndarray  # float32 0..1
    color: tuple
    label: int


class Canvas:
    def __init__(self, width, height, rng):
        self.w, self.h, self.rng = width, height, rng
        self.layers: list[Layer] = []

    def new_alpha(self):
        return Image.new('L', (self.w, self.h), 0)

    def add(self, img: Image.Image | np.ndarray, color, label):
        alpha = np.asarray(img, dtype=np.float32) / 255 if isinstance(img, Image.Image) else img.astype(np.float32)
        if alpha.max() > 0:
            self.layers.append(Layer(alpha, color, label))


def ink_color(rng, kind):
    if kind == 'black':
        v = rng.randint(0, 55)
        return (v + rng.randint(-6, 6), v + rng.randint(-6, 6), v + rng.randint(-6, 10))
    if kind == 'blue':
        return (rng.randint(5, 70), rng.randint(20, 90), rng.randint(110, 215))
    if kind == 'violet':
        return (rng.randint(70, 140), rng.randint(30, 85), rng.randint(140, 215))
    if kind == 'red':
        return (rng.randint(165, 225), rng.randint(15, 75), rng.randint(30, 90))
    if kind == 'green':
        return (rng.randint(10, 70), rng.randint(100, 160), rng.randint(40, 100))
    if kind == 'gray':
        v = rng.randint(70, 130)
        return (v, v, v)
    raise ValueError(kind)


def pen_kind(rng):
    return rng.choices(['blue', 'black', 'violet', 'blue'], weights=[55, 25, 12, 8])[0]


def stamp_kind(rng):
    return rng.choices(['blue', 'violet', 'red', 'black', 'green'], weights=[48, 30, 14, 6, 2])[0]


# ---------------------------------------------------------------- printed ---
def draw_text_line(canvas, draw, x, y, text, fnt, fill=255):
    draw.text((x, y), text, font=fnt, fill=fill)
    return draw.textlength(text, font=fnt)


def printed_block(canvas: Canvas, region, xh, rng, color_kind='black'):
    """Paragraph-like printed text inside region (x0, y0, x1, y1)."""
    x0, y0, x1, y1 = region
    path = rng.choice(available(PRINT_FONTS))
    size = max(6, int(xh * rng.uniform(1.75, 2.1)))
    fnt = font(path, size)
    img = canvas.new_alpha(); draw = ImageDraw.Draw(img)
    lead = size * rng.uniform(1.15, 1.7)
    y = y0
    boxes = []
    while y + size < y1:
        indent = rng.choice([0, 0, 0, size * 2])
        words = []
        line = ''
        while True:
            word = phrase(rng, 1)
            trial = (line + ' ' + word).strip()
            if draw.textlength(trial, font=fnt) > (x1 - x0 - indent):
                break
            line = trial
            words.append(word)
        if rng.random() < .15:
            line = ' '.join(line.split()[: max(1, len(line.split()) // 2)])
        if line:
            width = draw_text_line(canvas, draw, x0 + indent, y, line, fnt)
            boxes.append((x0 + indent, y, x0 + indent + width, y + size))
        y += lead
    canvas.add(img, ink_color(rng, color_kind), PRINT)
    return boxes, size


def form_lines(canvas: Canvas, rng, xh, count):
    """Field rules, captions and underscores typical for signature blocks."""
    lines_img = canvas.new_alpha(); ld = ImageDraw.Draw(lines_img)
    text_img = canvas.new_alpha(); td = ImageDraw.Draw(text_img)
    path = rng.choice(available(PRINT_FONTS))
    size = max(6, int(xh * rng.uniform(1.7, 2.1)))
    fnt = font(path, size)
    fields = []
    color = ink_color(rng, 'black')
    for _ in range(count):
        y = rng.randint(int(size * 3), canvas.h - size * 2)
        x = rng.randint(0, max(1, canvas.w // 3))
        caption = rng.choice(['Подпись', 'Директор', 'Гл. бухгалтер', 'Исполнитель', 'Заказчик', 'Принял', 'Сдал',
                              'Signature', 'М.П.', 'Ф.И.О.', 'Руководитель', 'Дата', 'Подпіс', 'Утверждаю'])
        width = td.textlength(caption, font=fnt)
        td.text((x, y - size), caption, font=fnt, fill=255)
        lx0 = int(x + width + rng.randint(4, 20)); lx1 = min(canvas.w - 1, lx0 + rng.randint(80, 360))
        thick = rng.choice([1, 1, 1, 2])
        if rng.random() < .25:  # underscores rendered as glyphs
            under = '_' * max(3, int((lx1 - lx0) / max(3, td.textlength('_', font=fnt))))
            td.text((lx0, y - size), under, font=fnt, fill=255)
        else:
            ld.line((lx0, y, lx1, y), fill=255, width=thick)
        if rng.random() < .4:
            sub = rng.choice(['(подпись)', '(Ф.И.О.)', '(расшифровка подписи)', '(signature)', '(дата)'])
            small = font(path, max(5, int(size * .7)))
            td.text(((lx0 + lx1) / 2 - td.textlength(sub, font=small) / 2, y + 2), sub, font=small, fill=255)
        if rng.random() < .5:
            tail = '/' + rng.choice(SURNAMES) + ' ' + rng.choice('АБВГДЕИКЛМНОПРСТ') + '.' + rng.choice('АБВГДЕИКЛМНОПРСТ') + './'
            td.text((lx1 + 6, y - size), tail, font=fnt, fill=255)
        fields.append((lx0, y, lx1))
    canvas.add(lines_img, color, LINE)
    canvas.add(text_img, color, PRINT)
    return fields


def table(canvas, rng, xh, region):
    x0, y0, x1, y1 = region
    lines_img = canvas.new_alpha(); ld = ImageDraw.Draw(lines_img)
    text_img = canvas.new_alpha(); td = ImageDraw.Draw(text_img)
    rows = rng.randint(2, 7); cols = rng.randint(2, 6)
    xs = sorted([x0, x1] + rng.sample(range(x0 + 20, max(x0 + 21, x1 - 20)), min(cols - 1, max(0, x1 - x0 - 40))))
    rh = max(int(xh * 3.2), (y1 - y0) // rows)
    fnt = font(rng.choice(available(PRINT_FONTS)), max(6, int(xh * 1.9)))
    thick = rng.choice([1, 1, 2])
    for r in range(rows + 1):
        y = y0 + r * rh
        if y > y1: break
        ld.line((x0, y, x1, y), fill=255, width=thick)
    for x in xs:
        ld.line((x, y0, x, min(y1, y0 + rows * rh)), fill=255, width=thick)
    for r in range(rows):
        for c in range(len(xs) - 1):
            if rng.random() < .8:
                txt = phrase(rng, rng.randint(1, 3))
                while txt and td.textlength(txt, font=fnt) > xs[c + 1] - xs[c] - 6:
                    txt = txt[:-1]
                td.text((xs[c] + 3, y0 + r * rh + 3), txt, font=fnt, fill=255)
    color = ink_color(rng, 'black')
    canvas.add(lines_img, color, LINE)
    canvas.add(text_img, color, PRINT)


def barcode(canvas, rng, xh):
    img = canvas.new_alpha(); d = ImageDraw.Draw(img)
    x = rng.randint(0, canvas.w - 120); y = rng.randint(0, canvas.h - 60)
    if rng.random() < .5:  # QR-like
        n = rng.randint(21, 33); cell = rng.randint(2, 4)
        for i in range(n):
            for j in range(n):
                if rng.random() < .5:
                    d.rectangle((x + i * cell, y + j * cell, x + (i + 1) * cell - 1, y + (j + 1) * cell - 1), fill=255)
    else:
        cx = x
        for _ in range(rng.randint(30, 60)):
            w = rng.choice([1, 1, 2, 3])
            d.rectangle((cx, y, cx + w - 1, y + rng.randint(30, 60)), fill=255)
            cx += w + rng.choice([1, 2, 3])
    canvas.add(img, ink_color(rng, 'black'), PRINT)


# ------------------------------------------------------------- handwriting ---
def elastic(alpha: np.ndarray, rng, amp, scale):
    h, w = alpha.shape
    nr = np.random.default_rng(rng.randint(0, 2**31))
    dx = cv2.GaussianBlur((nr.random((h, w), dtype=np.float32) - .5), (0, 0), scale) * amp * scale * 4
    dy = cv2.GaussianBlur((nr.random((h, w), dtype=np.float32) - .5), (0, 0), scale) * amp * scale * 4
    gx, gy = np.meshgrid(np.arange(w, dtype=np.float32), np.arange(h, dtype=np.float32))
    return cv2.remap(alpha, gx + dx, gy + dy, cv2.INTER_LINEAR, borderValue=0)


def pen_texture(alpha, rng, strength):
    """Ballpoint ink: slightly uneven density along strokes."""
    nr = np.random.default_rng(rng.randint(0, 2**31))
    noise = cv2.GaussianBlur(nr.random(alpha.shape, dtype=np.float32), (0, 0), rng.uniform(.8, 2.5))
    noise = (noise - noise.mean()) / (noise.std() + 1e-6)
    return np.clip(alpha * (1 - strength * .25 + strength * .25 * noise) * rng.uniform(.75, 1.0), 0, 1)


def render_text_patch(text, path, size, stroke=0):
    fnt = font(path, size)
    bbox = fnt.getbbox(text, stroke_width=stroke)
    w, h = bbox[2] - bbox[0] + 8, bbox[3] - bbox[1] + 8
    img = Image.new('L', (max(1, w), max(1, h)), 0)
    ImageDraw.Draw(img).text((4 - bbox[0], 4 - bbox[1]), text, font=fnt, fill=255, stroke_width=stroke, stroke_fill=255)
    return np.asarray(img, dtype=np.float32) / 255


def paste_alpha(dst, patch, x, y):
    h, w = patch.shape
    x0, y0 = max(0, x), max(0, y)
    x1, y1 = min(dst.shape[1], x + w), min(dst.shape[0], y + h)
    if x1 <= x0 or y1 <= y0:
        return False
    dst[y0:y1, x0:x1] = np.maximum(dst[y0:y1, x0:x1], patch[y0 - y:y1 - y, x0 - x:x1 - x])
    return True


def transform_patch(patch, rng, angle, shear, scale=1.0):
    h, w = patch.shape
    pad = int(max(h, w) * .6) + 4
    big = np.zeros((h + 2 * pad, w + 2 * pad), np.float32); big[pad:pad + h, pad:pad + w] = patch
    cx, cy = big.shape[1] / 2, big.shape[0] / 2
    m = cv2.getRotationMatrix2D((cx, cy), angle, scale)
    sh = np.array([[1, shear, -shear * cy], [0, 1, 0]], np.float32)
    m3 = np.vstack([m, [0, 0, 1]]) @ np.vstack([sh, [0, 0, 1]])
    out = cv2.warpAffine(big, m3[:2], (big.shape[1], big.shape[0]), flags=cv2.INTER_LINEAR)
    ys, xs = np.nonzero(out > .02)
    if len(xs) == 0:
        return out[:1, :1]
    return out[ys.min():ys.max() + 1, xs.min():xs.max() + 1]


def handwriting(canvas: Canvas, rng, xh, x, y, text=None, kind=None):
    fonts = hand_fonts(cyrillic_only=True)
    if text is None:
        r = rng.random()
        if r < .35:
            text = rng.choice(SURNAMES) + ' ' + rng.choice('АБВГДЕИКЛМНОПРСТ') + '.' + rng.choice('АБВГДЕИКЛМНОПРСТ') + '.'
        elif r < .55:
            text = f'{rng.randint(1, 28):02d}.{rng.randint(1, 12):02d}.{rng.choice(["2025", "2026", "25", "26"])}'
        elif r < .75:
            text = rng.choice(HAND_PHRASES)
        else:
            text = phrase(rng, rng.randint(1, 4)).lower()
    size = int(xh * rng.uniform(2.0, 3.6))
    path = rng.choice(fonts)
    patch = render_text_patch(text, path, size, stroke=rng.choice([0, 0, 1]) if size > 24 else 0)
    patch = elastic(patch, rng, .6, max(2, size / 6))
    patch = transform_patch(patch, rng, rng.uniform(-6, 6), rng.uniform(-.35, .15))
    patch = pen_texture(patch, rng, rng.uniform(.3, 1))
    alpha = np.zeros((canvas.h, canvas.w), np.float32)
    if paste_alpha(alpha, patch, int(x), int(y - patch.shape[0] * .75)):
        canvas.add(alpha, ink_color(rng, kind or pen_kind(rng)), HAND)
        return (int(x), int(y - patch.shape[0] * .75), int(x + patch.shape[1]), int(y + patch.shape[0] * .25))
    return None


def smooth_path(points, samples_per_seg=16):
    """Catmull-Rom spline through control points."""
    pts = np.asarray(points, np.float64)
    if len(pts) < 3:
        return pts
    pts = np.vstack([pts[0], pts, pts[-1]])
    out = []
    for i in range(1, len(pts) - 2):
        p0, p1, p2, p3 = pts[i - 1], pts[i], pts[i + 1], pts[i + 2]
        for t in np.linspace(0, 1, samples_per_seg, endpoint=False):
            t2, t3 = t * t, t * t * t
            out.append(.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3))
    out.append(pts[-2])
    return np.asarray(out)


def draw_polyline(shape, path, width, rng, ss=3, taper=True):
    """Antialiased variable-width polyline rendered at ss× supersampling."""
    h, w = shape
    img = np.zeros((h * ss, w * ss), np.uint8)
    n = len(path)
    phase = rng.uniform(0, 6.28); freq = rng.uniform(.01, .05)
    for i in range(n - 1):
        t = i / max(1, n - 1)
        pressure = .75 + .25 * math.sin(phase + i * freq * 6.28)
        if taper:
            pressure *= min(1, .35 + 4 * t, .35 + 4 * (1 - t))
        wid = max(1, int(round(width * pressure * ss)))
        p, q = path[i] * ss, path[i + 1] * ss
        cv2.line(img, (int(p[0]), int(p[1])), (int(q[0]), int(q[1])), 255, wid, cv2.LINE_AA)
    return cv2.resize(img, (w, h), interpolation=cv2.INTER_AREA).astype(np.float32) / 255


def signature(canvas: Canvas, rng, xh, cx, cy):
    """Procedural signature: optional script-font name + cursive trajectory + flourish."""
    height = xh * rng.uniform(3.5, 9)
    width = height * rng.uniform(1.6, 4.5)
    pad = int(height)
    W, H = int(width + 2 * pad), int(height * 2 + 2 * pad)
    alpha = np.zeros((H, W), np.float32)
    pen = max(1.0, xh * rng.uniform(.14, .32))
    style = rng.random()
    x0, base = pad, pad + height
    if style < .45:
        # Script font word with an exaggerated first capital.
        fonts = hand_fonts(cyrillic_only=False)
        name = rng.choice(SURNAMES)
        name = name[:rng.randint(2, len(name))] if rng.random() < .5 else name
        patch = render_text_patch(name, rng.choice(fonts), int(height * rng.uniform(.7, 1.1)), stroke=rng.choice([0, 1]))
        patch = elastic(patch, rng, 1.2, max(2, height / 7))
        patch = transform_patch(patch, rng, rng.uniform(-12, 8), rng.uniform(-.5, .1), scale=rng.uniform(.8, 1.1))
        paste_alpha(alpha, patch, int(x0), int(base - patch.shape[0] * .8))
        end_x = x0 + patch.shape[1]
    else:
        # Cursive-like trajectory of loops with drifting amplitude.
        letters = rng.randint(4, 12)
        pts = []
        x = x0
        for k in range(letters):
            step = width / letters * rng.uniform(.6, 1.4)
            amp = height * rng.uniform(.25, .7) * (1.6 if k == 0 and rng.random() < .6 else 1)
            loop = rng.random()
            if loop < .4:  # loop letter (l, e, o)
                pts += [(x, base), (x + step * .6, base - amp), (x + step * .1, base - amp * 1.1), (x + step * .3, base), (x + step, base - amp * .2)]
            elif loop < .7:  # arch (n, m)
                pts += [(x, base), (x + step * .3, base - amp * .7), (x + step * .6, base), (x + step * .8, base - amp * .6), (x + step, base)]
            else:  # descender
                pts += [(x, base - amp * .3), (x + step * .4, base + amp * .7), (x + step * .2, base + amp * .9), (x + step * .7, base - amp * .3)]
            x += step
            base += rng.uniform(-.06, .06) * height
        pts = [(px + rng.gauss(0, pen), py + rng.gauss(0, pen)) for px, py in pts]
        path = smooth_path(pts, 10)
        alpha = np.maximum(alpha, draw_polyline(alpha.shape, path, pen, rng))
        end_x = x
    # Flourish: underline swoosh, crossing stroke or big loop.
    for _ in range(rng.choice([0, 1, 1, 2])):
        f = rng.random()
        if f < .5:
            ys = base + height * rng.uniform(-.1, .35)
            pts = [(x0 - pad * rng.uniform(0, .5), ys + height * rng.uniform(-.2, .2)),
                   ((x0 + end_x) / 2, ys + height * rng.uniform(.0, .3)),
                   (end_x + pad * rng.uniform(.0, .8), ys - height * rng.uniform(.0, .6))]
        elif f < .75:
            sx = x0 + rng.uniform(0, width * .3)
            pts = [(sx, base), (sx + height * .4, base - height * 1.2), (sx - height * .3, base - height * .9),
                   (sx + height * .2, base + height * .4), (sx + width * .8, base - height * .2)]
        else:
            pts = [(end_x, base), (end_x + height * .5, base - height * .6), (end_x - width * .5, base + height * .3),
                   (end_x - width * .9, base - height * .1)]
        pts = [(px + rng.gauss(0, pen * 2), py + rng.gauss(0, pen * 2)) for px, py in pts]
        alpha = np.maximum(alpha, draw_polyline(alpha.shape, smooth_path(pts, 20), pen * rng.uniform(.7, 1.1), rng))
    # Initials before the signature body.
    alpha = transform_patch(alpha, rng, rng.uniform(-14, 14), rng.uniform(-.25, .25))
    alpha = pen_texture(alpha, rng, rng.uniform(.2, 1))
    if rng.random() < .15:  # gel pen / marker: heavier strokes
        alpha = cv2.dilate(alpha, np.ones((2, 2), np.uint8))
    full = np.zeros((canvas.h, canvas.w), np.float32)
    x, y = int(cx - alpha.shape[1] / 2), int(cy - alpha.shape[0] / 2)
    if paste_alpha(full, alpha, x, y):
        canvas.add(full, ink_color(rng, pen_kind(rng)), SIGN)
        return (x, y, x + alpha.shape[1], y + alpha.shape[0])
    return None


# ------------------------------------------------------------------- stamps ---
def arc_text(img, text, cx, cy, radius, fnt, start, sweep, inward=False):
    draw_w = sum(fnt.getlength(c) for c in text)
    if draw_w <= 0:
        return
    angle = start
    for ch in text:
        cw = fnt.getlength(ch)
        a = angle + (cw / 2) / radius * (sweep / abs(sweep))
        size = int(fnt.size * 1.6) + 4
        glyph = Image.new('L', (size, size), 0)
        ImageDraw.Draw(glyph).text((size / 2, size / 2), ch, font=fnt, fill=255, anchor='mm')
        rot = -math.degrees(a) - 90 if not inward else -math.degrees(a) + 90
        glyph = glyph.rotate(rot, resample=Image.BICUBIC, expand=False)
        gx = cx + radius * math.cos(a) - size / 2
        gy = cy + radius * math.sin(a) - size / 2
        img.paste(255, (int(gx), int(gy)), glyph)
        angle += cw / radius * (sweep / abs(sweep))


def emblem(draw, cx, cy, r, rng):
    kind = rng.random()
    if kind < .3:  # star
        pts = []
        for k in range(10):
            rr = r if k % 2 == 0 else r * .45
            a = -math.pi / 2 + k * math.pi / 5
            pts.append((cx + rr * math.cos(a), cy + rr * math.sin(a)))
        draw.polygon(pts, fill=255)
    elif kind < .55:  # shield
        w = r * .8
        draw.line([(cx - w, cy - r), (cx + w, cy - r), (cx + w, cy + r * .2), (cx, cy + r), (cx - w, cy + r * .2), (cx - w, cy - r)], fill=255, width=max(1, int(r / 10)))
        draw.ellipse((cx - r * .3, cy - r * .5, cx + r * .3, cy + r * .1), outline=255, width=max(1, int(r / 12)))
    elif kind < .75:  # concentric circle + rays
        draw.ellipse((cx - r * .5, cy - r * .5, cx + r * .5, cy + r * .5), outline=255, width=max(1, int(r / 10)))
        for k in range(12):
            a = k * math.pi / 6
            draw.line([(cx + r * .6 * math.cos(a), cy + r * .6 * math.sin(a)), (cx + r * math.cos(a), cy + r * math.sin(a))], fill=255, width=max(1, int(r / 14)))
    else:  # wreath-like arcs
        for side in (-1, 1):
            for k in range(6):
                a = math.pi / 2 + side * (k * .35 + .3)
                px, py = cx + r * .8 * math.cos(a), cy + r * .8 * math.sin(a)
                draw.ellipse((px - r * .15, py - r * .08, px + r * .15, py + r * .08), fill=255)


def stamp_texture(alpha, rng):
    nr = np.random.default_rng(rng.randint(0, 2**31))
    h, w = alpha.shape
    low = cv2.GaussianBlur(nr.random((h, w), dtype=np.float32), (0, 0), max(h, w) / rng.uniform(4, 12))
    low = (low - low.min()) / (low.max() - low.min() + 1e-6)
    high = cv2.GaussianBlur(nr.random((h, w), dtype=np.float32), (0, 0), rng.uniform(.6, 1.5))
    high = (high - high.mean()) / (high.std() + 1e-6)
    gx, gy = np.meshgrid(np.linspace(-1, 1, w), np.linspace(-1, 1, h))
    ang = rng.uniform(0, 6.28)
    ramp = (gx * math.cos(ang) + gy * math.sin(ang)) * .5 + .5
    density = rng.uniform(.55, 1.0) * (1 - rng.uniform(0, .6) * low) * (1 - rng.uniform(0, .5) * ramp)
    density = density * (1 + rng.uniform(.05, .35) * high)
    if rng.random() < .25:  # dry stamp: dropouts
        drop = cv2.GaussianBlur(nr.random((h, w), dtype=np.float32), (0, 0), rng.uniform(1, 3))
        density *= (drop > rng.uniform(.35, .5)).astype(np.float32) * .8 + .2
    out = np.clip(alpha * density, 0, 1)
    if rng.random() < .6:
        out = cv2.GaussianBlur(out, (0, 0), rng.uniform(.3, 1.0))
    if rng.random() < .15:  # smeared/double impression
        shift = np.float32([[1, 0, rng.uniform(-3, 3)], [0, 1, rng.uniform(-3, 3)]])
        out = np.maximum(out, cv2.warpAffine(out, shift, (w, h)) * rng.uniform(.3, .6))
    return out


def round_stamp(rng, radius):
    size = int(radius * 2 + 16)
    img = Image.new('L', (size, size), 0); d = ImageDraw.Draw(img)
    c = size / 2
    w1 = max(1, int(radius * rng.uniform(.012, .035)))
    d.ellipse((c - radius, c - radius, c + radius, c + radius), outline=255, width=w1)
    inner = radius - max(3, radius * rng.uniform(.05, .1))
    if rng.random() < .8:
        d.ellipse((c - inner, c - inner, c + inner, c + inner), outline=255, width=max(1, w1 - 1))
    fpath = rng.choice(available(STAMP_FONTS))
    th = max(6, int(radius * rng.uniform(.11, .17)))
    fnt = font(fpath, th)
    text_r = inner - th * .75
    org = rng.choice(FORMS) + ' «' + rng.choice(ORGS) + '»'
    ring = org + ' * ' + rng.choice(CITIES) + ' * '
    circumference = 2 * math.pi * text_r
    while sum(fnt.getlength(ch) for ch in ring) > circumference * .97 and fnt.size > 5:
        fnt = font(fpath, fnt.size - 1)
    start = rng.uniform(0, 2 * math.pi)
    arc_text(img, ring, c, c, text_r, fnt, start, 1)
    r2 = text_r - th * .8
    if rng.random() < .65:
        d.ellipse((c - r2, c - r2, c + r2, c + r2), outline=255, width=max(1, w1 - 1))
        if rng.random() < .5:  # second text ring
            fnt2 = font(fpath, max(5, int(th * .85)))
            arc_text(img, f'УНП {rng.randint(100000000, 999999999)}' + ' * ' * rng.randint(1, 3), c, c, r2 - th * .6, fnt2, rng.uniform(0, 6.28), 1)
            r2 -= th * 1.3
    centre = rng.random()
    if centre < .45:
        emblem(d, c, c, r2 * rng.uniform(.4, .7), rng)
    else:
        lines = rng.sample(['ДЛЯ', 'ДОКУМЕНТОВ', rng.choice(ORGS), f'{rng.randint(1000000, 99999999)}', 'МІНСК', 'БУХГАЛТЕРИЯ', 'ОТДЕЛ КАДРОВ', '№ ' + str(rng.randint(1, 99))], rng.randint(1, 3))
        cf = font(fpath, max(5, int(th * rng.uniform(.9, 1.4))))
        total = len(lines) * cf.size * 1.2
        for k, line in enumerate(lines):
            while cf.getlength(line) > r2 * 1.7 and cf.size > 5:
                cf = font(fpath, cf.size - 1)
            d.text((c, c - total / 2 + k * cf.size * 1.2 + cf.size / 2), line, font=cf, fill=255, anchor='mm')
    return np.asarray(img, np.float32) / 255


def rect_stamp(rng, xh):
    fpath = rng.choice(available(STAMP_FONTS))
    th = max(7, int(xh * rng.uniform(1.8, 3.2)))
    fnt = font(fpath, th)
    lines = [rng.choice(RECT_TEXT)]
    if rng.random() < .7:
        lines.append(rng.choice([rng.choice(FORMS)[:24], f'«{rng.choice(ORGS)}»', f'Вх. № {rng.randint(1, 999)}',
                                 '«___» ____________ 20__ г.', f'{rng.randint(1, 28):02d}.{rng.randint(1, 12):02d}.2026', 'Подпись ________']))
    if rng.random() < .4:
        lines.append(rng.choice(['Директор ________', f'УНП {rng.randint(100000000, 999999999)}', 'Исп. ________', rng.choice(CITIES)]))
    small = font(fpath, max(6, int(th * .75)))
    widths = [fnt.getlength(lines[0])] + [small.getlength(t) for t in lines[1:]]
    pad = th * rng.uniform(.5, 1.0)
    w = int(max(widths) + pad * 2); h = int(th * 1.35 + (len(lines) - 1) * small.size * 1.35 + pad * 2)
    img = Image.new('L', (w + 8, h + 8), 0); d = ImageDraw.Draw(img)
    bw = max(1, int(th * rng.uniform(.06, .15)))
    d.rectangle((4, 4, w + 3, h + 3), outline=255, width=bw)
    if rng.random() < .35:
        d.rectangle((4 + bw + 2, 4 + bw + 2, w + 1 - bw, h + 1 - bw), outline=255, width=max(1, bw - 1))
    y = 4 + pad
    d.text((4 + w / 2, y + th / 2), lines[0], font=fnt, fill=255, anchor='mm'); y += th * 1.35
    for line in lines[1:]:
        d.text((4 + w / 2, y + small.size / 2), line, font=small, fill=255, anchor='mm'); y += small.size * 1.35
    return np.asarray(img, np.float32) / 255


def oval_or_triangle(rng, radius):
    size = int(radius * 2 + 16)
    img = Image.new('L', (size, int(size * .8)), 0); d = ImageDraw.Draw(img)
    fpath = rng.choice(available(STAMP_FONTS))
    if rng.random() < .5:
        d.ellipse((8, 8, size - 8, size * .8 - 8), outline=255, width=max(1, int(radius * .03)))
        cf = font(fpath, max(6, int(radius * .16)))
        d.text((size / 2, size * .4), rng.choice(RECT_TEXT), font=cf, fill=255, anchor='mm')
        d.text((size / 2, size * .4 + cf.size * 1.3), rng.choice(ORGS), font=cf, fill=255, anchor='mm')
    else:
        pts = [(size / 2, 8), (size - 8, size * .8 - 8), (8, size * .8 - 8)]
        d.polygon(pts, outline=255, width=max(1, int(radius * .03)))
        cf = font(fpath, max(6, int(radius * .14)))
        d.text((size / 2, size * .55), rng.choice(['ДЛЯ ПАКЕТОВ', 'ОТК', 'ОПЛАЧЕНО', rng.choice(ORGS)]), font=cf, fill=255, anchor='mm')
    return np.asarray(img, np.float32) / 255


def stamp(canvas: Canvas, rng, xh, cx, cy):
    r = rng.random()
    if r < .62:
        radius = xh * rng.uniform(9, 19)
        alpha = round_stamp(rng, radius)
        angle = rng.uniform(0, 360)
    elif r < .9:
        alpha = rect_stamp(rng, xh)
        angle = rng.uniform(-25, 25) if rng.random() < .7 else rng.uniform(-90, 90)
    else:
        alpha = oval_or_triangle(rng, xh * rng.uniform(8, 15))
        angle = rng.uniform(-30, 30)
    alpha = transform_patch(alpha, rng, angle, 0, scale=rng.uniform(.95, 1.05))
    if rng.random() < .3:  # slightly elliptical impression (tilted press / photo)
        alpha = cv2.resize(alpha, (alpha.shape[1], max(4, int(alpha.shape[0] * rng.uniform(.85, 1.0)))))
    alpha = stamp_texture(alpha, rng)
    if rng.random() < .12:  # partial impression: one side missing
        h, w = alpha.shape
        gx, gy = np.meshgrid(np.arange(w), np.arange(h))
        a = rng.uniform(0, 6.28)
        cut = (gx - w / 2) * math.cos(a) + (gy - h / 2) * math.sin(a) < rng.uniform(.1, .4) * max(h, w)
        alpha *= cut
    full = np.zeros((canvas.h, canvas.w), np.float32)
    x, y = int(cx - alpha.shape[1] / 2), int(cy - alpha.shape[0] / 2)
    if paste_alpha(full, alpha, x, y):
        canvas.add(full, ink_color(rng, stamp_kind(rng)), STAMP)
        return (x, y, x + alpha.shape[1], y + alpha.shape[0])
    return None


# ------------------------------------------------------------ composition ---
def paper(rng, h, w):
    base = np.array([rng.uniform(232, 255), rng.uniform(232, 255), rng.uniform(225, 255)], np.float32)
    if rng.random() < .5:
        base[:] = base.mean()
    img = np.ones((h, w, 3), np.float32) * base
    nr = np.random.default_rng(rng.randint(0, 2**31))
    fiber = cv2.GaussianBlur(nr.normal(0, 1, (h, w)).astype(np.float32), (0, 0), rng.uniform(.5, 2)) * rng.uniform(0, 4)
    img += fiber[..., None]
    return img


def composite(canvas: Canvas, rng):
    img = paper(rng, canvas.h, canvas.w)
    labels = np.zeros((canvas.h, canvas.w), np.uint8)
    order = list(canvas.layers)
    # Stamps are usually pressed after signing; sometimes the pen goes over the seal.
    order.sort(key=lambda layer: (layer.label == STAMP) * (1 if rng.random() < .8 else -1))
    for layer in order:
        a = layer.alpha[..., None]
        color = np.array(layer.color, np.float32) / 255
        img = img * (1 - a + a * color)
        labels |= ((layer.alpha > .3) * layer.label).astype(np.uint8)
    return np.clip(img, 0, 255), labels


def degrade(img, labels, rng, info=None):
    h, w = labels.shape
    mode = rng.random()
    nr = np.random.default_rng(rng.randint(0, 2**31))
    if mode < .3:  # phone photo: perspective, light falloff, colour cast
        d = min(h, w) * rng.uniform(.0, .08)
        src = np.float32([[0, 0], [w, 0], [w, h], [0, h]])
        dst = src + np.float32([[rng.uniform(-d, d), rng.uniform(-d, d)] for _ in range(4)])
        m = cv2.getPerspectiveTransform(src, dst)
        if info is not None:
            info['homography'] = m
        img = cv2.warpPerspective(img, m, (w, h), borderMode=cv2.BORDER_REPLICATE)
        labels = cv2.warpPerspective(labels, m, (w, h), flags=cv2.INTER_NEAREST, borderValue=0)
        gx, gy = np.meshgrid(np.linspace(-1, 1, w), np.linspace(-1, 1, h))
        ang = rng.uniform(0, 6.28)
        light = 1 - rng.uniform(.0, .45) * ((gx * math.cos(ang) + gy * math.sin(ang)) * .5 + .5)
        light *= 1 - rng.uniform(0, .25) * (gx ** 2 + gy ** 2) / 2
        img = img * light[..., None] * np.array([rng.uniform(.85, 1.05), rng.uniform(.9, 1.05), rng.uniform(.85, 1.1)], np.float32)
        if rng.random() < .5:
            img = cv2.GaussianBlur(img, (0, 0), rng.uniform(.4, 1.3))
        img += nr.normal(0, rng.uniform(1, 7), img.shape).astype(np.float32)
    elif mode < .75:  # scanner
        if rng.random() < .5:
            img = cv2.GaussianBlur(img, (0, 0), rng.uniform(.3, .9))
        img += nr.normal(0, rng.uniform(0, 4), img.shape).astype(np.float32)
        g = rng.uniform(.8, 1.25)
        img = 255 * (np.clip(img, 0, 255) / 255) ** g
    # else: clean digital rendering
    img = np.clip(img, 0, 255)
    r = rng.random()
    if r < .14:  # greyscale scan / photocopy
        gray = cv2.cvtColor(img.astype(np.uint8), cv2.COLOR_RGB2GRAY).astype(np.float32)
        img = np.repeat(gray[..., None], 3, axis=2)
    elif r < .19:  # bitonal fax-like scan
        gray = cv2.cvtColor(img.astype(np.uint8), cv2.COLOR_RGB2GRAY)
        img = np.repeat(((gray > rng.randint(110, 190)) * 255).astype(np.float32)[..., None], 3, axis=2)
    if rng.random() < .3:  # resolution loss
        f = rng.uniform(.5, .85)
        small = cv2.resize(img, (int(w * f), int(h * f)), interpolation=cv2.INTER_AREA)
        img = cv2.resize(small, (w, h), interpolation=cv2.INTER_LINEAR)
    out = np.clip(img, 0, 255).astype(np.uint8)
    if rng.random() < .55:
        ok, enc = cv2.imencode('.jpg', cv2.cvtColor(out, cv2.COLOR_RGB2BGR), [cv2.IMWRITE_JPEG_QUALITY, rng.randint(35, 95)])
        out = cv2.cvtColor(cv2.imdecode(enc, cv2.IMREAD_COLOR), cv2.COLOR_BGR2RGB)
    return out, labels


def document(seed: int, size=(640, 640), objects=None):
    """Returns (rgb, labels). If `objects` is a list, it receives (kind, x0, y0, x1, y1, visible_fraction)."""
    rng = random.Random(seed)
    h, w = size
    c = Canvas(w, h, rng)
    xh = rng.choice([rng.uniform(4.5, 7), rng.uniform(6, 10), rng.uniform(8, 14)])
    # Printed body: stacked, non-overlapping horizontal bands like a real page.
    y = rng.randint(-40, h // 3)
    while y < h - 20 and rng.random() < .9:
        band = rng.randint(int(xh * 6), max(int(xh * 7), h // 2))
        x0 = rng.randint(-10, w // 5); x1 = rng.randint(w * 3 // 5, w + 10)
        if rng.random() < .2:
            table(c, rng, xh, (x0, y, x1, min(h, y + band)))
        else:
            printed_block(c, (x0, y, x1, min(h, y + band)), xh, rng,
                          'black' if rng.random() < .9 else rng.choice(['blue', 'red', 'gray']))
        y += band + rng.randint(int(xh * 2), int(xh * 12))
        if rng.random() < .35:  # leave room for a signature block
            y += rng.randint(h // 6, h // 2)
    if rng.random() < .1:
        barcode(c, rng, xh)
    fields = form_lines(c, rng, xh, rng.randint(0, 4))
    # Handwritten fills on fields and free notes.
    for lx0, ly, lx1 in fields:
        if rng.random() < .45:
            handwriting(c, rng, xh, lx0 + rng.uniform(0, max(1, (lx1 - lx0) * .4)), ly - rng.uniform(0, xh))
    for _ in range(rng.choice([0, 0, 1, 2, 3])):
        handwriting(c, rng, xh, rng.uniform(0, w * .7), rng.uniform(h * .1, h))
    # Signatures, usually on a field, often crossing printed text.
    sig_boxes, found = [], []
    for _ in range(rng.choice([0, 1, 1, 1, 2, 2, 3])):
        if fields and rng.random() < .6:
            lx0, ly, lx1 = rng.choice(fields)
            box = signature(c, rng, xh, rng.uniform(lx0, lx1), ly - rng.uniform(0, xh * 4))
        else:
            box = signature(c, rng, xh, rng.uniform(0, w), rng.uniform(0, h))
        if box:
            sig_boxes.append(box)
            found.append(('signature', box))
    # Stamps, often over a signature.
    for _ in range(rng.choice([0, 1, 1, 1, 2])):
        if sig_boxes and rng.random() < .55:
            x0, y0, x1, y1 = rng.choice(sig_boxes)
            box = stamp(c, rng, xh, rng.uniform(x0, x1), rng.uniform(y0, y1))
        else:
            box = stamp(c, rng, xh, rng.uniform(0, w), rng.uniform(0, h))
        if box:
            found.append(('stamp', box))
    img, labels = composite(c, rng)
    info = {}
    out = degrade(img, labels, rng, info)
    if objects is not None:
        m = info.get('homography')
        for kind, (x0, y0, x1, y1) in found:
            pts = np.float32([[x0, y0], [x1, y0], [x1, y1], [x0, y1]])
            if m is not None:
                pts = cv2.perspectiveTransform(pts[None], m)[0]
            bx0, by0 = pts.min(0); bx1, by1 = pts.max(0)
            full = max(1.0, (bx1 - bx0) * (by1 - by0))
            cx0, cy0, cx1, cy1 = max(0, bx0), max(0, by0), min(w, bx1), min(h, by1)
            visible = max(0.0, cx1 - cx0) * max(0.0, cy1 - cy0) / full
            objects.append((kind, float(cx0), float(cy0), float(cx1), float(cy1), float(visible)))
    return out


if __name__ == '__main__':
    import sys
    out = Path(sys.argv[1] if len(sys.argv) > 1 else 'preview'); out.mkdir(exist_ok=True)
    for s in range(int(sys.argv[2]) if len(sys.argv) > 2 else 6):
        img, lab = document(s)
        Image.fromarray(img).save(out / f'{s:04d}.png')
        vis = np.zeros(img.shape, np.uint8)
        vis[lab & PRINT > 0] = (0, 0, 0)
        vis[lab == 0] = (255, 255, 255)
        vis[lab & LINE > 0] = (128, 128, 128)
        vis[lab & HAND > 0] = (0, 160, 0)
        vis[lab & SIGN > 0] = (230, 120, 0)
        vis[lab & STAMP > 0] = (40, 60, 230)
        Image.fromarray(vis).save(out / f'{s:04d}_label.png')
