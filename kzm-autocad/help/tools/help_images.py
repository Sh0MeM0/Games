"""Картинки справки КЗМ.

Картограммы рисуются по данным, которые считает расчётная часть модуля (HelpDemo, Kzm.Core): сетка, отметки,
объёмы, линия нулевых работ, штриховка, таблица по рис. 8 СТБ 2073-2010, площади. Остальные картинки — схемы.

Запуск (из папки kzm-autocad):
    dotnet run --project help/tools/HelpDemo -- help/tools/demo.json
    python3 help/tools/help_images.py help/tools/demo.json help/img
"""
import json
import math
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

FONTS = Path('/usr/share/fonts/truetype')
REGULAR = FONTS / 'dejavu' / 'DejaVuSans.ttf'
BOLD = FONTS / 'dejavu' / 'DejaVuSans-Bold.ttf'

# AutoCAD-like model space and the colours of the module (ACI 1 red, 3 green, 5 blue, 7 white).
BACK = (33, 40, 48)
WHITE = (235, 235, 235)
RED = (255, 60, 60)
BLUE = (80, 120, 255)
GREEN = (60, 200, 60)
GREY = (150, 150, 150)


def font(size, bold=False):
    return ImageFont.truetype(str(BOLD if bold else REGULAR), size)


def volume(value, fill):
    rounded = int(math.floor(abs(value) + 0.5))
    if rounded == 0:
        return '0'
    return ('+' if fill else '-') + str(rounded)


def mark(value, signed=False):
    text = ('%.2f' % abs(value)).replace('.', ',')
    if value < 0 and text != '0,00':
        text = '-' + text
    elif signed and text != '0,00':
        text = '+' + text
    return text


class Sheet:
    """World (metres) to pixels; y up."""

    def __init__(self, x0, y0, x1, y1, ppm, margin=40):
        self.ppm = ppm
        self.x0, self.y1 = x0, y1
        self.margin = margin
        self.w = int((x1 - x0) * ppm + 2 * margin)
        self.h = int((y1 - y0) * ppm + 2 * margin)
        self.image = Image.new('RGB', (self.w, self.h), BACK)
        self.draw = ImageDraw.Draw(self.image)

    def p(self, x, y):
        return (self.margin + (x - self.x0) * self.ppm, self.margin + (self.y1 - y) * self.ppm)

    def line(self, a, b, color, width=1):
        self.draw.line([self.p(*a), self.p(*b)], fill=color, width=width)

    def dashed(self, points, color, width=1, dash=1.2, gap=0.8):
        for a, b in zip(points, points[1:]):
            length = math.dist(a, b)
            t = 0.0
            while t < length:
                t1 = min(length, t + dash)
                pa = (a[0] + (b[0] - a[0]) * t / length, a[1] + (b[1] - a[1]) * t / length)
                pb = (a[0] + (b[0] - a[0]) * t1 / length, a[1] + (b[1] - a[1]) * t1 / length)
                self.line(pa, pb, color, width)
                t = t1 + gap

    def text(self, x, y, value, size_m, color, anchor, bold=False, angle=0):
        size = max(9, int(round(size_m * self.ppm * 1.25)))
        f = font(size, bold)
        if angle == 0:
            self.draw.text(self.p(x, y), value, fill=color, font=f, anchor=anchor)
            return
        box = f.getbbox(value)
        tile = Image.new('RGBA', (box[2] + 4, box[3] + 4), (0, 0, 0, 0))
        ImageDraw.Draw(tile).text((2, 2), value, fill=color, font=f)
        tile = tile.rotate(angle, expand=True)
        cx, cy = self.p(x, y)
        self.image.paste(tile, (int(cx - tile.width / 2), int(cy - tile.height / 2)), tile)


