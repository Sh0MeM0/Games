"""Сборка книги Excel «Таксация Лайт» (только формулы, без макросов и защиты).

    python3 tools/build_excel.py [--rows 1000] [--out excel/Таксация-Лайт.xlsx]

Логика формул повторяет core/engine.py в режиме Таксации Лайт (режим compat=True ядра — формулы «ТАКСАЦИЯ-АВТО 4.2»).
Книга проверяется тестом tests/test_excel.py: пересчёт в LibreOffice и сравнение с ядром.
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
import re
import shutil
import tempfile
import zipfile

from openpyxl import Workbook
from openpyxl.comments import Comment
from openpyxl.formatting.rule import FormulaRule
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
from openpyxl.utils import get_column_letter
from openpyxl.workbook.defined_name import DefinedName
from openpyxl.worksheet.datavalidation import DataValidation
from openpyxl.worksheet.formula import ArrayFormula

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REF = json.load(open(os.path.join(ROOT, 'core', 'reference.json'), encoding='utf-8'))
FIRST = 9            # первая строка данных ведомости (выше — заголовок и панель подсказки породы)
HROW = FIRST - 1     # строка заголовков ведомости
LFIRST = 7           # первая строка перечня на листах-ведомостях (Существующие, Удаляемые …)
SH_VOL = 'Объемы'                           # отходы и объёмы древесины (раньше «Древесина»)
SH_G = 'Цветники и газоны'                  # ввод: цветники, газоны, иной травяной покров
SH_GL = 'Ведомость цветников и газонов'     # готовая ведомость цветников и газонов (для AutoCAD)
SH_ACCESS = 'Доступ'                        # вариант с защитой: лист, который виден, пока расчёт закрыт
SH_STORE = 'TK'                             # вариант с защитой: служебный лист (число владельца, код), всегда скрыт
G_HROW = 3           # строка заголовков листа «Цветники и газоны»
G_FIRST = G_HROW + 1
G_ROWS = 300         # строк ввода цветников и газонов
AUTHOR = 'Таксация Лайт'
NSTEM = 27           # стволов в одной строке (как в оригинале)
GREEN = 'FF2C6A4C'
F_IN = PatternFill('solid', fgColor='FFFFFFFF')
F_CALC = PatternFill('solid', fgColor='FFEEF3EE')
F_HEAD = PatternFill('solid', fgColor='FFFFE699')     # шапки всех таблиц — светло-жёлтые
F_SET = PatternFill('solid', fgColor='FFFFF7D6')
B_THIN = Border(*(Side(style='thin', color='FFC9D3CA'),) * 4)
FONT = 'Arial'


ACAD_NAMES = []     # (имя, лист, описание, вид: 'list' — меняет длину, 'fixed' — постоянный размер)


def defname(wb, name, ref):
    dn = DefinedName(name, attr_text=ref)
    try:
        wb.defined_names[name] = dn          # openpyxl >= 3.1
    except TypeError:
        wb.defined_names.append(dn)


def note(cell, text, width=270):
    """Примечание к ячейке (всплывает при наведении мыши)."""
    lines = sum(1 + len(p) // 44 for p in text.split('\n'))
    cell.comment = Comment(text, AUTHOR, height=22 + 13 * lines, width=width)


def style_header(c):
    c.font = Font(name=FONT, bold=True, size=9)
    c.fill = F_HEAD
    c.alignment = Alignment(wrap_text=True, vertical='center', horizontal='center')
    c.border = B_THIN


# =============================================================== Справочник
def build_ref(wb):
    ws = wb.create_sheet('Справочник')
    ws['A1'] = 'Справочники Таксации Лайт (перенесены из файла ТАКСАЦИЯ-АВТО 4.2). Можно дополнять: свои породы — в пустые строки библиотеки, базовые величины — в календарь.'
    ws['A1'].font = Font(name=FONT, bold=True)
    # --- библиотека пород: служебные строки + библиотека + 300 пустых строк для своих пород
    hdr = ['№', 'Вид (порода)', 'Тип породы', 'Куст (1)', 'Твёрдость', 'Инвазивный (1)', 'Ø кома при h<1м', 'h кома при h<1м', 'В библиотеке (1)', 'Служебная (1)', '2-е название, латынь']
    for i, h in enumerate(hdr):
        style_header(ws.cell(row=4, column=1 + i, value=h))
    r = 5
    lib_names = {s['name'].lower() for s in REF['species']}
    for s in REF['special']:
        vals = [r - 4, s['name'], s['type'], 1 if s['shrub'] else 0, 'мяг.', 0, s.get('ballD'), s.get('ballH'),
                1 if s['name'].lower() in lib_names else 0, 1, '']
        for i, v in enumerate(vals):
            ws.cell(row=r, column=1 + i, value=v)
        r += 1
    for s in REF['species']:
        vals = [r - 4, s['name'], s['type'], 1 if s['shrub'] else 0, s['hard'] or None, 1 if s['invasive'] else 0, s.get('ballD'), s.get('ballH'), 1, 0, s.get('alt') or None]
        for i, v in enumerate(vals):
            ws.cell(row=r, column=1 + i, value=v)
        r += 1
    lib_last = r + 300 - 1
    for rr in range(r, lib_last + 1):
        ws.cell(row=rr, column=1, value=rr - 4)
        for c in range(2, 12):
            ws.cell(row=rr, column=c).fill = F_SET
    ws.freeze_panes = 'C5'
    ws.auto_filter.ref = 'A4:K%d' % lib_last          # фильтр с поиском: удобно найти номер породы
    ws.column_dimensions['A'].width = 6
    note(ws.cell(row=r, column=2), 'Свои породы вписывайте сюда, подряд, без пустых строк: название, тип (хвой., медл., быст., плод., малоц. — деревья; '
         'лиан., хвой.., вечз., крас., медл.., быст.., плод.. — кусты), куст = 1, твёрдость (мяг./хвн./твд.), в библиотеке = 1. '
         'После этого порода появится в списке на листе «Ведомость».', 300)
    for name, col in [('Lib_Name', 'B'), ('Lib_Type', 'C'), ('Lib_Shrub', 'D'), ('Lib_Hard', 'E'), ('Lib_Inv', 'F'),
                      ('Lib_BallD', 'G'), ('Lib_BallH', 'H'), ('Lib_InLib', 'I'), ('Lib_Special', 'J')]:
        defname(wb, name, "'Справочник'!$%s$5:$%s$%d" % (col, col, lib_last))
    # список для выбора породы: только заполненные строки (иначе Excel открывает список на первой пустой строке — «с середины»)
    defname(wb, 'SPL', "'Справочник'!$B$5:INDEX('Справочник'!$B$5:$B$%d,MAX(1,COUNTA('Справочник'!$B$5:$B$%d)))" % (lib_last, lib_last))
    # список для поиска породы по началу любого слова (колонки AB:AC, скрыты): ключ — название с этого слова до конца,
    # значение — полное название; отсортирован по ключу, поэтому подходящие ключи идут подряд
    style_header(ws.cell(row=4, column=28, value='ключ поиска'))
    style_header(ws.cell(row=4, column=29, value='порода'))
    style_header(ws.cell(row=4, column=30, value='№'))
    numbers = {s0['name']: i + 1 for i, s0 in enumerate(REF['special'] + REF['species'])}
    keys = []
    for s0 in REF['special'] + REF['species']:
        nm = s0['name']
        words = nm.split(' ')
        seen = set()
        for i in range(len(words)):
            k = ' '.join(words[i:]).lstrip('*')
            if k and k.lower() not in seen:
                seen.add(k.lower())
                keys.append((k, nm))
    keys.sort(key=lambda kv: (kv[0].casefold(), kv[1].casefold()))
    for j, (k, nm) in enumerate(keys):
        ws.cell(row=5 + j, column=28, value=k)
        ws.cell(row=5 + j, column=29, value=nm)
        ws.cell(row=5 + j, column=30, value=numbers[nm])
    kl = 4 + len(keys)
    defname(wb, 'SPK', "'Справочник'!$AB$5:$AB$%d" % kl)
    defname(wb, 'SPV1', "'Справочник'!$AC$5")
    defname(wb, 'SPV', "'Справочник'!$AC$5:$AC$%d" % kl)
    defname(wb, 'SPN', "'Справочник'!$AD$5:$AD$%d" % kl)
    ws.column_dimensions['AB'].hidden = True
    ws.column_dimensions['AC'].hidden = True
    ws.column_dimensions['AD'].hidden = True
    for i, t in enumerate(['Номер породы. Его можно ввести в колонку «Порода» листа «Ведомость» вместо названия.',
                           'Название породы (вида). Отсюда берётся список выбора на листе «Ведомость». Фильтр в заголовке помогает найти породу и её номер.',
                           'Тип породы для тарифа и норм посадок: хвой., медл., быст., плод., малоц. — деревья; лиан., хвой.., вечз., крас., медл.., быст.., плод.. — кустарники; ж.и.1/ж.и.2 — живая изгородь.',
                           '1 — кустарник, 0 — дерево.', 'Твёрдость древесины для объёмов: мяг. — мягкая, хвн. — хвойная, твд. — твёрдая.',
                           '1 — инвазивный вид: компенсация не начисляется.', 'Диаметр кома, м, для пересадки растения высотой до 1 м.',
                           'Высота кома, м, для пересадки растения высотой до 1 м.', '1 — вид из библиотеки оригинала (для него считается размер кома).',
                           '1 — служебная строка (поросль, живая изгородь).', 'Второе название или латинское.']):
        note(ws.cell(row=4, column=1 + i), t)
    ws.column_dimensions['B'].width = 34
    ws.column_dimensions['K'].width = 30
    # --- справочные таблицы справа (колонки L и далее)
    c0 = 12   # L

    def put_table(row, col, title, headers, rows, names=None, zero_blank=False):
        ws.cell(row=row, column=col, value=title).font = Font(name=FONT, bold=True)
        for i, h in enumerate(headers):
            style_header(ws.cell(row=row + 1, column=col + i, value=h))
        for j, rr in enumerate(rows):
            for i, v in enumerate(rr):
                ws.cell(row=row + 2 + j, column=col + i, value=(0 if (v is None and zero_blank) else v))
        if names:
            for i, nm in enumerate(names):
                if nm:
                    L = get_column_letter(col + i)
                    defname(wb, nm, "'Справочник'!$%s$%d:$%s$%d" % (L, row + 2, L, row + 1 + len(rows)))
        return row + 3 + len(rows)

    row = 3
    # типы пород и коэффициенты кома
    types = [t['type'] for t in REF['types']]
    row = put_table(row, c0, 'Типы пород и коэффициенты размера кома (Ø/h при высоте 1–1,5; 1,5–2; 2–4; 4–5; 5–6 м)',
                    ['Тип'] + ['к%d' % i for i in range(1, 11)], [[t['type']] + t['k'] for t in REF['types']])
    defname(wb, 'TypeK_Type', "'Справочник'!$L$5:$L$%d" % (4 + len(types)))
    defname(wb, 'TypeK', "'Справочник'!$M$5:$V$%d" % (4 + len(types)))
    shrub_types = ['лиан.', 'хвой..', 'вечз.', 'крас.', 'медл..', 'быст..', 'плод..', 'ж.и.1', 'ж.и.2']
    row = put_table(row, c0, 'Типы кустарников', ['Тип'], [[t] for t in shrub_types], ['ShrubTypes'])
    # тарифы деревьев: пороги и значения (4 колонки: хвой, медл, быст/плод, малоц), дополнены «заглушкой» 1E9
    tt = REF['tariffTrees']
    cols = [tt['хвой.'], tt['медл.'], tt['быст.'], tt['малоц.']]
    n = max(len(c) for c in cols)
    thr = [[(c[i][0] if i < len(c) else 1e9) for c in cols] for i in range(n)]
    val = [[(c[i][1] if i < len(c) else c[-1][1]) for c in cols] for i in range(n)]
    start = row
    row = put_table(row, c0, 'Тарифы деревьев: диаметр «от», см (УДд!AC18:AC47)', ['хвой.', 'медл.', 'быст./плод.', 'малоц.'], thr)
    defname(wb, 'TarThr', "'Справочник'!$L$%d:$O$%d" % (start + 2, start + 1 + n))
    start = row
    row = put_table(row, c0, 'Тарифы деревьев: базовых величин (УДд!AF18:AF47)', ['хвой.', 'медл.', 'быст./плод.', 'малоц.'], val)
    defname(wb, 'TarVal', "'Справочник'!$L$%d:$O$%d" % (start + 2, start + 1 + n))
    pl = REF['plantedSmall']
    row = put_table(row, c0, 'Высаженные для озеленения (до 1 см; малоценные до 4 см), баз.вел.', ['Тип', 'баз.вел.'],
                    [[k, v] for k, v in pl.items()], ['Planted_Type', 'Planted_Val'])
    P = REF['plots']
    start = row
    row = put_table(row, c0, 'Поросль и живая изгородь, баз.вел.', ['Показатель', 'баз.вел.'],
                    [['Поросль деревьев, за 1 м²', P['treePlotPerM2']], ['Поросль деревьев менее 10 м²', P['treePlotSmall']],
                     ['Живая изгородь однорядная, 1 п.м.', P['hedge1PerM']], ['Живая изгородь двухрядная, 1 п.м.', P['hedge2PerM']],
                     ['Поросль кустарников, за 1 м²', P['shrubPlotPerM2']], ['Поросль кустарников менее 10 м²', P['shrubPlotSmall']]])
    for i, nm in enumerate(['P_TP', 'P_TPS', 'P_H1', 'P_H2', 'P_SP', 'P_SPS']):
        defname(wb, nm, "'Справочник'!$M$%d" % (start + 2 + i))
    sh = REF['tariffShrubs']
    keys = ['лиан.', 'хвой/вечз', 'крас.', 'медл..', 'быст/плод']
    start = row
    row = put_table(row, c0, 'Кустарники по высоте: высота «от», м', keys, [[sh[k][0][0] for k in keys], [sh[k][1][0] for k in keys]])
    defname(wb, 'ShrThr', "'Справочник'!$L$%d:$P$%d" % (start + 2, start + 3))
    start = row
    row = put_table(row, c0, 'Кустарники по высоте: баз.вел.', keys, [[sh[k][0][1] for k in keys], [sh[k][1][1] for k in keys]])
    defname(wb, 'ShrVal', "'Справочник'!$L$%d:$P$%d" % (start + 2, start + 3))
    ptr = REF['plantTrees']
    js = sorted(ptr, key=int)
    start = row
    row = put_table(row, c0, 'Компенсационные посадки деревьев, шт. взамен (УДд!AA3:AD9)', ['код', 'хвой.', 'медл.', 'быст.', 'описание'],
                    [[int(j), ptr[j]['хвой.'], ptr[j]['медл.'], ptr[j]['быст.'], ptr[j]['label']] for j in js],
                    ['PT_J', 'PT_X', 'PT_M', 'PT_B', None])
    defname(wb, 'PT_TBL', "'Справочник'!$M$%d:$O$%d" % (start + 2, start + 1 + len(ptr)))
    psr = REF['plantShrubs']
    js = sorted(psr, key=int)
    pkeys = ['лиан.', 'хвой/вечз', 'крас.', 'медл..', 'быст/плод', 'ж.и.1', 'ж.и.2']
    start = row
    defname(wb, 'PS_TBL', "'Справочник'!$M$%d:$S$%d" % (start + 2, start + 1 + len(psr)))
    row = put_table(row, c0, 'Компенсационные посадки кустарников, шт. (п.м.) взамен (УДд!AF3:AM11)', ['код'] + pkeys + ['описание'],
                    [[int(j)] + [psr[j][k] for k in pkeys] + [psr[j]['label']] for j in js],
                    ['PS_J', 'PS_1', 'PS_2', 'PS_3', 'PS_4', 'PS_5', 'PS_6', 'PS_7', None])
    V = REF['volumes']
    start = row
    # первая строка таблиц объёмов — служебные нули: стволы тоньше 8 см и пустые дают 0 без формул массива
    row = put_table(row, c0, 'Объёмы древесины, мягкие породы (Мягк, плотность %s; первая строка — служебные нули)' % V['density']['soft'],
                    ['диаметр', 'h1', 'h1б', 'всего1', 'ствол1', 'h2', 'h2б', 'всего2', 'ствол2'], [[0] * 9] + V['soft'],
                    ['VolDiam', 'V_S2', None, 'V_S4', 'V_S5', None, None, 'V_S8', 'V_S9'], zero_blank=True)
    start = row
    row = put_table(row, c0, 'Объёмы древесины, хвойные (Хвой, плотность %s; строки — по диаметрам таблицы мягких пород)' % V['density']['conif'],
                    ['h1', 'h1б', 'всего1', 'ствол1', 'h2', 'h2б', 'всего2', 'ствол2', 'h3', 'h3б', 'всего3', 'ствол3'], [[0] * 12] + V['conif'],
                    ['V_C1', None, 'V_C3', 'V_C4', 'V_C5', None, 'V_C7', 'V_C8', None, None, 'V_C11', 'V_C12'], zero_blank=True)
    start = row
    row = put_table(row, c0, 'Объёмы древесины, твёрдые (Твер, плотность %s)' % V['density']['hard'],
                    ['h1', 'h1б', 'всего1', 'ствол1', 'h2', 'h2б', 'всего2', 'ствол2'], [[0] * 8] + V['hard'],
                    ['V_H1', None, 'V_H3', 'V_H4', 'V_H5', None, 'V_H7', 'V_H8'], zero_blank=True)
    start = row
    row = put_table(row, c0, 'Плотность древесины, кг/м³', ['мяг.', 'хвн.', 'твд.'], [[V['density']['soft'], V['density']['conif'], V['density']['hard']]])
    for i, nm in enumerate(['DEN_S', 'DEN_C', 'DEN_H']):
        defname(wb, nm, "'Справочник'!$%s$%d" % (get_column_letter(c0 + i), start + 2))
    LZ = ['Д.хвой/вечз. ', 'Д.лис.м.р. ', 'Д.лис.б.р. ', 'К.древ.лиан. ', 'К.хвой/вечз. ', 'К.лис.крас.цв. ', 'К.лис.м.р. ', 'К.л.б.р./плод. ', 'жив.изг.1р. ', 'жив.изг.2р. ']
    LG = ['Д.хв./вчз. ', 'Д.мр. ', 'Д.бр. ', 'К.дл. ', 'К.хв./вчз. ', 'К.кц. ', 'К.мр. ', 'К.бр./пл. ', 'ж.изг.1р. ', 'ж.изг.2р. ']
    row = put_table(row, c0, 'Подписи типов посадок', ['для итогов', 'для ведомости'], [[a, b] for a, b in zip(LZ, LG)], ['LBL_Z', 'LBL_G'])
    SW = REF['shrubWaste']
    row = put_table(row, c0, 'Кустарник: сводка 1 га (файл пользователя, лист «кусты», A4:G6) — древесина и корневые остатки',
                    ['густота', 'древесина, пл. м³', 'древесина, скл. м³', 'древесина, т', 'корни, пл. м³', 'корни, скл. м³', 'корни, т'],
                    [[k, v['m3'], v['m3s'], v['t'], v['rm3'], v['rm3s'], v['rt']] for k, v in SW.items()],
                    ['SHW_Kind', None, 'SHW_M3', 'SHW_T', None, 'SHW_RM3', 'SHW_RT'])
    gt = row
    row = put_table(row, c0, 'Цветники, газоны, иной травяной покров: стоимость, баз.вел. за 1 м² (постановление Совмина № 1426, прил. 8 к Положению)',
                    ['№', 'Вид (как в списке)', 'Категория', 'баз.вел./м²', 'коэф. состояния (1 — есть)'],
                    [[i + 1, g['name'], g['cat'], g['value'], g['cond']] for i, g in enumerate(REF['greens'])],
                    [None, 'GR_Name', 'GR_Cat', 'GR_Val', 'GR_Q'])
    note(ws.cell(row=gt, column=c0), REF['sources']['greens'], 420)
    pt0 = row
    PTY = REF['plantTypes']
    row = put_table(row, c0, 'Типы компенсационных посадок (графы приложений 4 и 5 к Положению, постановление Совмина № 1426)',
                    ['№', 'Тип посадок (как в списке)', 'код', 'группа', 'графа норм'],
                    [[i + 1, p['name'], p['code'], p['group'], p['law']] for i, p in enumerate(PTY)],
                    [None, 'PTY_Name', 'PTY_Code', 'PTY_Grp', None])
    note(ws.cell(row=pt0, column=c0), REF['sources']['plantTypes'], 420)
    ntr = sum(1 for p in PTY if p['group'] == 'дерево')
    assert all(p['group'] == 'дерево' for p in PTY[:ntr])
    L1 = get_column_letter(c0 + 1)
    defname(wb, 'PTL_T', "'Справочник'!$%s$%d:$%s$%d" % (L1, pt0 + 2, L1, pt0 + 1 + ntr))
    defname(wb, 'PTL_S', "'Справочник'!$%s$%d:$%s$%d" % (L1, pt0 + 2 + ntr, L1, pt0 + 1 + len(PTY)))
    # календарь базовых величин (колонки X:Z)
    ws.cell(row=3, column=24, value='Календарь базовых величин — добавляйте новые значения в пустые строки').font = Font(name=FONT, bold=True)
    for i, h in enumerate(['Действует с', 'Рублей', 'Постановление']):
        style_header(ws.cell(row=4, column=24 + i, value=h))
    rr = 5
    for b in REF['baseValues']:
        c = ws.cell(row=rr, column=24, value=datetime.date.fromisoformat(b['from']))
        c.number_format = 'DD.MM.YYYY'
        ws.cell(row=rr, column=25, value=b['rub'])
        ws.cell(row=rr, column=26, value=b['act'] or None)
        rr += 1
    bv_last = rr + 40
    for r2 in range(rr, bv_last + 1):
        for c in (24, 25, 26):
            ws.cell(row=r2, column=c).fill = F_SET
        ws.cell(row=r2, column=24).number_format = 'DD.MM.YYYY'
    defname(wb, 'BV_From', "'Справочник'!$X$5:$X$%d" % bv_last)
    defname(wb, 'BV_Rub', "'Справочник'!$Y$5:$Y$%d" % bv_last)
    defname(wb, 'BV_Act', "'Справочник'!$Z$5:$Z$%d" % bv_last)
    ws.column_dimensions['X'].width = 13
    ws.column_dimensions['Z'].width = 44
    return ws


# =============================================================== Настройки
K_INFO = [('К1', 'зона особой охраны', 'да → К1 = 2, нет → 1'), ('К2', 'бюджет', 'да → К2 = 0,5, нет → 1'),
          ('К3', 'помеха инженерным сетям', 'да → К3 = 0,5, нет → 1'), ('К4', 'вне населённого пункта', 'да → К4 = 0,1, нет → 1')]


PT_CANON = {'вечз.': 'хвой..', 'плод..': 'быст..'}


def pt_name(code):
    """Код типа посадок (хвой., медл.., …) → название из списка (графы приложений 4 и 5)."""
    code = PT_CANON.get(code, code)
    for p in REF['plantTypes']:
        if p['code'] == code:
            return p['name']
    return None


def pt_code(ref_cell, group):
    """Формула: название типа посадок → код, если тип из нужной группы (дерево / куст), иначе пусто."""
    m = 'MATCH(TRIM(%s&""),PTY_Name,0)' % ref_cell
    return 'IFERROR(IF(INDEX(PTY_Grp,%s)="%s",INDEX(PTY_Code,%s),""),"")' % (m, group, m)


def build_settings(wb):
    ws = wb.create_sheet('Настройки', 0)
    ws.column_dimensions['A'].width = 3
    ws.column_dimensions['B'].width = 46
    ws.column_dimensions['C'].width = 26
    ws.column_dimensions['D'].width = 60
    ws['B1'] = 'Таксация Лайт — настройки объекта'
    ws['B1'].font = Font(name=FONT, bold=True, size=14, color=GREEN)
    ws['B2'] = 'Жёлтые ячейки — для ввода. Наведите мышь на подпись — появится пояснение.'
    rows = [
        ('S_ORG', 'Организация', '', None, 'Попадает в шапку ведомостей.'),
        ('S_NO', 'Номер объекта', '', None, 'Попадает в шапку ведомостей.'),
        ('S_NAME', 'Наименование объекта', '', None, 'Попадает в шапку ведомостей.'),
        ('S_STAGE', 'Стадия проекта', '', None, 'Попадает в шапку ведомостей.'),
        ('S_AUTHOR', 'Автор (ФИО)', '', None, 'Для себя: в расчёте не участвует.'), (None, None, None, None, None),
        ('S_DATE', 'Дата расчёта (для базовой величины)', datetime.date.fromisoformat(REF['defaults']['date']), 'дата',
         'По этой дате из календаря (лист «Справочник», колонки X–Z) выбирается базовая величина.'),
        ('S_BVOVR', 'Своя базовая величина, руб. (пусто — по календарю)', None, None,
         'Если базовая величина изменилась, а календарь ещё не дополнен, впишите её сюда. Пусто — по календарю.'),
        ('BASE', 'Базовая величина для расчёта, руб.', '=IF(S_BVOVR<>"",S_BVOVR,IFERROR(INDEX(BV_Rub,MATCH(BASE_FROM,BV_From,0)),0))', 'calc',
         'Считается сама: своя величина, если указана, иначе — из календаря по дате расчёта.'),
        ('BASE_ACT', 'Действует по календарю с / постановление', '=IF(BASE_FROM=0,"",RIGHT("0"&DAY(BASE_FROM),2)&"."&RIGHT("0"&MONTH(BASE_FROM),2)&"."&YEAR(BASE_FROM)&IFERROR(IF(INDEX(BV_Act,MATCH(BASE_FROM,BV_From,0))&""="",""," — "&INDEX(BV_Act,MATCH(BASE_FROM,BV_From,0))),""))', 'calc',
         'Строка календаря, из которой взята базовая величина.'),
        (None, None, None, None, None),
    ]
    for (nm, short, rule), dflt in zip(K_INFO, ['нет' if REF['defaults']['K1'] == 'нет' else 'да', REF['defaults']['K2'], REF['defaults']['K3'], REF['defaults']['K4']]):
        rows.append(('S_K' + nm[1], '%s — «%s?» (%s)' % (nm, short, rule.split(',')[0]), dflt, 'yn',
                     'Ответ для всего объекта: %s. Для отдельных деревьев можно ответить иначе в колонке «%s» листа «Ведомость».' % (rule, nm)))
    rows += [
        ('S_MONEY', 'Компенсация', 'выплатами', 'money', 'выплатами — сумма в базовых величинах; посадками — число саженцев взамен '
         '(за цветники и газоны — восстановление на той же площади). В строках ведомостей способ можно изменить в колонке «Компенсация».'),
        ('S_UNIT', 'Единицы выплат', REF['defaults']['unit'], 'unit', 'баз.в. — в базовых величинах; руб. — в рублях; баз.в. (руб.) — оба значения.'),
        ('S_VIEW', 'Графа «компенсация»', REF['defaults']['formulaView'], 'view', 'формула + итог — расчёт по шагам; итог — только тариф/итог.'),
        ('S_COND', 'Состояние по умолчанию', REF['defaults']['defaultCondition'], 'cond', 'Используется, если в строке ведомости состояние не указано.'),
        ('S_BUSHM2', 'Площадь одного куста (поштучно), м²', 1, None,
         'Для отходов кустарника: куст, подсчитанный поштучно, принимается как участок редкого кустарника такой площади '
         '(нормы «сводка 1 га кустарника», лист «Справочник»). Живая изгородь: 1 п.м. = 3 куста (однорядная) или 5 (двухрядная).'),
        ('S_PLOTDENS', 'Густота поросли (для отходов)', 'редкий', 'dens',
         'По какой норме «сводка 1 га кустарника» считать отходы участков поросли (кустарников и деревьев): редкий, средний или густой.'),
        ('S_TREEPL', 'Деревья компенсировать типом (посадками)', None, 'ptree',
         'Какими саженцами компенсировать удаляемые деревья в режиме «посадками» (варианты — графы приложения 4 к Положению, '
         'постановление Совмина № 1426). Пусто — по таблице «Компенсировать типами» внизу листа (там же пусто — своим типом). '
         'Для отдельной строки тип выбирается в колонке «Тип посадок» листа «Ведомость». Поросль — отдельной настройкой ниже.'),
        ('S_SHRUBPL', 'Кустарники компенсировать типом (посадками)', None, 'pshrub',
         'Какими саженцами компенсировать удаляемые кустарники, в т.ч. живую изгородь (варианты — графы приложения 5). '
         'Пусто — по таблице «Компенсировать типами» внизу листа. Для отдельной строки — колонка «Тип посадок» листа «Ведомость».'),
        ('S_PLOTTREE', 'Поросль деревьев компенсировать типом', pt_name(REF['compMap']['plotTree']), 'ptree',
         'В какую группу деревьев включается поросль деревьев в итогах и в посадках (графы приложения 4).'),
        ('S_PLOTSHRUB', 'Поросль кустарников компенсировать типом', pt_name(REF['compMap']['plotShrub']), 'pshrub',
         'В какую группу кустарников включается поросль кустарников (графы приложения 5).'),
    ]
    dvs = {
        'yn': DataValidation(type='list', formula1='"да,нет"', allow_blank=False),
        'money': DataValidation(type='list', formula1='"выплатами,посадками"'),
        'unit': DataValidation(type='list', formula1='"баз.в.,баз.в. (руб.),руб."'),
        'view': DataValidation(type='list', formula1='"формула + итог,итог"'),
        'cond': DataValidation(type='list', formula1='"хор.,уд.,плох.,ненадл."'),
        'ptree': DataValidation(type='list', formula1='PTL_T', allow_blank=True),
        'pshrub': DataValidation(type='list', formula1='PTL_S', allow_blank=True),
        'dens': DataValidation(type='list', formula1='"редкий,средний,густой"'),
    }
    for dv in dvs.values():
        ws.add_data_validation(dv)
    r = 4
    for name, label, value, kind, tip in rows:
        if name:
            lc = ws.cell(row=r, column=2, value=label)
            lc.font = Font(name=FONT)
            if tip:
                note(lc, tip)
            c = ws.cell(row=r, column=3, value=value)
            c.border = B_THIN
            c.fill = F_CALC if kind == 'calc' else F_SET
            if kind == 'дата':
                c.number_format = 'DD.MM.YYYY'
            if kind in dvs:
                dvs[kind].add(c)
            defname(wb, name, "'Настройки'!$C$%d" % r)
        r += 1
    flags = [('BASE_FROM', '=SUMPRODUCT(MAX((BV_From<=S_DATE)*BV_From))'), ('K1Y', '=S_K1<>"нет"'), ('K2Y', '=S_K2="да"'), ('K3Y', '=S_K3="да"'), ('K4Y', '=S_K4="да"'), ('MONEYY', '=S_MONEY<>"посадками"'),
             ('TRP_C', '=' + pt_code('S_TREEPL', 'дерево')), ('SHP_C', '=' + pt_code('S_SHRUBPL', 'куст')),
             ('PLT_C', '=' + pt_code('S_PLOTTREE', 'дерево')), ('PLS_C', '=' + pt_code('S_PLOTSHRUB', 'куст'))]
    ws.cell(row=r + 1, column=2, value='Служебные флаги (не изменять)').font = Font(name=FONT, italic=True, color='FF888888')
    for i, (nm, f) in enumerate(flags):
        ws.cell(row=r + 2 + i, column=2, value=nm).font = Font(name=FONT, color='FF888888')
        ws.cell(row=r + 2 + i, column=3, value=f).font = Font(name=FONT, color='FF888888')
        defname(wb, nm, "'Настройки'!$C$%d" % (r + 2 + i))
    r = r + 2 + len(flags) + 2
    ws.cell(row=r, column=2, value='Причины вырубки').font = Font(name=FONT, bold=True)
    tips = ['Название причины — выбирается в колонке «Причина вырубки» ведомости. Причину в ведомости можно не указывать.',
            'Группа работ (для себя).',
            'Номера коэффициентов, которые для этой причины меняются на противоположные (например 12), или «без компенcации» — без выплат.']
    for i, h in enumerate(['Причина вырубки', 'Группа работ', 'Номера заменяемых коэффициентов (например 12) или «без компенcации»']):
        c = ws.cell(row=r + 1, column=2 + i, value=h)
        style_header(c)
        note(c, tips[i])
    rs0 = r + 2
    for i in range(16):
        x = REF['reasons'][i] if i < len(REF['reasons']) else None
        for j, k in enumerate(['name', 'group', 'replace']):
            c = ws.cell(row=rs0 + i, column=2 + j, value=(x[k] or None) if x else None)
            c.fill = F_SET
            c.border = B_THIN
    defname(wb, 'RS_Name', "'Настройки'!$B$%d:$B$%d" % (rs0, rs0 + 15))
    defname(wb, 'RS_Group', "'Настройки'!$C$%d:$C$%d" % (rs0, rs0 + 15))
    defname(wb, 'RS_Repl', "'Настройки'!$D$%d:$D$%d" % (rs0, rs0 + 15))
    r = rs0 + 18
    ws.cell(row=r, column=2, value='Компенсировать типами (для посадок): пусто — своим типом').font = Font(name=FONT, bold=True)
    c = ws.cell(row=r + 1, column=2, value='Тип породы')
    style_header(c)
    c = ws.cell(row=r + 1, column=3, value='Компенсировать типом')
    style_header(c)
    note(c, 'Каким типом саженцев компенсировать удаляемый тип в режиме «посадками», если в строке «Ведомости» и в настройках '
            '«Деревья (Кустарники) компенсировать типом» тип не выбран. Пусто — тем же типом. Варианты — графы приложений 4 и 5 к Положению.')
    c = ws.cell(row=r + 1, column=4, value='код (считается сам)')
    style_header(c)
    cm0 = r + 2
    types = ['хвой.', 'медл.', 'быст.', 'плод.', 'малоц.', 'лиан.', 'хвой..', 'вечз.', 'крас.', 'медл..', 'быст..', 'плод..', 'ж.и.1', 'ж.и.2']
    dv_t = DataValidation(type='list', formula1='PTL_T', allow_blank=True)
    dv_s = DataValidation(type='list', formula1='PTL_S', allow_blank=True)
    ws.add_data_validation(dv_t)
    ws.add_data_validation(dv_s)
    for i, t in enumerate(types):
        tree = t in ('хвой.', 'медл.', 'быст.', 'плод.', 'малоц.')
        ws.cell(row=cm0 + i, column=2, value=t)
        c = ws.cell(row=cm0 + i, column=3, value=pt_name(REF['compMap'].get(t) or ''))
        c.fill = F_SET
        c.border = B_THIN
        (dv_t if tree else dv_s).add(c)
        k = ws.cell(row=cm0 + i, column=4, value='=' + pt_code('C%d' % (cm0 + i), 'дерево' if tree else 'куст'))
        k.font = Font(name=FONT, size=9, color='FF888888')
    defname(wb, 'CM_Type', "'Настройки'!$B$%d:$B$%d" % (cm0, cm0 + len(types) - 1))
    defname(wb, 'CM_Map', "'Настройки'!$C$%d:$C$%d" % (cm0, cm0 + len(types) - 1))
    defname(wb, 'CM_Code', "'Настройки'!$D$%d:$D$%d" % (cm0, cm0 + len(types) - 1))
    for row in ws.iter_rows(min_row=3, max_row=cm0 + 20):
        for c in row:
            if c.font and not c.font.bold:
                c.font = Font(name=FONT, size=10, color=c.font.color)
    return ws


# =============================================================== Ведомость
def cols_spec():
    """Колонки листа «Ведомость»: (имя, заголовок, формула для строки 5 | None — ввод, ширина, скрыта, примечание, массив)."""
    C = []

    def add(name, title, f=None, w=10, hidden=False, tip=None, arr=False):
        C.append((name, title, f, w, hidden, tip, arr))
    # ввод
    add('noin', '№ по плану', None, 9,
        tip='Номер по плану (текст):\n• пусто — номер продолжает нумерацию предыдущей строки;\n• «4» — начать с 4;\n'
            '• «4-10» — диапазон: если «Кол-во» пустое, оно считается само (4-10 = 7 шт.: 4, 5, 6, 7, 8, 9, 10);\n• другой текст (например «12а») — как есть.\n'
            'Итоговый номер для ведомостей — в колонке «№ в ведомостях».')
    add('cnt', 'Кол-во (шт., м², п.м.)', None, 8,
        tip='Количество. Деревья и кусты — шт.; участок поросли — площадь, м²; живая изгородь — длина, п.м. '
            'Пусто — 1 шт., а если в «№ по плану» диапазон «4-10» — число номеров (7).')
    add('sp', 'Порода, вид (№ или название)', None, 26,
        tip='Порода: № из справочника или название.\n'
            '• Введите № (например 199) — справа, в колонке «Порода по справочнику», появится «№199 Береза повислая»; в расчёт и ведомости пойдёт эта порода.\n'
            '• Введите начало названия (например «берез») — вверху листа, в жёлтой панели, появятся подходящие породы с номерами; '
            'затем введите в эту же ячейку нужный №.\n'
            '• Номера — лист «Справочник», колонка «№» (там есть фильтр для поиска).\n'
            'Служебные строки: №1 «Уч-к поросли дер.», №2 «Уч-к поросли куст.», №3–4 — то же «у дорог», №5–6 — живая изгородь.')
    add('sphint', 'Порода по справочнику (№)', 'IF({sp}&""="","",IF({libi}>0,"№"&{libi}&" "&INDEX(Lib_Name,{libi}),IF({hc}>0,"подходит "&{hc}&" — список вверху ↑",IF(ISNUMBER({spnum}),"нет № "&{spnum},"нет в справочнике"))))', 32,
        tip='Считается само: № и название породы по справочнику — то, что пойдёт в расчёт и в ведомости. Если введено начало названия — сколько пород подходит; '
            'список с номерами — в жёлтой панели вверху листа.')
    add('h', 'Высота, м', None, 8, tip='Высота, м. Нужна для тарифа кустарников, объёмов древесины (таблицы по высоте) и размера кома при пересадке.')
    add('d', 'Диаметр, см', None, 12,
        tip='Диаметр ствола на высоте 1,3 м, см:\n• целое число — один ствол;\n• «20,15,12» — многоствольное (до 27 стволов);\n• «-» — кустарник и живая изгородь;\n'
            '• «0-4» — участок поросли деревьев;\n• «до1», «до4» — мелкие деревья;\n• «**до1», «**до4» — высаженные в целях озеленения.')
    add('cond', 'Состояние', None, 9, tip='Качественное состояние: хор. (×1), уд. (×0,75), плох. (×0,5), ненадл. (×0,25). Пусто — значение по умолчанию с листа «Настройки».')
    add('act', 'Действие (В/П)', None, 9, tip='В — вырубить, П — пересадить, пусто — сохранить.')
    add('rsn', 'Причина вырубки', None, 16,
        tip='Причина из списка причин (лист «Настройки»). Можно не указывать — тогда коэффициенты как в настройках и в колонках К1–К4. '
            'Причина может менять коэффициенты, если так задано в списке причин.')
    for i, (nm, short, rule) in enumerate(K_INFO, 1):
        add('k%din' % i, '%s %s?' % (nm, short), None, 9,
            tip='%s для этой строки — «%s?»: %s.\nПусто — как на листе «Настройки» (с учётом причины). Указанное здесь значение окончательное для строки.\n'
                'Пример: часть деревьев в водоохранной зоне или вне населённого пункта — отметьте только их.' % (nm, short, rule))
    add('pay', 'Компенсация (выплатами / посадками)', None, 12,
        tip='Способ компенсации для этой строки: «выплатами» — сумма в базовых величинах, «посадками» — число саженцев взамен.\n'
            'Пусто — как на листе «Настройки». Итоги считаются отдельно: выплаты — по строкам с выплатами, посадки — по строкам с посадками; '
            'на листе «Удаляемые» под итогами по способу из настроек появится блок по второму способу.')
    add('ptype', 'Тип посадок (если «посадками»)', None, 16,
        tip='Какими саженцами компенсировать эту строку, если компенсация «посадками». Список зависит от строки: для деревьев и поросли '
            'деревьев — графы приложения 4 (хвойные, медленнорастущие, быстрорастущие лиственные деревья), для кустарников, изгороди и '
            'поросли кустарников — графы приложения 5 к Положению (постановление Совмина № 1426).\n'
            'Пусто — как на листе «Настройки»: «Деревья (Кустарники) компенсировать типом», для поросли — «Поросль … компенсировать типом», '
            'иначе таблица «Компенсировать типами». При выплатах колонка не учитывается.')
    add('note', 'Примечание', None, 14, tip='Попадает в наименование в скобках. Запись вида «V=50%» уменьшает объёмы кустарника.')
    # расчёт (видимые)
    add('no', '№ в ведомостях', 'IF({sp}="","",IF({rok},{ra}&"-"&{rb},IF({free},{nt},{s0}&IF({cnt}&""="","",IFERROR("-"&({s0}+VALUE({cnt}&"")-1),"")))))', 9,
        tip='Номер по плану, как он попадёт в ведомости: из колонки «№ по плану» или продолжение нумерации (для нескольких штук — диапазон).')
    add('title', 'Наименование в ведомости', 'IF({sp}="","",{sp}&IF(AND(NOT({dnum}),IFERROR(FIND(",",{dtxt},2),0)>0)," ("&(LEN({dtxt})-LEN(SUBSTITUTE({dtxt},",",""))+1)&" ств."&IF({note}<>"",", "&{note},"")&")",IF({note}<>""," ("&{note}&")",""))&IF(ISNUMBER(FIND("поросли",{sp})),", м2 ",IF(ISNUMBER(FIND("рядная живая",{sp})),", п.м. ","")))', 30,
        tip='Считается само: порода, число стволов, примечание; для поросли — «м2», для изгороди — «п.м.».')
    add('N', 'Тип', 'IF({sp}="","",IF({libi}>0,INDEX(Lib_Type,{libi}),""))', 7,
        tip='Тип породы из справочника: хвой., медл., быст., плод., малоц. — деревья; лиан., хвой.., вечз., крас., медл.., быст.., плод.. — кустарники; ж.и.1/ж.и.2 — изгородь.')
    add('Htxt', 'Решение', 'IF({sp}="","",IF(AND({shrubT},{dtxt}<>"-"),"ОШИБКА: Ø куста = только ""-""",IF(AND(NOT({shrubT}),{dtxt}="-"),"ОШИБКА: Ø дерева ≠ ""-""",IF({act}="В","вырубить"&IF({rsn}="",""," ("&{rsn}&")"),IF({act}="П","пересадить"&IF({rsn}="",""," ("&{rsn}&")"),"сохранить")))))', 18,
        tip='вырубить / пересадить / сохранить (с причиной, если указана) или текст ошибки ввода.')
    add('kdisp', 'К1; К2; К3; К4', 'IF({coefOK},IF({zero},"0; 0; 0; 0",{K1}&"; "&{K2}&"; "&{K3}&"; "&{K4}),"")', 13,
        tip='Коэффициенты, применённые к строке (после причины, «Инд. К» и колонок К1–К4).')
    add('G', 'Компенсационные посадки (выплаты)', 'IF({rem}<>1,IF({trn}=1,"ком: "&{ball},""),IF(AND(ISNUMBER({Y}),{Y}=0),"Без компенсации",IF({Y}="ОШИБКА",{err},IF(S_VIEW="итог",IF({mon}=1,"("&{AR}&") баз.в.",{Z}),IF({mon}=1,"("&{AS}&") баз.в. "&IF({isPlot}," х"&{qb}&"м²"&IF({qb}<>{qty}," (факт "&{qty}&"м²)","")&" ",IF({qty}>1," х"&{qty}&IF(OR({J}=16,{J}=17),"п.м. ","шт. "),""))&"х"&{Nc}&"х"&{K1}&"х"&{K2}&"х"&{K3}&"х"&{K4}&" ="&{Y},{mx}&" "&IF({mpos}>0,INDEX(LBL_G,{mpos}),"")&IF(AND({AQ}<>"",N({AQ})>=2),"х"&{AQ}&"ств.","")&"х"&{Nc}&"х"&{K1}&"х"&{K2}&"х"&{K3}&"х"&{K4}&" ="&{Y})))))', 40,
        tip='Расчёт строки: (тариф) баз.в. × кол-во (шт., м² или п.м.) × коэф. состояния × К1 × К2 × К3 × К4 = итог. В режиме «посадками» — число саженцев. Для пересадки — размер кома.')
    add('Y', 'Итог', 'IF({rem}<>1,"",IF({inv},0,IF(OR(NOT({coefOK}),{Nc}=""),"ОШИБКА",IF({mon}=1,IF(ISNUMBER({AR}),ROUND({AR}*IF({isPlot},{qb},IF({qty}>1,{qty},1))*{Nc}*{K1}*{K2}*{K3}*{K4},3),"ОШИБКА"),IFERROR(ROUND({mx}*IF(LEN({AO})<2,1,{AQ})*{Nc}*{K1}*{K2}*{K3}*{K4},3),"ОШИБКА")))))', 9,
        tip='Итог строки: базовых величин (выплаты) или штук саженцев (посадки). Поросль считается по каждой строке: площадь (не меньше 10 м²) × норма × коэффициенты.')
    add('err', 'Ошибка', 'IF({sp}="","",IF(LEFT({Htxt},6)="ОШИБКА",{Htxt},IF({qty}="ERR","ОШИБКА: количество не распознано",IF({nmis},"ОШИБКА: кол-во не совпадает с номерами",IF(NOT({found}),IF({hc}>0,"ВЫБЕРИТЕ породу: введите её № из колонки «Порода по справочнику» (подходит "&{hc}&")","ОШИБКА: породы нет в справочнике"),IF(AND({rem}=1,{Nc}=""),"ОШИБКА: неизвестное состояние",IF(AND({rem}=1,{Y}="ОШИБКА"),"ОШИБКА: нет тарифа","")))))))', 22,
        tip='Ошибка ввода в строке. Строка с ошибкой не входит в итоги; число таких строк показано на листе «Удаляемые».')
    # служебные
    h = True
    add('nt', '№ текстом', 'TRIM({noin}&"")', 6, h)
    add('ra', 'диапазон: от', 'IF(ISNUMBER(FIND("-",{nt})),IFERROR(VALUE(LEFT({nt},FIND("-",{nt})-1)),""),"")', 5, h)
    add('rb', 'диапазон: до', 'IF(ISNUMBER({ra}),IFERROR(VALUE(MID({nt},FIND("-",{nt})+1,20)),""),"")', 5, h)
    add('rok', 'диапазон задан', 'IF(AND(ISNUMBER({ra}),ISNUMBER({rb})),AND({ra}>=0,{ra}=INT({ra}),{rb}=INT({rb})),FALSE)', 5, h)
    add('n1', 'начальный номер', 'IFERROR(IF(OR({nt}="",ISNUMBER(FIND("-",{nt}))),"",IF(AND(VALUE({nt})>=0,VALUE({nt})=INT(VALUE({nt}))),VALUE({nt}),"")),"")', 5, h)
    add('free', 'номер — текст', 'AND({nt}<>"",NOT({rok}),NOT(ISNUMBER({n1})))', 5, h)
    add('s0', 'первый номер', 'IF(ISNUMBER({n1}),{n1},N({ne:prev})+1)', 5, h)
    add('nmis', 'кол-во ≠ диапазону', 'IF({rok},AND({cnt}&""<>"",N({qty})<>{rb}-{ra}+1),FALSE)', 5, h)
    add('qty', 'Кол-во, число', 'IF({sp}="","",IF({cnt}&""="",IF({rok},IF({rb}>={ra},{rb}-{ra}+1,"ERR"),1),IFERROR(VALUE({cnt}&""),"ERR")))', 8, h)
    add('ne', 'конец номера', 'IF({sp}="",N({ne:prev}),IF({rok},{rb},IF({free},N({ne:prev}),IF({cnt}&""="",{s0},IFERROR({s0}+VALUE({cnt}&"")-1,{s0})))))', 8, h)
    add('spnum', 'порода: введён №', 'IFERROR(IF(--({sp}&"")=INT(--({sp}&"")),--({sp}&""),""),"")', 5, h)
    add('spn', 'порода по справочнику', 'IF({sp}&""="","",IF(ISNUMBER({spnum}),IF(AND({spnum}>=1,{spnum}<=ROWS(Lib_Name)),IF(INDEX(Lib_Name,{spnum})&""="","№"&{spnum},INDEX(Lib_Name,{spnum})&""),"№"&{spnum}),TRIM({sp}&"")))', 20, h)
    add('hc', 'подходит пород', 'IF(OR({sp}&""="",ISNUMBER({spnum})),0,COUNTIF(SPK,{sp}&"*"))', 4, h)
    add('hp', 'первая подходящая', 'IF({hc}>0,MATCH({sp}&"*",SPK,0),0)', 4, h)
    add('need', 'порода не выбрана (№ строки)', 'IF(AND({sp}&""<>"",{libi}=0,{hc}>0),ROWS({sp:top}:{sp}),"")', 5, h)
    add('libi', 'строка справочника', 'IF({spn}="",0,IFERROR(MATCH({spn},Lib_Name,0),0))', 6, h)
    add('found', 'найдена', '{libi}>0', 6, h)
    add('special', 'служебная', 'IF({libi}>0,INDEX(Lib_Special,{libi})=1,FALSE)', 6, h)
    add('hard', 'твёрдость', 'IF({libi}>0,INDEX(Lib_Hard,{libi})&"","")', 6, h)
    add('inv', 'инвазивный', 'IF({libi}>0,AND(INDEX(Lib_Inv,{libi})=1,NOT({special})),FALSE)', 6, h)
    add('dtxt', 'диаметр текстом', '{d}&""', 8, h)
    add('dnum', 'диаметр числом', 'ISNUMBER({d})', 6, h)
    add('shrubT', 'тип куста', 'ISNUMBER(MATCH({N},ShrubTypes,0))', 6, h)
    add('shrubRow', 'строка куста', 'AND({sp}<>"",{dtxt}="-")', 6, h)
    add('rem', 'удаляется', 'IF(LEFT({Htxt},8)="вырубить",1,0)', 5, h)
    add('trn', 'пересадка', 'IF(LEFT({Htxt},10)="пересадить",1,0)', 5, h)
    add('rep', 'замена по причине', 'IFERROR(INDEX(RS_Repl,MATCH({rsn},RS_Name,0))&"","")', 8, h)
    add('zero', 'без компенсации', 'ISNUMBER(SEARCH("без*компен?ации",{rep}))', 6, h)
    for i, (on, off) in enumerate([(2, 1), (0.5, 1), (0.5, 1), (0.1, 1)], 1):
        same = 'NOT(ISNUMBER(FIND("%d",{rep})))' % i
        add('K%d' % i, 'К%d' % i, 'IF({zero},0,IF(TRIM({k%din})="да",%s,IF(TRIM({k%din})="нет",%s,IF(K%dY,IF(%s,%s,%s),IF(%s,%s,%s)))))' % (i, on, i, off, i, same, on, off, same, off, on), 5, h)
    add('coefOK', 'коэф. есть', 'AND({act}="В",{found})', 5, h)
    add('Es', 'диаметр без пробелов', 'SUBSTITUTE({dtxt}," ","")', 8, h)
    add('J', 'код ТКП', 'IF({rem}<>1,"",IF(NOT({shrubRow}),IF(ISNUMBER(SEARCH("уч-к поросли дер.",{sp})),60,IF(OR({dnum},ISERROR(FIND("до1",{Es}))),IF({N}="хвой.",10,IF({N}="медл.",20,IF(OR({N}="быст.",{N}="плод."),30,IF({N}="малоц.",IF(AND(ISERROR(FIND("до4",{Es})),IF({dnum},{d},IFERROR(VALUE(SUBSTITUTE(SUBSTITUTE(SUBSTITUTE({Es},"1,",""),"2,",""),"3,","")),4))>3.99),40,IF(ISNUMBER(FIND("**до4",{Es})),50,1)),1)))),IF(AND(OR({N}="хвой.",{N}="медл.",{N}="быст.",{N}="плод."),ISNUMBER(FIND("**до1",{Es}))),50,1))),IF(ISNUMBER(SEARCH("уч-к поросли куст.",{sp})),18,IF(ISNUMBER(SEARCH("однорядная живая изгородь",{sp})),16,IF(ISNUMBER(SEARCH("двухрядная живая изгородь",{sp})),17,IF({N}="лиан.",11,IF(OR({N}="хвой..",{N}="вечз."),12,IF({N}="крас.",13,IF({N}="медл..",14,IF(OR({N}="быст..",{N}="плод.."),15,1))))))))))', 5, h)
    add('isPlot', 'поросль', 'OR({J}=60,{J}=70,{J}=18,{J}=19)', 5, h)
    add('qb', 'кол-во для расчёта', 'IF({isPlot},MAX({qty},10),{qty})', 5, h)
    add('mon', 'выплатами (1) / посадками (0)', 'IF(TRIM({pay})="выплатами",1,IF(TRIM({pay})="посадками",0,IF(MONEYY,1,0)))', 4, h)
    add('pT', 'поросль деревьев (1)', 'IF(OR({J}=60,{J}=70),1,0)', 4, h)
    add('pS', 'поросль кустарников (1)', 'IF(OR({J}=18,{J}=19),1,0)', 4, h)
    add('plot', 'поросль (1)', 'IF({isPlot},1,0)', 4, h)
    add('Fx', 'состояние для расчёта', 'IF({isPlot},"-",IF({cond}="",S_COND,{cond}))', 6, h)
    add('Nc', 'коэф. состояния', 'IF(OR(ISNUMBER(SEARCH("~*уч-к поросли дер. у дорог",{sp})),ISNUMBER(SEARCH("~*уч-к поросли куст. у дорог",{sp}))),0.1,IF(OR({Fx}="хор.",{Fx}="-"),1,IF({Fx}="уд.",0.75,IF({Fx}="плох.",0.5,IF({Fx}="ненадл.",0.25,"")))))', 5, h)
    add('cmap', 'компенсировать типом', 'IFERROR(INDEX(CM_Code,MATCH({N},CM_Type,0))&"","")', 6, h)
    m_pt = 'MATCH(TRIM({ptype}&""),PTY_Name,0)'
    add('ptok', 'тип посадок строки (код)', 'IFERROR(IF(INDEX(PTY_Grp,%s)=IF({shrubRow},"куст","дерево"),INDEX(PTY_Code,%s),""),"")' % (m_pt, m_pt), 6, h)
    add('M', 'тип для посадок', 'IF({rem}<>1,"",IF({J}=1,{N},IF({ptok}<>"",{ptok},IF(OR({J}=60,{J}=70),PLT_C&"",IF(OR({J}=18,{J}=19),PLS_C&"",'
        'IF(AND(NOT({shrubRow}),TRP_C<>""),TRP_C,IF(AND({shrubRow},SHP_C<>""),SHP_C,IF({cmap}<>"",{cmap},IF(OR({N}="крас.",{N}="лиан."),IF({shrubRow},{N},"медл."),{N})))))))))', 6, h)
    add('AO', 'свой тип дерева', 'IF(AND({rem}=1,NOT({shrubRow}),ISERROR(SEARCH("ч-к поросли",{sp}))),{N},"")', 6, h)
    add('Hn', 'высота числом', 'IFERROR({h}+0,0)', 5, h)
    add('segs', 'стволы (служебно)', 'SUBSTITUTE(SUBSTITUTE({Es},"*",""),",",REPT(" ",200))', 6, h)
    for k in range(1, NSTEM + 1):
        seg = 'TRIM(MID({segs},%d,200))' % ((k - 1) * 200 + 1)
        first = '{d}' if k == 1 else '""'
        add('d%d' % k, 'ствол %d' % k, 'IF({rem}<>1,"",IF({dnum},%s,IF(%s="","",IF(RIGHT(%s,3)="до1",0.8,IF(RIGHT(%s,3)="до4",3.8,IFERROR(VALUE(RIGHT(SUBSTITUTE(%s,"-","  "),3)),""))))))' % (first, seg, seg, seg, seg), 5, h)
    add('tc', 'колонка тарифа', 'IF({J}=10,1,IF({J}=20,2,IF({J}=30,3,IF({J}=40,4,""))))', 4, h)
    add('AQ', 'стволов ≥12', 'IF(OR({rem}<>1,{shrubRow}),"",MAX(1,COUNTIF({d1}:{d%d},">"&11.9)))' % NSTEM, 5, h)
    add('AR', 'тариф, б.в.', 'IF({rem}<>1,"",IF({J}=1,0,IF(AND({AQ}<>"",N({AQ})>1),IFERROR(SUMPRODUCT((--(0&{d1}:{d%d})>=12)*LOOKUP(--(0&{d1}:{d%d}),INDEX(TarThr,0,{tc}),INDEX(TarVal,0,{tc}))),0),IF({J}=60,P_TP,IF({J}=70,P_TPS,IF({J}=16,P_H1,IF({J}=17,P_H2,IF({J}=18,P_SP,IF({J}=19,P_SPS,IF({J}=50,IFERROR(INDEX(Planted_Val,MATCH({N},Planted_Type,0)),""),IF(AND({J}>=11,{J}<=15),LOOKUP({Hn},INDEX(ShrThr,0,{J}-10),INDEX(ShrVal,0,{J}-10)),IFERROR(LOOKUP(MAX({d1}:{d%d}),INDEX(TarThr,0,{tc}),INDEX(TarVal,0,{tc})),""))))))))))))' % (NSTEM, NSTEM, NSTEM), 5, h)
    # тарифы стволов ≥ 12 см через «+» (раньше — 27 служебных колонок; теперь одна формула, вычисляется только для многоствольных)
    terms = '&'.join('IF(N({d%d})>=12,"+"&IFERROR(LOOKUP({d%d},INDEX(TarThr,0,{tc}),INDEX(TarVal,0,{tc})),0),"")' % (k, k) for k in range(1, NSTEM + 1))
    add('AS', 'тариф текстом', 'IF({rem}<>1,"",IF({J}=1,"0",IF(AND({AQ}<>"",N({AQ})>1),MID(%s,2,999),{AR}&"")))' % terms, 8, h)
    pt = [('c0', 'хвой.', 'PT_X', 'tree'), ('c1', 'медл.', 'PT_M', 'tree'), ('c2', 'быст.', 'PT_B', 'treeB'),
          ('c3', 'лиан.', 'PS_1', 'shrub'), ('c4', ('хвой..', 'вечз.'), 'PS_2', 'shrub'), ('c5', 'крас.', 'PS_3', 'shrub'),
          ('c6', 'медл..', 'PS_4', 'shrub'), ('c7', ('быст..', 'плод..'), 'PS_5', 'shrubB'), ('c8', 'ж.и.1', 'PS_6', 'shrub'), ('c9', 'ж.и.2', 'PS_7', 'shrub')]
    for name, typ, col, kind in pt:
        cond = ('OR(%s)' % ','.join('{M}="%s"' % t for t in typ)) if isinstance(typ, tuple) else '{M}="%s"' % typ
        if kind == 'tree':
            f = 'IF(OR({rem}<>1,{shrubRow}),"",IF(%s,IF({J}=1,0,{qb}*INDEX(%s,MATCH({J},PT_J,0))),""))' % (cond, col)
        elif kind == 'treeB':
            f = 'IF(OR({rem}<>1,{shrubRow}),"",IF(%s,IF({J}=1,0,{qb}*INDEX(%s,MATCH({J},PT_J,0))),0))' % (cond, col)
        elif kind == 'shrub':
            f = 'IF(OR({rem}<>1,NOT({shrubRow})),"",IF(%s,IF({J}=1,0,{qb}*INDEX(%s,MATCH({J},PS_J,0))),""))' % (cond, col)
        else:
            f = 'IF(OR({rem}<>1,NOT({shrubRow})),"",IF(%s,IF({J}=1,0,{qb}*INDEX(%s,MATCH({J},PS_J,0))),""))' % (cond, col)
        add(name, 'посадки ' + (typ if isinstance(typ, str) else '/'.join(typ)), f, 5, h)
    add('mx', 'посадок, шт.', 'IF({rem}<>1,"",MAX({c0}:{c9}))', 5, h)
    add('mpos', 'колонка посадок', 'IF({rem}<>1,"",IFERROR(MATCH({mx},{c0}:{c9},0),0))', 4, h)
    add('Z', 'метка итогов', 'IF({rem}<>1,"",IF(N({mpos})>0,INDEX(LBL_Z,{mpos}),"")&{Y})', 10, h)
    add('Kq', 'деревья, шт.', 'IF(AND({rem}=1,NOT({shrubRow})),{qty},"")', 5, h)
    add('Lq', 'кусты, шт.', 'IF(AND({rem}=1,{shrubRow}),{qty},"")', 5, h)
    # древесина: объёмы по таблицам «Мягк/Хвой/Твер» — одна формула массива на строку вместо 54 колонок
    add('CE', 'класс твёрдости', 'IF(OR({rem}<>1,{shrubRow}),0,IF({special},1,IF({hard}="мяг.",1,IF({hard}="хвн.",2,IF({hard}="твд.",3,-1)))))', 4, h)
    add('wtype', 'древесина', 'IF({CE}=1,"мягкая",IF({CE}=2,"хвойная",IF({CE}=3,"твёрдая","")))', 7, h)
    D = '{d1}:{d%d}' % NSTEM

    DN = '--(0&%s)' % D            # стволы числами: пусто → 0 (строка нулей в таблицах объёмов)

    def vol_sp(total, weight=''):
        """SUMPRODUCT объёмов по всем стволам строки без формул массива (Excel 2010+, LibreOffice)."""
        a, b = ('V_S4', 'V_S8') if total else ('V_S5', 'V_S9')
        c1, c2, c3 = ('V_C3', 'V_C7', 'V_C11') if total else ('V_C4', 'V_C8', 'V_C12')
        d1, d2 = ('V_H3', 'V_H7') if total else ('V_H4', 'V_H8')
        lk = lambda nm: 'LOOKUP(%s,VolDiam,%s)' % (DN, nm)
        w = ('*' + weight) if weight else ''
        soft = 'SUMPRODUCT((%s*(%s<={Hn})+%s*(%s>{Hn}))%s)' % (lk(a), lk('V_S2'), lk(b), lk('V_S2'), w)
        conif = 'SUMPRODUCT((%s*(%s<={Hn})+%s*(%s>{Hn})*(%s<={Hn})+%s*(%s>{Hn})*(%s>{Hn}))%s)' % (
            lk(c1), lk('V_C1'), lk(c2), lk('V_C1'), lk('V_C5'), lk(c3), lk('V_C1'), lk('V_C5'), w)
        hard = 'SUMPRODUCT((%s*(%s<={Hn})+%s*(%s>{Hn}))%s)' % (lk(d1), lk('V_H1'), lk(d2), lk('V_H1'), w)
        return 'IF({CE}=1,%s,IF({CE}=2,%s,%s))' % (soft, conif, hard)

    def vol(dref, total):
        m = 'MATCH(%s,VolDiam,1)' % dref
        a, b = ('V_S4', 'V_S8') if total else ('V_S5', 'V_S9')
        c1, c2, c3 = ('V_C3', 'V_C7', 'V_C11') if total else ('V_C4', 'V_C8', 'V_C12')
        d1, d2 = ('V_H3', 'V_H7') if total else ('V_H4', 'V_H8')
        return ('CHOOSE({CE},IF(INDEX(V_S2,%s)<={Hn},INDEX(%s,%s),INDEX(%s,%s)),IF(INDEX(V_C1,%s)<={Hn},INDEX(%s,%s),IF(INDEX(V_C5,%s)<={Hn},INDEX(%s,%s),INDEX(%s,%s))),'
                'IF(INDEX(V_H1,%s)<={Hn},INDEX(%s,%s),INDEX(%s,%s)))' % (m, a, m, b, m, m, c1, m, m, c2, m, c3, m, m, d1, m, d2, m))
    add('CA', 'сумма тонких стволов', 'IF({rem}<>1,0,SUMIF({d1}:{d%d},"<"&8))' % NSTEM, 5, h)
    add('CC', 'всего, м³', 'IF({CE}<1,"",%s+IF({CA}<8,0,%s-%s))' % (vol_sp(True), vol('{CA}', True), vol('{CA}', False)), 6, h)
    add('CD', 'стволы, м³', 'IF({CE}<1,"",%s)' % vol_sp(False), 6, h)
    add('CB', 'ветви, м³', 'IF({CE}<1,"",IF({CC}<{CD},"",{CC}-{CD}))', 6, h)
    add('dens', 'плотность', 'IF({CE}<1,"",CHOOSE({CE},DEN_S,DEN_C,DEN_H))', 5, h)
    add('BX', 'стволы, т', 'IF({CE}<1,"",{CD}*{dens}*{qty}/1000)', 6, h)
    add('BW', 'ветви, т', 'IF(OR({CE}<1,{CB}=""),"",{CB}*{dens}*{qty}/1000)', 6, h)
    add('BY', 'ветви, м³ скл.', 'IF(OR({CE}<1,{CB}=""),"",{CB}/0.1*{qty})', 6, h)
    wz = '((%s>=15)*IF({CE}=2,1.49,1.47)+(%s>=11)*(%s<15)/0.67+(%s>=3)*(%s<11)/0.6)' % (DN, DN, DN, DN, DN)
    add('BZ', 'стволы, м³ скл.', 'IF({CE}<1,"",{qty}*%s)' % vol_sp(False, wz), 6, h)
    add('big', 'стволы >50 см, м³', 'IF({CE}<1,0,%s)' % vol_sp(False, '(%s>50)' % DN), 6, h)
    add('pct', 'V% из примечания', 'IFERROR(VALUE(MID({note},SEARCH("V",{note})+2,SEARCH("%",{note},SEARCH("V",{note}))-SEARCH("V",{note})-1)),1)', 5, h)
    add('shHa', 'кустарник, га', 'IF({rem}<>1,"",IF(OR(AND({CA}>0.1,{CA}<8),{shrubRow}),IF(OR({J}=60,{J}=18),N({qty})/10000,N({qty})*IF({J}=16,3,IF({J}=17,5,1))*S_BUSHM2/10000)*{pct},""))', 6, h)
    add('shD', 'густота кустарника', 'IF({shHa}="","",MATCH(IF(OR({J}=60,{J}=18),S_PLOTDENS,"редкий"),SHW_Kind,0))', 4, h)
    add('SBm3', 'кустарник: сучья, м³ скл.', 'IF({shHa}="","",{shHa}*INDEX(SHW_M3,{shD}))', 6, h)
    add('SBt', 'кустарник: сучья, т', 'IF({shHa}="","",{shHa}*INDEX(SHW_T,{shD}))', 6, h)
    add('SSm3', 'кустарник: пни, м³ скл.', 'IF({shHa}="","",{shHa}*INDEX(SHW_RM3,{shD}))', 6, h)
    add('SSt', 'кустарник: пни, т', 'IF({shHa}="","",{shHa}*INDEX(SHW_RT,{shD}))', 6, h)
    # пересадка
    add('bcol', 'колонка кома', 'IF({Hn}>5,6,IF({Hn}>4,5,IF({Hn}>2,4,IF({Hn}>1.5,3,IF({Hn}>0.99,2,1)))))', 4, h)
    add('ball', 'Размер кома, м', 'IF({trn}<>1,"",IF({libi}=0,"",IF(INDEX(Lib_InLib,{libi})<>1,"",IF({bcol}=1,"Ø"&INDEX(Lib_BallD,{libi})&"; h"&INDEX(Lib_BallH,{libi}),IFERROR("Ø"&ROUND(N(INDEX(Lib_BallD,{libi}))*INDEX(TypeK,MATCH({N},TypeK_Type,0),({bcol}-2)*2+1),1)&"; h"&ROUND(N(INDEX(Lib_BallH,{libi}))*INDEX(TypeK,MATCH({N},TypeK_Type,0),({bcol}-2)*2+2),1),"")))))', 12, h)
    add('trType', 'тип пересаж. дерева', 'IF(AND({trn}=1,{act}="П",{dtxt}<>"-",ISERROR(SEARCH("ч-к поросли",{sp}))),{N},"")', 6, h)
    add('exCond', 'состояние (ведомости)', 'IF(AND({rem}=1,{isPlot}),"-",IF({cond}="",IF(S_COND="","-",S_COND),{cond}))', 6, h)
    add('dRep', 'диаметр (ведомость удал.)', 'IF({dtxt}="0-4","-",{d})', 6, h)
    add('hRep', 'высота (ведомости)', 'IF(ISNUMBER({h}),{h},IF({h}="","",{h}))', 6, h)
    add('rsnP', 'причина в скобках', 'IF({rsn}="","","("&{rsn}&")")', 8, h)
    add('keep', 'сохраняется (1)', 'IF({Htxt}="сохранить",1,0)', 4, h)
    # нарастающие счётчики для ведомостей (вместо COUNTIF по растущему диапазону — линейное время)
    add('exC', 'счётчик существующих', 'N({exC:prev})+IF({sp}<>"",1,0)', 5, h)
    add('keepC', 'счётчик сохраняемых', 'N({keepC:prev})+{keep}', 5, h)
    add('remC', 'счётчик удаляемых', 'N({remC:prev})+{rem}', 5, h)
    add('trC', 'счётчик пересаживаемых', 'N({trC:prev})+{trn}', 5, h)
    keep_sp = {'spnum', 'spn', 'hc', 'hp', 'sphint', 'libi', 'need'}
    C = [(n, t, (f.replace('{sp}', '{spn}') if (f and n not in keep_sp) else f), w, hd, tip, arr) for n, t, f, w, hd, tip, arr in C]
    return C


