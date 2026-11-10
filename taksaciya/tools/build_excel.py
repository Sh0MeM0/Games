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
FIRST = 5            # первая строка данных ведомости
LFIRST = 7           # первая строка перечня на листах-ведомостях (Существующие, Удаляемые …)
AUTHOR = 'Таксация Лайт'
NSTEM = 27           # стволов в одной строке (как в оригинале)
GREEN = '2C6A4C'
F_IN = PatternFill('solid', fgColor='FFFFFF')
F_CALC = PatternFill('solid', fgColor='EEF3EE')
F_HEAD = PatternFill('solid', fgColor='DCE8DF')
F_SET = PatternFill('solid', fgColor='FFF7D6')
B_THIN = Border(*(Side(style='thin', color='C9D3CA'),) * 4)
FONT = 'Arial'


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
    c.alignment = Alignment(wrap_text=True, vertical='center')
    c.border = B_THIN


# =============================================================== Справочник
def build_ref(wb):
    ws = wb.create_sheet('Справочник')
    ws['A1'] = 'Справочники Таксации Лайт (перенесены из файла ТАКСАЦИЯ-АВТО 4.2). Можно дополнять: свои породы — в пустые строки библиотеки, базовые величины — в календарь.'
    ws['A1'].font = Font(name=FONT, bold=True)
    # --- библиотека пород: служебные строки + библиотека + 300 пустых строк для своих пород
    hdr = ['Вид (порода)', 'Тип породы', 'Куст (1)', 'Твёрдость', 'Инвазивный (1)', 'Ø кома при h<1м', 'h кома при h<1м', 'В библиотеке (1)', 'Служебная (1)', '2-е название, латынь']
    for i, h in enumerate(hdr):
        style_header(ws.cell(row=4, column=1 + i, value=h))
    r = 5
    lib_names = {s['name'].lower() for s in REF['species']}
    for s in REF['special']:
        vals = [s['name'], s['type'], 1 if s['shrub'] else 0, 'мяг.', 0, s.get('ballD'), s.get('ballH'),
                1 if s['name'].lower() in lib_names else 0, 1, '']
        for i, v in enumerate(vals):
            ws.cell(row=r, column=1 + i, value=v)
        r += 1
    for s in REF['species']:
        vals = [s['name'], s['type'], 1 if s['shrub'] else 0, s['hard'] or None, 1 if s['invasive'] else 0, s.get('ballD'), s.get('ballH'), 1, 0, s.get('alt') or None]
        for i, v in enumerate(vals):
            ws.cell(row=r, column=1 + i, value=v)
        r += 1
    lib_last = r + 300 - 1
    for rr in range(r, lib_last + 1):
        for c in range(1, 11):
            ws.cell(row=rr, column=c).fill = F_SET
    note(ws.cell(row=r, column=1), 'Свои породы вписывайте сюда, подряд, без пустых строк: название, тип (хвой., медл., быст., плод., малоц. — деревья; '
         'лиан., хвой.., вечз., крас., медл.., быст.., плод.. — кусты), куст = 1, твёрдость (мяг./хвн./твд.), в библиотеке = 1. '
         'После этого порода появится в списке на листе «Ведомость».', 300)
    for name, col in [('Lib_Name', 'A'), ('Lib_Type', 'B'), ('Lib_Shrub', 'C'), ('Lib_Hard', 'D'), ('Lib_Inv', 'E'),
                      ('Lib_BallD', 'F'), ('Lib_BallH', 'G'), ('Lib_InLib', 'H'), ('Lib_Special', 'I')]:
        defname(wb, name, "'Справочник'!$%s$5:$%s$%d" % (col, col, lib_last))
    # список для выбора породы: только заполненные строки (иначе Excel открывает список на первой пустой строке — «с середины»)
    defname(wb, 'SPL', "'Справочник'!$A$5:INDEX('Справочник'!$A$5:$A$%d,MAX(1,COUNTA('Справочник'!$A$5:$A$%d)))" % (lib_last, lib_last))
    # список для поиска породы по началу любого слова (колонки AB:AC, скрыты): ключ — название с этого слова до конца,
    # значение — полное название; отсортирован по ключу, поэтому подходящие ключи идут подряд
    style_header(ws.cell(row=4, column=28, value='ключ поиска'))
    style_header(ws.cell(row=4, column=29, value='порода'))
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
    kl = 4 + len(keys)
    defname(wb, 'SPK', "'Справочник'!$AB$5:$AB$%d" % kl)
    defname(wb, 'SPV1', "'Справочник'!$AC$5")
    ws.column_dimensions['AB'].hidden = True
    ws.column_dimensions['AC'].hidden = True
    for i, t in enumerate(['Название породы (вида). Отсюда берётся список выбора на листе «Ведомость».',
                           'Тип породы для тарифа и норм посадок: хвой., медл., быст., плод., малоц. — деревья; лиан., хвой.., вечз., крас., медл.., быст.., плод.. — кустарники; ж.и.1/ж.и.2 — живая изгородь.',
                           '1 — кустарник, 0 — дерево.', 'Твёрдость древесины для объёмов: мяг. — мягкая, хвн. — хвойная, твд. — твёрдая.',
                           '1 — инвазивный вид: компенсация не начисляется.', 'Диаметр кома, м, для пересадки растения высотой до 1 м.',
                           'Высота кома, м, для пересадки растения высотой до 1 м.', '1 — вид из библиотеки оригинала (для него считается размер кома).',
                           '1 — служебная строка (поросль, живая изгородь).', 'Второе название или латинское.']):
        note(ws.cell(row=4, column=1 + i), t)
    ws.column_dimensions['A'].width = 34
    ws.column_dimensions['J'].width = 30
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
    row = put_table(row, c0, 'Цветники и травяной покров, баз.вел./м²', ['Вид', 'баз.вел.'], [[k, v] for k, v in REF['flowers'].items()], ['FL_Kind', 'FL_Val'])
    row = put_table(row, c0, 'Газоны, баз.вел./м²', ['Вид', 'баз.вел.'], [[k, v] for k, v in REF['lawns'].items()], ['LW_Kind', 'LW_Val'])
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
        ('BASE', 'Базовая величина для расчёта, руб.', '=IF(S_BVOVR<>"",S_BVOVR,IFERROR(INDEX(BV_Rub,MATCH(SUMPRODUCT(MAX((BV_From<=S_DATE)*BV_From)),BV_From,0)),0))', 'calc',
         'Считается сама: своя величина, если указана, иначе — из календаря по дате расчёта.'),
        ('BASE_ACT', 'Действует по календарю с / постановление', '=IFERROR(TEXT(SUMPRODUCT(MAX((BV_From<=S_DATE)*BV_From)),"DD.MM.YYYY")&" — "&INDEX(BV_Act,MATCH(SUMPRODUCT(MAX((BV_From<=S_DATE)*BV_From)),BV_From,0)),"")', 'calc',
         'Строка календаря, из которой взята базовая величина.'),
        (None, None, None, None, None),
    ]
    for (nm, short, rule), dflt in zip(K_INFO, ['нет' if REF['defaults']['K1'] == 'нет' else 'да', REF['defaults']['K2'], REF['defaults']['K3'], REF['defaults']['K4']]):
        rows.append(('S_K' + nm[1], '%s — «%s?» (%s)' % (nm, short, rule.split(',')[0]), dflt, 'yn',
                     'Ответ для всего объекта: %s. Для отдельных деревьев можно ответить иначе в колонке «%s» листа «Ведомость».' % (rule, nm)))
    rows += [
        ('S_MONEY', 'Компенсация', 'выплатами', 'money', 'выплатами — сумма в базовых величинах; посадками — число саженцев взамен.'),
        ('S_UNIT', 'Единицы выплат', REF['defaults']['unit'], 'unit', 'баз.в. — в базовых величинах; руб. — в рублях; баз.в. (руб.) — оба значения.'),
        ('S_VIEW', 'Графа «компенсация»', REF['defaults']['formulaView'], 'view', 'формула + итог — расчёт по шагам; итог — только тариф/итог.'),
        ('S_COND', 'Состояние по умолчанию', REF['defaults']['defaultCondition'], 'cond', 'Используется, если в строке ведомости состояние не указано.'),
        ('S_MASS', 'Масса куста при вырубке, кг', REF['defaults']['shrubMassKg'], None, 'Для расчёта массы кустарника (лист «Древесина»).'),
        ('S_FMODE', 'Учёт проектируемых цветников и газонов', REF['defaults']['flowerOffset'], 'fmode',
         'метрами — проектируемая площадь уменьшает оплачиваемую; стоимостью — уменьшает сумму.'),
        ('S_PLOTS', 'Показывать поросль в балансе', 'да', 'yn', 'да — в балансе «шт. + м²».'),
        ('S_HEDGE', 'Живую изгородь также штуками', 'да', 'yn', 'да — в балансе «п.м. / шт.» (1-р. × 3, 2-р. × 5).'),
        ('S_PLOTTREE', 'Поросль деревьев компенсировать типом', REF['compMap']['plotTree'], 'ptree', 'В какую группу деревьев включается поросль деревьев в итогах и в посадках.'),
        ('S_PLOTSHRUB', 'Поросль кустарников компенсировать типом', REF['compMap']['plotShrub'], 'pshrub', 'В какую группу кустарников включается поросль кустарников.'),
    ]
    dvs = {
        'yn': DataValidation(type='list', formula1='"да,нет"', allow_blank=False),
        'money': DataValidation(type='list', formula1='"выплатами,посадками"'),
        'unit': DataValidation(type='list', formula1='"баз.в.,баз.в. (руб.),руб."'),
        'view': DataValidation(type='list', formula1='"формула + итог,итог"'),
        'cond': DataValidation(type='list', formula1='"хор.,уд.,плох.,ненадл."'),
        'fmode': DataValidation(type='list', formula1='"метрами,стоимостью"'),
        'ptree': DataValidation(type='list', formula1='"хвой.,медл.,быст."'),
        'pshrub': DataValidation(type='list', formula1='"лиан.,хвой..,вечз.,крас.,медл..,быст..,плод..,ж.и.1,ж.и.2"'),
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
    flags = [('K1Y', '=S_K1<>"нет"'), ('K2Y', '=S_K2="да"'), ('K3Y', '=S_K3="да"'), ('K4Y', '=S_K4="да"'), ('MONEYY', '=S_MONEY<>"посадками"')]
    ws.cell(row=r + 1, column=2, value='Служебные флаги (не изменять)').font = Font(name=FONT, italic=True, color='888888')
    for i, (nm, f) in enumerate(flags):
        ws.cell(row=r + 2 + i, column=2, value=nm).font = Font(name=FONT, color='888888')
        ws.cell(row=r + 2 + i, column=3, value=f).font = Font(name=FONT, color='888888')
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
    note(c, 'Каким типом саженцев компенсировать удаляемый тип в режиме «посадками». Пусто — тем же типом.')
    cm0 = r + 2
    types = ['хвой.', 'медл.', 'быст.', 'плод.', 'малоц.', 'лиан.', 'хвой..', 'вечз.', 'крас.', 'медл..', 'быст..', 'плод..', 'ж.и.1', 'ж.и.2']
    dv = DataValidation(type='list', formula1='"' + ','.join(types) + '"', allow_blank=True)
    ws.add_data_validation(dv)
    for i, t in enumerate(types):
        ws.cell(row=cm0 + i, column=2, value=t)
        c = ws.cell(row=cm0 + i, column=3, value=REF['compMap'].get(t) or None)
        c.fill = F_SET
        c.border = B_THIN
        dv.add(c)
    defname(wb, 'CM_Type', "'Настройки'!$B$%d:$B$%d" % (cm0, cm0 + len(types) - 1))
    defname(wb, 'CM_Map', "'Настройки'!$C$%d:$C$%d" % (cm0, cm0 + len(types) - 1))
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
    add('sp', 'Порода, вид', None, 36,
        tip='Порода из списка (стрелка справа от ячейки). Список начинается с начала и берётся с листа «Справочник».\n'
            'Быстрый поиск: наберите начало любого слова названия (например «берез» или «повисл»), нажмите Enter и снова откройте список '
            '(стрелка ▼ справа от ячейки или Alt+↓) — останутся только подходящие породы. Листать список — стрелками ↑↓ на клавиатуре. '
            'В Microsoft 365 подсказки появляются сразу при наборе.\n'
            'Служебные строки: «Уч-к поросли дер.», «Уч-к поросли куст.», «*Уч-к поросли дер. у дорог», «*Уч-к поросли куст. у дорог», «Однорядная живая изгородь», «Двухрядная живая изгородь».')
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
    add('note', 'Примечание', None, 14, tip='Попадает в наименование в скобках. Запись вида «V=50%» уменьшает массу кустарника.')
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
    add('G', 'Компенсационные посадки (выплаты)', 'IF({rem}<>1,IF({trn}=1,"ком: "&{ball},""),IF(AND(ISNUMBER({Y}),{Y}=0),"Без компенсации",IF({Y}="ОШИБКА",{err},IF(S_VIEW="итог",IF(MONEYY,"("&{AR}&") баз.в.",{Z}),IF(MONEYY,"("&{AS}&") баз.в. "&IF({isPlot}," х"&{qty}&"м² ",IF({qty}>1," х"&{qty}&IF(OR({J}=16,{J}=17),"п.м. ","шт. "),""))&"х"&{Nc}&"х"&{K1}&"х"&{K2}&"х"&{K3}&"х"&{K4}&" ="&{Y},{mx}&" "&IF({mpos}>0,INDEX(LBL_G,{mpos}),"")&IF(AND({AQ}<>"",N({AQ})>=2),"х"&{AQ}&"ств.","")&"х"&{Nc}&"х"&{K1}&"х"&{K2}&"х"&{K3}&"х"&{K4}&" ="&{Y})))))', 40,
        tip='Расчёт строки: (тариф) баз.в. × кол-во (шт., м² или п.м.) × коэф. состояния × К1 × К2 × К3 × К4 = итог. В режиме «посадками» — число саженцев. Для пересадки — размер кома.')
    add('Y', 'Итог', 'IF({rem}<>1,"",IF({inv},0,IF(OR(NOT({coefOK}),{Nc}=""),"ОШИБКА",IF(MONEYY,IF(ISNUMBER({AR}),ROUND({AR}*IF({isPlot},{qty},IF({qty}>1,{qty},1))*{Nc}*{K1}*{K2}*{K3}*{K4},3),"ОШИБКА"),IFERROR(ROUND({mx}*IF(LEN({AO})<2,1,{AQ})*{Nc}*{K1}*{K2}*{K3}*{K4},3),"ОШИБКА")))))', 9,
        tip='Итог строки: базовых величин (выплаты) или штук саженцев (посадки). Поросль считается по каждой строке: площадь × норма × коэффициенты.')
    add('err', 'Ошибка', 'IF({sp}="","",IF(LEFT({Htxt},6)="ОШИБКА",{Htxt},IF({qty}="ERR","ОШИБКА: количество не распознано",IF({nmis},"ОШИБКА: кол-во не совпадает с номерами",IF(NOT({found}),IF(COUNTIF(SPK,{sp}&"*")>0,"ВЫБЕРИТЕ породу: стрелка ▼ в ячейке «Порода» — подходит "&COUNTIF(SPK,{sp}&"*"),"ОШИБКА: породы нет в справочнике"),IF(AND({rem}=1,{Nc}=""),"ОШИБКА: неизвестное состояние",IF(AND({rem}=1,{Y}="ОШИБКА"),"ОШИБКА: нет тарифа","")))))))', 22,
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
    add('libi', 'строка справочника', 'IF({sp}="",0,IFERROR(MATCH({sp},Lib_Name,0),0))', 6, h)
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
        add('K%d' % i, 'К%d' % i, 'IF({zero},0,IF({k%din}="да",%s,IF({k%din}="нет",%s,IF(K%dY,IF(%s,%s,%s),IF(%s,%s,%s)))))' % (i, on, i, off, i, same, on, off, same, off, on), 5, h)
    add('coefOK', 'коэф. есть', 'AND({act}="В",{found})', 5, h)
    add('Es', 'диаметр без пробелов', 'SUBSTITUTE({dtxt}," ","")', 8, h)
    add('J', 'код ТКП', 'IF({rem}<>1,"",IF(NOT({shrubRow}),IF(ISNUMBER(SEARCH("уч-к поросли дер.",{sp})),IF({qty}>9.99,60,70),IF(OR({dnum},ISERROR(FIND("до1",{Es}))),IF({N}="хвой.",10,IF({N}="медл.",20,IF(OR({N}="быст.",{N}="плод."),30,IF({N}="малоц.",IF(AND(ISERROR(FIND("до4",{Es})),IF({dnum},{d},IFERROR(VALUE(SUBSTITUTE(SUBSTITUTE(SUBSTITUTE({Es},"1,",""),"2,",""),"3,","")),4))>3.99),40,IF(ISNUMBER(FIND("**до4",{Es})),50,1)),1)))),IF(AND(OR({N}="хвой.",{N}="медл.",{N}="быст.",{N}="плод."),ISNUMBER(FIND("**до1",{Es}))),50,1))),IF(ISNUMBER(SEARCH("уч-к поросли куст.",{sp})),IF({qty}>9.99,18,19),IF(ISNUMBER(SEARCH("однорядная живая изгородь",{sp})),16,IF(ISNUMBER(SEARCH("двухрядная живая изгородь",{sp})),17,IF({N}="лиан.",11,IF(OR({N}="хвой..",{N}="вечз."),12,IF({N}="крас.",13,IF({N}="медл..",14,IF(OR({N}="быст..",{N}="плод.."),15,1))))))))))', 5, h)
    add('isPlot', 'поросль', 'OR({J}=60,{J}=70,{J}=18,{J}=19)', 5, h)
    add('pT', 'поросль деревьев (1)', 'IF(OR({J}=60,{J}=70),1,0)', 4, h)
    add('pS', 'поросль кустарников (1)', 'IF(OR({J}=18,{J}=19),1,0)', 4, h)
    add('plot', 'поросль (1)', 'IF({isPlot},1,0)', 4, h)
    add('Fx', 'состояние для расчёта', 'IF({isPlot},"-",IF({cond}="",S_COND,{cond}))', 6, h)
    add('Nc', 'коэф. состояния', 'IF(OR(ISNUMBER(SEARCH("~*уч-к поросли дер. у дорог",{sp})),ISNUMBER(SEARCH("~*уч-к поросли куст. у дорог",{sp}))),0.1,IF(OR({Fx}="хор.",{Fx}="-"),1,IF({Fx}="уд.",0.75,IF({Fx}="плох.",0.5,IF({Fx}="ненадл.",0.25,"")))))', 5, h)
    add('cmap', 'компенсировать типом', 'IFERROR(INDEX(CM_Map,MATCH({N},CM_Type,0))&"","")', 6, h)
    add('M', 'тип для посадок', 'IF({rem}<>1,"",IF({J}=1,{N},IF(OR({J}=60,{J}=70),S_PLOTTREE&"",IF(OR({J}=18,{J}=19),S_PLOTSHRUB&"",IF({cmap}<>"",{cmap},IF(OR({N}="крас.",{N}="лиан."),IF({shrubRow},{N},"медл."),{N}))))))', 6, h)
    add('AO', 'свой тип дерева', 'IF(AND({rem}=1,NOT({shrubRow}),ISERROR(SEARCH("ч-к поросли",{sp}))),{N},"")', 6, h)
    add('Hn', 'высота числом', 'IFERROR({h}+0,0)', 5, h)
    add('segs', 'стволы (служебно)', 'SUBSTITUTE(SUBSTITUTE({Es},"*",""),",",REPT(" ",200))', 6, h)
    for k in range(1, NSTEM + 1):
        seg = 'TRIM(MID({segs},%d,200))' % ((k - 1) * 200 + 1)
        first = '{d}' if k == 1 else '""'
        add('d%d' % k, 'ствол %d' % k, 'IF({rem}<>1,"",IF({dnum},%s,IF(%s="","",IF(RIGHT(%s,3)="до1",0.8,IF(RIGHT(%s,3)="до4",3.8,IFERROR(VALUE(RIGHT(SUBSTITUTE(%s,"-","  "),3)),""))))))' % (first, seg, seg, seg, seg), 5, h)
    add('tc', 'колонка тарифа', 'IFERROR(MATCH({J},{10,20,30,40},0),"")', 4, h)
    for k in range(1, NSTEM + 1):
        add('t%d' % k, 'тариф %d' % k, 'IF(N({d%d})>=12,IFERROR(LOOKUP({d%d},INDEX(TarThr,0,{tc}),INDEX(TarVal,0,{tc})),0),0)' % (k, k), 4, h)
    add('AQ', 'стволов ≥12', 'IF(OR({rem}<>1,{shrubRow}),"",MAX(1,COUNTIF({d1}:{d%d},">"&11.9)))' % NSTEM, 5, h)
    add('AR', 'тариф, б.в.', 'IF({rem}<>1,"",IF({J}=1,0,IF(AND({AQ}<>"",N({AQ})>1),SUM({t1}:{t%d}),IF({J}=60,P_TP,IF({J}=70,P_TPS,IF({J}=16,P_H1,IF({J}=17,P_H2,IF({J}=18,P_SP,IF({J}=19,P_SPS,IF({J}=50,IFERROR(INDEX(Planted_Val,MATCH({N},Planted_Type,0)),""),IF(AND({J}>=11,{J}<=15),LOOKUP({Hn},INDEX(ShrThr,0,{J}-10),INDEX(ShrVal,0,{J}-10)),IFERROR(LOOKUP(MAX({d1}:{d%d}),INDEX(TarThr,0,{tc}),INDEX(TarVal,0,{tc})),""))))))))))))' % (NSTEM, NSTEM), 5, h)
    terms = '&'.join('IF(N({d%d})>=12,"+"&{t%d},"")' % (k, k) for k in range(1, NSTEM + 1))
    add('AS', 'тариф текстом', 'IF({rem}<>1,"",IF({J}=1,"0",IF(AND({AQ}<>"",N({AQ})>1),MID(%s,2,999),{AR}&"")))' % terms, 8, h)
    pt = [('c0', 'хвой.', 'PT_X', 'tree'), ('c1', 'медл.', 'PT_M', 'tree'), ('c2', 'быст.', 'PT_B', 'treeB'),
          ('c3', 'лиан.', 'PS_1', 'shrub'), ('c4', ('хвой..', 'вечз.'), 'PS_2', 'shrub'), ('c5', 'крас.', 'PS_3', 'shrub'),
          ('c6', 'медл..', 'PS_4', 'shrub'), ('c7', ('быст..', 'плод..'), 'PS_5', 'shrubB'), ('c8', 'ж.и.1', 'PS_6', 'shrub'), ('c9', 'ж.и.2', 'PS_7', 'shrub')]
    for name, typ, col, kind in pt:
        cond = ('OR(%s)' % ','.join('{M}="%s"' % t for t in typ)) if isinstance(typ, tuple) else '{M}="%s"' % typ
        if kind == 'tree':
            f = 'IF(OR({rem}<>1,{shrubRow}),"",IF(%s,IF({J}=1,0,{qty}*INDEX(%s,MATCH({J},PT_J,0))),""))' % (cond, col)
        elif kind == 'treeB':
            f = 'IF(OR({rem}<>1,{shrubRow}),"",IF(%s,IF({J}=1,0,IF({J}=70,1,{qty})*INDEX(%s,MATCH({J},PT_J,0))),0))' % (cond, col)
        elif kind == 'shrub':
            f = 'IF(OR({rem}<>1,NOT({shrubRow})),"",IF(%s,IF({J}=1,0,{qty}*INDEX(%s,MATCH({J},PS_J,0))),""))' % (cond, col)
        else:
            f = 'IF(OR({rem}<>1,NOT({shrubRow})),"",IF(%s,IF({J}=1,0,IF({J}=19,1,{qty})*INDEX(%s,MATCH({J},PS_J,0))),""))' % (cond, col)
        add(name, 'посадки ' + (typ if isinstance(typ, str) else '/'.join(typ)), f, 5, h)
    add('mx', 'посадок, шт.', 'IF({rem}<>1,"",MAX({c0}:{c9}))', 5, h)
    add('mpos', 'колонка посадок', 'IF({rem}<>1,"",IFERROR(MATCH({mx},{c0}:{c9},0),0))', 4, h)
    add('Z', 'метка итогов', 'IF({rem}<>1,"",IF(N({mpos})>0,INDEX(LBL_Z,{mpos}),"")&{Y})', 10, h)
    add('Kq', 'деревья, шт.', 'IF(AND({rem}=1,NOT({shrubRow})),{qty},"")', 5, h)
    add('Lq', 'кусты, шт.', 'IF(AND({rem}=1,{shrubRow}),{qty},"")', 5, h)
    # древесина: объёмы по таблицам «Мягк/Хвой/Твер» — одна формула массива на строку вместо 54 колонок
    add('CE', 'класс твёрдости', 'IF(OR({rem}<>1,{shrubRow}),0,IF({special},1,IFERROR(MATCH({hard},{"мяг.","хвн.","твд."},0),-1)))', 4, h)
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
    add('BV', 'кустарник, т', 'IF({rem}<>1,"",IF(OR(AND({CA}>0.1,{CA}<8),AND({shrubRow},{qty}=1)),IF({Hn}>1,{Hn}+({Hn}-1)/3,{Hn})*S_MASS*{pct}*{qty}/1000,""))', 6, h)
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
    return C


def letters(spec):
    return {name: get_column_letter(i + 1) for i, (name, *_rest) in enumerate(spec)}


def render(tpl, L, row=FIRST, last=None):
    """{x} → $X5; {x:prev} → $X4; {x:top} → $X$5; {x:R} → $X$5:$X$last."""
    def rep(m):
        name, mod = m.group(1), m.group(2)
        if name not in L:
            return m.group(0)
        col = L[name]
        if mod == ':prev':
            return '$%s%d' % (col, row - 1)
        if mod == ':top':
            return '$%s$%d' % (col, FIRST)
        if mod == ':R':
            return "'Ведомость'!$%s$%d:$%s$%d" % (col, FIRST, col, last)
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
    ws['A2'] = ('Вводите данные в жёлтые колонки %s–%s. Наведите мышь на заголовок колонки — появится пояснение. Пустые строки допускаются. '
                'Чтобы убрать дерево, проще очистить его строку (выделить и нажать Delete), чем удалять строку целиком.' % (L[inputs[0]], L[inputs[-1]]))
    ws['A2'].alignment = Alignment(wrap_text=True, vertical='top')
    ws.merge_cells('A2:T2')
    ws.row_dimensions[2].height = 30
    ws['A3'] = ('="Строк в книге: "&ROWS(\'Ведомость\'!$%s$%d:$%s$%d)&", заполнено: "&COUNTA(\'Ведомость\'!$%s$%d:$%s$%d)&". '
                'Не хватает строк — возьмите книгу большего размера и перенесите колонки %s–%s. Служебные колонки справа скрыты (группа «+» над заголовком)."'
                % (L['sp'], FIRST, L['sp'], last, L['sp'], FIRST, L['sp'], last, L[inputs[0]], L[inputs[-1]]))
    ws['A3'].font = Font(name=FONT, italic=True, size=9, color='666666')
    for i, (name, title, f, w, hidden, tip, arr) in enumerate(spec):
        col = get_column_letter(i + 1)
        c = ws.cell(row=4, column=i + 1, value=title)
        style_header(c)
        if f is None:
            c.fill = PatternFill('solid', fgColor='FFE9A8')
        if tip:
            note(c, tip, 300)
        ws.column_dimensions[col].width = w
        if hidden:
            ws.column_dimensions[col].hidden = True
            ws.column_dimensions[col].outlineLevel = 1
        if f is not None:
            ref = '%s%d' % (col, FIRST)
            ws[ref] = ArrayFormula(ref, '=' + render(f, L)) if arr else '=' + render(f, L)
            ws[ref].fill = F_CALC
    ws.row_dimensions[4].height = 44
    ws.freeze_panes = '%s5' % get_column_letter(spec.index(next(x for x in spec if x[0] == 'sp')) + 2)
    ws.column_dimensions[L['noin']].number_format = '@'     # «4-10» остаётся текстом, а не датой
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
    for dv, cols in ((dv_cond, ['cond']), (dv_act, ['act']), (dv_sp, ['sp']), (dv_rs, ['rsn']), (dv_yn, ['k1in', 'k2in', 'k3in', 'k4in'])):
        ws.add_data_validation(dv)
        for c in cols:
            dv.add(rng(c))
    for name, col in L.items():
        defname(wb, 'R_' + name, "'Ведомость'!$%s$%d:$%s$%d" % (col, FIRST, col, last))
    return ws, spec, L, last


# =============================================================== ведомости: сначала перечень, под ним итоги
HDR_ROW = LFIRST - 1


def org_line(extra=''):
    return '=S_ORG&IF(S_NO<>""," · "&S_NO,"")&IF(S_NAME<>""," · "&S_NAME,"")&IF(S_STAGE<>""," · стадия "&S_STAGE,"")' + extra


def build_list(wb, sheet, title, cols, counter, nname, totals, cap, top_note=None):
    """Лист-ведомость. cols: [(заголовок, выражение через {I} — номер строки «Ведомости», ширина, примечание, формат)].
    Строки перечня идут подряд с LFIRST; сразу под последней позицией — пустая строка и строки итогов (totals: список строк
    по len(cols) формул без «=» или None). Итоги считаются в скрытых колонках справа."""
    ws = wb.create_sheet(sheet)
    nc = len(cols)
    T = len(totals)
    ws['A1'] = title
    ws['A1'].font = Font(name=FONT, bold=True, size=13)
    ws['A2'] = org_line()
    over = ('IF(%s+1+%d>%d,"Перечень не помещается (в нём %d строк). Возьмите книгу большего размера и перенесите туда колонки ввода «Ведомости». ","")'
            % (nname, T, cap, cap))
    ws['A3'] = '=' + over + ('&' + top_note[1:] if top_note else '')
    ws['A3'].font = Font(name=FONT, color='B42318', bold=True)
    last_col = get_column_letter(nc)
    ws['A4'] = ('=HYPERLINK("#\'{s}\'!A{h}:{c}"&({h}+{n}+1+{t}),"▶ Выделить таблицу для AutoCAD (затем Ctrl+C; в AutoCAD: Правка → Специальная вставка → Объекты AutoCAD)")'
                .format(s=sheet, h=HDR_ROW, c=last_col, n=nname, t=T))
    ws['A4'].font = Font(name=FONT, color='1F5FBF', underline='single')
    note(ws['A4'], 'Щелчок выделяет перечень вместе с итогами. Скопируйте (Ctrl+C), в AutoCAD выполните команду PASTESPEC '
                   '(«Правка → Специальная вставка») и выберите «Объекты AutoCAD» — получится таблица, которую можно править прямо в чертеже.', 300)
    for i, (h, expr, w, tip, fmt) in enumerate(cols):
        c = ws.cell(row=HDR_ROW, column=i + 1, value=h)
        style_header(c)
        if tip:
            note(c, tip)
        ws.column_dimensions[get_column_letter(i + 1)].width = w
    ws.row_dimensions[HDR_ROW].height = 40
    kc = nc + 2                        # скрытая колонка: номер строки «Ведомости»
    t0 = nc + 4                        # скрытый блок итогов
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
    ws.cell(row=r, column=kc, value='=IF(%s>%s,"",IFERROR(MATCH(%s-1,R_%s,1),0)+1)' % (k, nname, k, counter))
    j = '(%s-%s-1)' % (k, nname)
    for i, (h, expr, w, tip, fmt) in enumerate(cols):
        item = expr.replace('{I}', '$%s%d' % (K, r))
        c = ws.cell(row=r, column=i + 1, value='=IF($%s%d<>"",%s,IF(AND(%s>=1,%s<=%d),INDEX(%s,%s,%d),""))' % (K, r, item, j, j, T, tb, j, i + 1))
        if fmt:
            c.number_format = fmt
        c.font = Font(name=FONT, size=9)
        c.alignment = Alignment(vertical='top', wrap_text=(w >= 30))
    rng = 'A%d:%s%d' % (LFIRST, last_col, LFIRST + cap - 1)
    side = Side(style='thin', color='9AA79C')
    ws.conditional_formatting.add(rng, FormulaRule(formula=['ROWS($A$%d:$A%d)<=%s' % (LFIRST, LFIRST, nname)], border=Border(side, side, side, side)))
    ws.conditional_formatting.add(rng, FormulaRule(formula=['AND(ROWS($A$%d:$A%d)>%s+1,ROWS($A$%d:$A%d)<=%s+1+%d)' % (LFIRST, LFIRST, nname, LFIRST, LFIRST, nname, T)],
                                                   font=Font(bold=True), fill=PatternFill('solid', fgColor='EEF3EE', bgColor='EEF3EE')))
    ws.freeze_panes = 'A%d' % LFIRST
    ws._taks_kc = kc
    return ws


IDX = lambda name, txt=True: 'INDEX(R_%s,{I})%s' % (name, '&""' if txt else '')
C_NO = ('Номер по плану', IDX('no'), 11, 'Номер дерева (куста) по плану.', None)
C_TITLE = ('Порода, вид', IDX('title'), 32, 'Порода и вид; число стволов и примечание — в скобках.', None)
C_QTY = ('Кол-во, шт. (м², п.м.)', IDX('qty', False), 9, 'Количество: шт.; для поросли — м², для живой изгороди — п.м. Пустое «Кол-во» в ведомости = 1 шт.', None)
C_H = ('Высота, м', IDX('hRep', False), 8, 'Высота, м.', None)
C_DT = ('Диаметр ствола, см', IDX('dtxt'), 14, 'Диаметр на высоте 1,3 м; многоствольные — через запятую; «-» — кустарник.', None)
C_COND = ('Качественное состояние', IDX('exCond'), 21, 'хор., уд., плох., ненадл.', None)


def removal_lines():
    """Итоговый блок ведомости удаляемых (УДд!A2037:H2049 оригинала): 13 строк × 8 колонок."""
    P = 'NOT(MONEYY)'
    rows = [['"-------"', '"-- Удаление --"', '"-------"', '"-------"', '"-------"', 'IF(%s,"--- Компенсация ","-------")' % P,
             'IF(%s,"--------------","Компенсация  выплатами -----")' % P, '"--------------"']]

    def blk(A, B, Cc, D, E, F, G, H):
        return ['IF((%s)="","",%s)' % (Cc, A), 'IF((%s)="","",%s)' % (Cc, B), Cc, 'IF((%s)="","",%s)' % (Cc, D),
                'IF((%s)="","",%s)' % (G, E), 'IF((%s)="","",%s)' % (G, F), G, 'IF((%s)="","",%s)' % (G, H)]
    nz = lambda v: 'IF(%s=0,"",%s)' % (v, v)
    pn = lambda t: 'IF(S_PLOTTREE="%s"," ("&PLOT_T&")","")' % t
    pns = lambda ts: 'IF(OR(%s)," ("&PLOT_S&")","")' % ','.join('S_PLOTSHRUB="%s"' % t for t in ts)
    U = 'S_UNIT'
    rows.append(blk('"Итого"', '"_________деревьев:"', nz('C_TREES'), '"шт."', '"Итого"', 'IF(%s,"_______деревьев:","за деревья:")' % P,
                    'IF(%s,%s,IF(AP_TREES=0,"",%s))' % (P, nz('AN_TREES'), money_f('AP_TREES')), 'IF(%s,"шт. саженцев",%s)' % (P, U)))
    rows.append(blk('"в т.ч."', '"лиственные декор-е:"', nz('(C_TREES-C_X-C_P)'), '"шт."', 'IF(%s,"в т.ч.","--- в т.ч. за")' % P,
                    'IF(%s,"хвойн./ вечнозел.:","лиственные декор-е:")' % P,
                    'IF(%s,IF(AN_X=0,"",AP_X&%s),IF(AP_M+AP_B+AP_L=0,"",%s))' % (P, pn('хвой.'), money_f('(AP_M+AP_B+AP_L)')),
                    'IF(%s,"шт. "&IF(S_PLOTTREE="хвой.","(в т.ч. за поросль)",""),%s)' % (P, U)))
    rows.append(blk('"в т.ч."', '"хвойные и вечнозел.:"', nz('C_X'), '"шт."', 'IF(%s,"в т.ч.","--- в т.ч. за")' % P,
                    'IF(%s,"листв. медл-раст-х:","хвойные и вечнозел.:")' % P,
                    'IF(%s,IF(AN_M=0,"",AN_M&%s),IF(AP_X=0,"",%s))' % (P, pn('медл.'), money_f('AP_X')),
                    'IF(%s,"шт. "&IF(S_PLOTTREE="медл.","(в т.ч. за поросль)",""),%s)' % (P, U)))
    rows.append(blk('"в т.ч."', '"плодовые:"', nz('C_P'), '"шт."', 'IF(%s,"в т.ч.","--- в т.ч. за")' % P,
                    'IF(%s,"листв. быст-раст-х:","плодовые:")' % P,
                    'IF(%s,IF(AN_B=0,"",AN_B&%s),IF(AP_P=0,"",%s))' % (P, pn('быст.'), money_f('AP_P')),
                    'IF(%s,"шт. "&IF(S_PLOTTREE="быст.","(в т.ч. за поросль)",""),%s)' % (P, U)))
    rows.append(blk('"Итого"', '"уч-ки поросли дер-ев:"', nz('C_TPLOT'), '"м²"', 'IF(%s,"Итого","--- Итого за")' % P,
                    'IF(%s,"_____кустарников:","уч-ки поросли дер-ев:")' % P,
                    'IF(%s,%s,IF(C_TPLOT=0,"",%s))' % (P, nz('AN_ALL'), money_f('PLOT_T')), 'IF(%s,"шт. (в т.ч. в жив. изг.)",%s)' % (P, U)))
    rows.append(blk('""', '""', '""', '""', 'IF(%s,"в т.ч.","")' % P, 'IF(%s,"древесных лиан:","")' % P,
                    'IF(%s,IF(AP_S0+AN_S1=0,"",AN_S1&%s),"")' % (P, pns(['лиан.'])), 'IF(%s,"шт. ","")' % P))
    sh_cnt = '(C_H1+C_H2+C_SPLOT+C_S0+C_S1+C_S2+C_S3+C_S4+C_S5+C_S6)'
    rows.append(blk('"Итого"', '"_______кустарников:"', 'IF(%s=0,"",C_SH35)' % sh_cnt, '"шт."', 'IF(%s,"в т.ч.","--- Итого за")' % P,
                    'IF(%s,"хвойных/вечноз-х:","кустарники:")' % P,
                    'IF(%s,IF(AN_S2=0,"",AN_S2&%s),IF(AP_SHRUBS=0,"",%s))' % (P, pns(['хвой..', 'вечз.']), money_f('AP_SHRUBS')), 'IF(%s,"шт. ",%s)' % (P, U)))
    rows.append(blk('"Итого"', '"уч-ки поросли куст-ов:"', nz('C_SPLOT'), '"м²"', 'IF(%s,"в т.ч.","--- Итого за")' % P,
                    'IF(%s,"красивоцветущих:","поросль куст.")' % P,
                    'IF(%s,IF(AN_S3=0,"",AN_S3&%s),IF(PLOT_S=0,"",%s))' % (P, pns(['крас.']), money_f('PLOT_S')), 'IF(%s,"шт. ",%s)' % (P, U)))
    rows.append(blk('"Итого"', '"1-рядн. жив. изгородь:"', nz('C_H1'), '"п.м."', 'IF(%s,"в т.ч.","--- Итого за")' % P,
                    'IF(%s,"медл-растущих:","1-рядн. ж.изг.")' % P,
                    'IF(%s,IF(AN_S4=0,"",AN_S4&%s),IF(AP_S7=0,"",%s))' % (P, pns(['медл..']), money_f('AP_S7')), 'IF(%s,"шт. ",%s)' % (P, U)))
    rows.append(blk('"Итого"', '"2-рядн. жив. изгородь:"', nz('C_H2'), '"п.м."', 'IF(%s,"в т.ч.","--- Итого за")' % P,
                    'IF(%s,"быстр-х/плод-х:","2-рядн. ж.изг.")' % P,
                    'IF(%s,IF(AN_S5=0,"",AN_S5&%s),IF(AP_S8=0,"",%s))' % (P, pns(['быст..', 'плод..']), money_f('AP_S8')), 'IF(%s,"шт. ",%s)' % (P, U)))
    g48 = 'IF(%s,IF(AN_H1=0,"",ROUNDUP(AN_H1*3,3)&%s),%s)' % (P, pns(['ж.и.1']), money_f('AP_TOTAL'))
    rows.append(['""', '""', '""', '""', 'IF(%s,IF((%s)="","","в т.ч."),"ИТОГО всех")' % (P, g48), 'IF(%s,IF((%s)="","","1-рядн. жив. изг.:"),"выплат:")' % (P, g48),
                 g48, 'IF((%s)="","",IF(%s,"шт. ",%s))' % (g48, P, U)])
    g49 = 'IF(%s,IF(AN_H2=0,"",ROUNDUP(AN_H2*5,3)&%s),"")' % (P, pns(['ж.и.2']))
    rows.append(['""', '""', '""', '""', 'IF((%s)="","","в т.ч.")' % g49, 'IF((%s)="","","2-рядн. жив. изг.:")' % g49, g49, 'IF((%s)="","","шт. ")' % g49])
    return rows


def money_f(v):
    """Текст суммы как в УДд!G2038 (с пересчётом по H2035)."""
    return '{v}*IF(UNIT2035="руб.",BASE,1)&IF(UNIT2035="баз.в. (руб.)"," ("&{v}*BASE&")","")'.format(v=v)


def simple_lines(items, first='"Итого:"'):
    """Строки «подпись — значение — ед.» для итогов под перечнем."""
    out = []
    for i, (label, val, unit) in enumerate(items):
        out.append([first if i == 0 else None, '"%s"' % label, val, '"%s"' % unit])
    return out


def build_lists(wb, nrows):
    caps = {}
    err = '=IF(N_ERR>0,"Внимание: строк с ошибками — "&N_ERR&". Они не вошли в итоги (колонка «Ошибка» на листе «Ведомость»).","")'
    # существующие
    ex_cols = [C_NO, C_TITLE, C_QTY, C_H, C_DT, C_COND, ('Примечание', IDX('Htxt'), 22, 'Решение по дереву: сохранить, вырубить или пересадить (с причиной).', None)]
    ex_tot = simple_lines([('Деревья', 'EX_TREES', 'шт.'), ('Кустарники', 'EX_SHRUBS', 'шт.'), ('Поросль деревьев', 'EX_PD', 'м²'),
                           ('Поросль кустарников', 'EX_PK', 'м²'), ('Живая изгородь однорядная', 'EX_H1', 'п.м.'), ('Живая изгородь двухрядная', 'EX_H2', 'п.м.')])
    caps['Существующие'] = nrows + len(ex_tot) + 2
    ws = build_list(wb, 'Существующие', 'Ведомость существующих деревьев и кустарников', ex_cols, 'exC', 'N_EX', ex_tot, caps['Существующие'])
    # сохраняемые
    kp_cols = [C_NO, C_TITLE, C_QTY, C_H, C_DT, C_COND, ('Примечание', IDX('note'), 22, 'Примечание из ведомости.', None)]
    kp_tot = simple_lines([('Деревья', 'KB_TREES', 'шт.'), ('в т.ч. лиственные декоративные', 'KB_L', 'шт.'), ('в т.ч. плодовые', 'KB_P', 'шт.'),
                           ('в т.ч. хвойные', 'KB_X', 'шт.'), ('Кустарники', 'KB_SHRUBS', 'шт.'), ('Поросль деревьев', 'KB_PD', 'м²'),
                           ('Поросль кустарников', 'KB_PK', 'м²'), ('Живая изгородь однорядная', 'KB_H1', 'п.м.'), ('Живая изгородь двухрядная', 'KB_H2', 'п.м.')])
    caps['Сохраняемые'] = nrows + len(kp_tot) + 2
    ws = build_list(wb, 'Сохраняемые', 'Ведомость сохраняемых деревьев и кустарников', kp_cols, 'keepC', 'N_KEEP', kp_tot, caps['Сохраняемые'])
    # удаляемые
    rm_cols = [C_NO, C_TITLE, C_QTY, C_H, ('Диаметр ствола, см', IDX('dRep'), 14, C_DT[3], None),
               ('Качественное состояние', IDX('Fx'), 21, 'хор., уд., плох., ненадл.; для поросли — «-».', None),
               ('Компенсационные посадки (выплаты)', IDX('G'), 42, 'Расчёт: (тариф) баз.в. × кол-во × коэф. состояния × К1 × К2 × К3 × К4 = итог. В режиме «посадками» — число саженцев.', None),
               ('Примечание', IDX('rsnP'), 16, 'Причина вырубки (если указана).', None)]
    rm_tot = removal_lines()
    caps['Удаляемые'] = nrows + len(rm_tot) + 2
    ws = build_list(wb, 'Удаляемые', 'Ведомость удаляемых деревьев и кустарников', rm_cols, 'remC', 'N_REM', rm_tot, caps['Удаляемые'], err)
    ws['A2'] = org_line('&" · "&IF(MONEYY,"компенсационные выплаты, "&S_UNIT,"компенсационные посадки")&" · базовая величина "&BASE&" руб."')
    # пересаживаемые
    tr_cols = [C_NO, C_TITLE, C_QTY, C_H, C_DT, C_COND, ('Размер кома, м', IDX('ball'), 14, 'Диаметр и высота кома по справочнику и высоте растения.', None),
               ('Примечание', IDX('rsnP'), 16, 'Причина пересадки (если указана).', None)]
    tr_tot = simple_lines([('Деревья', 'TR_TREES', 'шт.'), ('в т.ч. хвойные', 'TR_X', 'шт.'), ('в т.ч. плодовые', 'TR_P', 'шт.'),
                           ('Кустарники', 'TR_SHRUBS', 'шт.'), ('Живая изгородь однорядная', 'TR_H1', 'п.м.'), ('Живая изгородь двухрядная', 'TR_H2', 'п.м.')],
                          '"Итого пересадить:"')
    caps['Пересаживаемые'] = nrows + len(tr_tot) + 2
    ws = build_list(wb, 'Пересаживаемые', 'Ведомость пересаживаемых деревьев и кустарников', tr_cols, 'trC', 'N_TR', tr_tot, caps['Пересаживаемые'])
    # древесина
    F3 = '0.000'
    num = lambda nm, mul='': 'IF(ISNUMBER(INDEX(R_%s,{I})),INDEX(R_%s,{I})%s,"")' % (nm, nm, mul)
    wd_cols = [C_NO, C_TITLE, C_QTY, C_H, ('Диаметр ствола, см', IDX('dRep'), 12, C_DT[3], None),
               ('Древесина', IDX('wtype'), 10, 'Тип древесины по библиотеке пород: мягкая, хвойная или твёрдая. От него зависят таблица объёмов и плотность.', None),
               ('Стволы, м³ скл.', num('BZ'), 10, 'Складочный объём стволов: объём ствола по таблице (диаметр каждого ствола и высота) × коэф. укладки (Ø ≥ 15 см — ×1,47, хвойные ×1,49; 11–15 см — ÷0,67; 3–11 см — ÷0,6) × кол-во.', F3),
               ('Стволы, т', num('BX'), 9, 'Масса стволов: объём стволов по таблице × плотность древесины × кол-во.', F3),
               ('Пни, м³ скл.', num('BZ', '*0.2'), 9, 'Пни: 20 % складочного объёма стволов (как в оригинале). Корни отдельно не считаются: в оригинале такого расчёта нет.', F3),
               ('Пни, т', num('BX', '*0.2'), 8, 'Пни: 20 % массы стволов (как в оригинале).', F3),
               ('Ветви, м³ скл.', num('BY'), 9, 'Ветви и сучья (крона): объём всего дерева минус объём ствола по таблицам, ÷ 0,1 (складочный), × кол-во.', F3),
               ('Ветви, т', num('BW'), 8, 'Ветви и сучья: объём кроны × плотность × кол-во.', F3),
               ('Кустарник, т', num('BV'), 9, 'Масса кустарника: высота × масса куста (лист «Настройки») × кол-во. Как в оригинале, считается только для строк с кол-вом 1 шт.', F3),
               ('Кустарник, м³ скл.', num('BV', '*1000/DEN_S/0.1'), 10, 'Складочный объём кустарника: масса ÷ плотность мягкой древесины ÷ 0,1.', F3)]
    tw = lambda key, ce=None: ('SUMIFS(R_%s,R_CE,%d)' % (key, ce)) if ce else 'SUM(R_%s)' % key
    wd_tot = []
    for label, ce in (('"Итого"', None), ('"в т.ч. мягкая"', 1), ('"в т.ч. хвойная"', 2), ('"в т.ч. твёрдая"', 3)):
        line = [label if ce is None else None, None if ce is None else label, None, None, None, None,
                tw('BZ', ce), tw('BX', ce), tw('BZ', ce) + '*0.2', tw('BX', ce) + '*0.2', tw('BY', ce), tw('BW', ce)]
        line += ['VOL_BV', 'VOL_BVM3'] if ce is None else [None, None]
        wd_tot.append(line)
    wd_tot.append([None, '"стволы Ø ≤ 50 см, м³ скл."', None, None, None, None, 'VOL_LE50_ALL'])
    wd_tot.append([None, '"стволы Ø > 50 см, м³ скл."', None, None, None, None, 'VOL_GT50_ALL'])
    wd_tot.append([None, '"округлённо (как в оригинале)"', None, None, None, None, 'ROUND(VOL_BZ_ALL,2)', 'ROUND(VOL_BX_ALL,3)', 'ROUND(VOL_BZ_ALL*0.2,2)',
                   'ROUND(VOL_BX_ALL*0.2,2)', 'ROUND(VOL_BY_ALL,2)', 'ROUND(VOL_BW_ALL,2)', 'ROUND(VOL_BV,2)', 'ROUND(VOL_BVM3,2)'])
    caps['Древесина'] = nrows + len(wd_tot) + 2
    ws = build_list(wb, 'Древесина', 'Отходы древесины по удаляемым деревьям и кустарникам: стволы, пни, ветви', wd_cols, 'remC', 'N_REM', wd_tot, caps['Древесина'])
    ws['A5'] = 'Объёмы — по таблицам «Мягк», «Хвой», «Твер» оригинала (лист «Справочник»), по диаметру каждого ствола и высоте. Корни отдельно не считаются: в оригинале такого расчёта нет.'
    ws['A5'].font = Font(name=FONT, italic=True, size=9, color='555555')
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
    r += 1
    ws.cell(row=r, column=1, value='Поросль (считается по каждой строке)').font = Font(name=FONT, bold=True)
    r += 1
    put('PLOT_T', 'Поросль деревьев, итого', 'SUMIFS(R_Y,R_pT,1)', 'Сумма строк «Уч-к поросли дер.» (в т.ч. «у дорог»): площадь × норма × коэффициенты.')
    put('PLOT_S', 'Поросль кустарников, итого', 'SUMIFS(R_Y,R_pS,1)', 'Сумма строк «Уч-к поросли куст.» (в т.ч. «у дорог»).')
    r += 1
    ws.cell(row=r, column=1, value='Количество удаляемых').font = Font(name=FONT, bold=True)
    r += 1
    for ty, nm in (('хвой.', 'C_X'), ('медл.', 'C_M'), ('быст.', 'C_B'), ('плод.', 'C_P'), ('малоц.', 'C_L')):
        put(nm, 'деревьев типа ' + ty, 'SUMIFS(R_qty,R_rem,1,R_AO,"%s")' % ty)
    put('C_TREES', 'деревьев всего (AP2009)', 'C_X+C_M+C_B+C_P+C_L')
    put('C_TPLOT', 'поросль деревьев, м² (AP2008)', 'SUMIFS(R_Kq,R_sp,"*уч-к поросли дер*")')
    put('C_SPLOT', 'поросль кустарников, м² (AP2030)', 'SUMIFS(R_Lq,R_sp,"*уч-к поросли куст.*")')
    put('C_H1', 'живая изгородь однорядная, п.м.', 'SUMIFS(R_qty,R_rem,1,R_N,"ж.и.1")')
    put('C_H2', 'живая изгородь двухрядная, п.м.', 'SUMIFS(R_qty,R_rem,1,R_N,"ж.и.2")')
    shrub_types = ['лиан.', 'хвой..', 'вечз.', 'крас.', 'медл..', 'быст..', 'плод..']
    for i, ty in enumerate(shrub_types):
        put('C_S%d' % i, 'кустов типа ' + ty, 'SUMIFS(R_qty,R_rem,1,R_N,"%s")' % ty)
    put('C_SUMK', 'сумма K (деревья и поросль дер.)', 'SUM(R_Kq)')
    put('C_SUML', 'сумма L (кусты, поросль, изгородь)', 'SUM(R_Lq)')
    put('C_PD', 'поросль дер. (C2035)', 'SUMIFS(R_Kq,R_sp,"*ч-к поросли дер*")')
    put('C_PK', 'поросль куст. (C2035)', 'SUMIFS(R_Lq,R_sp,"*ч-к поросли куст*")')
    put('C_HH1', 'изгородь 1-р. (E2035)', 'SUMIFS(R_Lq,R_sp,"*днорядная живая изгород*")')
    put('C_HH2', 'изгородь 2-р. (E2035)', 'SUMIFS(R_Lq,R_sp,"*вухрядная живая изгород*")')
    put('C_TR35', 'деревьев удаляется (C2035)', 'C_SUMK-C_PD')
    put('C_SH35', 'кустарников удаляется (C2035)', 'C_SUML-C_PK-C_HH1-C_HH2')
    r += 1
    ws.cell(row=r, column=1, value='Выплаты, баз.вел. (AP2011…)').font = Font(name=FONT, bold=True)
    r += 1
    for ty, nm in (('хвой.', 'AP_X'), ('медл.', 'AP_M'), ('быст.', 'AP_B')):
        put(nm, 'деревья ' + ty + ' (+ поросль по типу)', 'SUMIFS(R_Y,R_AO,"%s")+IF(S_PLOTTREE="%s",PLOT_T,0)' % (ty, ty))
    put('AP_P', 'деревья плод.', 'SUMIFS(R_Y,R_AO,"плод.")')
    put('AP_L', 'деревья малоц.', 'SUMIFS(R_Y,R_AO,"малоц.")')
    put('AP_TREES', 'за деревья (AP2017)', 'AP_X+AP_M+AP_B+AP_P+AP_L')
    for i, ty in enumerate(shrub_types + ['ж.и.1', 'ж.и.2']):
        put('AP_S%d' % i, 'кусты ' + ty + ' (+ поросль по типу)', 'SUMIFS(R_Y,R_rem,1,R_N,"%s",R_plot,0)+IF(S_PLOTSHRUB="%s",PLOT_S,0)' % (ty, ty))
    put('AP_SHRUBS', 'за кустарники (AP2042)', '+'.join('AP_S%d' % i for i in range(7)))
    put('AP_TOTAL', 'ИТОГО всех выплат', 'AP_TREES+AP_S7+AP_S8+AP_SHRUBS',
        'Деревья (с порослью деревьев по типу) + кустарники (с порослью кустарников по типу) + живая изгородь. '
        'В оригинале поросль добавлялась сюда второй раз — исправлено.')
    r += 1
    ws.cell(row=r, column=1, value='Посадки, шт. (AN2011…)').font = Font(name=FONT, bold=True)
    r += 1
    put('AN_X', 'деревья хвойные', 'SUMIFS(R_Y,R_Z,"Д.хв*")')
    put('AN_M', 'деревья медленнорастущие', 'SUMIFS(R_Y,R_Z,"Д.лис.м*")')
    put('AN_B', 'деревья быстрорастущие', 'SUMIFS(R_Y,R_Z,"Д.лис.б.р*")')
    put('AN_TREES', 'деревьев всего (AN2017)', 'AN_X+AN_M+AN_B')
    put('AN_S1', 'кусты лианы', 'SUMIFS(R_Y,R_Z,"К.древ.лиан*")')
    put('AN_S2', 'кусты хвойные/вечнозелёные', 'SUMIFS(R_Y,R_Z,"К.хв*")')
    put('AN_S3', 'кусты красивоцветущие', 'SUMIFS(R_Y,R_Z,"К.лис.кр*")')
    put('AN_S4', 'кусты медленнорастущие', 'SUMIFS(R_Y,R_Z,"К.лис.м.р*")')
    put('AN_S5', 'кусты быстрорастущие/плодовые', 'SUMIFS(R_Y,R_Z,"К.л.б.р./плод*")')
    put('AN_H1', 'живая изгородь 1-р., п.м.', 'SUMIFS(R_Y,R_Z,"жив.изг.1р*")')
    put('AN_H2', 'живая изгородь 2-р., п.м.', 'SUMIFS(R_Y,R_Z,"жив.изг.2р*")')
    put('AN_SHRUBS', 'кустов всего (AN2042)', 'AN_S1+AN_S2+AN_S3+AN_S4+AN_S5')
    put('AN_ALL', 'кустарников, шт. (в т.ч. в жив. изг.) (G2042)', 'AN_H1+AN_H2+AN_SHRUBS')
    put('UNIT2035', 'единица пересчёта итогов (H2035)', 'IF(MONEYY,IF(AN_SHRUBS+AN_TREES=0,"",S_UNIT),"")')
    r += 1
    ws.cell(row=r, column=1, value='Древесина (УДд!AE72:AM83)').font = Font(name=FONT, bold=True)
    r += 1
    for i, hh in enumerate(['мяг.', 'хвн.', 'твд.', 'итого']):
        style_header(ws.cell(row=r, column=2 + i, value=hh))
    r += 1
    vol_rows = [('VOL_LE50', 'стволы, м³ скл. Ø ≤ 50 см'), ('VOL_GT50', 'стволы, м³ скл. Ø > 50 см'), ('VOL_BZ', 'стволы, м³ скл.'), ('VOL_BX', 'стволы, т'),
                ('VOL_STM3', 'пни, м³ скл.'), ('VOL_STT', 'пни, т'), ('VOL_BY', 'ветви, м³ скл.'), ('VOL_BW', 'ветви, т')]
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
    put('VOL_BV', 'кустарник, т', 'SUM(R_BV)')
    put('VOL_BVM3', 'кустарник, м³ скл.', '((VOL_BV*1000)/DEN_S)/0.1')
    T['vol'] = vr
    r += 1
    ws.cell(row=r, column=1, value='Стволы по диаметрам, шт. (УДд!AK74:AM80)').font = Font(name=FONT, bold=True)
    r += 1
    for i, hh in enumerate(['мяг.', 'хвн.', 'твд.', 'итого']):
        style_header(ws.cell(row=r, column=2 + i, value=hh))
    r += 1
    buckets = [('до 12', None, 12), ('12-16', 12, 16), ('16-20', 16, 20), ('20-24', 20, 24), ('24-28', 24, 28), ('28-32', 28, 32), ('более 32', 32, None)]
    T['stems0'] = r
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
    put('EX_PD', 'поросль деревьев, м²', 'SUMIFS(R_qty,R_sp,"*ч-к поросли дер*")')
    put('EX_PK', 'поросль кустарников, м²', 'SUMIFS(R_qty,R_sp,"*ч-к поросли куст*")')
    put('EX_HED', 'живая изгородь (все), п.м.', 'SUMIFS(R_qty,R_sp,"*рядная живая*")')
    put('EX_H1', 'живая изгородь 1-р., п.м.', 'SUMIFS(R_qty,R_sp,"*днорядная живая*")')
    put('EX_H2', 'живая изгородь 2-р., п.м.', 'SUMIFS(R_qty,R_sp,"*вухрядная живая*")')
    put('EX_TREES', 'деревьев (Дер.)', 'EX_ALL-EX_DASH-EX_PD')
    put('EX_SHRUBS', 'кустарников (Кус.)', 'EX_DASH-EX_PK-EX_HED')
    put('TR_ALL', 'пересадка: всего', 'SUMIFS(R_qty,R_trn,1)')
    put('TR_H1', 'пересадка: изгородь 1-р.', 'SUMIFS(R_qty,R_trn,1,R_sp,"*днорядная живая изгород*")')
    put('TR_H2', 'пересадка: изгородь 2-р.', 'SUMIFS(R_qty,R_trn,1,R_sp,"*вухрядная живая изгород*")')
    put('TR_SHRUBS', 'пересадка: кустов', 'SUMIFS(R_qty,R_trn,1,R_dtxt,"-")-TR_H1-TR_H2')
    put('TR_TREES', 'пересадка: деревьев', 'TR_ALL-TR_SHRUBS-TR_H1-TR_H2-SUMIFS(R_qty,R_trn,1,R_sp,"*ч-к поросли дер*")-SUMIFS(R_qty,R_trn,1,R_sp,"*ч-к поросли куст*")')
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
    return ws, T


# =============================================================== цветники, газоны, травяной покров
AREA_BLOCKS = [('fl', 'Цветники', 13, 'FL'), ('lw', 'Газоны', 17, 'LW'), ('gr', 'Иной травяной покров', 17, 'GR')]
AREA_TIPS = ['Номер позиции (считается сам).', 'Вид цветника или газона из справочника (лист «Справочник»).', 'Качественное состояние: хор. (×1), уд. (×0,75), плох. (×0,5).',
             'Площадь, м².', '«п» — пересадить (без компенсации).', 'Причина удаления из списка (лист «Настройки»). Можно не указывать.', 'Примечание.',
             'Номера коэффициентов, которые меняются на противоположные (например 24), или «без компенcации».',
             'Расчёт: площадь × стоимость (баз.в./м²) × коэф. состояния × К1 × К2 × К3 × К4.', 'Применённые коэффициенты.']


def build_areas(wb):
    ws = wb.create_sheet('Цветники и газоны')
    ws['A1'] = 'Удаляемые цветники, газоны и иной травяной покров (лист «ЦвГаТр» оригинала)'
    ws['A1'].font = Font(name=FONT, bold=True, size=13)
    ws['A2'] = 'Ввод — в жёлтые колонки. Пересадить: «п». Стоимость, баз.вел./м²: многолетники 10, 1-2-летники 2, газоны 0,5–2, иной травяной покров 0,25.'
    widths = [5, 30, 11, 11, 9, 16, 14, 7, 44, 13, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9]
    for i, w in enumerate(widths):
        ws.column_dimensions[get_column_letter(i + 1)].width = w
    ws['K3'] = 'Проектируемое озеленение, м²'
    ws['K3'].font = Font(name=FONT, bold=True)
    note(ws['K3'], 'Площади проектируемых цветников и газонов. В режиме «метрами» они уменьшают оплачиваемую площадь, в режиме «стоимостью» — сумму.')
    proj = [('PR_PER', 'цветники из многолетников'), ('PR_ANN', 'цветники из 1-2-летников')] + \
           [('PR_L%d' % i, 'газон «%s»' % k) for i, k in enumerate(REF['lawns'])]
    for i, (nm, label) in enumerate(proj):
        ws.cell(row=4 + i, column=11, value=label)
        c = ws.cell(row=4 + i, column=14)
        c.fill = F_SET
        c.border = B_THIN
        defname(wb, nm, "'Цветники и газоны'!$N$%d" % (4 + i))
    lawn_vals = list(REF['lawns'].values())
    r = 14
    tot = {}
    for key, title, n, tk in AREA_BLOCKS:
        ws.cell(row=r, column=1, value=title).font = Font(name=FONT, bold=True, size=11)
        r += 1
        heads = ['Поз.', 'Вид', 'Качественное состояние', 'Площадь, м²', 'Пересадить (п)', 'Причина', 'Примечание', 'Инд. К',
                 'Компенсационные посадки (выплаты)', 'К1;К2;К3;К4', 'есть', 'T', 'U', 'K1', 'K2', 'K3', 'K4', 'тариф', 'кач.', 'P, б.в.', 'Q, руб.', 'Tc']
        for i, hh in enumerate(heads):
            c = ws.cell(row=r, column=i + 1, value=hh)
            style_header(c)
            if i < len(AREA_TIPS) and not (key == 'gr' and i == 1):
                note(c, AREA_TIPS[i])
        hdr = r
        r0 = r + 1
        for i in range(n):
            rr = r0 + i
            for c in (2, 3, 4, 5, 6, 7, 8):
                if key == 'gr' and c == 2:
                    continue
                ws.cell(row=rr, column=c).fill = F_SET
                ws.cell(row=rr, column=c).border = B_THIN
            if key == 'gr':
                ws.cell(row=rr, column=2, value='=IF(AND(C{r}="",D{r}=""),"","Иной травяной покров")'.format(r=rr))
                pres = 'OR(D{r}<>"",C{r}<>"")'.format(r=rr)
            else:
                pres = 'OR(B{r}<>"",D{r}<>"",C{r}<>"")'.format(r=rr)
            ws.cell(row=rr, column=11, value='=' + pres)
            ws.cell(row=rr, column=1, value='=IF(K{r},COUNTIF(K${f}:K{r},TRUE),"")'.format(r=rr, f=r0))
            trn = 'LOWER(E{r})="п"'.format(r=rr)
            if key == 'gr':
                Tf = 'IF(NOT(K{r}),"",IF(V{p}="",N(D{r})-GR_LEFT,IF(V{p}>0,N(D{r}),N(D{r})+V{p})))'.format(r=rr, p=rr - 1)
            else:
                proj_area = '(PR_PER+PR_ANN)' if key == 'fl' else '(' + '+'.join('PR_L%d' % i for i in range(len(lawn_vals))) + ')'
                Tf = 'IF(NOT(K{r}),"",IF(V{p}="",IF({t},0,N(D{r}))-{pa},IF(V{p}>0,N(D{r}),IF({t},V{p},N(D{r})+V{p}))))'.format(r=rr, p=rr - 1, t=trn, pa=proj_area)
            ws.cell(row=rr, column=12, value='=' + Tf)
            ws.cell(row=rr, column=13, value='=IF(NOT(K{r}),"",IF(L{r}<0,N(D{r}),IF(L{r}=N(D{r}),0,N(D{r})-L{r})))'.format(r=rr))
            ws.cell(row=rr, column=22, value='=IF(K{r},L{r},V{p})'.format(r=rr, p=rr - 1))
            rep = 'IFERROR(INDEX(RS_Repl,MATCH(F{r},RS_Name,0))&"","")'.format(r=rr)
            ind = 'H{r}&""'.format(r=rr)
            zero = 'OR(ISNUMBER(SEARCH("без*компен?ации",{i})),ISNUMBER(SEARCH("без*компен?ации",{p})))'.format(i=ind, p=rep)
            for kk, (on, off) in enumerate([(2, 1), (0.5, 1), (0.5, 1), (0.1, 1)], 1):
                same = 'ISNUMBER(FIND("{k}",{i}))=ISNUMBER(FIND("{k}",{p}))'.format(k=kk, i=ind, p=rep)
                ws.cell(row=rr, column=13 + kk, value='=IF({z},0,IF(K{k}Y,IF({s},{on},{off}),IF({s},{off},{on})))'.format(z=zero, k=kk, s=same, on=on, off=off))
            kind_ref = 'B{r}'.format(r=rr)
            tab = {'fl': ('FL_Kind', 'FL_Val'), 'lw': ('LW_Kind', 'LW_Val'), 'gr': ('FL_Kind', 'FL_Val')}[key]
            ws.cell(row=rr, column=18, value='=IFERROR(INDEX({v},MATCH({k},{n},0)),"")'.format(v=tab[1], k=kind_ref, n=tab[0]))
            ws.cell(row=rr, column=19, value='=IF(C{r}="хор.",1,IF(C{r}="уд.",0.75,IF(C{r}="плох.",0.5,0)))'.format(r=rr))
            ws.cell(row=rr, column=10, value='=IF(NOT(K{r}),"",IF({z},"0; 0; 0; 0",N{r}&"; "&O{r}&"; "&P{r}&"; "&Q{r}))'.format(r=rr, z=zero))
            ws.cell(row=rr, column=20, value='=IF(OR(NOT(K{r}),{t}),"",IF(R{r}="","ОШИБКА",IF(S_FMODE="стоимостью",N(D{r}),MAX(L{r},0))*R{r}*S{r}*N{r}*O{r}*P{r}*Q{r}))'.format(r=rr, t=trn))
            ws.cell(row=rr, column=21, value='=IF(ISNUMBER(T{r}),T{r}*BASE,"")'.format(r=rr))
            unit = 'IF(S_UNIT="баз.в.",FIXED(T{r},0)&" баз.в.",IF(S_UNIT="руб.",FIXED(U{r},2)&" руб.",FIXED(T{r},0)&" баз.в.("&FIXED(U{r},2)&" руб.)"))'.format(r=rr)
            ws.cell(row=rr, column=9, value=('=IF(NOT(K{r}),"",IF({t},"-",IF(T{r}="ОШИБКА","ОШИБКА: вид не найден",'
                                              'IF(S_VIEW="итог",{u},{u}&" = "&D{r}&IF(S_FMODE="стоимостью","",IF(M{r}>0,"-"&M{r},""))&"м² x"&R{r}&"б.в. x"&S{r}&"x"&N{r}&"х"&O{r}&"х"&P{r}&"х"&Q{r}))))').format(r=rr, t=trn, u=unit))
            for c in range(9, 23):
                ws.cell(row=rr, column=c).fill = F_CALC
        last = r0 + n - 1
        ws.cell(row=hdr, column=22, value='')
        tot[key] = (r0, last)
        r = last + 1
        P = '{k}_P'.format(k=tk)
        defname(wb, P, "'Цветники и газоны'!$T$%d" % r)
        ws.cell(row=r, column=2, value='Итого по объекту:').font = Font(name=FONT, bold=True)
        ws.cell(row=r, column=4, value='=SUMIFS(D%d:D%d,K%d:K%d,TRUE)' % (r0, last, r0, last))
        ws.cell(row=r, column=20, value='=SUM(T%d:T%d)' % (r0, last))
        ws.cell(row=r, column=21, value='=SUM(U%d:U%d)' % (r0, last))
        defname(wb, '%s_AREA' % tk, "'Цветники и газоны'!$D$%d" % r)
        if key == 'fl':
            pv = 'IF(S_FMODE="стоимостью",PR_PER*INDEX(FL_Val,1)+PR_ANN*INDEX(FL_Val,2),0)'
        elif key == 'lw':
            pv = 'IF(S_FMODE="стоимостью",' + '+'.join('PR_L%d*INDEX(LW_Val,%d)' % (i, i + 1) for i in range(len(lawn_vals))) + ',0)'
        else:
            pv = 'IF(S_FMODE="стоимостью",IF(-(LW_P-LW_PV)<0,0,-(LW_P-LW_PV)),0)'
        ws.cell(row=r, column=10, value='проект.:')
        ws.cell(row=r, column=11, value='=' + pv)
        defname(wb, '%s_PV' % tk, "'Цветники и газоны'!$K$%d" % r)
        ws.cell(row=r, column=12, value='=%s-%s_PV' % (P, tk))
        defname(wb, '%s_REST' % tk, "'Цветники и газоны'!$L$%d" % r)
        ws.cell(row=r, column=13, value='=IF(%s_REST>0,%s_REST,0)' % (tk, tk))
        defname(wb, '%s_RES' % tk, "'Цветники и газоны'!$M$%d" % r)
        alltr = 'AND(COUNTIF(K%d:K%d,TRUE)>0,COUNTIFS(K%d:K%d,TRUE,E%d:E%d,"п")=COUNTIF(K%d:K%d,TRUE))' % (r0, last, r0, last, r0, last, r0, last)
        ws.cell(row=r, column=9, value=('=IF(COUNTIF(K{a}:K{b},TRUE)=0,"—",IF({at},"не требуется",IF(AND({tk}_REST<=0,{P}>0),"компенсировано проектируемым озеленением",'
                                         'IF(S_UNIT="баз.в.",FIXED({tk}_RES,0)&" баз.в.",IF(S_UNIT="руб.",FIXED({tk}_RES*BASE,2)&" руб.",FIXED({tk}_RES,0)&" баз.в.("&FIXED({tk}_RES*BASE,2)&" руб.)")))))').format(
            a=r0, b=last, at=alltr, tk=tk, P=P))
        ws.cell(row=r, column=9).font = Font(name=FONT, bold=True)
        if key == 'lw':
            ws.cell(row=r + 1, column=10, value='остаток газона, м²:')
            ws.cell(row=r + 1, column=11, value='=IF(COUNTIF(L{a}:L{b},">"&0)>0,0,-IF(COUNT(L{a}:L{b})=0,-({pa}),MAX(L{a}:L{b})))'.format(
                a=r0, b=last, pa='+'.join('PR_L%d' % i for i in range(len(lawn_vals)))))
            defname(wb, 'GR_LEFT', "'Цветники и газоны'!$K$%d" % (r + 1))
        r += 3
    for c in range(10, 23):
        ws.column_dimensions[get_column_letter(c)].hidden = c not in (10,)
    ws.column_dimensions['J'].hidden = False
    ws.column_dimensions['K'].hidden = False
    ws.column_dimensions['N'].hidden = False
    return ws, tot


# =============================================================== баланс
def build_balance(wb):
    ws = wb.create_sheet('Баланс')
    ws['A1'] = 'Баланс существующих деревьев и кустарников, объёмы древесины'
    ws['A1'].font = Font(name=FONT, bold=True, size=13)
    ws['A2'] = org_line()
    ws['A3'] = '=HYPERLINK("#\'Баланс\'!A4:H8","▶ Выделить таблицу баланса для AutoCAD (затем Ctrl+C; в AutoCAD: Правка → Специальная вставка → Объекты AutoCAD)")'
    ws['A3'].font = Font(name=FONT, color='1F5FBF', underline='single')
    widths = [22, 20, 16, 12, 12, 20, 20, 20]
    for i, w in enumerate(widths):
        ws.column_dimensions[get_column_letter(i + 1)].width = w
    heads = ['Проектные предложения', '=IF(S_PLOTS="нет","Деревья всего, шт.","Деревья всего, шт. + поросль, м²")', 'в т.ч. лиственные декоративные',
             'плодовые', 'хвойные', '=IF(S_PLOTS="нет","Кустарники, шт.","Кустарники, шт. + поросль, м²")',
             '=IF(S_HEDGE="нет","Живая изгородь однорядная, м","Живая изгородь однорядная, м / шт.")', '=IF(S_HEDGE="нет","двухрядная, м","двухрядная, м / шт.")']
    tips = ['Сохраняемые = всего − пересаживаемые − удаляемые.', 'Деревья, шт.; через «+» — поросль деревьев, м² (настройка «Показывать поросль в балансе»).',
            'Деревья без плодовых и хвойных.', 'Плодовые деревья.', 'Хвойные деревья.', 'Кустарники, шт.; через «+» — поросль кустарников, м².',
            'Однорядная живая изгородь, п.м.; через «/» — шт. (× 3 на 1 п.м.).', 'Двухрядная живая изгородь, п.м.; через «/» — шт. (× 5 на 1 п.м.).']
    for i, hh in enumerate(heads):
        c = ws.cell(row=4, column=i + 1, value=hh)
        style_header(c)
        note(c, tips[i])
    data = {
        'trees': ('EX_TREES', 'TR_TREES', 'C_TR35'), 'tplot': ('EX_PD', '0', 'C_PD'), 'shrubs': ('EX_SHRUBS', 'TR_SHRUBS', 'C_SH35'),
        'splot': ('EX_PK', '0', 'C_PK'), 'h1': ('EX_H1', 'TR_H1', 'C_HH1'), 'h2': ('EX_H2', 'TR_H2', 'C_HH2'),
    }

    def v(field, which):
        tot, tr, rm = data[field]
        return {'total': tot, 'tr': tr, 'rm': rm}.get(which, '(%s-%s-%s)' % (tot, tr, rm))
    rows = [('Сохраняемые', 'kept'), ('Пересаживаемые', 'tr'), ('Удаляемые', 'rm'), ('Итого', 'total')]
    leafy = {'kept': '(%s-KP_X-KP_P)' % v('trees', 'kept'), 'tr': '(TR_TREES-TR_P-TR_X)', 'rm': '(C_TREES-C_P-C_X)'}
    fruit = {'kept': 'KP_P', 'tr': 'TR_P', 'rm': 'C_P'}
    conif = {'kept': 'KP_X', 'tr': 'TR_X', 'rm': 'C_X'}
    for i, (label, w) in enumerate(rows):
        r = 5 + i
        ws.cell(row=r, column=1, value=label)
        if w == 'total':
            lf = '+'.join(leafy[k] for k in ('kept', 'tr', 'rm'))
            fr = '+'.join(fruit[k] for k in ('kept', 'tr', 'rm'))
            cf = '+'.join(conif[k] for k in ('kept', 'tr', 'rm'))
        else:
            lf, fr, cf = leafy[w], fruit[w], conif[w]
        ws.cell(row=r, column=2, value='=IF(S_PLOTS="нет",{a},{a}&" + "&{b})'.format(a=v('trees', w), b=v('tplot', w)))
        ws.cell(row=r, column=3, value='=' + lf)
        ws.cell(row=r, column=4, value='=' + fr)
        ws.cell(row=r, column=5, value='=' + cf)
        ws.cell(row=r, column=6, value='=IF(S_PLOTS="нет",{a},{a}&" + "&{b})'.format(a=v('shrubs', w), b=v('splot', w)))
        ws.cell(row=r, column=7, value='=IF(S_HEDGE="нет",{a},{a}&" / "&{a}*3)'.format(a=v('h1', w)))
        ws.cell(row=r, column=8, value='=IF(S_HEDGE="нет",{a},{a}&" / "&{a}*5)'.format(a=v('h2', w)))
    ws['A11'] = 'Объёмы выхода древесины и древесных отходов'
    ws['A11'].font = Font(name=FONT, bold=True)
    vt = ['Единицы измерения', 'Стволы', 'Пни', 'Ветви', 'Кустарник']
    vtips = [None, 'Складочный объём и масса стволов (по каждой позиции — лист «Древесина»).', 'Пни: 20 % от стволов (как в оригинале).',
             'Ветви и сучья (крона).', 'Кустарник: масса по высоте и массе куста; объём через плотность мягкой древесины.']
    for i, hh in enumerate(vt):
        c = ws.cell(row=12, column=1 + i, value=hh)
        style_header(c)
        if vtips[i]:
            note(c, vtips[i])
    ws['A13'] = 'складочный объём, м³'
    ws['A14'] = 'масса, тонн'
    ws['B13'] = "=ROUND(INDEX(VOL_TBL,3,4),2)"
    ws['C13'] = "=ROUND(INDEX(VOL_TBL,3,4)*0.2,2)"
    ws['D13'] = "=ROUND(INDEX(VOL_TBL,7,4),2)"
    ws['E13'] = "=ROUND(VOL_BVM3,2)"
    ws['B14'] = "=ROUND(INDEX(VOL_TBL,4,4),3)"
    ws['C14'] = "=ROUND(INDEX(VOL_TBL,4,4)*0.2,2)"
    ws['D14'] = "=ROUND(INDEX(VOL_TBL,8,4),2)"
    ws['E14'] = "=ROUND(VOL_BV,2)"
    ws['A16'] = 'По каждой позиции — лист «Древесина»; по типам твёрдости и по диаметрам стволов — лист «Итоги».'
    ws['A18'] = 'Баланс цветников, газонов, иного травяного покрова, м²'
    ws['A18'].font = Font(name=FONT, bold=True)
    for i, hh in enumerate(['', 'Цветники', 'Газоны', 'Иной травяной покров']):
        style_header(ws.cell(row=19, column=1 + i, value=hh))
    for i, label in enumerate(['Сохраняемые', 'Пересаживаемые', 'Удаляемые', 'Итого (всего на участке — ввод)']):
        ws.cell(row=20 + i, column=1, value=label)
    note(ws['A23'], 'Впишите в жёлтые ячейки всю площадь цветников, газонов и травяного покрова на участке — сохраняемые посчитаются сами.')
    for j, tk in enumerate(['FL', 'LW', 'GR']):
        col = get_column_letter(2 + j)
        ws['%s23' % col].fill = F_SET
        ws['%s21' % col] = '=%s_TR' % tk
        ws['%s22' % col] = '=IF(%s_AREA-%s_TR<0.1,0,%s_AREA-%s_TR)' % (tk, tk, tk, tk)
        ws['%s20' % col] = '=N(%s23)-%s21-%s22' % (col, col, col)
    return ws


def build(nrows=1000, out=None):
    wb = Workbook()
    wb.remove(wb.active)
    build_settings(wb)
    build_ref(wb)
    ws_main, spec, L, last = build_main(wb, nrows)
    build_totals(wb, L)
    caps = build_lists(wb, nrows)
    ws_areas, area_tot = build_areas(wb)
    for tk, (key, rng) in zip(['FL', 'LW', 'GR'], area_tot.items()):
        defname(wb, '%s_TR' % tk, "SUMIFS('Цветники и газоны'!$D$%d:$D$%d,'Цветники и газоны'!$E$%d:$E$%d,\"п\")" % (rng[0], rng[1], rng[0], rng[1]))
    build_balance(wb)
    order = ['Настройки', 'Ведомость', 'Существующие', 'Сохраняемые', 'Удаляемые', 'Пересаживаемые', 'Древесина',
             'Цветники и газоны', 'Баланс', 'Итоги', 'Справочник']
    wb._sheets = [wb[n] for n in order]
    for ws in wb.worksheets:
        ws.sheet_view.zoomScale = 100 if ws.title == 'Ведомость' else 90
    from openpyxl.workbook.properties import CalcProperties
    wb.calculation = CalcProperties(fullCalcOnLoad=True)   # Excel пересчитает книгу при открытии
    out = out or out_name(nrows)
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    tmp = out + '.tmp.xlsx'
    wb.save(tmp)
    ranges = {'Ведомость': (FIRST, FIRST + nrows - 1, None)}
    for sh, (cap, kc) in caps.items():
        ranges[sh] = (LFIRST, LFIRST + cap - 1, kc)
    expand_shared(tmp, out, ranges)
    os.remove(tmp)
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
            if maxcol and col_num(col) > maxcol:
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
    with zipfile.ZipFile(dst, 'w', zipfile.ZIP_DEFLATED) as z:
        for n in names:
            z.writestr(n, files[n])


SIZES = [2000]   # книги на 500, 1000, 5000 и 10000 строк в excel/ — прежняя версия, больше не пересобираются


def out_name(n):
    return os.path.join(ROOT, 'excel', 'Таксация-Лайт-%d.xlsx' % n)


if __name__ == '__main__':
    ap = argparse.ArgumentParser(description='Без параметров — книги всех размеров: ' + ', '.join(map(str, SIZES)) + ' строк.')
    ap.add_argument('--rows', type=int, default=None, help='число строк ведомости (одна книга)')
    ap.add_argument('--out', default=None)
    a = ap.parse_args()
    for n in ([a.rows] if a.rows else SIZES):
        p = build(n, a.out or out_name(n))
        print(p, os.path.getsize(p) // 1024, 'КБ')
