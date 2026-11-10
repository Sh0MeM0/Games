"""Сборка книги Excel «Таксация Лайт» (только формулы, без макросов и защиты).

    python3 tools/build_excel.py [--rows 2000] [--out excel/Таксация-Лайт.xlsx]

Логика формул повторяет core/engine.js (а тот — формулы «ТАКСАЦИЯ-АВТО 4.2»).
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
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
from openpyxl.utils import get_column_letter
from openpyxl.workbook.defined_name import DefinedName
from openpyxl.worksheet.datavalidation import DataValidation

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REF = json.load(open(os.path.join(ROOT, 'core', 'reference.json'), encoding='utf-8'))
FIRST = 5            # первая строка данных ведомости
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
    ws.cell(row=r, column=1).comment = Comment('Свои породы: название, тип (хвой., медл., быст., плод., малоц. — деревья; лиан., хвой.., вечз., крас., медл.., быст.., плод.. — кусты), куст=1, твёрдость (мяг./хвн./твд.), в библиотеке=1.', 'Таксация Лайт')
    for name, col in [('Lib_Name', 'A'), ('Lib_Type', 'B'), ('Lib_Shrub', 'C'), ('Lib_Hard', 'D'), ('Lib_Inv', 'E'),
                      ('Lib_BallD', 'F'), ('Lib_BallH', 'G'), ('Lib_InLib', 'H'), ('Lib_Special', 'I')]:
        defname(wb, name, "'Справочник'!$%s$5:$%s$%d" % (col, col, lib_last))
    ws.column_dimensions['A'].width = 34
    ws.column_dimensions['J'].width = 30
    # --- справочные таблицы справа (колонки L и далее)
    c0 = 12   # L

    def put_table(row, col, title, headers, rows, names=None):
        ws.cell(row=row, column=col, value=title).font = Font(name=FONT, bold=True)
        for i, h in enumerate(headers):
            style_header(ws.cell(row=row + 1, column=col + i, value=h))
        for j, rr in enumerate(rows):
            for i, v in enumerate(rr):
                ws.cell(row=row + 2 + j, column=col + i, value=v)
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
    row = put_table(row, c0, 'Объёмы древесины, мягкие породы (Мягк, плотность %s)' % V['density']['soft'],
                    ['диаметр', 'h1', 'h1б', 'всего1', 'ствол1', 'h2', 'h2б', 'всего2', 'ствол2'], V['soft'],
                    ['VolDiam', 'V_S2', None, 'V_S4', 'V_S5', None, None, 'V_S8', 'V_S9'])
    start = row
    row = put_table(row, c0, 'Объёмы древесины, хвойные (Хвой, плотность %s; строки — по диаметрам таблицы мягких пород)' % V['density']['conif'],
                    ['h1', 'h1б', 'всего1', 'ствол1', 'h2', 'h2б', 'всего2', 'ствол2', 'h3', 'h3б', 'всего3', 'ствол3'], V['conif'],
                    ['V_C1', None, 'V_C3', 'V_C4', 'V_C5', None, 'V_C7', 'V_C8', None, None, 'V_C11', 'V_C12'])
    start = row
    row = put_table(row, c0, 'Объёмы древесины, твёрдые (Твер, плотность %s)' % V['density']['hard'],
                    ['h1', 'h1б', 'всего1', 'ствол1', 'h2', 'h2б', 'всего2', 'ствол2'], V['hard'],
                    ['V_H1', None, 'V_H3', 'V_H4', 'V_H5', None, 'V_H7', 'V_H8'])
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
def build_settings(wb):
    ws = wb.create_sheet('Настройки', 0)
    ws.column_dimensions['A'].width = 3
    ws.column_dimensions['B'].width = 46
    ws.column_dimensions['C'].width = 26
    ws.column_dimensions['D'].width = 60
    ws['B1'] = 'Таксация Лайт — настройки объекта'
    ws['B1'].font = Font(name=FONT, bold=True, size=14, color=GREEN)
    ws['B2'] = 'Жёлтые ячейки — для ввода. Расчёт: лист «Ведомость». Итоги и баланс — на своих листах.'
    rows = [
        ('S_ORG', 'Организация', '', None), ('S_NO', 'Номер объекта', '', None), ('S_NAME', 'Наименование объекта', '', None),
        ('S_STAGE', 'Стадия проекта', '', None), ('S_AUTHOR', 'Автор (ФИО)', '', None), (None, None, None, None),
        ('S_DATE', 'Дата расчёта (для базовой величины)', datetime.date.fromisoformat(REF['defaults']['date']), 'дата'),
        ('S_BVOVR', 'Своя базовая величина, руб. (пусто — по календарю)', None, None),
        ('BASE', 'Базовая величина для расчёта, руб.', '=IF(S_BVOVR<>"",S_BVOVR,IFERROR(INDEX(BV_Rub,MATCH(SUMPRODUCT(MAX((BV_From<=S_DATE)*BV_From)),BV_From,0)),0))', 'calc'),
        ('BASE_ACT', 'Действует по календарю с / постановление', '=IFERROR(TEXT(SUMPRODUCT(MAX((BV_From<=S_DATE)*BV_From)),"DD.MM.YYYY")&" — "&INDEX(BV_Act,MATCH(SUMPRODUCT(MAX((BV_From<=S_DATE)*BV_From)),BV_From,0)),"")', 'calc'),
        (None, None, None, None),
        ('S_K1', 'К1 — «зона особ. охр.?» (да → 2)', 'нет' if REF['defaults']['K1'] == 'нет' else 'да', 'yn'),
        ('S_K2', 'К2 — «бюджет или нет?» (да → 0,5)', REF['defaults']['K2'], 'yn'),
        ('S_K3', 'К3 — «помеха инж.?» (да → 0,5)', REF['defaults']['K3'], 'yn'),
        ('S_K4', 'К4 — «вне насел. пункта?» (да → 0,1)', REF['defaults']['K4'], 'yn'),
        ('S_MONEY', 'Компенсация', 'выплатами', 'money'),
        ('S_UNIT', 'Единицы выплат', REF['defaults']['unit'], 'unit'),
        ('S_VIEW', 'Графа «компенсация»', REF['defaults']['formulaView'], 'view'),
        ('S_COND', 'Состояние по умолчанию', REF['defaults']['defaultCondition'], 'cond'),
        ('S_MASS', 'Масса куста при вырубке, кг', REF['defaults']['shrubMassKg'], None),
        ('S_FMODE', 'Учёт проектируемых цветников и газонов', REF['defaults']['flowerOffset'], 'fmode'),
        ('S_PLOTS', 'Показывать поросль в балансе', 'да', 'yn'),
        ('S_HEDGE', 'Живую изгородь также штуками', 'да', 'yn'),
        ('S_PLOTTREE', 'Поросль деревьев компенсировать типом', REF['compMap']['plotTree'], 'ptree'),
        ('S_PLOTSHRUB', 'Поросль кустарников компенсировать типом', REF['compMap']['plotShrub'], 'pshrub'),
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
    for name, label, value, kind in rows:
        if name:
            ws.cell(row=r, column=2, value=label).font = Font(name=FONT)
            c = ws.cell(row=r, column=3, value=value)
            c.border = B_THIN
            c.fill = F_CALC if kind == 'calc' else F_SET
            if kind == 'дата':
                c.number_format = 'DD.MM.YYYY'
            if kind in dvs:
                dvs[kind].add(c)
            defname(wb, name, "'Настройки'!$C$%d" % r)
        r += 1
    # логические флаги (служебные)
    flags = [('K1Y', '=S_K1<>"нет"'), ('K2Y', '=S_K2="да"'), ('K3Y', '=S_K3="да"'), ('K4Y', '=S_K4="да"'), ('MONEYY', '=S_MONEY<>"посадками"')]
    ws.cell(row=r + 1, column=2, value='Служебные флаги (не изменять)').font = Font(name=FONT, italic=True, color='888888')
    for i, (nm, f) in enumerate(flags):
        ws.cell(row=r + 2 + i, column=2, value=nm).font = Font(name=FONT, color='888888')
        ws.cell(row=r + 2 + i, column=3, value=f).font = Font(name=FONT, color='888888')
        defname(wb, nm, "'Настройки'!$C$%d" % (r + 2 + i))
    r = r + 2 + len(flags) + 2
    # причины вырубки
    ws.cell(row=r, column=2, value='Причины вырубки').font = Font(name=FONT, bold=True)
    for i, h in enumerate(['Причина вырубки', 'Группа работ', 'Номера заменяемых коэффициентов (например 12) или «без компенcации»']):
        style_header(ws.cell(row=r + 1, column=2 + i, value=h))
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
    style_header(ws.cell(row=r + 1, column=2, value='Тип породы'))
    style_header(ws.cell(row=r + 1, column=3, value='Компенсировать типом'))
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
    """Колонки листа «Ведомость»: (имя, заголовок, формула для строки 5 | None — ввод, ширина, скрыта)."""
    C = []

    def add(name, title, f=None, w=10, hidden=False):
        C.append((name, title, f, w, hidden))
    # видимые
    add('no', '№ по плану', 'IF({sp}="","",IF(ISNUMBER(FIND("№",{araw}&"")),MID({araw},FIND("№",{araw})+1,10)&IFERROR("-"&(VALUE(LEFT({araw},FIND("№",{araw})-1))-1+VALUE(MID({araw},FIND("№",{araw})+1,10))),""),(IFERROR({ne:prev}+0,0)+1)&IF({araw}="","",IFERROR("-"&({araw}-1+IFERROR({ne:prev}+0,0)+1),""))))', 10)
    add('cnt', 'Кол-во (шт., м², п.м.)', None, 8)
    add('start', '№ нач.', None, 7)
    add('sp', 'Порода, вид', None, 30)
    add('h', 'Высота, м', None, 8)
    add('d', 'Диаметр, см', None, 12)
    add('cond', 'Состояние', None, 9)
    add('act', 'Действие (В/П)', None, 9)
    add('rsn', 'Причина вырубки', None, 16)
    add('kr', 'Инд. К', None, 7)
    add('note', 'Примечание', None, 14)
    add('title', 'Наименование в ведомости', 'IF({sp}="","",{sp}&IF(AND(NOT({dnum}),IFERROR(FIND(",",{dtxt},2),0)>0)," ("&(LEN({dtxt})-LEN(SUBSTITUTE({dtxt},",",""))+1)&" ств."&IF({note}<>"",", "&{note},"")&")",IF({note}<>""," ("&{note}&")",""))&IF(ISNUMBER(FIND("поросли",{sp})),", м2 ",IF(ISNUMBER(FIND("рядная живая",{sp})),", п.м. ","")))', 30)
    add('N', 'Тип', 'IF({sp}="","",IF({libi}>0,INDEX(Lib_Type,{libi}),""))', 7)
    add('Htxt', 'Решение', 'IF({sp}="","",IF(AND({shrubT},{dtxt}<>"-"),"ОШИБКА: Ø куста = только ""-""",IF(AND(NOT({shrubT}),{dtxt}="-"),"ОШИБКА: Ø дерева ≠ ""-""",IF({act}="В","вырубить ("&{rsn}&")",IF({act}="П","пересадить ("&{rsn}&")","сохранить")))))', 18)
    add('kdisp', 'К1; К2; К3; К4', 'IF({coefOK},IF({zero},"0; 0; 0; 0",{K1}&"; "&{K2}&"; "&{K3}&"; "&{K4}),"")', 13)
    add('G', 'Компенсационные посадки (выплаты)', 'IF({rem}<>1,IF({trn}=1,"ком: "&{ball},""),IF(AND(ISNUMBER({Y}),{Y}=0),"Без компенсации",IF({Y}="ОШИБКА",{err},IF(S_VIEW="итог",IF(MONEYY,"("&{AR}&") баз.в.",{Z}),IF(MONEYY,"("&{AS}&") баз.в. "&IF({qty}>1," х"&{qty}&"шт. ","")&"х"&{Nc}&"х"&{K1}&"х"&{K2}&"х"&{K3}&"х"&{K4}&" ="&IF({Y}="-"," + др. уч-ки поросли",{Y}),{mx}&" "&IF({mpos}>0,INDEX(LBL_G,{mpos}),"")&IF(AND({AQ}<>"",N({AQ})>=2),"х"&{AQ}&"ств.","")&"х"&{Nc}&"х"&{K1}&"х"&{K2}&"х"&{K3}&"х"&{K4}&" ="&IF({Y}="-"," + др. уч-ки поросли",{Y}))))))', 40)
    add('Y', 'Итог', 'IF({rem}<>1,"",IF(AND({coefOK},NOT({zero}),{isPlot}),"-",IF({inv},0,IF(OR(NOT({coefOK}),{Nc}=""),"ОШИБКА",IF(MONEYY,IF(ISNUMBER({AR}),ROUND({AR}*IF({qty}>1,{qty},1)*{Nc}*{K1}*{K2}*{K3}*{K4},3),"ОШИБКА"),IFERROR(ROUND({mx}*IF(LEN({AO})<2,1,{AQ})*{Nc}*{K1}*{K2}*{K3}*{K4},3),"ОШИБКА"))))))', 9)
    add('err', 'Ошибка', 'IF({sp}="","",IF(LEFT({Htxt},6)="ОШИБКА",{Htxt},IF({qty}="ERR","ОШИБКА: количество не распознано",IF(NOT({found}),"ОШИБКА: породы нет в справочнике",IF(AND({rem}=1,NOT({coefOK})),"ОШИБКА: не указана причина вырубки",IF(AND({rem}=1,{Nc}=""),"ОШИБКА: неизвестное состояние",IF(AND({rem}=1,{Y}="ОШИБКА"),"ОШИБКА: нет тарифа","")))))))', 22)
    # служебные
    h = True
    add('araw', 'A (кол-во№нач.)', 'IF({start}="",{cnt},{cnt}&"№"&{start})', 8, h)
    add('qty', 'Кол-во, число', 'IF({sp}="","",IF({araw}="",1,IF(LEFT({araw},1)="№",1,IFERROR(VALUE(IFERROR(LEFT({araw},FIND("№",{araw})-1),{araw})),"ERR"))))', 8, h)
    add('ne', 'конец номера', 'IF({sp}="",IFERROR({ne:prev}+0,0),IFERROR(VALUE(MID({no},FIND("-",{no})+1,10)),IFERROR(VALUE({no}),0)))', 8, h)
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
    add('ind', 'инд. замена', '{kr}&""', 6, h)
    add('zero', 'без компенсации', 'OR(ISNUMBER(SEARCH("без*компен?ации",{ind})),ISNUMBER(SEARCH("без*компен?ации",{rep})))', 6, h)
    for i, (on, off) in enumerate([(2, 1), (0.5, 1), (0.5, 1), (0.1, 1)], 1):
        same = 'ISNUMBER(FIND("%d",{ind}))=ISNUMBER(FIND("%d",{rep}))' % (i, i)
        add('K%d' % i, 'К%d' % i, 'IF({zero},0,IF(K%dY,IF(%s,%s,%s),IF(%s,%s,%s)))' % (i, same, on, off, same, off, on), 5, h)
    add('coefOK', 'коэф. есть', 'AND({rsn}<>"",{act}="В",{found})', 5, h)
    add('kkey', 'ключ коэф.', 'IF({coefOK},{K1}&"|"&{K2}&"|"&{K3}&"|"&{K4},"")', 10, h)
    add('Es', 'диаметр без пробелов', 'SUBSTITUTE({dtxt}," ","")', 8, h)
    add('J', 'код ТКП', 'IF({rem}<>1,"",IF(NOT({shrubRow}),IF(ISNUMBER(SEARCH("уч-к поросли дер.",{sp})),IF({qty}>9.99,60,70),IF(OR({dnum},ISERROR(FIND("до1",{Es}))),IF({N}="хвой.",10,IF({N}="медл.",20,IF(OR({N}="быст.",{N}="плод."),30,IF({N}="малоц.",IF(AND(ISERROR(FIND("до4",{Es})),IF({dnum},{d},IFERROR(VALUE(SUBSTITUTE(SUBSTITUTE(SUBSTITUTE({Es},"1,",""),"2,",""),"3,","")),4))>3.99),40,IF(ISNUMBER(FIND("**до4",{Es})),50,1)),1)))),IF(AND(OR({N}="хвой.",{N}="медл.",{N}="быст.",{N}="плод."),ISNUMBER(FIND("**до1",{Es}))),50,1))),IF(ISNUMBER(SEARCH("уч-к поросли куст.",{sp})),IF({qty}>9.99,18,19),IF(ISNUMBER(SEARCH("однорядная живая изгородь",{sp})),16,IF(ISNUMBER(SEARCH("двухрядная живая изгородь",{sp})),17,IF({N}="лиан.",11,IF(OR({N}="хвой..",{N}="вечз."),12,IF({N}="крас.",13,IF({N}="медл..",14,IF(OR({N}="быст..",{N}="плод.."),15,1))))))))))', 5, h)
    add('isPlot', 'поросль', 'OR({J}=60,{J}=70,{J}=18,{J}=19)', 5, h)
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
    col_j = 'MATCH({J},{10,20,30,40},0)'
    for k in range(1, NSTEM + 1):
        add('t%d' % k, 'тариф %d' % k, 'IF(AND(ISNUMBER({d%d}),N({d%d})>=12),IFERROR(LOOKUP({d%d},INDEX(TarThr,0,%s),INDEX(TarVal,0,%s)),0),0)' % (k, k, k, col_j, col_j), 4, h)
    add('AQ', 'стволов ≥12', 'IF(OR({rem}<>1,{shrubRow}),"",MAX(1,COUNTIF({d1}:{d%d},">"&11.9)))' % NSTEM, 5, h)
    add('AR', 'тариф, б.в.', 'IF({rem}<>1,"",IF({J}=1,0,IF(AND({AQ}<>"",N({AQ})>1),SUM({t1}:{t%d}),IF({J}=60,P_TP,IF({J}=70,P_TPS,IF({J}=16,P_H1,IF({J}=17,P_H2,IF({J}=18,P_SP,IF({J}=19,P_SPS,IF({J}=50,IFERROR(INDEX(Planted_Val,MATCH({N},Planted_Type,0)),""),IF(AND({J}>=11,{J}<=15),LOOKUP({Hn},INDEX(ShrThr,0,{J}-10),INDEX(ShrVal,0,{J}-10)),IFERROR(LOOKUP(MAX({d1}:{d%d}),INDEX(TarThr,0,%s),INDEX(TarVal,0,%s)),""))))))))))))' % (NSTEM, NSTEM, col_j, col_j), 5, h)
    terms = '&'.join('IF(AND(ISNUMBER({d%d}),N({d%d})>=12),"+"&{t%d},"")' % (k, k, k) for k in range(1, NSTEM + 1))
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
    add('pkT', 'ключ поросли дер.', 'IF(AND({coefOK},ISNUMBER(SEARCH("уч-к поросли дер",{sp}))),{kkey},"")', 8, h)
    add('pkS', 'ключ поросли куст.', 'IF(AND({coefOK},ISNUMBER(SEARCH("уч-к поросли куст",{sp}))),{kkey},"")', 8, h)
    # древесина
    add('CE', 'класс твёрдости', 'IF(OR({rem}<>1,{shrubRow}),0,IF({special},1,IFERROR(MATCH({hard},{"мяг.","хвн.","твд."},0),-1)))', 4, h)

    def vol(dref, total):
        m = 'MATCH(%s,VolDiam,1)' % dref
        a, b = ('V_S4', 'V_S8') if total else ('V_S5', 'V_S9')
        c1, c2, c3 = ('V_C3', 'V_C7', 'V_C11') if total else ('V_C4', 'V_C8', 'V_C12')
        d1, d2 = ('V_H3', 'V_H7') if total else ('V_H4', 'V_H8')
        return ('CHOOSE({CE},IF(INDEX(V_S2,%s)<={Hn},INDEX(%s,%s),INDEX(%s,%s)),IF(INDEX(V_C1,%s)<={Hn},INDEX(%s,%s),IF(INDEX(V_C5,%s)<={Hn},INDEX(%s,%s),INDEX(%s,%s))),'
                'IF(INDEX(V_H1,%s)<={Hn},INDEX(%s,%s),INDEX(%s,%s)))' % (m, a, m, b, m, m, c1, m, m, c2, m, c3, m, m, d1, m, d2, m))
    for k in range(1, NSTEM + 1):
        add('vt%d' % k, 'объём всего %d' % k, 'IF(OR({CE}<1,NOT(ISNUMBER({d%d}))),"",IFERROR(%s,""))' % (k, vol('{d%d}' % k, True)), 4, h)
    for k in range(1, NSTEM + 1):
        add('vs%d' % k, 'объём ствола %d' % k, 'IF(OR({CE}<1,NOT(ISNUMBER({d%d}))),"",IFERROR(%s,""))' % (k, vol('{d%d}' % k, False)), 4, h)
    add('CA', 'сумма тонких стволов', 'IF({rem}<>1,0,SUMIF({d1}:{d%d},"<"&8))' % NSTEM, 5, h)
    add('CC', 'всего, м³', 'IF({CE}<1,"",SUM({vt1}:{vt%d})+IF({CA}<8,0,%s-%s))' % (NSTEM, vol('{CA}', True), vol('{CA}', False)), 6, h)
    add('CD', 'стволы, м³', 'IF({CE}<1,"",SUM({vs1}:{vs%d}))' % NSTEM, 6, h)
    add('CB', 'ветви, м³', 'IF({CE}<1,"",IF({CC}<{CD},"",{CC}-{CD}))', 6, h)
    add('dens', 'плотность', 'IF({CE}<1,"",CHOOSE({CE},DEN_S,DEN_C,DEN_H))', 5, h)
    add('BX', 'стволы, т', 'IF({CE}<1,"",{CD}*{dens}*{qty}/1000)', 6, h)
    add('BW', 'ветви, т', 'IF(OR({CE}<1,{CB}=""),"",{CB}*{dens}*{qty}/1000)', 6, h)
    add('BY', 'ветви, м³ скл.', 'IF(OR({CE}<1,{CB}=""),"",{CB}/0.1*{qty})', 6, h)
    add('BZ', 'стволы, м³ скл.', 'IF({CE}<1,"",{qty}*(SUMIF({d1}:{d%d},">="&15,{vs1}:{vs%d})*IF({CE}=2,1.49,1.47)+SUMIFS({vs1}:{vs%d},{d1}:{d%d},"<"&15,{d1}:{d%d},">="&11)/0.67+SUMIFS({vs1}:{vs%d},{d1}:{d%d},"<"&11,{d1}:{d%d},">="&3)/0.6))' % ((NSTEM,) * 8), 6, h)
    add('big', 'стволы >50 см, м³', 'IF({CE}<1,0,SUMIF({d1}:{d%d},">"&50,{vs1}:{vs%d}))' % (NSTEM, NSTEM), 6, h)
    add('pct', 'V% из примечания', 'IFERROR(VALUE(MID({note},SEARCH("V",{note})+2,SEARCH("%",{note},SEARCH("V",{note}))-SEARCH("V",{note})-1)),1)', 5, h)
    add('BV', 'кустарник, т', 'IF({rem}<>1,"",IF(OR(AND({CA}>0.1,{CA}<8),AND({shrubRow},{qty}=1)),IF({Hn}>1,{Hn}+({Hn}-1)/3,{Hn})*S_MASS*{pct}*{qty}/1000,""))', 6, h)
    # пересадка
    add('bcol', 'колонка кома', 'IF({Hn}>5,6,IF({Hn}>4,5,IF({Hn}>2,4,IF({Hn}>1.5,3,IF({Hn}>0.99,2,1)))))', 4, h)
    add('ball', 'Размер кома, м', 'IF({trn}<>1,"",IF({libi}=0,"",IF(INDEX(Lib_InLib,{libi})<>1,"",IF({bcol}=1,"Ø"&INDEX(Lib_BallD,{libi})&"; h"&INDEX(Lib_BallH,{libi}),IFERROR("Ø"&ROUND(N(INDEX(Lib_BallD,{libi}))*INDEX(TypeK,MATCH({N},TypeK_Type,0),({bcol}-2)*2+1),1)&"; h"&ROUND(N(INDEX(Lib_BallH,{libi}))*INDEX(TypeK,MATCH({N},TypeK_Type,0),({bcol}-2)*2+2),1),"")))))', 12, h)
    add('trType', 'тип пересаж. дерева', 'IF(AND({trn}=1,{act}="П",{dtxt}<>"-",ISERROR(SEARCH("ч-к поросли",{sp}))),{N},"")', 6, h)
    add('exCond', 'состояние (ведомость сущ.)', 'IF(AND({rem}=1,{isPlot}),"-",IF({cond}="",IF(S_COND="","-",S_COND),{cond}))', 6, h)
    add('dRep', 'диаметр (ведомость удал.)', 'IF({dtxt}="0-4","-",{d})', 6, h)
    add('hRep', 'высота (ведомости)', 'IF(ISNUMBER({h}),{h},IF({h}="","",{h}))', 6, h)
    # порядковые номера для ведомостей
    add('exI', 'п/п существующих', 'IF({sp}<>"",COUNTIF({sp:top}:{sp},"?*"),"")', 5, h)
    add('remI', 'п/п удаляемых', 'IF({rem}=1,COUNTIF({rem:top}:{rem},1),"")', 5, h)
    add('trI', 'п/п пересаживаемых', 'IF({trn}=1,COUNTIF({trn:top}:{trn},1),"")', 5, h)
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
    ws['A1'] = 'Ведомость существующих деревьев и кустарников (ввод) — Таксация Лайт'
    ws['A1'].font = Font(name=FONT, bold=True, size=13, color=GREEN)
    ws['A2'] = ('Вводите данные в белые колонки B–K. Пустые строки допускаются. Диаметр: «20» или «20,15,12» (стволы), «-» — кустарник и живая изгородь, '
                '«0-4» — поросль деревьев, «до1», «до4», «**до1», «**до4». Кол-во для поросли — м², для живой изгороди — п.м. Действие: В — вырубить, П — пересадить.')
    ws['A2'].alignment = Alignment(wrap_text=True, vertical='top')
    ws.merge_cells('A2:R2')
    ws.row_dimensions[2].height = 42
    ws['A3'] = 'Служебные колонки справа скрыты (группа «+» над заголовком). Строк в шаблоне: %d.' % nrows
    ws['A3'].font = Font(name=FONT, italic=True, size=9, color='666666')
    for i, (name, title, f, w, hidden) in enumerate(spec):
        col = get_column_letter(i + 1)
        c = ws.cell(row=4, column=i + 1, value=title)
        style_header(c)
        if f is None:
            c.fill = PatternFill('solid', fgColor='FFE9A8')
        ws.column_dimensions[col].width = w
        if hidden:
            ws.column_dimensions[col].hidden = True
            ws.column_dimensions[col].outlineLevel = 1
        if f is not None:
            cell = ws.cell(row=FIRST, column=i + 1, value='=' + render(f, L))
            cell.fill = F_CALC
    ws.row_dimensions[4].height = 44
    ws.freeze_panes = 'E5'
    # проверки ввода
    dv_cond = DataValidation(type='list', formula1='"хор.,уд.,плох.,ненадл."', allow_blank=True)
    dv_act = DataValidation(type='list', formula1='"В,П"', allow_blank=True)
    dv_sp = DataValidation(type='list', formula1='Lib_Name', allow_blank=True, showErrorMessage=False)
    dv_rs = DataValidation(type='list', formula1='RS_Name', allow_blank=True, showErrorMessage=False)
    for dv, col in ((dv_cond, 'cond'), (dv_act, 'act'), (dv_sp, 'sp'), (dv_rs, 'rsn')):
        ws.add_data_validation(dv)
        dv.add('%s%d:%s%d' % (L[col], FIRST, L[col], last))
    for name, col in L.items():
        defname(wb, 'R_' + name, "'Ведомость'!$%s$%d:$%s$%d" % (col, FIRST, col, last))
    return ws, spec, L, last