def letters(spec):
    return {name: get_column_letter(i + 1) for i, (name, *_rest) in enumerate(spec)}


def render(tpl, L, row=FIRST, last=None, hrow=HROW, first=FIRST, sheet='Ведомость'):
    """{x} → $X9; {x:prev} → значение строки выше через INDEX($X$8:$X8,…) (устойчиво к удалению строк); {x:top} → $X$9; {x:R} → $X$9:$X$last."""
    def rep(m):
        name, mod = m.group(1), m.group(2)
        if name not in L:
            return m.group(0)
        col = L[name]
        if mod == ':prev':
            return 'INDEX($%s$%d:$%s%d,ROWS($%s$%d:$%s%d))' % (col, hrow, col, row - 1, col, hrow, col, row - 1)
        if mod == ':top':
            return '$%s$%d' % (col, first)
        if mod == ':R':
            return "'%s'!$%s$%d:$%s$%d" % (sheet, col, first, col, last)
        return '$%s%d' % (col, row)
    return re.sub(r'\{([A-Za-z_][A-Za-z0-9_]*)(:prev|:top|:R)?\}', rep, tpl)


def build_main(wb, nrows):
    ws = wb.create_sheet('Ведомость', 1)
    spec = cols_spec()
    L = letters(spec)
    last = FIRST + nrows - 1
    inputs = [n for n, t, f, *_ in spec if f is None]
    ws['A1'] = 'Ведомость существующих деревьев и кустарников (ввод) — Таксация Лайт'
    ws['A1'].font = Font(name=FONT, bold=True, size=13, color=GREEN)
    note(ws['A1'], 'Вводите данные в колонки с жёлтым заголовком. Наведите мышь на заголовок колонки — появится пояснение. '
                   'Пустые строки допускаются. Лишнюю позицию можно удалить целиком (правый щелчок по номеру строки → «Удалить») или очистить (Delete).', 320)
    ws['J1'] = ('="Строк в книге: "&ROWS(R_sp)&", заполнено: "&COUNTA(R_sp)&". Служебные колонки справа скрыты (группа «+» над заголовком)."')
    ws['J1'].font = Font(name=FONT, italic=True, size=9, color='FF666666')
    # панель подсказки: подходящие породы с номерами для последней строки, где порода введена не полностью
    PANEL = PatternFill('solid', fgColor='FFFFF7D6')
    ws['A2'] = ('=IF(SP_PEND=0,"Поиск породы: введите в колонку «Порода» номер из справочника или начало названия (например «берез») — '
                'здесь появятся подходящие породы с номерами.","Строка "&(SP_PEND+%d)&": «"&SP_TXT&"» — подходит "&SP_HC&'
                '". Введите в колонку «Порода» нужный номер:")' % (FIRST - 1))
    ws['A2'].font = Font(name=FONT, bold=True, size=10, color='FF7A4A00')
    for rr in range(2, HROW):
        for cc in range(1, 15):
            ws.cell(row=rr, column=cc).fill = PANEL
    slot = 0
    for cc in (1, 4, 7, 10):
        for rr in range(3, HROW):
            slot += 1
            f = ('=IF(SP_HC>={i},IF(AND({i}=20,SP_HC>20),"… и ещё "&(SP_HC-19)&" — уточните начало названия",'
                 'INDEX(SPN,SP_HP+{j})&"   "&INDEX(SPV,SP_HP+{j})),"")').format(i=slot, j=slot - 1)
            c = ws.cell(row=rr, column=cc, value=f)
            c.font = Font(name=FONT, size=10, color='FF1F3F7A')
    for i, (name, title, f, w, hidden, tip, arr) in enumerate(spec):
        col = get_column_letter(i + 1)
        c = ws.cell(row=HROW, column=i + 1, value=title)
        style_header(c)
        c.fill = PatternFill('solid', fgColor='FFFFD966' if f is None else 'FFC6E0B4')   # ввод — жёлтая шапка, расчёт — зелёная
        if tip:
            note(c, tip, 300)
        ws.column_dimensions[col].width = w
        if hidden:
            ws.column_dimensions[col].hidden = True
            ws.column_dimensions[col].outlineLevel = 1
        if f is not None:
            ref = '%s%d' % (col, FIRST)
            ws[ref] = ArrayFormula(ref, '=' + render(f, L)) if arr else '=' + render(f, L)
            if not hidden:
                ws[ref].fill = F_CALC      # скрытые колонки без оформления: файл меньше
    ws.row_dimensions[HROW].height = 44
    ws.freeze_panes = '%s5' % get_column_letter(spec.index(next(x for x in spec if x[0] == 'sphint')) + 2)
    ws.column_dimensions[L['noin']].number_format = '@'     # «4-10» остаётся текстом, а не датой
    ws.column_dimensions[L['d']].number_format = '@'        # «20,15» — два ствола, а не число 20,15 (в русских настройках)
    ws.column_dimensions[L['sp']].alignment = Alignment(horizontal='left')
    rng = lambda c: '%s%d:%s%d' % (L[c], FIRST, L[c], last)
    dv_cond = DataValidation(type='list', formula1='"хор.,уд.,плох.,ненадл."', allow_blank=True)
    dv_act = DataValidation(type='list', formula1='"В,П"', allow_blank=True)
    sp = '$%s%d' % (L['sp'], FIRST)
    dv_sp = DataValidation(type='list', allow_blank=True, showErrorMessage=False,
                           formula1='IF(OR({s}="",COUNTIF(Lib_Name,{s})>0),SPL,IF(COUNTIF(SPK,{s}&"*")=0,SPL,OFFSET(SPV1,MATCH({s}&"*",SPK,0)-1,0,COUNTIF(SPK,{s}&"*"),1)))'.format(s=sp))
    assert len(dv_sp.formula1) < 255, len(dv_sp.formula1)
    dv_rs = DataValidation(type='list', formula1='RS_Name', allow_blank=True, showErrorMessage=False)
    dv_yn = DataValidation(type='list', formula1='"да,нет"', allow_blank=True, showErrorMessage=True,
                           errorTitle='К1–К4', error='Выберите «да», «нет» или оставьте пусто (как в настройках).')
    dv_pay = DataValidation(type='list', formula1='"выплатами,посадками"', allow_blank=True, showErrorMessage=True,
                            errorTitle='Компенсация', error='Выберите «выплатами», «посадками» или оставьте пусто (как в настройках).')
    dv_pt = DataValidation(type='list', formula1='IF($%s%d,PTL_S,PTL_T)' % (L['shrubRow'], FIRST), allow_blank=True, showErrorMessage=True,
                           errorTitle='Тип посадок', error='Выберите тип из списка (для деревьев — деревья, для кустарников — кусты и изгородь) '
                                                          'или оставьте пусто (как в настройках).')
    for dv, cols in ((dv_cond, ['cond']), (dv_act, ['act']), (dv_sp, ['sp']), (dv_rs, ['rsn']), (dv_yn, ['k1in', 'k2in', 'k3in', 'k4in']), (dv_pay, ['pay']),
                     (dv_pt, ['ptype'])):
        ws.add_data_validation(dv)
        for c in cols:
            dv.add(rng(c))
    for name, col in L.items():
        defname(wb, 'R_' + name, "'Ведомость'!$%s$%d:$%s$%d" % (col, FIRST, col, last))
    return ws, spec, L, last