def corners(sheet, at, working, project, fact, h):
    """Labels of a node by its corners, as in example Б.4: working top-left, project top-right, fact bottom-right."""
    g = 0.35 * h
    x, y = at
    sheet.text(x - g, y + g, mark(working, True), h, RED if working > 0 else BLUE if working < 0 else WHITE, 'rd')
    sheet.text(x + g, y + g, mark(project), h, WHITE, 'ld')
    sheet.text(x + g, y - g, mark(fact), h, WHITE, 'la')


def cartogram(data, out, title=None, ppm=14):
    step = data['step']
    k = data['scale'] / 1000.0          # metres in the drawing per millimetre on the sheet
    i0, i1, j0, j1 = data['range']
    gap, rh = 10 * k, 8 * k
    u0, u1 = i0 * step, (i1 + 1) * step
    top = j0 * step - gap
    bottom = top - 2 * rh
    areas_top = bottom - 5 * k
    h = 0.9                              # text height in the drawing (замечание 29)
    top_y = max(p[1] for b in data['boundaries'] for p in b) + 3
    sheet = Sheet(u0 - 25 * k - 2, areas_top - 5 * 1.6 * h - 2, u1 + 25 * k + 3, top_y, ppm)
    # Lines a little thicker than in AutoCAD: the picture is scaled down in the browser.
    for a, b in data['hatch']:
        sheet.line(a, b, BLUE, 2)
    for a, b in data['grid']:
        sheet.line(a, b, GREEN, 2)
    for line in data['zero']:
        sheet.dashed([tuple(p) for p in line], WHITE, 2)
    for boundary in data['boundaries']:
        pts = [tuple(p) for p in boundary] + [tuple(boundary[0])]
        for a, b in zip(pts, pts[1:]):
            sheet.line(a, b, WHITE, 3)
    for s in data['squares']:
        x, y = s['at']
        fill, cut = volume(s['fill'], True), volume(s['cut'], False)
        if fill != '0' and cut != '0':
            sheet.text(x, y + 0.15 * h, fill, h, RED, 'md')
            sheet.text(x, y - 0.15 * h, cut, h, BLUE, 'ma')
        else:
            sheet.text(x, y, cut if cut != '0' else fill, h, BLUE if cut != '0' else RED if fill != '0' else WHITE, 'mm')
    for n in data['nodes'] + data['boundaryPoints']:
        corners(sheet, n['at'], n['working'], n['project'], n['fact'], h)
    # Table under the grid, рис. 8 СТБ 2073-2010: 5 + 20 mm on the left, columns l, 5 + 20 mm on the right; rows 8 mm.
    left, label_left, total_left, right = u0 - 25 * k, u0 - 20 * k, u1 + 5 * k, u1 + 25 * k
    sheet.line((left, top), (right, top), WHITE, 1)
    sheet.line((label_left, top - rh), (right, top - rh), WHITE, 1)
    sheet.line((left, bottom), (right, bottom), WHITE, 1)
    for u in [left, label_left, total_left, right] + [i * step for i in range(i0, i1 + 2)]:
        sheet.line((u, top), (u, bottom), WHITE, 1)
    th = 0.9
    sheet.text((left + label_left) / 2, top - rh, 'Итого, м³', th, WHITE, 'mm', angle=90)
    sheet.text((label_left + u0) / 2, top - rh / 2, 'Насыпь (+)', th, RED, 'mm')
    sheet.text((label_left + u0) / 2, top - 1.5 * rh, 'Выемка (–)', th, BLUE, 'mm')
    for c in data['columns']:
        x = (c['i'] + 0.5) * step
        sheet.text(x, top - rh / 2, volume(c['fill'], True), th, RED, 'mm')
        sheet.text(x, top - 1.5 * rh, volume(c['cut'], False), th, BLUE, 'mm')
    centre = (u1 + total_left) / 2
    sheet.text(centre - 0.45, top - rh / 2, 'Всего,', 0.65, WHITE, 'mm', angle=90)
    sheet.text(centre + 0.45, top - rh / 2, 'м³', 0.65, WHITE, 'mm', angle=90)
    sheet.text((total_left + right) / 2, top - rh / 2, volume(data['totalFill'], True), th, RED, 'mm')
    sheet.text((total_left + right) / 2, top - 1.5 * rh, volume(data['totalCut'], False), th, BLUE, 'mm')
    for n, line in enumerate(data['areas']):
        sheet.text(left, areas_top - th - n * 1.6 * th, line, th, WHITE, 'ls')
    if title:
        sheet.draw.text((12, 10), title, fill=GREY, font=font(15))
    sheet.image.save(out)