# =============================================================== ведомости (выборка строк)
def build_list_sheet(wb, title, sheet, idx_col, cols, first_row, last, nrows, note=None):
    ws = wb.create_sheet(sheet)
    ws['A1'] = title
    ws['A1'].font = Font(name=FONT, bold=True, size=13)
    ws['A2'] = '=S_ORG&IF(S_NO<>""," · "&S_NO,"")&IF(S_NAME<>""," · "&S_NAME,"")&IF(S_STAGE<>""," · стадия "&S_STAGE,"")'
    if note:
        ws['A3'] = note
    for i, (h, src, w) in enumerate(cols):
        style_header(ws.cell(row=first_row - 1, column=i + 1, value=h))
        ws.column_dimensions[get_column_letter(i + 1)].width = w
    kcol = get_column_letter(len(cols) + 2)
    ws.column_dimensions[kcol].hidden = True
    ws.cell(row=first_row - 1, column=len(cols) + 2, value='строка')
    ws.cell(row=first_row, column=len(cols) + 2,
            value='=IF(ROWS($A$%d:$A%d)>MAX(R_%s),"",MATCH(ROWS($A$%d:$A%d),R_%s,0))' % (first_row, first_row, idx_col, first_row, first_row, idx_col))
    for i, (h, src, w) in enumerate(cols):
        if src == 'rsnP':
            f = '=IF($%s%d="","","("&INDEX(R_rsn,$%s%d)&")")' % (kcol, first_row, kcol, first_row)
        elif src in ('qty', 'hRep'):
            f = '=IF($%s%d="","",INDEX(R_%s,$%s%d))' % (kcol, first_row, src, kcol, first_row)
        else:
            f = '=IF($%s%d="","",INDEX(R_%s,$%s%d)&"")' % (kcol, first_row, src, kcol, first_row)
        ws.cell(row=first_row, column=i + 1, value=f)
    ws.freeze_panes = 'A%d' % first_row
    return ws, kcol