# =============================================================== ведомости: сначала перечень, под ним итоги
HDR_ROW = LFIRST - 1


def org_line(extra=''):
    """Шапка ведомости: организация · номер · объект · стадия (+ extra, начинается с « · »); без « · » в начале строки."""
    return ('=MID(IF(S_ORG<>""," · "&S_ORG,"")&IF(S_NO<>""," · "&S_NO,"")&IF(S_NAME<>""," · "&S_NAME,"")&IF(S_STAGE<>""," · стадия "&S_STAGE,"")'
            + extra + ',4,999)')


def build_list(wb, sheet, title, cols, counter, nname, totals, cap, top_note=None, hidden_at=None, teff=None, src='Ведомость', acad=None):
    """Лист-ведомость. cols: [(заголовок, выражение через {I} — номер строки «Ведомости», ширина, примечание, формат)].
    Строки перечня идут подряд с LFIRST; сразу под последней позицией — пустая строка и строки итогов (totals: список строк
    по len(cols) формул без «=» или None). Итоги считаются в скрытых колонках справа."""
    ws = wb.create_sheet(sheet)
    nc = len(cols)
    T = len(totals)
    TE = teff or str(T)                # сколько строк итогов показывать (может зависеть от данных)
    ws['A1'] = title
    ws['A1'].font = Font(name=FONT, bold=True, size=13)
    ws['A2'] = org_line()
    over = ('IF(%s+1+%d>%d,"Перечень не помещается (в нём %d строк). Возьмите книгу большего размера и перенесите туда колонки ввода листа «%s». ","")'
            % (nname, T, cap, cap, src))
    ws['A3'] = '=' + over + ('&' + top_note[1:] if top_note else '')
    ws['A3'].font = Font(name=FONT, color='FFB42318', bold=True)
    last_col = get_column_letter(nc)
    ws['A4'] = ('=HYPERLINK("#\'{s}\'!A{h}:{c}"&({h}+{n}+1+{te}),"▶ Выделить таблицу для AutoCAD (затем Ctrl+C; в AutoCAD: Правка → Специальная вставка → Объекты AutoCAD)")'
                .format(s=sheet, h=HDR_ROW, c=last_col, n=nname, te=TE))
    ws['A4'].font = Font(name=FONT, color='FF1F5FBF', underline='single')
    link_tip = ''
    if acad:
        # имена для связи данных AutoCAD: диапазон сам охватывает шапку, позиции и итоги — без пустых строк в хвосте
        full = "'{s}'!$A${h}:INDEX('{s}'!${c}:${c},{h}+{n}+1+({te}))".format(s=sheet, h=HDR_ROW, c=last_col, n=nname, te=TE)
        items = "'{s}'!$A${h}:INDEX('{s}'!${c}:${c},{h}+{n})".format(s=sheet, h=HDR_ROW, c=last_col, n=nname)
        defname(wb, 'АКАД_' + acad, full)
        defname(wb, 'АКАД_' + acad + '_перечень', items)
        ACAD_NAMES.append(('АКАД_' + acad, sheet, 'шапка, перечень и итоги', 'list'))
        ACAD_NAMES.append(('АКАД_' + acad + '_перечень', sheet, 'шапка и перечень без итогов', 'list'))
        link_tip = ('\nОбновляемая таблица в AutoCAD: команда DATALINK → связь с именованным диапазоном «АКАД_%s» (с итогами) '
                    'или «АКАД_%s_перечень» (без итогов). Диапазон сам меняет длину по числу позиций.' % (acad, acad))
    note(ws['A4'], 'Щелчок выделяет перечень вместе с итогами. Скопируйте (Ctrl+C), в AutoCAD выполните команду PASTESPEC '
                   '(«Правка → Специальная вставка») и выберите «Объекты AutoCAD» — получится таблица, которую можно править прямо в чертеже.'
         + link_tip, 320)
    for i, (h, expr, w, tip, fmt) in enumerate(cols):
        c = ws.cell(row=HDR_ROW, column=i + 1, value=h)
        style_header(c)
        if tip:
            note(c, tip)
        ws.column_dimensions[get_column_letter(i + 1)].width = w
    ws.row_dimensions[HDR_ROW].height = 40
    kc = hidden_at or nc + 2           # скрытая колонка: номер строки «Ведомости»
    t0 = kc + 2                        # скрытый блок итогов
    K = get_column_letter(kc)
    ws.column_dimensions[K].hidden = True
    ws.cell(row=HDR_ROW, column=kc, value='строка')
    for j in range(nc):
        ws.column_dimensions[get_column_letter(t0 + j)].hidden = True
    for li, line in enumerate(totals):
        for j in range(nc):
            f = line[j] if j < len(line) else None
            ws.cell(row=LFIRST + li, column=t0 + j, value='=' + (f if f else '""'))
    tb = "$%s$%d:$%s$%d" % (get_column_letter(t0), LFIRST, get_column_letter(t0 + nc - 1), LFIRST + T - 1)
    r = LFIRST
    k = 'ROWS($A$%d:$A%d)' % (LFIRST, r)
    ws.cell(row=r, column=kc, value='=IF(%s>%s,"",IFERROR(MATCH(%s,R_%s,0),""))' % (k, nname, k, counter))
    j = '(%s-%s-1)' % (k, nname)
    for i, (h, expr, w, tip, fmt) in enumerate(cols):
        item = expr.replace('{I}', '$%s%d' % (K, r))
        c = ws.cell(row=r, column=i + 1, value='=IF($%s%d<>"",%s,IF(AND(%s>=1,%s<=%s),INDEX(%s,%s,%d),""))' % (K, r, item, j, j, TE, tb, j, i + 1))
        if fmt:
            c.number_format = fmt
        c.font = Font(name=FONT, size=9)
        c.alignment = Alignment(vertical='top', wrap_text=(w >= 30))
    rng = 'A%d:%s%d' % (LFIRST, last_col, LFIRST + cap - 1)
    side = Side(style='thin', color='FF9AA79C')
    ws.conditional_formatting.add(rng, FormulaRule(formula=['ROWS($A$%d:$A%d)<=%s' % (LFIRST, LFIRST, nname)], border=Border(side, side, side, side)))
    ws.conditional_formatting.add(rng, FormulaRule(formula=['AND(ROWS($A$%d:$A%d)>%s+1,ROWS($A$%d:$A%d)<=%s+1+%s)' % (LFIRST, LFIRST, nname, LFIRST, LFIRST, nname, TE)],
                                                   font=Font(bold=True), fill=PatternFill('solid', fgColor='FFEEF3EE', bgColor='FFEEF3EE')))
    ws.freeze_panes = 'A%d' % LFIRST
    ws._taks_kc = (nc, kc)
    return ws