def leader(out):
    """Одна ручка у отметок узла и выноска с полкой (замечания 25, 32)."""
    ppm = 34
    sheet = Sheet(-6, -6, 33, 6, ppm, margin=20)
    h = 0.9
    for x0, label in ((0, 'Ручка в узле'), (19, 'Потянули за ручку — выноска с полкой')):
        sheet.line((x0 - 5, 0), (x0 + 9, 0), GREEN)
        sheet.line((x0, -5), (x0, 5), GREEN)
        sheet.draw.text((sheet.p(x0 - 5, 5)[0], sheet.p(0, 5.6)[1]), label, fill=GREY, font=font(15))
    corners(sheet, (0, 0), -0.01, 176.15, 176.16, h)
    gx, gy = sheet.p(0, 0)
    sheet.draw.rectangle([gx - 6, gy - 6, gx + 6, gy + 6], outline=(40, 140, 255), width=3)
    node, corner = (19, 0), (24, -3.5)
    sheet.line(node, corner, WHITE, 1)
    sheet.line(corner, (corner[0] + 5.6, corner[1]), WHITE, 1)
    corners(sheet, corner, -0.01, 176.15, 176.16, h)
    cx, cy = sheet.p(*corner)
    sheet.draw.rectangle([cx - 6, cy - 6, cx + 6, cy + 6], outline=(40, 140, 255), width=3)
    sheet.image.save(out)


def statement(data, out):
    """Ведомость объёмов земляных масс по рис. 9 СТБ 2073-2010 (строка «Грунт планировки территории» — из картограммы)."""
    s = 4                                   # px per mm
    widths = [85, 20, 20, 20, 20, 20]
    head = [5, 10, 10]
    names = ['Грунт планировки территории', 'Вытесненный грунт, всего', 'в том числе при устройстве:',
             '     а) подземных частей зданий (сооружений)', '     б) автодорожных покрытий', '…', 'Итого перерабатываемого грунта']
    rows = [8] * len(names)
    W = sum(widths) * s + 40
    H = (12 + sum(head) + sum(rows)) * s + 40
    image = Image.new('RGB', (W, H), (255, 255, 255))
    d = ImageDraw.Draw(image)
    x0, y0 = 20, 20 + 12 * s
    f, fb = font(11), font(13, True)
    d.text((x0 + sum(widths) * s / 2, 20 + 6 * s), 'Ведомость объемов земляных масс', fill=(0, 0, 0), font=fb, anchor='mm')
    xs = [x0]
    for w in widths:
        xs.append(xs[-1] + w * s)
    ys = [y0]
    for r in head + rows:
        ys.append(ys[-1] + r * s)
    black = (0, 0, 0)
    d.rectangle([xs[0], ys[0], xs[-1], ys[-1]], outline=black, width=1)
    d.line([xs[1], ys[0], xs[1], ys[-1]], fill=black)
    d.line([xs[5], ys[0], xs[5], ys[-1]], fill=black)
    d.line([xs[1], ys[1], xs[5], ys[1]], fill=black)
    d.line([xs[1], ys[2], xs[5], ys[2]], fill=black)
    d.line([xs[3], ys[1], xs[3], ys[-1]], fill=black)
    for i in (2, 4):
        d.line([xs[i], ys[2], xs[i], ys[-1]], fill=black)
    for y in ys[3:-1]:
        d.line([xs[0], y, xs[-1], y], fill=black)
    d.text(((xs[0] + xs[1]) / 2, (ys[0] + ys[3]) / 2), 'Наименование грунта', fill=black, font=f, anchor='mm')
    d.text(((xs[1] + xs[5]) / 2, (ys[0] + ys[1]) / 2), 'Количество, м³', fill=black, font=f, anchor='mm')
    d.text(((xs[1] + xs[3]) / 2, (ys[1] + ys[2]) / 2), 'Картограмма 1', fill=black, font=f, anchor='mm')
    for i in (1, 3):
        d.multiline_text(((xs[i] + xs[i + 1]) / 2, (ys[2] + ys[3]) / 2), 'Насыпь\n(+)', fill=black, font=f, anchor='mm', align='center')
        d.multiline_text(((xs[i + 1] + xs[i + 2]) / 2, (ys[2] + ys[3]) / 2), 'Выемка\n(–)', fill=black, font=f, anchor='mm', align='center')
    d.multiline_text(((xs[5] + xs[6]) / 2, (ys[0] + ys[3]) / 2), 'Приме-\nчание', fill=black, font=f, anchor='mm', align='center')
    for n, name in enumerate(names):
        y = (ys[3 + n] + ys[4 + n]) / 2
        d.text((xs[0] + 6, y), name, fill=black, font=f, anchor='lm')
    fill = str(int(math.floor(data['totalFill'] + 0.5)))
    cut = str(int(math.floor(data['totalCut'] + 0.5)))
    y = (ys[3] + ys[4]) / 2
    d.text(((xs[1] + xs[2]) / 2, y), fill, fill=black, font=f, anchor='mm')
    d.text(((xs[2] + xs[3]) / 2, y), cut, fill=black, font=f, anchor='mm')
    for i, w in enumerate(widths):
        d.text(((xs[i] + xs[i + 1]) / 2, ys[-1] + 10), str(w), fill=(120, 120, 120), font=font(10), anchor='mt')
    image.save(out)