# =============================================================== итоги
def build_totals(wb, L):
    ws = wb.create_sheet('Итоги')
    ws.column_dimensions['A'].width = 44
    for c in 'BCDEFGHI':
        ws.column_dimensions[c].width = 15
    ws['A1'] = 'Итоги удаления, поросль, древесина (расчётные ячейки — не изменять)'
    ws['A1'].font = Font(name=FONT, bold=True, size=13)
    T = {}
    r = 3

    def put(name, label, formula):
        nonlocal r
        ws.cell(row=r, column=1, value=label)
        ws.cell(row=r, column=2, value='=' + formula)
        defname(wb, name, "'Итоги'!$B$%d" % r)
        T[name] = r
        r += 1
    # поросль: 17 сочетаний коэффициентов
    ws.cell(row=r, column=1, value='Поросль по группам коэффициентов (как УДд!AF2011:AJ2047)').font = Font(name=FONT, bold=True)
    r += 1
    heads = ['К1', 'К2', 'К3', 'К4', 'ключ', 'деревья: м²', 'норма', 'сумма', 'кусты: м²', 'норма', 'сумма']
    for i, hh in enumerate(heads):
        style_header(ws.cell(row=r, column=2 + i, value=hh))
    r += 1
    combos = [(0, 0, 0, 0)]
    for k1 in (1, 2):
        for k2 in (0.5, 1):
            for k3 in (0.5, 1):
                for k4 in (0.1, 1):
                    combos.append((k1, k2, k3, k4))
    g0 = r
    for cmb in combos:
        for i, v in enumerate(cmb):
            ws.cell(row=r, column=2 + i, value=v)
        ws.cell(row=r, column=6, value='=B%d&"|"&C%d&"|"&D%d&"|"&E%d' % (r, r, r, r))
        ws.cell(row=r, column=7, value='=SUMIFS(R_qty,R_pkT,F%d)' % r)
        ws.cell(row=r, column=8, value='=IF(MONEYY,IF(G{r}>9.99,P_TP,P_TPS),IFERROR(INDEX(PT_TBL,IF(G{r}>9.99,6,7),MATCH(S_PLOTTREE,{{"хвой.","медл.","быст."}},0)),0))'.format(r=r))
        ws.cell(row=r, column=9, value='=G%d*H%d*B%d*C%d*D%d*E%d' % (r, r, r, r, r, r))
        ws.cell(row=r, column=10, value='=SUMIFS(R_qty,R_pkS,F%d)' % r)
        ws.cell(row=r, column=11, value='=IF(MONEYY,IF(J{r}>9.99,P_SP,P_SPS),IFERROR(INDEX(PS_TBL,IF(J{r}>9.99,8,9),CHOOSE(MATCH(S_PLOTSHRUB,{{"лиан.","хвой..","вечз.","крас.","медл..","быст..","плод..","ж.и.1","ж.и.2"}},0),1,2,2,3,4,5,5,6,7)),0))'.format(r=r))
        ws.cell(row=r, column=12, value='=J%d*K%d*B%d*C%d*D%d*E%d' % (r, r, r, r, r, r))
        r += 1
    r += 1
    put('PLOT_T', 'Поросль деревьев, итого (AF2028)', 'SUM(I%d:I%d)' % (g0, r - 2))
    put('PLOT_S', 'Поросль кустарников, итого (AF2048)', 'SUM(L%d:L%d)' % (g0, r - 2))
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
        put('AP_S%d' % i, 'кусты ' + ty + ' (+ поросль по типу)', 'SUMIFS(R_Y,R_rem,1,R_N,"%s")+IF(S_PLOTSHRUB="%s",PLOT_S,0)' % (ty, ty))
    put('AP_SHRUBS', 'за кустарники (AP2042)', '+'.join('AP_S%d' % i for i in range(7)))
    put('AP_TOTAL', 'ИТОГО всех выплат (G2048)', 'PLOT_T+AP_TREES+AP_S7+AP_S8+AP_SHRUBS+PLOT_S')
    r += 1
    ws.cell(row=r, column=1, value='Посадки, шт. (AN2011…)').font = Font(name=FONT, bold=True)
    r += 1
    put('AN_X', 'деревья хвойные', 'SUMIFS(R_Y,R_Z,"Д.хв*")+IF(S_PLOTTREE="хвой.",PLOT_T,0)')
    put('AN_M', 'деревья медленнорастущие', 'SUMIFS(R_Y,R_Z,"Д.лис.м*")+IF(S_PLOTTREE="медл.",PLOT_T,0)')
    put('AN_B', 'деревья быстрорастущие', 'SUMIFS(R_Y,R_Z,"Д.лис.б.р*")+IF(S_PLOTTREE="быст.",PLOT_T,0)')
    put('AN_TREES', 'деревьев всего (AN2017)', 'AN_X+AN_M+AN_B')
    put('AN_S1', 'кусты лианы', 'SUMIFS(R_Y,R_Z,"К.древ.лиан*")+IF(S_PLOTSHRUB="лиан.",PLOT_S,0)')
    put('AN_S2', 'кусты хвойные/вечнозелёные', 'SUMIFS(R_Y,R_Z,"К.хв*")+IF(OR(S_PLOTSHRUB="хвой..",S_PLOTSHRUB="вечз."),PLOT_S,0)')
    put('AN_S3', 'кусты красивоцветущие', 'SUMIFS(R_Y,R_Z,"К.лис.кр*")+IF(S_PLOTSHRUB="крас.",PLOT_S,0)')
    put('AN_S4', 'кусты медленнорастущие', 'SUMIFS(R_Y,R_Z,"К.лис.м.р*")+IF(S_PLOTSHRUB="медл..",PLOT_S,0)')
    put('AN_S5', 'кусты быстрорастущие/плодовые', 'SUMIFS(R_Y,R_Z,"К.л.б.р./плод*")+IF(OR(S_PLOTSHRUB="быст..",S_PLOTSHRUB="плод.."),PLOT_S,0)')
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
    vol_rows = [('VOL_LE50', 'стволы, м³ скл. Ø ≤ 50 см', None), ('VOL_GT50', 'стволы, м³ скл. Ø > 50 см', None),
                ('VOL_BZ', 'стволы, м³ скл.', 'R_BZ'), ('VOL_BX', 'стволы, т', 'R_BX'), ('VOL_STM3', 'пни, м³ скл.', None),
                ('VOL_STT', 'пни, т', None), ('VOL_BY', 'ветви, м³ скл.', 'R_BY'), ('VOL_BW', 'ветви, т', 'R_BW')]
    vr = {}
    for nm, label, rng in vol_rows:
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
    # существующие и пересаживаемые, баланс
    ws.cell(row=r, column=1, value='Существующие (итоговая строка ведомости) и пересадка').font = Font(name=FONT, bold=True)
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
    put('N_ERR', 'строк с ошибками', 'COUNTIF(R_err,"ОШИБКА*")')
    return ws, T