IDX = lambda name, txt=True: 'INDEX(R_%s,{I})%s' % (name, '&""' if txt else '')
C_NO = ('Номер по плану', IDX('no'), 11, 'Номер дерева (куста) по плану.', None)
C_TITLE = ('Порода, вид', IDX('title'), 32, 'Порода и вид; число стволов и примечание — в скобках.', None)
C_QTY = ('Кол-во, шт. (м², п.м.)', IDX('qty', False), 9, 'Количество: шт.; для поросли — м², для живой изгороди — п.м. Пустое «Кол-во» в ведомости = 1 шт.', None)
C_H = ('Высота, м', IDX('hRep', False), 8, 'Высота, м.', None)
C_DT = ('Диаметр ствола, см', IDX('dtxt'), 14, 'Диаметр на высоте 1,3 м; многоствольные — через запятую; «-» — кустарник.', None)
C_COND = ('Качественное состояние', IDX('exCond'), 21, 'хор., уд., плох., ненадл.', None)


def removal_lines(P='NOT(MONEYY)', left=True):
    """Итоговый блок ведомости удаляемых (УДд!A2037:H2049 оригинала): 13 строк × 8 колонок. P — условие «блок посадок»;
    left=False — без левой части (количества): второй блок, когда в строках выбран другой способ компенсации."""
    rows = [['"-------"', '"-- Удаление --"', '"-------"', '"-------"', '"-------"', 'IF(%s,"--- Компенсация ","-------")' % P,
             'IF(%s,"--------------","Компенсация  выплатами -----")' % P, '"--------------"']]

    def blk(A, B, Cc, D, E, F, G, H):
        return ['IF((%s)="","",%s)' % (Cc, A), 'IF((%s)="","",%s)' % (Cc, B), Cc, 'IF((%s)="","",%s)' % (Cc, D),
                'IF((%s)="","",%s)' % (G, E), 'IF((%s)="","",%s)' % (G, F), G, 'IF((%s)="","",%s)' % (G, H)]
    nz = lambda v: 'IF(%s=0,"",%s)' % (v, v)
    pn = lambda t: 'IF(PLT_C="%s"," ("&ROUND(PLOTN_T,6)&")","")' % t
    pns = lambda ts: 'IF(OR(%s)," ("&ROUND(PLOTN_S,6)&")","")' % ','.join('PLS_C="%s"' % t for t in ts)
    U = 'S_UNIT'
    rows.append(blk('"Итого"', '"_________деревьев:"', nz('C_TREES'), '"шт."', '"Итого"', 'IF(%s,"_______деревьев:","за деревья:")' % P,
                    'IF(%s,%s,IF(AP_TREES=0,"",%s))' % (P, nz('AN_TREES'), money_f('AP_TREES')), 'IF(%s,"шт. саженцев",%s)' % (P, U)))
    rows.append(blk('"в т.ч."', '"лиственные декор-е:"', nz('(C_TREES-C_X-C_P)'), '"шт."', 'IF(%s,"в т.ч.","--- в т.ч. за")' % P,
                    'IF(%s,"хвойн./ вечнозел.:","лиственные декор-е:")' % P,
                    'IF(%s,IF(AN_X=0,"",ROUND(AN_X,6)&%s),IF(AP_M+AP_B+AP_L=0,"",%s))' % (P, pn('хвой.'), money_f('(AP_M+AP_B+AP_L)')),
                    'IF(%s,"шт. "&IF(PLT_C="хвой.","(в т.ч. за поросль)",""),%s)' % (P, U)))
    rows.append(blk('"в т.ч."', '"хвойные и вечнозел.:"', nz('C_X'), '"шт."', 'IF(%s,"в т.ч.","--- в т.ч. за")' % P,
                    'IF(%s,"листв. медл-раст-х:","хвойные и вечнозел.:")' % P,
                    'IF(%s,IF(AN_M=0,"",ROUND(AN_M,6)&%s),IF(AP_X=0,"",%s))' % (P, pn('медл.'), money_f('AP_X')),
                    'IF(%s,"шт. "&IF(PLT_C="медл.","(в т.ч. за поросль)",""),%s)' % (P, U)))
    rows.append(blk('"в т.ч."', '"плодовые:"', nz('C_P'), '"шт."', 'IF(%s,"в т.ч.","--- в т.ч. за")' % P,
                    'IF(%s,"листв. быст-раст-х:","плодовые:")' % P,
                    'IF(%s,IF(AN_B=0,"",ROUND(AN_B,6)&%s),IF(AP_P=0,"",%s))' % (P, pn('быст.'), money_f('AP_P')),
                    'IF(%s,"шт. "&IF(PLT_C="быст.","(в т.ч. за поросль)",""),%s)' % (P, U)))
    rows.append(blk('"Итого"', '"уч-ки поросли дер-ев:"', nz('C_TPLOT'), '"м²"', 'IF(%s,"Итого","--- Итого за")' % P,
                    'IF(%s,"_____кустарников:","уч-ки поросли дер-ев:")' % P,
                    'IF(%s,%s,IF(C_TPLOT=0,"",%s))' % (P, nz('AN_ALL'), money_f('PLOT_T')), 'IF(%s,"шт. (в т.ч. в жив. изг.)",%s)' % (P, U)))
    rows.append(blk('""', '""', '""', '""', 'IF(%s,"в т.ч.","")' % P, 'IF(%s,"древесных лиан:","")' % P,
                    'IF(%s,IF(AP_S0+AN_S1=0,"",ROUND(AN_S1,6)&%s),"")' % (P, pns(['лиан.'])), 'IF(%s,"шт. ","")' % P))
    sh_cnt = '(C_H1+C_H2+C_SPLOT+C_S0+C_S1+C_S2+C_S3+C_S4+C_S5+C_S6)'
    rows.append(blk('"Итого"', '"_______кустарников:"', 'IF(%s=0,"",C_SH35)' % sh_cnt, '"шт."', 'IF(%s,"в т.ч.","--- Итого за")' % P,
                    'IF(%s,"хвойных/вечноз-х:","кустарники:")' % P,
                    'IF(%s,IF(AN_S2=0,"",ROUND(AN_S2,6)&%s),IF(AP_SHRUBS=0,"",%s))' % (P, pns(['хвой..', 'вечз.']), money_f('AP_SHRUBS')), 'IF(%s,"шт. ",%s)' % (P, U)))
    rows.append(blk('"Итого"', '"уч-ки поросли куст-ов:"', nz('C_SPLOT'), '"м²"', 'IF(%s,"в т.ч.","--- Итого за")' % P,
                    'IF(%s,"красивоцветущих:","поросль куст.")' % P,
                    'IF(%s,IF(AN_S3=0,"",ROUND(AN_S3,6)&%s),IF(PLOT_S=0,"",%s))' % (P, pns(['крас.']), money_f('PLOT_S')), 'IF(%s,"шт. ",%s)' % (P, U)))
    rows.append(blk('"Итого"', '"1-рядн. жив. изгородь:"', nz('C_H1'), '"п.м."', 'IF(%s,"в т.ч.","--- Итого за")' % P,
                    'IF(%s,"медл-растущих:","1-рядн. ж.изг.")' % P,
                    'IF(%s,IF(AN_S4=0,"",ROUND(AN_S4,6)&%s),IF(AP_S7=0,"",%s))' % (P, pns(['медл..']), money_f('AP_S7')), 'IF(%s,"шт. ",%s)' % (P, U)))
    rows.append(blk('"Итого"', '"2-рядн. жив. изгородь:"', nz('C_H2'), '"п.м."', 'IF(%s,"в т.ч.","--- Итого за")' % P,
                    'IF(%s,"быстр-х/плод-х:","2-рядн. ж.изг.")' % P,
                    'IF(%s,IF(AN_S5=0,"",ROUND(AN_S5,6)&%s),IF(AP_S8=0,"",%s))' % (P, pns(['быст..', 'плод..']), money_f('AP_S8')), 'IF(%s,"шт. ",%s)' % (P, U)))
    g48 = 'IF(%s,IF(AN_H1=0,"",ROUNDUP(AN_H1*3,3)&%s),%s)' % (P, pns(['ж.и.1']), money_f('AP_TOTAL'))
    rows.append(['""', '""', '""', '""', 'IF(%s,IF((%s)="","","в т.ч."),"ИТОГО всех")' % (P, g48), 'IF(%s,IF((%s)="","","1-рядн. жив. изг.:"),"выплат:")' % (P, g48),
                 g48, 'IF((%s)="","",IF(%s,"шт. ",%s))' % (g48, P, U)])
    g49 = 'IF(%s,IF(AN_H2=0,"",ROUNDUP(AN_H2*5,3)&%s),"")' % (P, pns(['ж.и.2']))
    rows.append(['""', '""', '""', '""', 'IF((%s)="","","в т.ч.")' % g49, 'IF((%s)="","","2-рядн. жив. изг.:")' % g49, g49, 'IF((%s)="","","шт. ")' % g49])
    if not left:
        rows = [['""'] * 4 + rw[4:] for rw in rows]
    return rows