def box(d, xy, text, fill=(245, 247, 250), outline=(120, 130, 140), bold=False, size=13, color=(20, 20, 20)):
    d.rounded_rectangle(xy, radius=6, fill=fill, outline=outline, width=2)
    d.multiline_text(((xy[0] + xy[2]) / 2, (xy[1] + xy[3]) / 2), text, fill=color, font=font(size, bold), anchor='mm', align='center', spacing=4)


def arrow(d, a, b, color=(90, 100, 110), width=3):
    d.line([a, b], fill=color, width=width)
    angle = math.atan2(b[1] - a[1], b[0] - a[0])
    for side in (-1, 1):
        d.line([b, (b[0] - 12 * math.cos(angle + side * 0.45), b[1] - 12 * math.sin(angle + side * 0.45))], fill=color, width=width)


def workflow(out):
    steps = ['1. Проект', '2. Фактическая\nповерхность\n(импорт съёмки)', '3. Проектная\nповерхность', '4. Картограмма:\nграница работ',
             '5. «1. Сетка\nи отметки»', '6. «2. Рассчитать\nобъёмы»', '7. Ведомость,\nExcel']
    W, H = 1180, 170
    image = Image.new('RGB', (W, H), (255, 255, 255))
    d = ImageDraw.Draw(image)
    bw, gap = 138, 28
    for n, text in enumerate(steps):
        x = 20 + n * (bw + gap)
        box(d, (x, 40, x + bw, 130), text, fill=(232, 241, 252) if n in (4, 5) else (245, 247, 250), bold=n in (4, 5), size=13)
        if n + 1 < len(steps):
            arrow(d, (x + bw + 3, 85), (x + bw + gap - 3, 85))
    d.text((20, 12), 'Порядок работы', fill=(60, 60, 60), font=font(15, True))
    image.save(out)