def money_f(v):
    """Текст суммы как в УДд!G2038 (с пересчётом по H2035)."""
    return '{v}*IF(UNIT2035="руб.",BASE,1)&IF(UNIT2035="баз.в. (руб.)"," ("&{v}*BASE&")","")'.format(v=v)


def build_removal(wb, last, nrows):
    cols = [('Номер по плану', 'no', 11), ('Порода, вид', 'title', 32), ('Кол-во, шт.', 'qty', 8), ('Высота, м', 'hRep', 8),
            ('Диаметр ствола, см', 'dRep', 13), ('Качественное состояние', 'Fx', 12), ('Компенсационные посадки (выплаты)', 'G', 42), ('Примечание', 'rsnP', 14)]
    # «(причина)» — отдельная колонка в ведомости
    ws = wb.create_sheet('Удаляемые')
    ws['A1'] = 'Ведомость удаляемых деревьев и кустарников'
    ws['A1'].font = Font(name=FONT, bold=True, size=13)
    ws['A2'] = '=S_ORG&IF(S_NO<>""," · "&S_NO,"")&IF(S_NAME<>""," · "&S_NAME,"")&IF(S_STAGE<>""," · стадия "&S_STAGE,"")&" · "&IF(MONEYY,"компенсационные выплаты, "&S_UNIT,"компенсационные посадки")&" · базовая величина "&BASE&" руб."'
    for i, (h, src, w) in enumerate(cols):
        ws.column_dimensions[get_column_letter(i + 1)].width = w
    # итоговый блок (УДд!A2037:H2049)
    r = 4
    ws.cell(row=r, column=1, value='Итоги').font = Font(name=FONT, bold=True)
    P = 'NOT(MONEYY)'
    rows = []
    rows.append(['"-------"', '"-- Удаление --"', '"-------"', '"-------"', '"-------"', 'IF(%s,"--- Компенсация ","-------")' % P,
                 'IF(%s,"--------------","Компенсация  выплатами -----")' % P, '"--------------"'])

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
    r = 5
    for rowf in rows:
        for i, f in enumerate(rowf):
            c = ws.cell(row=r, column=i + 1, value='=' + f)
            c.fill = F_CALC
            c.font = Font(name=FONT, size=9, bold=(i == 6))
        r += 1
    ws.cell(row=r, column=1, value='=IF(N_ERR>0,"Внимание: строк с ошибками — "&N_ERR&". Они не вошли в итоги (см. колонку «Ошибка» на листе «Ведомость»).","")').font = Font(name=FONT, color='B42318', bold=True)
    first = r + 3
    ws.cell(row=first - 2, column=1, value='Перечень удаляемых').font = Font(name=FONT, bold=True)
    for i, (h, src, w) in enumerate(cols):
        style_header(ws.cell(row=first - 1, column=i + 1, value=h))
    kcol = get_column_letter(len(cols) + 2)
    ws.column_dimensions[kcol].hidden = True
    ws.cell(row=first, column=len(cols) + 2, value='=IF(ROWS($A$%d:$A%d)>MAX(R_remI),"",MATCH(ROWS($A$%d:$A%d),R_remI,0))' % (first, first, first, first))
    for i, (h, src, w) in enumerate(cols):
        if src == 'rsnP':
            f = '=IF($%s%d="","","("&INDEX(R_rsn,$%s%d)&")")' % (kcol, first, kcol, first)
        elif src in ('qty', 'hRep', 'dRep'):
            f = '=IF($%s%d="","",INDEX(R_%s,$%s%d))' % (kcol, first, src, kcol, first)
        else:
            f = '=IF($%s%d="","",INDEX(R_%s,$%s%d)&"")' % (kcol, first, src, kcol, first)
        ws.cell(row=first, column=i + 1, value=f)
    ws.freeze_panes = 'A%d' % first
    return ws, first, kcol