def money_f(v):
    """Текст суммы как в УДд!G2038 (с пересчётом по H2035)."""
    return 'ROUND({v}*IF(UNIT2035="руб.",BASE,1),6)&IF(UNIT2035="баз.в. (руб.)"," ("&ROUND({v}*BASE,6)&")","")'.format(v=v)


def simple_lines(items, first='"Итого:"'):
    """Строки «подпись — значение — ед.» для итогов под перечнем."""
    out = []
    for i, (label, val, unit) in enumerate(items):
        out.append([first if i == 0 else None, '"%s"' % label, val, '"%s"' % unit])
    return out


def wood_summary(ws, c0):
    """Сводные таблицы справа от перечня: объёмы выхода древесины и отходов, объёмы по типам древесины, стволы по диаметрам."""
    L0 = get_column_letter(c0)
    ws.column_dimensions[L0].width = 30
    for j in range(1, 6):
        ws.column_dimensions[get_column_letter(c0 + j)].width = 14
    side = Side(style='thin', color='FF9AA79C')
    box = Border(side, side, side, side)

    def table(r, title, heads, rows, fmt, tip=None, acad=None):
        last = get_column_letter(c0 + len(heads) - 1)
        link = ws.cell(row=r, column=c0, value='=HYPERLINK("#\'%s\'!%s%d:%s%d","▶ для AutoCAD")' % (SH_VOL, L0, r + 2, last, r + 2 + len(rows)))
        if acad:
            defname(ws.parent, acad, "'%s'!$%s$%d:$%s$%d" % (SH_VOL, L0, r + 2, last, r + 2 + len(rows)))
            ACAD_NAMES.append((acad, SH_VOL, title, 'fixed'))
        link.font = Font(name=FONT, size=9, color='FF1F5FBF', underline='single')
        t = ws.cell(row=r + 1, column=c0, value=title)
        t.font = Font(name=FONT, bold=True)
        if tip:
            note(t, tip, 300)
        for j, h in enumerate(heads):
            style_header(ws.cell(row=r + 2, column=c0 + j, value=h))
        for i, row in enumerate(rows):
            for j, v in enumerate(row):
                c = ws.cell(row=r + 3 + i, column=c0 + j, value=v)
                c.border = box
                c.font = Font(name=FONT, size=9, bold=(j == 0))
                if j:
                    c.number_format = fmt
        return r + 4 + len(rows)
    r = 4
    r = table(r, 'Таблица объёмов выхода древесины и древесных отходов',
              ['Единицы измерения', 'Стволы', 'Пни', 'Сучья, ветви, вершины', 'Кустарник (сучья, ветви, вершины)', 'Кустарник (пни)'],
              [['складочный объём, м³', '=ROUND(VOL_BZ_ALL,2)', '=ROUND(VOL_STM3_ALL,2)', '=ROUND(VOL_BY_ALL,2)', '=ROUND(VOL_SBM3,3)', '=ROUND(VOL_SSM3,3)'],
               ['масса, тонн', '=ROUND(VOL_BX_ALL,3)', '=ROUND(VOL_STT_ALL,2)', '=ROUND(VOL_BW_ALL,2)', '=ROUND(VOL_SBT,3)', '=ROUND(VOL_SST,3)']],
              '0.0000', 'Как «Таблица объёмов выхода древесины и древесных отходов» оригинала (лист БАЛАНСЫ), с тем же округлением. '
              'Кустарник — по нормам «сводка 1 га кустарника» (лист «Справочник»).', acad='АКАД_ОбъемыОтходов')
    vt = lambda i: ['=INDEX(VOL_TBL,%d,%d)' % (i, j) for j in range(1, 5)]
    r = table(r, 'Объёмы по типам древесины', ['', 'Мягкая', 'Хвойная', 'Твёрдая', 'Итого'],
              [['Стволы, м³: диаметр ≤ 50 см'] + vt(1), ['Стволы, м³: диаметр > 50 см'] + vt(2), ['Стволы, м³: все диаметры'] + vt(3),
               ['Пни, м³'] + vt(5), ['Сучья, ветви, вершины, м³'] + vt(7), ['Стволы, тонн'] + vt(4), ['Пни, тонн'] + vt(6),
               ['Сучья, ветви, вершины, тонн'] + vt(8)], '0.0000',
              'Объёмы (м³ — складочные) и масса по мягкой, хвойной и твёрдой древесине, как на листе БАЛАНСЫ оригинала.', acad='АКАД_ОбъемыПоТипам')
    labels = ['до 12', '12-16', '16-20', '20-24', '24-28', '28-32', 'более 32']
    r = table(r, 'Число стволов по диаметрам, шт.', ['Диаметр стволов, см', 'Мягкая', 'Хвойная', 'Твёрдая', 'Итого'],
              [[lb] + ['=INDEX(STEMS_TBL,%d,%d)' % (i + 1, j) for j in range(1, 5)] for i, lb in enumerate(labels)], '0',
              'Число удаляемых стволов по диаметрам и типам древесины (как на листе БАЛАНСЫ оригинала).', acad='АКАД_Стволы')
    balance_table(ws, c0, r)


def balance_table(ws, c0, r):
    """«Баланс существующих деревьев и кустарников» в виде для AutoCAD: двухуровневая шапка; колонки поросли деревьев и
    кустарников стоят справа и попадают в выделение для AutoCAD, только если поросль есть."""
    side = Side(style='thin', color='FF000000')
    box = Border(side, side, side, side)
    col = lambda j: get_column_letter(c0 + j)          # j = 0 … 9
    for j in range(6, 10):
        ws.column_dimensions[col(j)].width = 14
    t, h1, h2, h3, d0 = r + 1, r + 2, r + 3, r + 4, r + 5
    link = ws.cell(row=r, column=c0, value=('=HYPERLINK("#\'{sh}\'!{a}{t}:"&IF(EX_PK>0,"{j9}",IF(EX_PD>0,"{j8}","{j7}"))&"{e}",'
                                            '"▶ для AutoCAD (колонки поросли — только если она есть)")').format(
        sh=SH_VOL, a=col(0), t=t, j7=col(7), j8=col(8), j9=col(9), e=d0 + 3))
    link.font = Font(name=FONT, size=9, color='FF1F5FBF', underline='single')
    defname(ws.parent, 'АКАД_Баланс', "'{sh}'!${a}${t}:INDEX('{sh}'!${a}${e}:${j9}${e},1,IF(EX_PK>0,10,IF(EX_PD>0,9,8)))".format(
        sh=SH_VOL, a=col(0), t=t, j9=col(9), e=d0 + 3))
    ACAD_NAMES.append(('АКАД_Баланс', SH_VOL, 'баланс существующих деревьев и кустарников (колонки поросли — только если она есть)', 'fixed'))
    head = {(t, 0): 'Баланс существующих деревьев и кустарников',
            (h1, 0): 'Проектные предложения', (h1, 1): 'Деревья, шт.', (h1, 5): 'Кустарники',
            (h2, 1): 'Всего', (h2, 2): 'в том числе', (h2, 5): 'Кусты, шт.', (h2, 6): 'Живая изгородь, м',
            (h3, 2): 'лиственные декоративные', (h3, 3): 'плодовые', (h3, 4): 'хвойные', (h3, 6): 'однорядная', (h3, 7): 'двухрядная',
            (h1, 8): 'Деревья (поросль), м²', (h1, 9): 'Кустарники (поросль), м²'}
    for rr in (t, h1, h2, h3):
        for j in range(10):
            c = ws.cell(row=rr, column=c0 + j, value=head.get((rr, j)))
            style_header(c)
            c.alignment = Alignment(horizontal='center', vertical='center', wrap_text=True)
            c.border = box
    ws.cell(row=t, column=c0).font = Font(name=FONT, bold=True, size=11)
    for a, b in (((t, 0), (t, 7)), ((h1, 0), (h3, 0)), ((h1, 1), (h1, 4)), ((h1, 5), (h1, 7)), ((h2, 1), (h3, 1)), ((h2, 2), (h2, 4)),
                 ((h2, 5), (h3, 5)), ((h2, 6), (h2, 7)), ((h1, 8), (h3, 8)), ((h1, 9), (h3, 9))):
        ws.merge_cells(start_row=a[0], start_column=c0 + a[1], end_row=b[0], end_column=c0 + b[1])
    ws.row_dimensions[h3].height = 30
    rows = [('Сохраняемые', 'KB_TREES', 'KB_L', 'KP_P', 'KP_X', 'KB_SHRUBS', 'KB_H1', 'KB_H2', 'KB_PD', 'KB_PK'),
            ('Пересаживаемые', 'TR_TREES', '(TR_TREES-TR_P-TR_X)', 'TR_P', 'TR_X', 'TR_SHRUBS', 'TR_H1', 'TR_H2', '0', '0'),
            ('Удаляемые', 'C_TR35', '(C_TREES-C_P-C_X)', 'C_P', 'C_X', 'C_SH35', 'C_HH1', 'C_HH2', 'C_PD', 'C_PK')]
    for i, row in enumerate(rows):
        ws.cell(row=d0 + i, column=c0, value=row[0])
        for j in range(1, 10):
            ws.cell(row=d0 + i, column=c0 + j, value='=' + row[j])
    ws.cell(row=d0 + 3, column=c0, value='Итого')
    for j in range(1, 10):
        ws.cell(row=d0 + 3, column=c0 + j, value='=SUM(%s%d:%s%d)' % (col(j), d0, col(j), d0 + 2))
    for rr in range(d0, d0 + 4):
        for j in range(10):
            c = ws.cell(row=rr, column=c0 + j)
            c.border = box
            c.font = Font(name=FONT, size=9, bold=(rr == d0 + 3))
            c.alignment = Alignment(horizontal='left' if j == 0 else 'center', vertical='center')
    note(ws.cell(row=h1, column=c0 + 8), 'Колонки поросли попадают в таблицу для AutoCAD, только если на объекте есть поросль деревьев (кустарников).')