def panel(data, out):
    """Схема боковой панели «КЗМ» с номерами частей."""
    fill, cut = volume(data['totalFill'], True), volume(data['totalCut'], False).replace('-', '−')
    W, H = 1010, 640
    image = Image.new('RGB', (W, H), (255, 255, 255))
    d = ImageDraw.Draw(image)
    x0, y0, x1 = 20, 20, 460
    d.rectangle([x0, y0, x1, H - 20], outline=(110, 110, 110), width=2, fill=(240, 240, 240))
    d.rectangle([x0, y0, x1, y0 + 28], fill=(60, 64, 72))
    d.text((x0 + 10, y0 + 14), 'КЗМ — картограмма земляных масс', fill=(255, 255, 255), font=font(13), anchor='lm')
    d.rectangle([x0 + 6, y0 + 36, x1 - 6, y0 + 62], fill=(250, 250, 250), outline=(190, 190, 190))
    d.text((x0 + 12, y0 + 49), 'Проект: Площадка ▾', fill=(20, 20, 20), font=font(13, True), anchor='lm')
    d.rectangle([x0 + 6, y0 + 68, x1 - 6, y0 + 94], fill=(250, 250, 250), outline=(190, 190, 190))
    d.text((x0 + 12, y0 + 81), '+ Поверхность  + Картограмма  Импорт  Пересчитать  Настройки  ?', fill=(20, 20, 20), font=font(12), anchor='lm')
    d.rectangle([x0 + 6, y0 + 100, x1 - 6, y0 + 280], fill=(255, 255, 255), outline=(190, 190, 190))
    tree = [(0, '▣ Фактические (1)'), (1, '◭ Факт — 427 т.'), (2, '• Точки (427)'), (0, '▣ Проектные (1)'), (1, '◭ Проект — 20 т., линий 1 (текущая)'),
            (2, '• Точки (20)'), (0, '▣ Картограммы (1)'), (1, '▦ Картограмма 1  ' + fill + ' / ' + cut + ' м³')]
    for n, (level, text) in enumerate(tree):
        d.text((x0 + 16 + level * 18, y0 + 114 + n * 20), text, fill=(20, 20, 20), font=font(12))
    d.rectangle([x0 + 6, y0 + 288, x1 - 6, H - 50], fill=(255, 255, 255), outline=(190, 190, 190))
    page = ['Картограмма', 'Имя: Картограмма 1', 'Поверхности для расчёта: Факт / Проект', 'Граница работ: 1. полилиния 14891C',
            '[Добавить границу ▾] [Показать] [Убрать]', 'Сетка: шаг 10 м, базовая точка авто', 'Оформление: 1:1000, текст 0,9 мм…',
            '[1. Сетка и отметки]  [2. Рассчитать объёмы]', '[Проверить] [Ведомость объёмов…] [В Excel…]']
    for n, text in enumerate(page):
        d.text((x0 + 16, y0 + 302 + n * 26), text, fill=(20, 20, 20), font=font(12, n == 0 or n == 7))
    d.rectangle([x0 + 6, H - 44, x1 - 6, H - 26], fill=(250, 250, 250), outline=(190, 190, 190))
    d.text((x0 + 12, H - 35), '«Картограмма 1»: насыпь ' + fill + ' м³, выемка ' + cut + ' м³…', fill=(60, 60, 60), font=font(11), anchor='lm')
    # (where it points, where the number stands, number, text)
    callouts = [(y0 + 49, y0 + 30, '1', 'Проект: открыть другой, создать,\nперенести из другого чертежа'),
                (y0 + 81, y0 + 100, '2', 'Кнопки: поверхность, картограмма,\nимпорт, пересчёт устаревших,\nнастройки, справка «?»'),
                (y0 + 190, y0 + 200, '3', 'Дерево проекта. Правый щелчок —\nвсе действия, двойной — показать\nна чертеже'),
                (y0 + 420, y0 + 420, '4', 'Свойства выбранного: здесь всё\nнастраивается и запускается'),
                (H - 35, H - 35, '5', 'Итог последнего действия;\nподробности — в командной строке')]
    for target, y, n, text in callouts:
        d.line([x1 - 6, target, x1 + 20, y], fill=(30, 110, 220), width=2)
        d.ellipse([x1 + 20, y - 13, x1 + 46, y + 13], fill=(30, 110, 220))
        d.text((x1 + 33, y), n, fill=(255, 255, 255), font=font(14, True), anchor='mm')
        d.multiline_text((x1 + 56, y), text, fill=(30, 30, 30), font=font(13), anchor='lm', spacing=3)
    image.save(out)