# =============================================================== цветники, газоны, травяной покров
AREA_BLOCKS = [('fl', 'Цветники', 13, 'FL'), ('lw', 'Газоны', 17, 'LW'), ('gr', 'Иной травяной покров', 17, 'GR')]


def build_areas(wb):
    ws = wb.create_sheet('Цветники и газоны')
    ws['A1'] = 'Удаляемые цветники, газоны и иной травяной покров (лист «ЦвГаТр» оригинала)'
    ws['A1'].font = Font(name=FONT, bold=True, size=13)
    ws['A2'] = 'Ввод — в жёлтые колонки. Пересадить: «п». Стоимость, баз.вел./м²: многолетники 10, 1-2-летники 2, газоны 0,5–2, иной травяной покров 0,25.'
    widths = [5, 30, 11, 11, 9, 16, 14, 7, 44, 13, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9]
    for i, w in enumerate(widths):
        ws.column_dimensions[get_column_letter(i + 1)].width = w
    # проектируемое озеленение (справа)
    ws['K3'] = 'Проектируемое озеленение, м²'
    ws['K3'].font = Font(name=FONT, bold=True)
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
            style_header(ws.cell(row=r, column=i + 1, value=hh))
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
            pres = 'AND(B{r}<>"",OR(D{r}<>"",C{r}<>"",B{r}<>""))'.format(r=rr) if key != 'gr' else 'OR(D{r}<>"",C{r}<>"")'.format(r=rr)
            if key != 'gr':
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
            ws.cell(row=rr, column=10, value='=IF(F{r}="","",IF({z},"0; 0; 0; 0",N{r}&"; "&O{r}&"; "&P{r}&"; "&Q{r}))'.format(r=rr, z=zero))
            ws.cell(row=rr, column=20, value='=IF(OR(NOT(K{r}),{t}),"",IF(OR(F{r}="",R{r}=""),"ОШИБКА",IF(S_FMODE="стоимостью",N(D{r}),MAX(L{r},0))*R{r}*S{r}*N{r}*O{r}*P{r}*Q{r}))'.format(r=rr, t=trn))
            ws.cell(row=rr, column=21, value='=IF(ISNUMBER(T{r}),T{r}*BASE,"")'.format(r=rr))
            unit = 'IF(S_UNIT="баз.в.",FIXED(T{r},0)&" баз.в.",IF(S_UNIT="руб.",FIXED(U{r},2)&" руб.",FIXED(T{r},0)&" баз.в.("&FIXED(U{r},2)&" руб.)"))'.format(r=rr)
            ws.cell(row=rr, column=9, value=('=IF(NOT(K{r}),"",IF({t},"-",IF(T{r}="ОШИБКА",IF(F{r}="","ОШИБКА: не указана причина","ОШИБКА: вид не найден"),'
                                              'IF(S_VIEW="итог",{u},{u}&" = "&D{r}&IF(S_FMODE="стоимостью","",IF(M{r}>0,"-"&M{r},""))&"м² x"&R{r}&"б.в. x"&S{r}&"x"&N{r}&"х"&O{r}&"х"&P{r}&"х"&Q{r}))))').format(r=rr, t=trn, u=unit))
            for c in range(9, 23):
                ws.cell(row=rr, column=c).fill = F_CALC
        last = r0 + n - 1
        ws.cell(row=hdr, column=22).value = 'Tc'
        ws.cell(row=hdr - 0, column=22)
        # строка-«заглушка» для Tc перед первой строкой: заголовок (текст) → нужно пустое значение
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
    ws['A2'] = '=S_ORG&IF(S_NO<>""," · "&S_NO,"")&IF(S_NAME<>""," · "&S_NAME,"")'
    widths = [22, 20, 16, 12, 12, 20, 20, 20]
    for i, w in enumerate(widths):
        ws.column_dimensions[get_column_letter(i + 1)].width = w
    heads = ['Проектные предложения', '=IF(S_PLOTS="нет","Деревья всего, шт.","Деревья всего, шт. + поросль, м²")', 'в т.ч. лиственные декоративные',
             'плодовые', 'хвойные', '=IF(S_PLOTS="нет","Кустарники, шт.","Кустарники, шт. + поросль, м²")',
             '=IF(S_HEDGE="нет","Живая изгородь однорядная, м","Живая изгородь однорядная, м / шт.")', '=IF(S_HEDGE="нет","двухрядная, м","двухрядная, м / шт.")']
    for i, hh in enumerate(heads):
        style_header(ws.cell(row=4, column=i + 1, value=hh))
    # значения: total, transplanted, removed — по полям
    data = {
        'trees': ('EX_TREES', 'TR_TREES', 'C_TR35'), 'tplot': ('EX_PD', '0', 'C_PD'), 'shrubs': ('EX_SHRUBS', 'TR_SHRUBS', 'C_SH35'),
        'splot': ('EX_PK', '0', 'C_PK'), 'h1': ('EX_H1', 'TR_H1', 'C_HH1'), 'h2': ('EX_H2', 'TR_H2', 'C_HH2'),
        'conif': (None, 'TR_X', 'C_X'), 'fruit': (None, 'TR_P', 'C_P'),
    }

    def v(field, which):
        tot, tr, rm = data[field]
        if which == 'total':
            return tot
        if which == 'tr':
            return tr
        if which == 'rm':
            return rm
        return '(%s-%s-%s)' % (tot, tr, rm)
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
    for i, hh in enumerate(['Единицы измерения', 'Стволы', 'Пни', 'Ветви', 'Кустарник']):
        style_header(ws.cell(row=12, column=1 + i, value=hh))
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
    ws['A16'] = 'Подробно — на листе «Итоги» (по типам твёрдости и по диаметрам стволов).'
    ws['A18'] = 'Баланс цветников, газонов, иного травяного покрова, м²'
    ws['A18'].font = Font(name=FONT, bold=True)
    for i, hh in enumerate(['', 'Цветники', 'Газоны', 'Иной травяной покров']):
        style_header(ws.cell(row=19, column=1 + i, value=hh))
    for i, label in enumerate(['Сохраняемые', 'Пересаживаемые', 'Удаляемые', 'Итого (всего на участке — ввод)']):
        ws.cell(row=20 + i, column=1, value=label)
    for j, tk in enumerate(['FL', 'LW', 'GR']):
        col = get_column_letter(2 + j)
        ws['%s23' % col].fill = F_SET
        ws['%s21' % col] = '=%s_TR' % tk
        ws['%s22' % col] = '=IF(%s_AREA-%s_TR<0.1,0,%s_AREA-%s_TR)' % (tk, tk, tk, tk)
        ws['%s20' % col] = '=N(%s23)-%s21-%s22' % (col, col, col)
    return ws