def build_lists(wb, nrows):
    caps = {}
    err = '=IF(N_ERR>0,"Внимание: строк с ошибками — "&N_ERR&". Они не вошли в итоги (колонка «Ошибка» на листе «Ведомость»).","")'
    # существующие
    ex_cols = [C_NO, C_TITLE, C_QTY, C_H, C_DT, C_COND, ('Примечание', IDX('Htxt'), 22, 'Решение по дереву: сохранить, вырубить или пересадить (с причиной).', None)]
    ex_tot = simple_lines([('Деревья', 'EX_TREES', 'шт.'), ('Кустарники', 'EX_SHRUBS', 'шт.'), ('Поросль деревьев', 'EX_PD', 'м²'),
                           ('Поросль кустарников', 'EX_PK', 'м²'), ('Живая изгородь однорядная', 'EX_H1', 'п.м.'), ('Живая изгородь двухрядная', 'EX_H2', 'п.м.')])
    caps['Существующие'] = nrows + len(ex_tot) + 2
    ws = build_list(wb, 'Существующие', 'Ведомость существующих деревьев и кустарников', ex_cols, 'exC', 'N_EX', ex_tot, caps['Существующие'],
                    acad='Существующие')
    # сохраняемые
    kp_cols = [C_NO, C_TITLE, C_QTY, C_H, C_DT, C_COND, ('Примечание', IDX('note'), 22, 'Примечание из ведомости.', None)]
    kp_tot = simple_lines([('Деревья', 'KB_TREES', 'шт.'), ('в т.ч. лиственные декоративные', 'KB_L', 'шт.'), ('в т.ч. плодовые', 'KB_P', 'шт.'),
                           ('в т.ч. хвойные', 'KB_X', 'шт.'), ('Кустарники', 'KB_SHRUBS', 'шт.'), ('Поросль деревьев', 'KB_PD', 'м²'),
                           ('Поросль кустарников', 'KB_PK', 'м²'), ('Живая изгородь однорядная', 'KB_H1', 'п.м.'), ('Живая изгородь двухрядная', 'KB_H2', 'п.м.')])
    caps['Сохраняемые'] = nrows + len(kp_tot) + 2
    ws = build_list(wb, 'Сохраняемые', 'Ведомость сохраняемых деревьев и кустарников', kp_cols, 'keepC', 'N_KEEP', kp_tot, caps['Сохраняемые'],
                    acad='Сохраняемые')
    # удаляемые
    rm_cols = [C_NO, C_TITLE, C_QTY, C_H, ('Диаметр ствола, см', IDX('dRep'), 14, C_DT[3], None),
               ('Качественное состояние', IDX('Fx'), 21, 'хор., уд., плох., ненадл.; для поросли — «-».', None),
               ('Компенсационные посадки (выплаты)', IDX('G'), 42, 'Расчёт: (тариф) баз.в. × кол-во × коэф. состояния × К1 × К2 × К3 × К4 = итог. В режиме «посадками» — число саженцев.', None),
               ('Примечание', IDX('rsnP'), 16, 'Причина вырубки (если указана).', None)]
    rm_tot = removal_lines() + removal_lines('MONEYY', left=False)
    caps['Удаляемые'] = nrows + len(rm_tot) + 2
    ws = build_list(wb, 'Удаляемые', 'Ведомость удаляемых деревьев и кустарников', rm_cols, 'remC', 'N_REM', rm_tot, caps['Удаляемые'], err,
                    teff='IF(N_OTHER>0,26,13)', acad='Удаляемые')
    ws['A2'] = org_line('&" · "&IF(AND(N_MROWS>0,N_PROWS>0),"компенсационные выплаты, "&S_UNIT&", и посадки",IF(OR(N_MROWS>0,AND(N_PROWS=0,MONEYY)),"компенсационные выплаты, "&S_UNIT,"компенсационные посадки"))&" · базовая величина "&BASE&" руб."')
    # пересаживаемые
    tr_cols = [C_NO, C_TITLE, C_QTY, C_H, C_DT, C_COND, ('Размер кома, м', IDX('ball'), 14, 'Диаметр и высота кома по справочнику и высоте растения.', None),
               ('Примечание', IDX('rsnP'), 16, 'Причина пересадки (если указана).', None)]
    tr_tot = simple_lines([('Деревья', 'TR_TREES', 'шт.'), ('в т.ч. хвойные', 'TR_X', 'шт.'), ('в т.ч. плодовые', 'TR_P', 'шт.'),
                           ('Кустарники', 'TR_SHRUBS', 'шт.'), ('Живая изгородь однорядная', 'TR_H1', 'п.м.'), ('Живая изгородь двухрядная', 'TR_H2', 'п.м.')],
                          '"Итого пересадить:"')
    caps['Пересаживаемые'] = nrows + len(tr_tot) + 2
    ws = build_list(wb, 'Пересаживаемые', 'Ведомость пересаживаемых деревьев и кустарников', tr_cols, 'trC', 'N_TR', tr_tot, caps['Пересаживаемые'],
                    acad='Пересаживаемые')
    # древесина
    F3 = '0.000'
    F4 = '0.0000'
    num = lambda nm, mul='': 'IF(ISNUMBER(INDEX(R_%s,{I})),INDEX(R_%s,{I})%s,"")' % (nm, nm, mul)
    SVV = 'Сучья, ветви, вершины'
    wd_cols = [C_NO, C_TITLE, C_QTY, C_H, ('Диаметр ствола, см', IDX('dRep'), 12, C_DT[3], None),
               ('Тип древесины', IDX('wtype'), 10, 'Тип древесины по библиотеке пород: мягкая, хвойная или твёрдая. От него зависят таблица объёмов и плотность.', None),
               (SVV + ', м³ скл.', num('BY'), 11, 'Сучья, ветви и вершины (крона): объём всего дерева минус объём ствола по таблицам оригинала, ÷ 0,1 (складочный), × кол-во.', F3),
               (SVV + ', т', num('BW'), 10, 'Сучья, ветви и вершины: объём кроны по таблицам × плотность древесины × кол-во.', F3),
               ('Пни, м³ скл.', num('BZ', '*0.2'), 9, 'Пни: 20 % складочного объёма древесины стволов (как в оригинале). Корни отдельно не считаются.', F3),
               ('Пни, т', num('BX', '*0.2'), 8, 'Пни: 20 % массы древесины стволов (как в оригинале).', F3),
               ('Древесина, м³ скл.', num('BZ'), 10, 'Древесина (стволы), складочный объём: объём ствола по таблице (диаметр каждого ствола и высота) × коэф. укладки (Ø ≥ 15 см — ×1,47, хвойные ×1,49; 11–15 см — ÷0,67; 3–11 см — ÷0,6) × кол-во.', F3),
               ('Древесина, т', num('BX'), 9, 'Древесина (стволы), масса: объём стволов по таблице × плотность древесины × кол-во.', F3),
               ('Кустарник (Сучья, ветви, вершины), м³ скл.', num('SBm3'), 12,
                'Кустарник по нормам «сводка 1 га кустарника» (лист «Справочник»): древесина, складометры × площадь. Поштучно — 1 куст = '
                '«Площадь одного куста» м² редкого кустарника (лист «Настройки»); живая изгородь — 3 или 5 кустов на 1 п.м.; поросль — по площади и густоте из настроек.', F4),
               ('Кустарник (Сучья, ветви, вершины), т', num('SBt'), 11, 'Масса древесины кустарника по нормам «сводка 1 га кустарника».', F4),
               ('Кустарник (Пни), м³ скл.', num('SSm3'), 10, 'Корневые остатки кустарника (пни), складометры, по нормам «сводка 1 га кустарника».', F4),
               ('Кустарник (Пни), т', num('SSt'), 9, 'Масса корневых остатков кустарника по нормам «сводка 1 га кустарника».', F4)]
    tw = lambda key, ce=None: ('SUMIFS(R_%s,R_CE,%d)' % (key, ce)) if ce else 'SUM(R_%s)' % key
    wd_tot = []
    for label, ce in (('"Итого"', None), ('"в т.ч. мягкая"', 1), ('"в т.ч. хвойная"', 2), ('"в т.ч. твёрдая"', 3)):
        line = [label if ce is None else None, None if ce is None else label, None, None, None, None,
                tw('BY', ce), tw('BW', ce), tw('BZ', ce) + '*0.2', tw('BX', ce) + '*0.2', tw('BZ', ce), tw('BX', ce)]
        line += ['VOL_SBM3', 'VOL_SBT', 'VOL_SSM3', 'VOL_SST'] if ce is None else [None] * 4
        wd_tot.append(line)
    wd_tot.append([None, '"древесина Ø ≤ 50 см, м³ скл."', None, None, None, None, None, None, None, None, 'VOL_LE50_ALL'])
    wd_tot.append([None, '"древесина Ø > 50 см, м³ скл."', None, None, None, None, None, None, None, None, 'VOL_GT50_ALL'])
    wd_tot.append([None, '"округлённо (как в оригинале)"', None, None, None, None, 'ROUND(VOL_BY_ALL,2)', 'ROUND(VOL_BW_ALL,2)', 'ROUND(VOL_BZ_ALL*0.2,2)',
                   'ROUND(VOL_BX_ALL*0.2,2)', 'ROUND(VOL_BZ_ALL,2)', 'ROUND(VOL_BX_ALL,3)',
                   'ROUND(VOL_SBM3,3)', 'ROUND(VOL_SBT,3)', 'ROUND(VOL_SSM3,3)', 'ROUND(VOL_SST,3)'])
    caps[SH_VOL] = nrows + len(wd_tot) + 2
    ws = build_list(wb, SH_VOL, 'Объемы: отходы по удаляемым деревьям и кустарникам — сучья, ветви, вершины; пни; древесина', wd_cols, 'remC', 'N_REM', wd_tot,
                    caps[SH_VOL], hidden_at=40, acad='Отходы')
    wood_summary(ws, 18)
    ws['A5'] = ('Объёмы — по таблицам «Мягк», «Хвой», «Твер» оригинала (лист «Справочник»), по диаметру каждого ствола и высоте. '
                'Пни = 20 % от древесины стволов. Корни отдельно не считаются.')
    ws['A5'].font = Font(name=FONT, italic=True, size=9, color='FF555555')
    return {sh: (cap, wb[sh]._taks_kc) for sh, cap in caps.items()}


# =============================================================== итоги
def build_totals(wb, L):
    ws = wb.create_sheet('Итоги')
    ws.column_dimensions['A'].width = 46
    for c in 'BCDEFGHI':
        ws.column_dimensions[c].width = 15
    ws['A1'] = 'Итоги удаления, поросль, древесина (расчётные ячейки — не изменять)'
    ws['A1'].font = Font(name=FONT, bold=True, size=13)
    T = {}
    r = 3

    def put(name, label, formula, tip=None):
        nonlocal r
        c = ws.cell(row=r, column=1, value=label)
        if tip:
            note(c, tip)
        ws.cell(row=r, column=2, value='=' + formula)
        defname(wb, name, "'Итоги'!$B$%d" % r)
        T[name] = r
        r += 1
    ws.cell(row=r, column=1, value='Число строк в ведомостях').font = Font(name=FONT, bold=True)
    r += 1
    put('N_EX', 'существующих', 'INDEX(R_exC,ROWS(R_exC))')
    put('N_KEEP', 'сохраняемых', 'INDEX(R_keepC,ROWS(R_keepC))')
    put('N_REM', 'удаляемых', 'INDEX(R_remC,ROWS(R_remC))')
    put('N_TR', 'пересаживаемых', 'INDEX(R_trC,ROWS(R_trC))')
    put('SP_PEND', 'панель «Порода»: строка с неполным названием (последняя)', 'MAX(R_need)')
    put('SP_HC', 'панель «Порода»: подходит пород', 'IF(SP_PEND>0,INDEX(R_hc,SP_PEND),0)')
    put('SP_HP', 'панель «Порода»: первая подходящая', 'IF(SP_PEND>0,INDEX(R_hp,SP_PEND),0)')
    put('SP_TXT', 'панель «Порода»: введённый текст', 'IF(SP_PEND>0,INDEX(R_sp,SP_PEND)&"","")')
    r += 1
    ws.cell(row=r, column=1, value='Поросль (считается по каждой строке)').font = Font(name=FONT, bold=True)
    r += 1
    put('PLOT_T', 'Поросль деревьев, выплаты', 'SUMIFS(R_Y,R_pT,1,R_mon,1)', 'Строки «Уч-к поросли дер.» (в т.ч. «у дорог») с выплатами: площадь × норма × коэффициенты.')
    put('PLOT_S', 'Поросль кустарников, выплаты', 'SUMIFS(R_Y,R_pS,1,R_mon,1)', 'Строки «Уч-к поросли куст.» (в т.ч. «у дорог») с выплатами.')
    put('PLOTN_T', 'Поросль деревьев, посадки, шт.', 'SUMIFS(R_Y,R_pT,1,R_mon,0)')
    put('PLOTN_S', 'Поросль кустарников, посадки, шт.', 'SUMIFS(R_Y,R_pS,1,R_mon,0)')
    put('N_MROWS', 'удаляемых строк с выплатами', 'SUMIFS(R_rem,R_mon,1)')
    put('N_PROWS', 'удаляемых строк с посадками', 'SUMIFS(R_rem,R_mon,0)')
    put('N_OTHER', 'строк со способом не как в настройках', 'IF(MONEYY,N_PROWS,N_MROWS)')
    r += 1
    ws.cell(row=r, column=1, value='Количество удаляемых').font = Font(name=FONT, bold=True)
    r += 1
    for ty, nm in (('хвой.', 'C_X'), ('медл.', 'C_M'), ('быст.', 'C_B'), ('плод.', 'C_P'), ('малоц.', 'C_L')):
        put(nm, 'деревьев типа ' + ty, 'SUMIFS(R_qty,R_rem,1,R_AO,"%s")' % ty)
    put('C_TREES', 'деревьев всего (AP2009)', 'C_X+C_M+C_B+C_P+C_L')
    put('C_TPLOT', 'поросль деревьев, м² (AP2008)', 'SUMIFS(R_Kq,R_spn,"*уч-к поросли дер*")')
    put('C_SPLOT', 'поросль кустарников, м² (AP2030)', 'SUMIFS(R_Lq,R_spn,"*уч-к поросли куст.*")')
    put('C_H1', 'живая изгородь однорядная, п.м.', 'SUMIFS(R_qty,R_rem,1,R_N,"ж.и.1")')
    put('C_H2', 'живая изгородь двухрядная, п.м.', 'SUMIFS(R_qty,R_rem,1,R_N,"ж.и.2")')
    shrub_types = ['лиан.', 'хвой..', 'вечз.', 'крас.', 'медл..', 'быст..', 'плод..']
    for i, ty in enumerate(shrub_types):
        put('C_S%d' % i, 'кустов типа ' + ty, 'SUMIFS(R_qty,R_rem,1,R_N,"%s")' % ty)
    put('C_SUMK', 'сумма K (деревья и поросль дер.)', 'SUM(R_Kq)')
    put('C_SUML', 'сумма L (кусты, поросль, изгородь)', 'SUM(R_Lq)')
    put('C_PD', 'поросль дер. (C2035)', 'SUMIFS(R_Kq,R_spn,"*ч-к поросли дер*")')
    put('C_PK', 'поросль куст. (C2035)', 'SUMIFS(R_Lq,R_spn,"*ч-к поросли куст*")')
    put('C_HH1', 'изгородь 1-р. (E2035)', 'SUMIFS(R_Lq,R_spn,"*днорядная живая изгород*")')
    put('C_HH2', 'изгородь 2-р. (E2035)', 'SUMIFS(R_Lq,R_spn,"*вухрядная живая изгород*")')
    put('C_TR35', 'деревьев удаляется (C2035)', 'C_SUMK-C_PD')
    put('C_SH35', 'кустарников удаляется (C2035)', 'C_SUML-C_PK-C_HH1-C_HH2')
    r += 1
    ws.cell(row=r, column=1, value='Выплаты, баз.вел. (AP2011…)').font = Font(name=FONT, bold=True)
    r += 1
    for ty, nm in (('хвой.', 'AP_X'), ('медл.', 'AP_M'), ('быст.', 'AP_B')):
        put(nm, 'деревья ' + ty + ' (+ поросль по типу)', 'SUMIFS(R_Y,R_AO,"%s",R_mon,1)+IF(PLT_C="%s",PLOT_T,0)' % (ty, ty))
    put('AP_P', 'деревья плод.', 'SUMIFS(R_Y,R_AO,"плод.",R_mon,1)')
    put('AP_L', 'деревья малоц.', 'SUMIFS(R_Y,R_AO,"малоц.",R_mon,1)')
    put('AP_TREES', 'за деревья (AP2017)', 'AP_X+AP_M+AP_B+AP_P+AP_L')
    for i, ty in enumerate(shrub_types + ['ж.и.1', 'ж.и.2']):
        put('AP_S%d' % i, 'кусты ' + ty + ' (+ поросль по типу)', 'SUMIFS(R_Y,R_rem,1,R_N,"%s",R_plot,0,R_mon,1)+IF(PLS_C="%s",PLOT_S,0)' % (ty, ty))
    put('AP_SHRUBS', 'за кустарники (AP2042)', '+'.join('AP_S%d' % i for i in range(7)))
    put('AP_TOTAL', 'ИТОГО всех выплат', 'AP_TREES+AP_S7+AP_S8+AP_SHRUBS',
        'Деревья (с порослью деревьев по типу) + кустарники (с порослью кустарников по типу) + живая изгородь. '
        'В оригинале поросль добавлялась сюда второй раз — исправлено.')
    r += 1
    ws.cell(row=r, column=1, value='Посадки, шт. (AN2011…)').font = Font(name=FONT, bold=True)
    r += 1
    put('AN_X', 'деревья хвойные', 'SUMIFS(R_Y,R_Z,"Д.хв*",R_mon,0)')
    put('AN_M', 'деревья медленнорастущие', 'SUMIFS(R_Y,R_Z,"Д.лис.м*",R_mon,0)')
    put('AN_B', 'деревья быстрорастущие', 'SUMIFS(R_Y,R_Z,"Д.лис.б.р*",R_mon,0)')
    put('AN_TREES', 'деревьев всего (AN2017)', 'AN_X+AN_M+AN_B')
    put('AN_S1', 'кусты лианы', 'SUMIFS(R_Y,R_Z,"К.древ.лиан*",R_mon,0)')
    put('AN_S2', 'кусты хвойные/вечнозелёные', 'SUMIFS(R_Y,R_Z,"К.хв*",R_mon,0)')
    put('AN_S3', 'кусты красивоцветущие', 'SUMIFS(R_Y,R_Z,"К.лис.кр*",R_mon,0)')
    put('AN_S4', 'кусты медленнорастущие', 'SUMIFS(R_Y,R_Z,"К.лис.м.р*",R_mon,0)')
    put('AN_S5', 'кусты быстрорастущие/плодовые', 'SUMIFS(R_Y,R_Z,"К.л.б.р./плод*",R_mon,0)')
    put('AN_H1', 'живая изгородь 1-р., п.м.', 'SUMIFS(R_Y,R_Z,"жив.изг.1р*",R_mon,0)')
    put('AN_H2', 'живая изгородь 2-р., п.м.', 'SUMIFS(R_Y,R_Z,"жив.изг.2р*",R_mon,0)')
    put('AN_SHRUBS', 'кустов всего (AN2042)', 'AN_S1+AN_S2+AN_S3+AN_S4+AN_S5')
    put('AN_ALL', 'кустарников, шт. (в т.ч. в жив. изг.) (G2042)', 'AN_H1+AN_H2+AN_SHRUBS')
    put('ZM_TS', 'выплаты: сумма по подписям посадок (для H2035)', '+'.join('SUMIFS(R_Y,R_Z,"%s*",R_mon,1)' % p for p in
        ('Д.хв', 'Д.лис.м', 'Д.лис.б.р', 'К.древ.лиан', 'К.хв', 'К.лис.кр', 'К.лис.м.р', 'К.л.б.р./плод')))
    put('UNIT2035', 'единица пересчёта итогов выплат (H2035)', 'IF(ZM_TS=0,"",S_UNIT)')
    r += 1
    ws.cell(row=r, column=1, value='Древесина (УДд!AE72:AM83)').font = Font(name=FONT, bold=True)
    r += 1
    for i, hh in enumerate(['мяг.', 'хвн.', 'твд.', 'итого']):
        style_header(ws.cell(row=r, column=2 + i, value=hh))
    r += 1
    vol_rows = [('VOL_LE50', 'древесина (стволы), м³ скл. Ø ≤ 50 см'), ('VOL_GT50', 'древесина (стволы), м³ скл. Ø > 50 см'),
                ('VOL_BZ', 'древесина (стволы), м³ скл.'), ('VOL_BX', 'древесина (стволы), т'),
                ('VOL_STM3', 'пни, м³ скл.'), ('VOL_STT', 'пни, т'), ('VOL_BY', 'сучья, ветви, вершины, м³ скл.'), ('VOL_BW', 'сучья, ветви, вершины, т')]
    vr = {}
    for nm, label in vol_rows:
        vr[nm] = r
        ws.cell(row=r, column=1, value=label)
        r += 1
    for j, col in enumerate('BCD'):
        ce = j + 1
        fac = 1.49 if ce == 2 else 1.47
        ws['%s%d' % (col, vr['VOL_BZ'])] = '=SUMIFS(R_BZ,R_CE,%d)' % ce
        ws['%s%d' % (col, vr['VOL_BX'])] = '=SUMIFS(R_BX,R_CE,%d)' % ce
        ws['%s%d' % (col, vr['VOL_BY'])] = '=SUMIFS(R_BY,R_CE,%d)' % ce
        ws['%s%d' % (col, vr['VOL_BW'])] = '=SUMIFS(R_BW,R_CE,%d)' % ce
        ws['%s%d' % (col, vr['VOL_GT50'])] = '=SUMIFS(R_big,R_CE,%d)*%s' % (ce, fac)
        ws['%s%d' % (col, vr['VOL_LE50'])] = '=%s%d-%s%d' % (col, vr['VOL_BZ'], col, vr['VOL_GT50'])
        ws['%s%d' % (col, vr['VOL_STM3'])] = '=%s%d*0.2' % (col, vr['VOL_BZ'])
        ws['%s%d' % (col, vr['VOL_STT'])] = '=%s%d*0.2' % (col, vr['VOL_BX'])
    for nm, rr in vr.items():
        ws['E%d' % rr] = '=SUM(B%d:D%d)' % (rr, rr)
        defname(wb, nm + '_ALL', "'Итоги'!$E$%d" % rr)
    defname(wb, 'VOL_TBL', "'Итоги'!$B$%d:$E$%d" % (vr['VOL_LE50'], vr['VOL_BW']))
    put('VOL_SBM3', 'кустарник (сучья, ветви, вершины), м³ скл.', 'SUM(R_SBm3)')
    put('VOL_SBT', 'кустарник (сучья, ветви, вершины), т', 'SUM(R_SBt)')
    put('VOL_SSM3', 'кустарник (пни), м³ скл.', 'SUM(R_SSm3)')
    put('VOL_SST', 'кустарник (пни), т', 'SUM(R_SSt)')
    T['vol'] = vr
    r += 1
    ws.cell(row=r, column=1, value='Стволы по диаметрам, шт. (УДд!AK74:AM80)').font = Font(name=FONT, bold=True)
    r += 1
    for i, hh in enumerate(['мяг.', 'хвн.', 'твд.', 'итого']):
        style_header(ws.cell(row=r, column=2 + i, value=hh))
    r += 1
    buckets = [('до 12', None, 12), ('12-16', 12, 16), ('16-20', 16, 20), ('20-24', 20, 24), ('24-28', 24, 28), ('28-32', 28, 32), ('более 32', 32, None)]
    T['stems0'] = r
    defname(wb, 'STEMS_TBL', "'Итоги'!$B$%d:$E$%d" % (r, r + 6))
    for bi, (label, lo, hi) in enumerate(buckets):
        ws.cell(row=r, column=1, value=label)
        for j, col in enumerate('BCD'):
            ce = j + 1
            terms = []
            for k in range(1, NSTEM + 1):
                crit = 'R_CE,%d' % ce
                if lo is not None:
                    crit += ',R_d%d,">"&%s' % (k, lo)
                if hi is not None:
                    crit += ',R_d%d,"<="&%s' % (k, hi)
                if bi == 0 and k == 1:
                    crit += ',R_J,"<"&59'
                terms.append('SUMIFS(R_Kq,%s)' % crit)
            ws['%s%d' % (col, r)] = '=' + '+'.join(terms)
        ws['E%d' % r] = '=SUM(B%d:D%d)' % (r, r)
        r += 1
    r += 1
    ws.cell(row=r, column=1, value='Существующие, сохраняемые, пересаживаемые').font = Font(name=FONT, bold=True)
    r += 1
    put('EX_ALL', 'всего по ведомости', 'SUM(R_qty)')
    put('EX_DASH', 'строк с диаметром «-»', 'SUMIFS(R_qty,R_dtxt,"-")')
    put('EX_PD', 'поросль деревьев, м²', 'SUMIFS(R_qty,R_spn,"*ч-к поросли дер*")')
    put('EX_PK', 'поросль кустарников, м²', 'SUMIFS(R_qty,R_spn,"*ч-к поросли куст*")')
    put('EX_HED', 'живая изгородь (все), п.м.', 'SUMIFS(R_qty,R_spn,"*рядная живая*")')
    put('EX_H1', 'живая изгородь 1-р., п.м.', 'SUMIFS(R_qty,R_spn,"*днорядная живая*")')
    put('EX_H2', 'живая изгородь 2-р., п.м.', 'SUMIFS(R_qty,R_spn,"*вухрядная живая*")')
    put('EX_TREES', 'деревьев (Дер.)', 'EX_ALL-EX_DASH-EX_PD')
    put('EX_SHRUBS', 'кустарников (Кус.)', 'EX_DASH-EX_PK-EX_HED')
    put('TR_ALL', 'пересадка: всего', 'SUMIFS(R_qty,R_trn,1)')
    put('TR_H1', 'пересадка: изгородь 1-р.', 'SUMIFS(R_qty,R_trn,1,R_spn,"*днорядная живая изгород*")')
    put('TR_H2', 'пересадка: изгородь 2-р.', 'SUMIFS(R_qty,R_trn,1,R_spn,"*вухрядная живая изгород*")')
    put('TR_SHRUBS', 'пересадка: кустов', 'SUMIFS(R_qty,R_trn,1,R_dtxt,"-")-TR_H1-TR_H2')
    put('TR_TREES', 'пересадка: деревьев', 'TR_ALL-TR_SHRUBS-TR_H1-TR_H2-SUMIFS(R_qty,R_trn,1,R_spn,"*ч-к поросли дер*")-SUMIFS(R_qty,R_trn,1,R_spn,"*ч-к поросли куст*")')
    put('TR_X', 'пересадка: хвойных', 'SUMIFS(R_qty,R_trType,"хвой.")')
    put('TR_P', 'пересадка: плодовых', 'SUMIFS(R_qty,R_trType,"плод.")')
    put('KP_X', 'сохраняемых хвойных', 'SUMIFS(R_qty,R_act,"",R_N,"хвой.",R_qty,">"&0.5,R_dtxt,"<>-")')
    put('KP_P', 'сохраняемых плодовых', 'SUMIFS(R_qty,R_act,"",R_N,"плод.",R_qty,">"&0.5,R_dtxt,"<>-")')
    put('KB_TREES', 'сохраняемые: деревья', 'EX_TREES-TR_TREES-C_TR35')
    put('KB_X', 'сохраняемые: хвойные', 'KP_X')
    put('KB_P', 'сохраняемые: плодовые', 'KP_P')
    put('KB_L', 'сохраняемые: лиственные декоративные', 'KB_TREES-KP_X-KP_P')
    put('KB_SHRUBS', 'сохраняемые: кустарники', 'EX_SHRUBS-TR_SHRUBS-C_SH35')
    put('KB_PD', 'сохраняемые: поросль деревьев, м²', 'EX_PD-C_PD')
    put('KB_PK', 'сохраняемые: поросль кустарников, м²', 'EX_PK-C_PK')
    put('KB_H1', 'сохраняемые: изгородь 1-р., п.м.', 'EX_H1-TR_H1-C_HH1')
    put('KB_H2', 'сохраняемые: изгородь 2-р., п.м.', 'EX_H2-TR_H2-C_HH2')
    put('N_ERR', 'строк с ошибками', 'COUNTIF(R_err,"?*")')
    r += 1
    ws.cell(row=r, column=1, value='Цветники, газоны, иной травяной покров (лист «%s»), м²' % SH_G).font = Font(name=FONT, bold=True)
    r += 1
    put('GZ_N', 'строк в ведомости цветников и газонов', 'INDEX(R_gC,ROWS(R_gC))')
    put('GZ_ERR', 'строк с ошибками', 'COUNTIF(R_gerr,"?*")')
    for k, flag, what in (('K', 'R_gkeep', 'сохраняемые'), ('T', 'R_gtrn', 'пересаживаемые'), ('R', 'R_grem', 'удаляемые')):
        for c, cat in (('F', 'цветник'), ('L', 'газон'), ('G', 'иной травяной покров')):
            put('GZ_%s%s' % (k, c), '%s: %s' % (what, cat), 'SUMIFS(R_garn,R_gok,1,%s,1,R_gcat,"%s")' % (flag, cat))
    put('GZ_PF', 'посадками: восстановить цветники', 'SUMIFS(R_gpl,R_gcat,"цветник")', 'п. 10 Положения: за цветник — цветник на площади не менее удалённой.')
    put('GZ_PL', 'посадками: восстановить газоны', 'SUMIFS(R_gpl,R_gcat,"газон")+SUMIFS(R_gpl,R_gcat,"иной травяной покров")', 'п. 10 Положения: за газон и иной травяной покров — газон на площади не менее удалённой.')
    put('GZ_V', 'выплаты, баз.в.', 'SUM(R_gv)', 'п. 11 Положения: стоимость × площадь × коэф. состояния × К1 × К2 × К3 × К4.')
    put('GZ_NM', 'удаляемых строк с выплатами', 'COUNTIFS(R_gok,1,R_grem,1,R_gmon,1)')
    put('GZ_NP', 'удаляемых строк с посадками', 'COUNTIFS(R_gok,1,R_grem,1,R_gmon,0)')
    return ws, T