def projects(out):
    """Перенос проекта из другого чертежа (замечание 35)."""
    W, H = 1000, 330
    image = Image.new('RGB', (W, H), (255, 255, 255))
    d = ImageDraw.Draw(image)
    box(d, (20, 50, 330, 300), 'Площадка.dwg\n\nПроект «Площадка»\n• Факт — 427 т.\n• Проект — 20 т.\n• Картограмма 1\n  (с оформлением)',
        fill=(245, 247, 250))
    d.text((20, 18), 'Чертёж, где проект делали раньше', fill=(60, 60, 60), font=font(14, True))
    box(d, (640, 50, 980, 300), 'Новый чертёж\n\nПроекты в других чертежах:\n▸ Площадка — Площадка.dwg\n   (поверхностей 2, картограмм 1)\n\n'
                               '[Перенести в этот чертёж]', fill=(232, 241, 252))
    d.text((640, 18), 'Страница «Проекты чертежа»', fill=(60, 60, 60), font=font(14, True))
    arrow(d, (340, 175), (630, 175), width=4)
    d.multiline_text((485, 150), 'копия точек, границ, линий\nи картограмм; исходный\nфайл не меняется', fill=(60, 60, 60), font=font(12), anchor='md',
                     align='center')
    image.save(out)


def boundary_points(out):
    """Граница по точкам: щелчки по порядку, Enter — замкнуть (отметки — с выбранной поверхности)."""
    W, H = 900, 300
    image = Image.new('RGB', (W, H), BACK)
    d = ImageDraw.Draw(image)
    pts = [(80, 230), (330, 240), (420, 90), (200, 60)]
    for n, (x, y) in enumerate(pts):
        d.ellipse([x - 5, y - 5, x + 5, y + 5], outline=WHITE, width=2)
        d.text((x + 10, y - 22), str(n + 1) + '  176,4' + str(n), fill=WHITE, font=font(14))
    for a, b in zip(pts, pts[1:]):
        d.line([a, b], fill=WHITE, width=3)
    for i in range(18):
        t0, t1 = i / 18, (i + 0.5) / 18
        a = (pts[-1][0] + (pts[0][0] - pts[-1][0]) * t0, pts[-1][1] + (pts[0][1] - pts[-1][1]) * t0)
        b = (pts[-1][0] + (pts[0][0] - pts[-1][0]) * t1, pts[-1][1] + (pts[0][1] - pts[-1][1]) * t1)
        d.line([a, b], fill=GREY, width=2)
    d.multiline_text((580, 50), '1, 2, 3… — точки по порядку:\nсуществующая точка (её отметка)\nили любое место (отметка\nс поверхности; Enter — принять,\nможно ввести свою)\n\n'
                               'Enter («Замкнуть») — граница\nготова; «Отменить» — убрать\nпоследнюю точку', fill=WHITE, font=font(13), spacing=4)
    image.save(out)


def main():
    data = json.loads(Path(sys.argv[1]).read_text(encoding='utf-8'))
    out = Path(sys.argv[2])
    out.mkdir(parents=True, exist_ok=True)
    cartogram(data['main'], out / 'cartogram.png')
    cartogram(data['parts'], out / 'parts.png', ppm=13)
    leader(out / 'leader.png')
    statement(data['main'], out / 'statement.png')
    workflow(out / 'workflow.png')
    panel(data['main'], out / 'panel.png')
    projects(out / 'projects.png')
    boundary_points(out / 'boundary.png')


if __name__ == '__main__':
    main()