def build(nrows=2000, out=None):
    wb = Workbook()
    wb.remove(wb.active)
    build_settings(wb)
    build_ref(wb)
    ws_main, spec, L, last = build_main(wb, nrows)
    ws_tot, T = build_totals(wb, L)
    ws_rem, rem_first, rem_k = build_removal(wb, last, nrows)
    ex_cols = [('Номер по плану', 'no', 11), ('Порода, вид', 'title', 32), ('Кол-во, шт.', 'qty', 8), ('Высота, м', 'hRep', 8),
               ('Диаметр ствола, см', 'dtxt', 13), ('Качественное состояние', 'exCond', 12), ('Примечание', 'Htxt', 22)]
    ws_ex, ex_k = build_list_sheet(wb, 'Ведомость существующих деревьев и кустарников', 'Существующие', 'exI', ex_cols, 6, last, nrows)
    ws_ex['A3'] = ('="Итого: Дер. "&EX_TREES&"; Кус. "&EX_SHRUBS&"; Дер.поросль "&EX_PD&"_м²; Кус.поросль "&EX_PK&"_м²; ж.изг.1ряд_"&EX_H1&"_п.м.; ж.изг.2ряд_"&EX_H2&"_п.м."')
    tr_cols = [('Номер по плану', 'no', 11), ('Порода, вид', 'title', 32), ('Кол-во, шт.', 'qty', 8), ('Высота, м', 'hRep', 8),
               ('Диаметр ствола, см', 'dtxt', 13), ('Качественное состояние', 'exCond', 12), ('Размер кома, м', 'ball', 14), ('Примечание', 'rsnP', 14)]
    ws_tr, tr_k = build_list_sheet(wb, 'Ведомость пересаживаемых деревьев и кустарников', 'Пересаживаемые', 'trI', tr_cols, 6, last, nrows)
    ws_tr['A3'] = '="Итого пересадить по объекту: деревья, шт. "&TR_TREES&"; кусты, шт. "&TR_SHRUBS&"; жив.изг.1р., п.м. "&TR_H1&"; жив.изг.2р., п.м. "&TR_H2'
    ws_areas, area_tot = build_areas(wb)
    for tk, (key, rng) in zip(['FL', 'LW', 'GR'], area_tot.items()):
        defname(wb, '%s_TR' % tk, "SUMIFS('Цветники и газоны'!$D$%d:$D$%d,'Цветники и газоны'!$E$%d:$E$%d,\"п\")" % (rng[0], rng[1], rng[0], rng[1]))
    build_balance(wb)
    # порядок листов
    order = ['Настройки', 'Ведомость', 'Удаляемые', 'Существующие', 'Пересаживаемые', 'Цветники и газоны', 'Баланс', 'Итоги', 'Справочник']
    wb._sheets = [wb[n] for n in order]
    for ws in wb.worksheets:
        ws.sheet_view.zoomScale = 90
    from openpyxl.workbook.properties import CalcProperties
    wb.calculation = CalcProperties(fullCalcOnLoad=True)   # Excel пересчитает книгу при открытии
    out = out or os.path.join(ROOT, 'excel', 'Таксация-Лайт.xlsx')
    tmp = out + '.tmp.xlsx'
    wb.save(tmp)
    expand_shared(tmp, out, {
        'Ведомость': (FIRST, FIRST + nrows - 1),
        'Удаляемые': (rem_first, rem_first + nrows - 1),
        'Существующие': (6, 6 + nrows - 1),
        'Пересаживаемые': (6, 6 + nrows - 1),
    })
    os.remove(tmp)
    return out