# =============================================================== цветники, газоны, иной травяной покров
def greens_spec():
    """Колонки листа «Цветники и газоны» (формат как у cols_spec). Правила — постановление Совмина РБ № 1426,
    Положение о порядке определения условий осуществления компенсационных мероприятий (п. 9–11, приложение 8)."""
    C = []

    def add(name, title, f=None, w=10, hidden=False, tip=None):
        C.append((name, title, f, w, hidden, tip, False))
    h = True
    kinds = '\n'.join('• %s — %s баз.вел./м²' % (g['name'], str(g['value']).replace('.', ',')) for g in REF['greens'])
    add('gnoin', '№ по плану', None, 9,
        tip='Номер по плану (текст):\n• пусто — следующий номер (предыдущий + 1);\n• целое число — начать нумерацию с него;\n'
            '• другой текст (например «Г-1») — как есть.')
    add('gkind', 'Вид (выбор из списка)', None, 30,
        tip='Выберите из списка. Стоимость (приложение 8 к Положению, постановление Совмина № 1426):\n' + kinds)
    add('garea', 'Площадь, м²', None, 9, tip='Площадь цветника, газона, иного травяного покрова, м² (число).')
    add('gcond', 'Состояние', None, 9,
        tip='Для цветников и газонов: хор. (×1), уд. (×0,75), плох. (×0,5) — сноска к приложению 8.\n'
            'Пусто — хор.: по п. 9 Положения, если состояние определить нельзя (окончание вегетации, снег), цветник, газон признаются '
            'находящимися в хорошем состоянии.\nДля иного травяного покрова состояние на расчёт не влияет.')
    add('gact', 'Действие (В/П)', None, 8, tip='В — удалить, П — пересадить (без компенсации), пусто — сохранить.')
    add('grsn', 'Причина', None, 14,
        tip='Причина из списка причин (лист «Настройки»). Можно не указывать. Причина с «без компенсации» снимает компенсацию; '
            'номера коэффициентов в списке причин меняют К1–К4 на противоположные.')
    for i, (nm, short, rule) in enumerate(K_INFO, 1):
        add('gk%din' % i, '%s %s?' % (nm, short), None, 9,
            tip='%s для этой строки — «%s?»: %s.\nПусто — как на листе «Настройки» (с учётом причины). Применяется к выплатам; '
                'к посадкам за цветники и газоны коэффициенты не применяются (п. 10 Положения).' % (nm, short, rule))
    add('gpay', 'Компенсация (выплатами / посадками)', None, 12,
        tip='выплатами — стоимость × площадь × коэф. состояния × К1 × К2 × К3 × К4 (п. 11 Положения);\n'
            'посадками — восстановить цветник (за цветник) или газон (за газон и иной травяной покров) на площади не менее удалённой, '
            'без коэффициентов (п. 10 Положения).\nПусто — как на листе «Настройки».\n'
            'Если восстановить можно только часть площади, разбейте позицию на две строки: восстанавливаемую площадь — «посадками», '
            'остаток — «выплатами» (по п. 10 за невосстановленную площадь — выплаты).')
    add('gnote', 'Примечание', None, 14, tip='Попадает в наименование в скобках.')
    # расчёт (видимые)
    add('gno', '№ в ведомости', 'IF(NOT({gpres}),"",IF({gnt}="",{gne},IF(ISNUMBER({gn1}),{gn1},{gnt})))', 9,
        tip='Номер, который пойдёт в ведомость: свой или следующий по порядку.')
    add('gdec', 'Решение', 'IF(NOT({gpres}),"",IF({grem}=1,"удалить",IF({gtrn}=1,"пересадить","сохранить"))&IF(AND({grsn}&""<>"",{grem}+{gtrn}>0)," ("&{grsn}&")",""))', 18,
        tip='Сохранить, удалить или пересадить (с причиной).')
    add('gcomp', 'Компенсационные посадки (выплаты)',
        'IF(NOT({gpres}),"",IF({gerrt}<>"","ОШИБКА: "&{gerrt},IF({gtrn}=1,"-",IF({grem}=0,"",IF({gzero},"Без компенсации",IF({gmon}=0,'
        'IF({gcat}="цветник","цветник","газон")&" не менее "&ROUND({garea},6)&" м²",IF(S_VIEW="итог",ROUND({gv},6)&" баз.в.",'
        '"("&{gtar}&") баз.в. х"&ROUND({garea},6)&"м² х"&{gq}&"х"&{gK1}&"х"&{gK2}&"х"&{gK3}&"х"&{gK4}&" ="&ROUND({gv},6))))))))', 40,
        tip='Выплаты: (стоимость) баз.в. × площадь × коэф. состояния × К1 × К2 × К3 × К4 = итог (п. 11 Положения).\n'
            'Посадки: восстановить цветник или газон на площади не менее удалённой (п. 10 Положения).\nПересадка — без компенсации («-»).')
    add('gv', 'Выплата, баз.в.', 'IF(AND({gok}=1,{grem}=1,{gmon}=1),IF({gzero},0,{garea}*{gtar}*{gq}*{gK1}*{gK2}*{gK3}*{gK4}),"")', 10,
        tip='Сумма выплаты по строке в базовых величинах (для строк удаления с выплатами).')
    add('gerr', 'Ошибка', 'IF({gerrt}="","","ОШИБКА: "&{gerrt})', 30, tip='Ошибка ввода. Строка с ошибкой не входит в итоги и баланс.')
    # служебные (скрытые)
    add('gkt', 'вид текстом', 'TRIM({gkind}&"")', 8, h)
    add('gpres', 'строка заполнена', 'OR({gkt}<>"",TRIM({garea}&"")<>"")', 5, h)
    add('ggi', 'строка справочника', 'IF({gkt}="",0,IFERROR(MATCH({gkt},GR_Name,0),0))', 5, h)
    add('gcat', 'категория', 'IF({ggi}>0,INDEX(GR_Cat,{ggi}),"")', 8, h)
    add('gtar', 'стоимость, баз.в./м²', 'IF({ggi}>0,INDEX(GR_Val,{ggi}),"")', 5, h)
    add('guseq', 'коэф. состояния есть', 'IF({ggi}>0,INDEX(GR_Q,{ggi})=1,FALSE)', 5, h)
    add('gct', 'состояние текстом', 'TRIM({gcond}&"")', 6, h)
    add('gcs', 'состояние (ведомость)', 'IF(NOT({gpres}),"",IF({guseq},IF({gct}="","хор.",{gct}),IF({gct}="","-",{gct})))', 6, h)
    qk = REF['greensCondK']
    add('gq', 'коэф. состояния', 'IF({guseq},IF({gcs}="хор.",%s,IF({gcs}="уд.",%s,IF({gcs}="плох.",%s,""))),1)' % (qk['хор.'], qk['уд.'], qk['плох.']), 5, h)
    add('gat', 'действие текстом', 'TRIM({gact}&"")', 4, h)
    add('grem', 'удаляется (1)', 'IF(AND({gpres},{gat}="В"),1,0)', 4, h)
    add('gtrn', 'пересаживается (1)', 'IF(AND({gpres},{gat}="П"),1,0)', 4, h)
    add('gkeep', 'сохраняется (1)', 'IF(AND({gpres},{grem}=0,{gtrn}=0),1,0)', 4, h)
    add('gnt', '№ текстом', 'TRIM({gnoin}&"")', 6, h)
    add('gn1', '№ числом', 'IFERROR(IF(AND(VALUE({gnt})>=0,VALUE({gnt})=INT(VALUE({gnt})),VALUE({gnt})&""={gnt}),VALUE({gnt}),""),"")', 5, h)
    add('gpe', 'номер строки выше', 'N({gne:prev})', 5, h)      # ссылка на строку выше — один раз (пересчёт после удаления строк)
    add('gne', 'конец нумерации', 'IF(NOT({gpres}),{gpe},IF(ISNUMBER({gn1}),{gn1},IF({gnt}<>"",{gpe},{gpe}+1)))', 5, h)
    add('garn', 'площадь числом', 'IF(ISNUMBER({garea}),{garea},"")', 6, h)
    add('gerrt', 'ошибка (текст)',
        'IF(NOT({gpres}),"",IF({gkt}="","выберите вид из списка",IF({ggi}=0,"вида нет в списке — выберите из списка",'
        'IF(TRIM({garea}&"")="","укажите площадь, м²",IF(NOT(ISNUMBER({garea})),"площадь — не число",IF({garea}<=0,"площадь должна быть больше 0",'
        'IF(AND({guseq},NOT(ISNUMBER({gq}))),"состояние цветника, газона — хор., уд. или плох.","")))))))', 10, h)
    add('gok', 'без ошибок (1)', 'IF(AND({gpres},{gerrt}=""),1,0)', 4, h)
    add('grep', 'замена по причине', 'IFERROR(INDEX(RS_Repl,MATCH({grsn},RS_Name,0))&"","")', 6, h)
    add('gzero', 'без компенсации', 'ISNUMBER(SEARCH("без*компен?ации",{grep}))', 5, h)
    for i, (on, off) in enumerate([(2, 1), (0.5, 1), (0.5, 1), (0.1, 1)], 1):
        add('gK%d' % i, 'К%d' % i,
            'IF({gzero},0,IF(TRIM({gk%din})="да",%s,IF(TRIM({gk%din})="нет",%s,IF(K%dY,IF(NOT(ISNUMBER(FIND("%d",{grep}))),%s,%s),IF(NOT(ISNUMBER(FIND("%d",{grep}))),%s,%s)))))'
            % (i, on, i, off, i, i, on, off, i, off, on), 4, h)
    add('gmon', 'выплатами (1) / посадками (0)', 'IF(TRIM({gpay}&"")="выплатами",1,IF(TRIM({gpay}&"")="посадками",0,IF(MONEYY,1,0)))', 4, h)
    add('gpl', 'восстановить, м²', 'IF(AND({gok}=1,{grem}=1,{gmon}=0,NOT({gzero})),{garea},0)', 5, h)
    add('gtitle', 'вид (ведомость)', 'IF(NOT({gpres}),"",IF({ggi}>0,INDEX(GR_Name,{ggi}),{gkt})&IF({gnote}&""<>""," ("&{gnote}&")",""))', 10, h)
    add('gC', 'счётчик строк', 'N({gC:prev})+IF({gpres},1,0)', 4, h)
    return C


def build_greens(wb):
    """Лист ввода «Цветники и газоны»: одна ведомость, устроена как «Ведомость» деревьев."""
    ws = wb.create_sheet(SH_G)
    spec = greens_spec()
    L = letters(spec)
    last = G_FIRST + G_ROWS - 1
    ws['A1'] = 'Цветники, газоны, иной травяной покров (ввод) — Таксация Лайт'
    ws['A1'].font = Font(name=FONT, bold=True, size=13, color=GREEN)
    note(ws['A1'], 'Вводите данные в колонки с жёлтым заголовком; наведите мышь на заголовок — появится пояснение. Пустые строки допускаются. '
                   'Лишнюю строку можно удалить целиком или очистить. Готовая ведомость для AutoCAD — на листе «%s».' % SH_GL, 320)
    ws['A2'] = ('="В — удалить, П — пересадить, пусто — сохранить. Правила и стоимость — постановление Совмина № 1426 (Положение о порядке '
                'определения условий осуществления компенсационных мероприятий, п. 9–11, прил. 8). Строк: "&ROWS(R_gkind)&", заполнено: "&GZ_N&"."')
    ws['A2'].font = Font(name=FONT, italic=True, size=9, color='FF555555')
    for i, (name, title, f, w, hidden, tip, arr) in enumerate(spec):
        col = get_column_letter(i + 1)
        c = ws.cell(row=G_HROW, column=i + 1, value=title)
        style_header(c)
        c.fill = PatternFill('solid', fgColor='FFFFD966' if f is None else 'FFC6E0B4')
        if tip:
            note(c, tip, 300)
        ws.column_dimensions[col].width = w
        if hidden:
            ws.column_dimensions[col].hidden = True
            ws.column_dimensions[col].outlineLevel = 1
        if f is not None:
            ref = '%s%d' % (col, G_FIRST)
            ws[ref] = '=' + render(f, L, row=G_FIRST, hrow=G_HROW, first=G_FIRST, sheet=SH_G)
            if not hidden:
                ws[ref].fill = F_CALC
    ws.row_dimensions[G_HROW].height = 44
    ws.freeze_panes = 'C%d' % G_FIRST
    ws.column_dimensions[L['gnoin']].number_format = '@'
    rng = lambda c: '%s%d:%s%d' % (L[c], G_FIRST, L[c], last)
    dv_kind = DataValidation(type='list', formula1='GR_Name', allow_blank=True, showErrorMessage=True,
                             errorTitle='Вид', error='Выберите вид из списка (цветник, газон или иной травяной покров).')
    dv_cond = DataValidation(type='list', formula1='"%s"' % ','.join(REF['greensCondK']), allow_blank=True, showErrorMessage=True,
                             errorTitle='Состояние', error='Для цветников и газонов: хор., уд. или плох. Пусто — хор.')
    dv_act = DataValidation(type='list', formula1='"В,П"', allow_blank=True)
    dv_rs = DataValidation(type='list', formula1='RS_Name', allow_blank=True, showErrorMessage=False)
    dv_yn = DataValidation(type='list', formula1='"да,нет"', allow_blank=True, showErrorMessage=True,
                           errorTitle='К1–К4', error='Выберите «да», «нет» или оставьте пусто (как в настройках).')
    dv_pay = DataValidation(type='list', formula1='"выплатами,посадками"', allow_blank=True, showErrorMessage=True,
                            errorTitle='Компенсация', error='Выберите «выплатами», «посадками» или оставьте пусто (как в настройках).')
    for dv, cols in ((dv_kind, ['gkind']), (dv_cond, ['gcond']), (dv_act, ['gact']), (dv_rs, ['grsn']),
                     (dv_yn, ['gk1in', 'gk2in', 'gk3in', 'gk4in']), (dv_pay, ['gpay'])):
        ws.add_data_validation(dv)
        for c in cols:
            dv.add(rng(c))
    for name, col in L.items():
        defname(wb, 'R_' + name, "'%s'!$%s$%d:$%s$%d" % (SH_G, col, G_FIRST, col, last))
    return ws


def greens_money_f(v):
    """Сумма выплат за цветники и газоны в единицах из настроек; рубли — до копеек."""
    return ('IF(S_UNIT="руб.",ROUND({v}*BASE,2)&" руб.",IF(S_UNIT="баз.в.",ROUND({v},6)&" баз.в.",'
            'ROUND({v},6)&" баз.в. ("&ROUND({v}*BASE,2)&" руб.)"))').format(v=v)