def expand_shared(src, dst, sheets):
    """Растянуть формулы первой строки данных вниз как общие (shared) формулы — файл остаётся маленьким."""
    with zipfile.ZipFile(src) as z:
        names = z.namelist()
        files = {n: z.read(n) for n in names}
    wbx = files['xl/workbook.xml'].decode('utf-8')
    rels = files['xl/_rels/workbook.xml.rels'].decode('utf-8')
    relmap = dict(re.findall(r'Id="([^"]+)"[^>]*?Target="([^"]+)"', rels)) or {}
    relmap.update({a: b for b, a in re.findall(r'Target="([^"]+)"[^>]*?Id="([^"]+)"', rels)})
    for sname, (r1, r2) in sheets.items():
        m = re.search(r'<sheet [^>]*name="%s"[^>]*r:id="([^"]+)"' % re.escape(sname), wbx)
        path = 'xl/' + relmap[m.group(1)].lstrip('/').replace('xl/', '')
        x = files[path].decode('utf-8')
        rm = re.search(r'<row r="%d"[^>]*>(.*?)</row>' % r1, x, re.S)
        row_xml = rm.group(1)
        cells = [(m2.group(1), m2.group(2), m2.group(3) or '') for m2 in re.finditer(r'<c r="([A-Z]+)%d"([^>]*?)(?:/>|>(.*?)</c>)' % r1, row_xml, re.S)]
        si = 0
        new_master = []
        deps = []
        for col, attrs, inner in cells:
            fm = re.search(r'<f>(.*?)</f>', inner, re.S)
            if not fm:
                new_master.append('<c r="%s%d"%s>%s</c>' % (col, r1, attrs, inner))
                continue
            new_master.append('<c r="%s%d"%s><f t="shared" ref="%s%d:%s%d" si="%d">%s</f><v></v></c>' % (col, r1, attrs, col, r1, col, r2, si, fm.group(1)))
            deps.append((col, attrs, si))
            si += 1
        new_row1 = '<row r="%d">%s</row>' % (r1, ''.join(new_master))
        extra = []
        for rr in range(r1 + 1, r2 + 1):
            cs = ''.join('<c r="%s%d"%s><f t="shared" si="%d"/></c>' % (col, rr, attrs, s) for col, attrs, s in deps)
            extra.append('<row r="%d">%s</row>' % (rr, cs))
        x = x[:rm.start()] + new_row1 + ''.join(extra) + x[rm.end():]
        # убрать прежние строки ниже r1 (их не было) и поправить размер
        x = re.sub(r'<dimension ref="[^"]*"/>', '', x, count=1)
        files[path] = x.encode('utf-8')
    with zipfile.ZipFile(dst, 'w', zipfile.ZIP_DEFLATED) as z:
        for n in names:
            z.writestr(n, files[n])


if __name__ == '__main__':
    ap = argparse.ArgumentParser()
    ap.add_argument('--rows', type=int, default=2000)
    ap.add_argument('--out', default=None)
    a = ap.parse_args()
    p = build(a.rows, a.out)
    print(p, os.path.getsize(p) // 1024, 'КБ')