def greens_lines():
    """Итоги под ведомостью цветников и газонов: 5 строк × 6 колонок (№, вид, площадь, состояние, решение, компенсация)."""
    nz = lambda v, t: 'IF(%s=0,"",%s)' % (v, t)
    R = 'GZ_RF+GZ_RL+GZ_RG'
    TT = 'GZ_TF+GZ_TL+GZ_TG'
    KT = 'GZ_KF+GZ_KL+GZ_KG'
    return [
        [nz(R, '"Итого"'), nz('GZ_RF', '"удаляемые цветники:"'), nz('GZ_RF', 'GZ_RF'), nz('GZ_RF', '"м²"'),
         nz('GZ_PF+GZ_PL', '"Посадки:"'), nz('GZ_PF', '"цветник не менее "&ROUND(GZ_PF,6)&" м²"')],
        ['""', nz('GZ_RL', '"удаляемые газоны:"'), nz('GZ_RL', 'GZ_RL'), nz('GZ_RL', '"м²"'), '""',
         nz('GZ_PL', '"газон не менее "&ROUND(GZ_PL,6)&" м²"')],
        ['""', nz('GZ_RG', '"удаляемый иной травяной покров:"'), nz('GZ_RG', 'GZ_RG'), nz('GZ_RG', '"м²"'),
         nz('GZ_NM', '"Выплаты:"'), nz('GZ_NM', greens_money_f('GZ_V'))],
        [nz(TT, '"Итого"'), nz(TT, '"пересаживаемые:"'), nz(TT, TT), nz(TT, '"м²"'), '""', '""'],
        [nz(KT, '"Итого"'), nz(KT, '"сохраняемые:"'), nz(KT, KT), nz(KT, '"м²"'), '""', '""'],
    ]


GREEN_BAL_COL = 8    # H: баланс цветников и газонов справа от ведомости


def build_greens_list(wb):
    """Готовая ведомость цветников и газонов (для AutoCAD) и баланс цветников, газонов, иного травяного покрова."""
    G = lambda nm, txt=True: 'INDEX(R_%s,{I})%s' % (nm, '&""' if txt else '')
    cols = [('Номер по плану', G('gno'), 11, 'Номер по плану.', None),
            ('Вид', G('gtitle'), 34, 'Вид цветника, газона, иного травяного покрова; примечание — в скобках.', None),
            ('Площадь, м²', 'IF(ISNUMBER(INDEX(R_garea,{I})),INDEX(R_garea,{I}),INDEX(R_garea,{I})&"")', 10, 'Площадь, м².', None),
            ('Качественное состояние', G('gcs'), 13, 'хор., уд., плох. — для цветников и газонов (пусто в листе ввода = хор.); иной травяной покров — «-».', None),
            ('Решение', G('gdec'), 20, 'Сохранить, удалить или пересадить (с причиной).', None),
            ('Компенсационные посадки (выплаты)', G('gcomp'), 44,
             'Выплаты — (стоимость) баз.в. × площадь × коэф. состояния × К1 × К2 × К3 × К4 = итог (п. 11 Положения). Посадки — восстановить цветник '
             'или газон на площади не менее удалённой (п. 10 Положения).', None)]
    lines = greens_lines()
    cap = G_ROWS + len(lines) + 2
    err = '=IF(GZ_ERR>0,"Внимание: строк с ошибками — "&GZ_ERR&". Они не вошли в итоги (колонка «Ошибка» на листе «%s»).","")' % SH_G
    ws = build_list(wb, SH_GL, 'Ведомость цветников, газонов, иного травяного покрова', cols, 'gC', 'GZ_N', lines, cap, err,
                    hidden_at=20, src=SH_G, acad='Цветники')
    ws['A2'] = org_line('&" · базовая величина "&BASE&" руб."')
    # баланс справа
    c0 = GREEN_BAL_COL
    side = Side(style='thin', color='FF000000')
    box = Border(side, side, side, side)
    ws.column_dimensions[get_column_letter(c0 - 1)].width = 3
    ws.column_dimensions[get_column_letter(c0)].width = 22
    for j in range(1, 4):
        ws.column_dimensions[get_column_letter(c0 + j)].width = 13
    t = HDR_ROW - 1
    link = ws.cell(row=t - 1, column=c0, value='=HYPERLINK("#\'%s\'!%s%d:%s%d","▶ Баланс для AutoCAD")' % (
        SH_GL, get_column_letter(c0), t, get_column_letter(c0 + 3), HDR_ROW + 4))
    link.font = Font(name=FONT, size=9, color='FF1F5FBF', underline='single')
    defname(wb, 'АКАД_БалансЦветники', "'%s'!$%s$%d:$%s$%d" % (SH_GL, get_column_letter(c0), t, get_column_letter(c0 + 3), HDR_ROW + 4))
    ACAD_NAMES.append(('АКАД_БалансЦветники', SH_GL, 'баланс цветников, газонов, иного травяного покрова', 'fixed'))
    ws.cell(row=t, column=c0, value='Баланс цветников, газонов, иного травяного покрова, м²').font = Font(name=FONT, bold=True)
    for j, hh in enumerate(['Проектные предложения', 'Цветники', 'Газоны', 'Иной травяной покров']):
        c = ws.cell(row=HDR_ROW, column=c0 + j, value=hh)
        style_header(c)
        c.border = box
    rows = [('Сохраняемые', 'K'), ('Пересаживаемые', 'T'), ('Удаляемые', 'R')]
    for i, (label, k) in enumerate(rows):
        ws.cell(row=HDR_ROW + 1 + i, column=c0, value=label)
        for j, cat in enumerate('FLG'):
            ws.cell(row=HDR_ROW + 1 + i, column=c0 + 1 + j, value='=GZ_%s%s' % (k, cat))
    ws.cell(row=HDR_ROW + 4, column=c0, value='Итого')
    for j in range(1, 4):
        L0 = get_column_letter(c0 + j)
        ws.cell(row=HDR_ROW + 4, column=c0 + j, value='=SUM(%s%d:%s%d)' % (L0, HDR_ROW + 1, L0, HDR_ROW + 3))
    for rr in range(HDR_ROW + 1, HDR_ROW + 5):
        for j in range(4):
            c = ws.cell(row=rr, column=c0 + j)
            c.border = box
            c.font = Font(name=FONT, size=9, bold=(rr == HDR_ROW + 4 or j == 0))
            c.alignment = Alignment(horizontal='left' if j == 0 else 'center', vertical='center')
    note(ws.cell(row=t, column=c0), 'Считается по листу «%s»: сохраняемые (действие пусто), пересаживаемые (П), удаляемые (В). '
                                   'Строки с ошибками не входят.' % SH_G, 300)
    return ws, cap


SPLASH_LINES = [
    ('Таксация Лайт — книга с защитой доступа', 'title'),
    ('Если вы видите только этот лист, расчёт закрыт: макросы не включены или нет действующего кода доступа.', 'bold'),
    ('', None),
    ('Как открыть расчёт', 'head'),
    ('1. Если Excel показал жёлтую полосу с предупреждением о макросах — нажмите «Включить содержимое».', None),
    ('2. Если Excel пишет, что макросы заблокированы (файл получен из интернета или по почте), — закройте файл, '
     'щёлкните по нему правой кнопкой мыши → «Свойства», отметьте «Разблокировать», нажмите «ОК» и откройте файл снова.', None),
    ('3. После включения макросов книга спросит код доступа. Код действует 7 дней, его выдаёт владелец программы.', None),
    ('', None),
    ('Владельцу', 'head'),
    ('• При первом открытии с включёнными макросами задайте логин и пароль владельца и запишите их.', None),
    ('• Код на 7 дней для другого человека: Alt+F8 → TK_KodDlyaPeredachi → «Выполнить», затем логин и пароль.', None),
    ('• Копия для передачи (доступ в ней закрыт до ввода кода): Alt+F8 → TK_KopiyaDlyaPeredachi.', None),
    ('• Ввести код или войти владельцем без повторного открытия: Alt+F8 → TK_Vhod.', None),
    ('• Через 7 дней книга снова спросит код: введите свой логин, затем пароль.', None),
    ('', None),
    ('Это защита от обычного пользователя, а не криптографическая: знающий Excel человек может её обойти. '
     'Книга рассчитана на Microsoft Excel для Windows; в других программах защита может не работать.', 'note'),
]


def build_splash(wb):
    ws = wb.create_sheet(SH_ACCESS)
    ws.column_dimensions['A'].width = 3
    ws.column_dimensions['B'].width = 120
    styles = {'title': Font(name=FONT, bold=True, size=15, color=GREEN), 'bold': Font(name=FONT, bold=True, size=11, color='FFB42318'),
              'head': Font(name=FONT, bold=True, size=12), 'note': Font(name=FONT, italic=True, size=10, color='FF555555'),
              None: Font(name=FONT, size=11)}
    for i, (text, st) in enumerate(SPLASH_LINES):
        c = ws.cell(row=2 + i, column=2, value=text or None)
        c.font = styles[st]
        c.alignment = Alignment(wrap_text=True, vertical='top')
    ws.sheet_view.showGridLines = False
    return ws


def add_vba(path, codenames):
    """Упаковать книгу как .xlsm: проект макросов (tools/vba) с модулями ЭтаКнига (ThisWorkbook), листов и TK."""
    import vba_project as VP
    here = os.path.dirname(os.path.abspath(__file__))
    rd = lambda f: open(os.path.join(here, 'vba', f), encoding='utf-8').read()
    mods = [('ThisWorkbook', 'workbook', rd('ThisWorkbook.cls'))] + [(c, 'sheet', '') for c in codenames] + [('TK', 'std', rd('TK.bas'))]
    vba = VP.build_vba_project(mods)
    with zipfile.ZipFile(path) as z:
        names = z.namelist()
        files = {n: z.read(n) for n in names}
    ct = files['[Content_Types].xml'].decode('utf-8')
    main = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml'
    assert ct.count(main) == 1
    ct = ct.replace(main, 'application/vnd.ms-excel.sheet.macroEnabled.main+xml')
    ct = ct.replace('</Types>', '<Override PartName="/xl/vbaProject.bin" ContentType="application/vnd.ms-office.vbaProject"/></Types>')
    files['[Content_Types].xml'] = ct.encode('utf-8')
    rels = files['xl/_rels/workbook.xml.rels'].decode('utf-8')
    rels = rels.replace('</Relationships>', '<Relationship Id="rIdVBA" Type="http://schemas.microsoft.com/office/2006/relationships/vbaProject" '
                                            'Target="vbaProject.bin"/></Relationships>')
    files['xl/_rels/workbook.xml.rels'] = rels.encode('utf-8')
    files['xl/vbaProject.bin'] = vba
    names.append('xl/vbaProject.bin')
    with zipfile.ZipFile(path, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for n in names:
            z.writestr(n, files[n])


def build_acad_summary(wb):
    """Лист «Итоги»: имена диапазонов для связи с AutoCAD и их текущий размер (строк × столбцов)."""
    ws = wb['Итоги']
    r = ws.max_row + 2
    ws.cell(row=r, column=1, value='Таблицы для связи с AutoCAD (команда DATALINK → связь с именованным диапазоном)').font = Font(name=FONT, bold=True)
    note(ws.cell(row=r, column=1), 'Имена «АКАД_…» для обновляемых таблиц в AutoCAD. Ведомости меняют длину сами: шапка, позиции и итоги, '
                                  'без пустых строк. После изменений сохраните книгу и выполните в AutoCAD DATALINKUPDATE.', 320)
    for i, h in enumerate(['имя диапазона', 'строк', 'столбцов', 'лист', 'что внутри']):
        style_header(ws.cell(row=r + 1, column=1 + i, value=h))
    for k, (nm, sheet, what, kind) in enumerate(ACAD_NAMES):
        rr = r + 2 + k
        ws.cell(row=rr, column=1, value=nm)
        ws.cell(row=rr, column=2, value='=ROWS(%s)' % nm)
        ws.cell(row=rr, column=3, value='=COLUMNS(%s)' % nm)
        ws.cell(row=rr, column=4, value=sheet)
        ws.cell(row=rr, column=5, value=what + ('; длина меняется' if kind == 'list' else ''))


def build(nrows=1000, out=None, protect=False):
    """protect=True — вариант с защитой доступа (.xlsm): лист «Доступ», листы с расчётом скрыты до ввода кода."""
    ACAD_NAMES.clear()
    wb = Workbook()
    wb.remove(wb.active)
    build_settings(wb)
    build_ref(wb)
    ws_main, spec, L, last = build_main(wb, nrows)
    build_totals(wb, L)
    caps = build_lists(wb, nrows)
    build_greens(wb)
    ws_gl, gcap = build_greens_list(wb)
    caps[SH_GL] = (gcap, ws_gl._taks_kc)
    build_acad_summary(wb)
    order = ['Настройки', 'Ведомость', 'Существующие', 'Сохраняемые', 'Удаляемые', 'Пересаживаемые', SH_VOL,
             SH_G, SH_GL, 'Итоги', 'Справочник']
    if protect:
        build_splash(wb)
        st = wb.create_sheet(SH_STORE)
        for a in ('A1', 'A2'):
            st[a].number_format = '@'
        order = [SH_ACCESS] + order + [SH_STORE]
    wb._sheets = [wb[n] for n in order]
    codenames = []
    if protect:   # кодовые имена латиницей — на них ссылаются макросы; листы с расчётом скрыты (veryHidden) до ввода кода
        wb.code_name = 'ThisWorkbook'
        for i, ws in enumerate(wb.worksheets):
            ws.sheet_properties.codeName = {SH_ACCESS: 'SheetTK', SH_STORE: 'SheetTKD'}.get(ws.title, 'Sheet%d' % i)
            if ws.title != SH_ACCESS:
                ws.sheet_state = 'veryHidden'
            codenames.append(ws.sheet_properties.codeName)
        wb.active = 0
    for ws in wb.worksheets:
        ws.sheet_view.zoomScale = 100 if ws.title == 'Ведомость' else 90
    from openpyxl.workbook.properties import CalcProperties
    wb.calculation = CalcProperties(fullCalcOnLoad=True)   # Excel пересчитает книгу при открытии
    out = out or out_name(nrows)
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    tmp = out + '.tmp.xlsx'
    wb.save(tmp)
    ranges = {'Ведомость': (FIRST, FIRST + nrows - 1, None), SH_G: (G_FIRST, G_FIRST + G_ROWS - 1, None)}
    for sh, (cap, kc) in caps.items():
        ranges[sh] = (LFIRST, LFIRST + cap - 1, kc)
    expand_shared(tmp, out, ranges)
    os.remove(tmp)
    if protect:
        add_vba(out, codenames)
    return out


CELL_RE = re.compile(r'<c r="([A-Z]+)(\d+)"([^>]*?)(?:/>|>(.*?)</c>)', re.S)


def col_num(c):
    n = 0
    for ch in c:
        n = n * 26 + ord(ch) - 64
    return n


def expand_shared(src, dst, sheets):
    """Растянуть формулы первой строки данных вниз: обычные — как общие (shared, файл остаётся маленьким),
    формулы массива — построчно со сдвигом номера строки. Ячейки, уже записанные ниже (скрытые итоги), сохраняются."""
    with zipfile.ZipFile(src) as z:
        names = z.namelist()
        files = {n: z.read(n) for n in names}
    wbx = files['xl/workbook.xml'].decode('utf-8')
    rels = files['xl/_rels/workbook.xml.rels'].decode('utf-8')
    relmap = dict(re.findall(r'Id="([^"]+)"[^>]*?Target="([^"]+)"', rels)) or {}
    relmap.update({a: b for b, a in re.findall(r'Target="([^"]+)"[^>]*?Id="([^"]+)"', rels)})
    for sname, (r1, r2, maxcol) in sheets.items():
        m = re.search(r'<sheet [^>]*name="%s"[^>]*r:id="([^"]+)"' % re.escape(sname), wbx)
        path = 'xl/' + relmap[m.group(1)].lstrip('/').replace('xl/', '')
        x = files[path].decode('utf-8')
        sd0, sd1 = x.index('<sheetData>') + len('<sheetData>'), x.index('</sheetData>')
        rows = {}
        rattrs = {}
        for rm in re.finditer(r'<row r="(\d+)"([^>]*?)(?:/>|>(.*?)</row>)', x[sd0:sd1], re.S):
            rn = int(rm.group(1))
            rattrs[rn] = re.sub(r'\s*spans="[^"]*"', '', rm.group(2))
            rows[rn] = {cm.group(1): cm.group(0) for cm in CELL_RE.finditer(rm.group(3) or '')}
        master = rows.get(r1, {})
        si = max([int(v) for v in re.findall(r'si="(\d+)"', x)] + [-1]) + 1
        deps = []
        for col in sorted(master, key=col_num):
            if maxcol and col_num(col) > maxcol[0] and col_num(col) != maxcol[1]:
                continue
            cm = CELL_RE.match(master[col])
            attrs, inner = cm.group(3), cm.group(4) or ''
            fa = re.search(r'<f t="array" ref="[A-Z]+\d+"[^>]*>(.*?)</f>', inner, re.S)
            fm = re.search(r'<f>(.*?)</f>', inner, re.S)
            if fa:
                deps.append(('arr', col, attrs, fa.group(1)))
            elif fm:
                master[col] = '<c r="%s%d"%s><f t="shared" ref="%s%d:%s%d" si="%d">%s</f><v></v></c>' % (col, r1, attrs, col, r1, col, r2, si, fm.group(1))
                deps.append(('sh', col, attrs, si))
                si += 1
        shift = re.compile(r'(\$[A-Z]{1,3})%d(?![0-9])' % r1)
        for rr in range(r1 + 1, r2 + 1):
            cur = rows.setdefault(rr, {})
            for kind, col, attrs, v in deps:
                if kind == 'sh':
                    cur[col] = '<c r="%s%d"%s><f t="shared" si="%d"/></c>' % (col, rr, attrs, v)
                else:
                    f = shift.sub(lambda mm: mm.group(1) + str(rr), v)
                    cur[col] = '<c r="%s%d"%s><f t="array" ref="%s%d">%s</f></c>' % (col, rr, attrs, col, rr, f)
        out = []
        for rn in sorted(rows):
            cells = ''.join(rows[rn][c] for c in sorted(rows[rn], key=col_num))
            out.append('<row r="%d"%s>%s</row>' % (rn, rattrs.get(rn, ''), cells))
        x = x[:sd0] + ''.join(out) + x[sd1:]
        x = re.sub(r'<dimension ref="[^"]*"/>', '', x, count=1)
        files[path] = x.encode('utf-8')
    for path in [n for n in names if re.match(r'xl/worksheets/sheet\d+\.xml$', n)]:
        x = files[path].decode('utf-8')
        if '<ignoredErrors' not in x:
            ie = ('<ignoredErrors><ignoredError sqref="A1:XFD1048576" numberStoredAsText="1" formula="1" formulaRange="1" '
                  'emptyCellReference="1" unlockedFormula="1"/></ignoredErrors>')
            pos = min([x.find(t) for t in ('<drawing', '<legacyDrawing', '</worksheet>') if x.find(t) >= 0])
            x = x[:pos] + ie + x[pos:]
            files[path] = x.encode('utf-8')
    # openpyxl пишет русские буквы как «&#1042;» (7 байт вместо 2) — переводим в обычный UTF-8; служебные &#60; и т.п. не трогаем
    nref = re.compile(r'&#(\d+);')
    for n in names:
        if n.endswith('.xml') or n.endswith('.vml'):
            x = files[n].decode('utf-8')
            if '&#' in x:
                x = nref.sub(lambda m: chr(int(m.group(1))) if int(m.group(1)) >= 128 else m.group(0), x)
            if n.endswith('.xml') and not x.startswith('<?xml'):
                x = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n' + x     # кодировка явно, как в файлах Excel
            files[n] = x.encode('utf-8')
    with zipfile.ZipFile(dst, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:   # максимальное сжатие: файл меньше
        for n in names:
            z.writestr(n, files[n])


SIZES = [2000]   # книги на 500, 1000, 5000 и 10000 строк в excel/ — прежняя версия, больше не пересобираются


def out_name(n, protect=False):
    return os.path.join(ROOT, 'excel', ('Таксация-Лайт-%d-защита.xlsm' if protect else 'Таксация-Лайт-%d.xlsx') % n)


if __name__ == '__main__':
    ap = argparse.ArgumentParser(description='Без параметров — книги всех размеров: ' + ', '.join(map(str, SIZES)) + ' строк.')
    ap.add_argument('--rows', type=int, default=None, help='число строк ведомости (одна книга)')
    ap.add_argument('--out', default=None)
    ap.add_argument('--no-protect', action='store_true', help='не собирать вариант с защитой доступа (.xlsm)')
    a = ap.parse_args()
    for n in ([a.rows] if a.rows else SIZES):
        p = build(n, a.out or out_name(n))
        print(p, os.path.getsize(p) // 1024, 'КБ')
        if not a.no_protect and not a.out:
            p = build(n, out_name(n, True), protect=True)
            print(p, os.path.getsize(p) // 1024, 'КБ')
