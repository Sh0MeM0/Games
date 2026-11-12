"""Проверка Excel-книги: входные данные эталонных векторов вписываются в книгу, книга пересчитывается
LibreOffice (локаль ru-RU), результаты сравниваются с ядром (core/engine.py).

    python3 tests/test_excel.py [имя_вектора ...]

Нужен LibreOffice (soffice). Книга собирается заново на нужное число строк.
"""
from __future__ import annotations

import datetime
import html
import json
import math
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, ROOT)
sys.path.insert(0, os.path.join(ROOT, 'tools'))
from core import engine as E  # noqa: E402
import build_excel as BX  # noqa: E402

REF = E.load_reference()
ALL_SHEETS = ['Настройки', 'Ведомость', 'Существующие', 'Сохраняемые', 'Удаляемые', 'Пересаживаемые', BX.SH_VOL, BX.SH_G, BX.SH_GL,
              'Итоги', 'Справочник']
LOCALE = os.environ.get('TAKS_LOCALE', 'ru-RU')     # en-US — проверка с точкой как десятичным разделителем
XCU = """<?xml version="1.0" encoding="UTF-8"?>
<oor:items xmlns:oor="http://openoffice.org/2001/registry" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
<item oor:path="/org.openoffice.Setup/L10N"><prop oor:name="ooSetupSystemLocale" oor:op="fuse"><value>LOCALE</value></prop></item>
<item oor:path="/org.openoffice.Setup/L10N"><prop oor:name="ooLocale" oor:op="fuse"><value>LOCALE</value></prop></item>
<item oor:path="/org.openoffice.Office.Calc/Formula/Load"><prop oor:name="OOXMLRecalcMode" oor:op="fuse"><value>0</value></prop></item>
</oor:items>""".replace('LOCALE', LOCALE)


# ------------------------------------------------------------------ xlsx editing (regex, без openpyxl — быстро)
def col_idx(c):
    n = 0
    for ch in c:
        n = n * 26 + ord(ch) - 64
    return n


def set_cells(xml, cells):
    byrow = {}
    for ref, val in cells.items():
        m = re.match(r'([A-Z]+)(\d+)$', ref)
        byrow.setdefault(int(m.group(2)), {})[m.group(1)] = val
    starts = {int(m.group(1)): m.start() for m in re.finditer(r'<row r="(\d+)"', xml)}
    out, pos = [], 0
    missing = sorted(r for r in byrow if r not in starts)
    if missing:
        # добавить недостающие строки перед </sheetData>
        add = ''.join('<row r="%d"></row>' % r for r in missing)
        xml = xml.replace('<sheetData/>', '<sheetData></sheetData>')
        # вставить в правильные места: проще — пересобрать после sheetData
        rows = re.findall(r'(<row r="(\d+)"[^>]*?(?:/>|>.*?</row>))', xml, re.S)
        body = {int(n): t for t, n in rows}
        for r in missing:
            body[r] = '<row r="%d"></row>' % r
        sd = '<sheetData>' + ''.join(body[k] for k in sorted(body)) + '</sheetData>'
        xml = re.sub(r'<sheetData>.*</sheetData>', lambda m: sd, xml, flags=re.S)
        starts = {int(m.group(1)): m.start() for m in re.finditer(r'<row r="(\d+)"', xml)}
    for r in sorted(byrow):
        st = starts[r]
        head_end = xml.index('>', st) + 1
        if xml[head_end - 2] == '/':
            rowhead, body, en = xml[st:head_end - 2] + '>', '', head_end
        else:
            en = xml.index('</row>', st) + 6
            rowhead, body = xml[st:head_end], xml[head_end:en - 6]
        cur = {}
        for m in re.finditer(r'<c r="([A-Z]+)\d+"([^>]*?)(?:/>|>(.*?)</c>)', body, re.S):
            cur[m.group(1)] = m.group(0)
        for col, v in byrow[r].items():
            ref = '%s%d' % (col, r)
            sm = re.search(r' s="(\d+)"', cur.get(col, ''))
            s = ' s="%s"' % sm.group(1) if sm else ''
            if v is None or v == '':
                cur[col] = '<c r="%s"%s/>' % (ref, s)
            elif isinstance(v, bool):
                cur[col] = '<c r="%s"%s t="b"><v>%d</v></c>' % (ref, s, 1 if v else 0)
            elif isinstance(v, (int, float)):
                cur[col] = '<c r="%s"%s><v>%r</v></c>' % (ref, s, v)
            else:
                cur[col] = '<c r="%s"%s t="inlineStr"><is><t xml:space="preserve">%s</t></is></c>' % (ref, s, html.escape(str(v), quote=False))
        out.append(xml[pos:st])
        out.append(rowhead + ''.join(cur[k] for k in sorted(cur, key=col_idx)) + '</row>')
        pos = en
    out.append(xml[pos:])
    return ''.join(out)


def num_or_text(v):
    if isinstance(v, str) and re.fullmatch(r'\d+(\.\d+)?', v.strip()):
        x = float(v)
        return int(x) if x == int(x) else x
    return v


def fill_workbook(src, dst, project):
    with zipfile.ZipFile(src) as z:
        files = {n: z.read(n) for n in z.namelist()}
    wb = files['xl/workbook.xml'].decode('utf-8')
    rels = files['xl/_rels/workbook.xml.rels'].decode('utf-8')
    relmap = {a: b for a, b in re.findall(r'Id="([^"]+)"[^>]*?Target="([^"]+)"', rels)}
    relmap.update({b: a for a, b in re.findall(r'Target="([^"]+)"[^>]*?Id="([^"]+)"', rels)})
    sheets = {html.unescape(n): 'xl/' + relmap[rid].lstrip('/').replace('xl/', '') for n, rid in re.findall(r'<sheet [^>]*?name="([^"]+)"[^>]*?r:id="([^"]+)"', wb)}
    names = {html.unescape(n): html.unescape(v) for n, v in re.findall(r'<definedName [^>]*?name="([^"]+)"[^>]*>([^<]+)</definedName>', wb)}

    def cell_of(name):
        m = re.match(r"'?([^'!]+)'?!\$([A-Z]+)\$(\d+)$", names[name])
        return m.group(1), m.group(2) + m.group(3)

    def range_of(name):
        m = re.match(r"'?([^'!]+)'?!\$([A-Z]+)\$(\d+):\$([A-Z]+)\$(\d+)$", names[name])
        return m.group(1), m.group(2), int(m.group(3)), int(m.group(5))
    edits = {}

    def put(sheet, ref, v):
        edits.setdefault(sheet, {})[ref] = v
    S = project['settings']
    yn = lambda b: 'да' if b else 'нет'
    d = datetime.date.fromisoformat(S['date'])
    vals = {'S_DATE': (d - datetime.date(1899, 12, 30)).days, 'S_K1': yn(S['k1']), 'S_K2': yn(S['k2']), 'S_K3': yn(S['k3']), 'S_K4': yn(S['k4']),
            'S_MONEY': 'выплатами' if S['money'] else 'посадками', 'S_UNIT': S['unit'], 'S_VIEW': S['view'], 'S_COND': S['defaultCond'],
            'S_BUSHM2': S.get('bushArea', 1), 'S_PLOTDENS': S.get('plotDensity', 'редкий'),
            'S_PLOTTREE': BX.pt_name(S['compMap']['plotTree']), 'S_PLOTSHRUB': BX.pt_name(S['compMap']['plotShrub']),
            'S_TREEPL': BX.pt_name(S['compMap'].get('trees') or ''), 'S_SHRUBPL': BX.pt_name(S['compMap'].get('shrubs') or '')}
    for k, v in vals.items():
        sh, ref = cell_of(k)
        put(sh, ref, v)
    sh, col, r1, r2 = range_of('RS_Name')
    for i in range(r2 - r1 + 1):
        x = S['reasons'][i] if i < len(S['reasons']) else None
        put(sh, 'B%d' % (r1 + i), x['name'] if x else None)
        put(sh, 'C%d' % (r1 + i), x['group'] if x else None)
        put(sh, 'D%d' % (r1 + i), num_or_text(x['replace']) if x and x['replace'] else None)
    sh, col, r1, r2 = range_of('CM_Map')
    types = ['хвой.', 'медл.', 'быст.', 'плод.', 'малоц.', 'лиан.', 'хвой..', 'вечз.', 'крас.', 'медл..', 'быст..', 'плод..', 'ж.и.1', 'ж.и.2']
    for i, t in enumerate(types):
        put(sh, 'C%d' % (r1 + i), BX.pt_name(S['compMap'].get(t) or ''))
    # деревья
    L = BX.letters(BX.cols_spec())
    for i, t in enumerate(project['trees']):
        r = BX.FIRST + i
        a = t.get('a', '')
        cnt, start = a, ''
        if isinstance(a, str) and '№' in a:
            p = a.index('№')
            cnt, start = a[:p], a[p + 1:]
        put('Ведомость', L['cnt'] + str(r), num_or_text(cnt) if cnt != '' else None)
        noin = t.get('noIn') if t.get('noIn') not in (None, '') else start
        put('Ведомость', L['noin'] + str(r), str(noin) if noin not in (None, '') else None)
        put('Ведомость', L['sp'] + str(r), num_or_text(t['species']))
        put('Ведомость', L['h'] + str(r), t.get('height'))
        dv = t.get('diam')
        put('Ведомость', L['d'] + str(r), (str(dv) if t.get('diamAsText') else num_or_text(dv)) if dv not in (None, '') else None)
        put('Ведомость', L['cond'] + str(r), t.get('cond') or None)
        put('Ведомость', L['act'] + str(r), t.get('action') or None)
        put('Ведомость', L['rsn'] + str(r), t.get('reason') or None)
        put('Ведомость', L['note'] + str(r), t.get('note') or None)
        for k in range(1, 5):
            put('Ведомость', L['k%din' % k] + str(r), t.get('k%d' % k) or None)
        put('Ведомость', L['pay'] + str(r), t.get('pay') or None)
        put('Ведомость', L['ptype'] + str(r), t.get('ptype') or None)
    # цветники, газоны, иной травяной покров — одна ведомость (старые блоки векторов переводятся так же, как в ядре)
    GL = BX.letters(BX.greens_spec())
    for i, g in enumerate(E.greens_input(project, REF)):
        r = BX.G_FIRST + i
        val = lambda k: (g.get(k) if g.get(k) not in (None, '') else None)
        put(BX.SH_G, GL['gnoin'] + str(r), str(g['noIn']) if val('noIn') is not None else None)
        put(BX.SH_G, GL['gkind'] + str(r), val('kind'))
        put(BX.SH_G, GL['garea'] + str(r), val('area'))
        put(BX.SH_G, GL['gcond'] + str(r), val('cond'))
        put(BX.SH_G, GL['gact'] + str(r), val('action'))
        put(BX.SH_G, GL['grsn'] + str(r), val('reason'))
        for k in range(1, 5):
            put(BX.SH_G, GL['gk%din' % k] + str(r), val('k%d' % k))
        put(BX.SH_G, GL['gpay'] + str(r), val('pay'))
        put(BX.SH_G, GL['gnote'] + str(r), val('note'))
    for sh, cells in edits.items():
        path = sheets[sh]
        files[path] = set_cells(files[path].decode('utf-8'), cells).encode('utf-8')
    with zipfile.ZipFile(dst, 'w', zipfile.ZIP_DEFLATED) as z:
        for n, b in files.items():
            z.writestr(n, b)
    return sheets, names


# ------------------------------------------------------------------ LibreOffice
MACRO = """<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE script:module PUBLIC "-//OpenOffice.org//DTD OfficeDocument 1.0//EN" "module.dtd">
<script:module xmlns:script="http://openoffice.org/2000/script" script:name="Module1" script:language="StarBasic">
Sub RecalcSave()
  ThisComponent.calculateAll()
  Dim a(0) As New com.sun.star.beans.PropertyValue
  a(0).Name = "FilterName"
  a(0).Value = "Calc MS Excel 2007 XML"
  ThisComponent.storeToURL(ThisComponent.getURL() &amp; ".out.xlsx", a())
  ThisComponent.close(True)
End Sub
Sub DelRecalcSave()
  ThisComponent.Sheets.getByName(Environ("TAKS_DEL_SHEET")).Rows.removeByIndex(CLng(Environ("TAKS_DEL_START")), CLng(Environ("TAKS_DEL_COUNT")))
  ThisComponent.calculateAll()
  Dim a(0) As New com.sun.star.beans.PropertyValue
  a(0).Name = "FilterName"
  a(0).Value = "Calc MS Excel 2007 XML"
  ThisComponent.storeToURL(ThisComponent.getURL() &amp; ".out.xlsx", a())
  ThisComponent.close(True)
End Sub
</script:module>"""


def lo_profile():
    prof = os.path.join(tempfile.gettempdir(), 'taks_lo_profile' + ('' if LOCALE == 'ru-RU' else '_' + LOCALE))
    if not os.path.exists(os.path.join(prof, 'user', 'basic', 'Standard', 'Module1.xba')):
        subprocess.run(['soffice', '--headless', '--terminate_after_init', '-env:UserInstallation=file://' + prof], capture_output=True, timeout=180)
        open(os.path.join(prof, 'user', 'basic', 'Standard', 'Module1.xba'), 'w').write(MACRO)
        reg = os.path.join(prof, 'user', 'registrymodifications.xcu')
        t = open(reg).read()
        inner = XCU.split('xsi="http://www.w3.org/2001/XMLSchema-instance">')[1].replace('</oor:items>', '')
        open(reg, 'w').write(t.replace('</oor:items>', inner + '</oor:items>'))
    return prof


def recalc(path, delete=None, sheet='Ведомость'):
    """Пересчитать книгу в LibreOffice; delete=(первая строка листа sheet, сколько строк) — сначала удалить строки."""
    prof = lo_profile()
    open(os.path.join(prof, 'user', 'basic', 'Standard', 'Module1.xba'), 'w').write(MACRO)
    out = path + '.out.xlsx'
    if os.path.exists(out):
        os.remove(out)
    env = dict(os.environ, SAL_USE_VCLPLUGIN='svp')
    sub = 'RecalcSave'
    if delete:
        env.update(TAKS_DEL_START=str(delete[0] - 1), TAKS_DEL_COUNT=str(delete[1]), TAKS_DEL_SHEET=sheet)
        sub = 'DelRecalcSave'
    subprocess.run(['soffice', '--headless', '--norestore', '-env:UserInstallation=file://' + prof,
                    'vnd.sun.star.script:Standard.Module1.%s?language=Basic&location=application' % sub, path],
                   capture_output=True, timeout=1800, env=env)
    if not os.path.exists(out):
        raise RuntimeError('LibreOffice не пересчитал книгу')
    return out


def read_values(path, sheets_wanted):
    with zipfile.ZipFile(path) as z:
        ss = []
        if 'xl/sharedStrings.xml' in z.namelist():
            x = z.read('xl/sharedStrings.xml').decode('utf-8')
            for m in re.finditer(r'<si>(.*?)</si>', x, re.S):
                ss.append(''.join(html.unescape(t) for t in re.findall(r'<t[^>]*>(.*?)</t>', m.group(1), re.S)))
        wb = z.read('xl/workbook.xml').decode('utf-8')
        rels = z.read('xl/_rels/workbook.xml.rels').decode('utf-8')
        relmap = {a: b for a, b in re.findall(r'Id="([^"]+)"[^>]*?Target="([^"]+)"', rels)}
        relmap.update({b: a for a, b in re.findall(r'Target="([^"]+)"[^>]*?Id="([^"]+)"', rels)})
        out = {}
        for n, rid in re.findall(r'<sheet [^>]*?name="([^"]+)"[^>]*?r:id="([^"]+)"', wb):
            n = html.unescape(n)
            if n not in sheets_wanted:
                continue
            x = z.read('xl/' + relmap[rid].lstrip('/').replace('xl/', '')).decode('utf-8')
            vals = {}
            for m in re.finditer(r'<c r="([A-Z]+\d+)"([^>]*?)(?:/>|>(.*?)</c>)', x, re.S):
                inner = m.group(3) or ''
                t = re.search(r' t="([^"]+)"', m.group(2))
                t = t.group(1) if t else None
                vm = re.search(r'<v>(.*?)</v>', inner, re.S)
                if t == 's' and vm:
                    v = ss[int(vm.group(1))]
                elif t == 'inlineStr':
                    im = re.search(r'<t[^>]*>(.*?)</t>', inner, re.S)
                    v = html.unescape(im.group(1)) if im else ''
                elif t in ('str', 'e'):
                    v = ('#ERR:' if t == 'e' else '') + html.unescape(vm.group(1) if vm else '')
                elif t == 'b':
                    v = vm.group(1) == '1' if vm else None
                elif vm:
                    v = float(vm.group(1))
                else:
                    v = None
                vals[m.group(1)] = v
            out[n] = vals
        names = {html.unescape(n): html.unescape(v) for n, v in re.findall(r'<definedName [^>]*?name="([^"]+)"[^>]*>([^<]+)</definedName>', wb)}
    return out, names


# ------------------------------------------------------------------ сравнение
def norm(v):
    if isinstance(v, bool):
        return v
    if isinstance(v, (int, float)):
        return float(v)
    if v is None:
        return ''
    s = str(v).replace(' ', ' ').replace(' ', ' ')
    if re.fullmatch(r'-?\d+(,\d+)?', s.strip()):
        return float(s.strip().replace(',', '.'))
    if re.fullmatch(r'-?\d+(\.\d+)?(E-?\d+)?', s.strip(), re.I):
        return float(s.strip())
    return s


def loc_norm(s, excel):
    """Тексты с числами к виду ядра (ru-RU): без разделителей тысяч, десятичная запятая."""
    if not isinstance(s, str):
        return s
    s = re.sub(r'(?<=\d)[ \u00a0\u202f](?=\d{3}(?!\d))', '', s)
    if excel and LOCALE != 'ru-RU':
        s = re.sub(r'(?<=\d),(?=\d{3}(?!\d))', '', s)
        s = re.sub(r'(?<=\d)\.(?=\d)', ',', s)
    return s


def same(a, b):
    a, b = norm(loc_norm(a, False)), norm(loc_norm(b, True))
    if a == '' and b in ('', 0.0):
        return True
    if b == '' and a in ('', 0.0):
        return True
    if isinstance(a, float) and isinstance(b, float):
        return abs(a - b) <= 1e-9 * max(1, abs(a))
    return a == b


def compare(vec_name, project, verbose=False):
    tmp = tempfile.mkdtemp(prefix='taksx_')
    n = len(project['trees'])
    prot = os.environ.get('TAKS_PROTECT') == '1'          # вариант с защитой доступа (.xlsm): формулы те же, листы скрыты
    tpl = BX.build(max(n + 2, 20), os.path.join(tmp, 'tpl.xlsm' if prot else 'tpl.xlsx'), protect=prot)
    filled = os.path.join(tmp, 'case.xlsm' if prot else 'case.xlsx')
    fill_workbook(tpl, filled, project)
    out = recalc(filled)
    all_sheets = set(ALL_SHEETS)
    V, names = read_values(out, all_sheets)
    res = E.compute(project, REF)
    L = BX.letters(BX.cols_spec())
    diffs = []
    checked = 0

    def chk(label, x, y):
        nonlocal checked
        checked += 1
        if not same(x, y):
            diffs.append((label, x, y))
    # ни одной ошибки формулы (#ЗНАЧ!, #Н/Д …) ни на одном листе
    for sh in sorted(all_sheets):
        bad = sorted(ref for ref, v in V[sh].items() if isinstance(v, str) and v.startswith('#ERR'))
        chk('ошибки формул на листе %s' % sh, [], bad[:5])
    W = V['Ведомость']
    rows_by_idx = {r['idx']: r for r in res['rows']}
    numbers = {}
    for i, s in enumerate(REF['special'] + REF['species']):
        numbers.setdefault(s['name'].lower(), (i + 1, s['name']))      # как MATCH: первое вхождение
    for r in res['rows']:
        rr = BX.FIRST + r['idx']
        e = W.get('%s%d' % (L['err'], rr))
        chk('Ошибка (есть/нет) %d' % rr, bool(r['errors']), e not in (None, ''))
        if r['info']['found']:
            chk('Порода по справочнику %d' % rr, '№%d %s' % numbers[r['species'].lower()], W.get('%s%d' % (L['sphint'], rr)))
    for i in range(n):
        r = rows_by_idx.get(i)
        rr = BX.FIRST + i
        g = lambda name: W.get('%s%d' % (L[name], rr))
        if not r:
            continue
        chk('no %d' % rr, r['no'], g('no'))
        chk('title %d' % rr, r['title'], g('title'))
        chk('N %d' % rr, r['N'], g('N'))
        chk('H %d' % rr, r['H'], g('Htxt'))
        if r['removed'] and r['rm']:
            m = r['rm']
            chk('J %d' % rr, m['J'], g('J'))
            if m['AR'] is not None:
                chk('AR %d' % rr, m['AR'], g('AR'))
                chk('AS %d' % rr, m['AS'], g('AS'))
            if E.is_num(m['Y']) or m['Y'] == '-':
                chk('Y %d' % rr, m['Y'], g('Y'))
                chk('G %d' % rr, m['G'], g('G'))
            chk('M %d' % rr, m['M'], g('M'))
            w = m['wood']
            if not w['err']:
                for k in ('BW', 'BX', 'BY', 'BZ', 'CB', 'CC', 'CD', 'SBm3', 'SBt', 'SSm3', 'SSt'):
                    chk('%s %d' % (k, rr), '' if w[k] is None else w[k], g(k))
        if r['transplanted']:
            chk('ball %d' % rr, r['ball'], g('ball'))
    # именованные итоги
    def named(nm):
        m = re.match(r"'?([^'!]+)'?!\$([A-Z]+)\$(\d+)$", names[nm])
        return V[m.group(1)].get(m.group(2) + m.group(3))
    T = res['removal']
    AP, AN, C = T['money'], T['plant'], T['count']
    pairs = [('C_X', C['хвой.']), ('C_M', C['медл.']), ('C_B', C['быст.']), ('C_P', C['плод.']), ('C_L', C['малоц.']), ('C_TREES', C['trees']),
             ('C_TPLOT', C['treePlots']), ('C_SPLOT', C['shrubPlots']), ('C_H1', C['hedge1']), ('C_H2', C['hedge2']),
             ('C_TR35', C['treesC2035']), ('C_SH35', C['shrubsC2035']), ('PLOT_T', T['treePlots']['total']), ('PLOT_S', T['shrubPlots']['total']),
             ('AP_X', AP['хвой.']), ('AP_M', AP['медл.']), ('AP_B', AP['быст.']), ('AP_P', AP['плод.']), ('AP_L', AP['малоц.']),
             ('AP_TREES', AP['trees']), ('AP_SHRUBS', AP['shrubs']), ('AP_TOTAL', AP['total']),
             ('AN_X', AN['хвой.']), ('AN_M', AN['медл.']), ('AN_B', AN['быст.']), ('AN_TREES', AN['trees']), ('AN_SHRUBS', AN['shrubs']),
             ('AN_ALL', AN['shrubsAll']), ('AN_H1', AN['hedge1']), ('AN_H2', AN['hedge2']),
             ('EX_TREES', res['existing']['trees']), ('EX_SHRUBS', res['existing']['shrubs']), ('EX_PD', res['existing']['treePlots']),
             ('EX_PK', res['existing']['shrubPlots']), ('EX_H1', res['existing']['hedge1']), ('EX_H2', res['existing']['hedge2']),
             ('TR_TREES', res['transplant']['trees']), ('TR_SHRUBS', res['transplant']['shrubs']), ('TR_H1', res['transplant']['hedge1']),
             ('TR_H2', res['transplant']['hedge2']), ('TR_X', res['transplant']['byType']['хвой.']), ('TR_P', res['transplant']['byType']['плод.']),
             ('VOL_SBM3', res['volume']['shrub']['branchesM3']), ('VOL_SBT', res['volume']['shrub']['branchesT']), ('VOL_SSM3', res['volume']['shrub']['stumpsM3']), ('VOL_SST', res['volume']['shrub']['stumpsT']), ('BASE', res['baseValue'])]
    for nm, val in pairs:
        chk(nm, val, named(nm))
    # таблица древесины в «Итогах»
    vol = res['volume']
    ws_it = V['Итоги']
    vr0 = int(re.search(r'\$(\d+):', names['VOL_TBL']).group(1))
    keys = ['le50', 'gt50', 'stemsM3', 'stemsT', 'stumpsM3', 'stumpsT', 'branchesM3', 'branchesT']
    for j, key in enumerate(keys):
        for c, col in enumerate('BCD'):
            chk('vol %s %d' % (key, c + 1), vol[key][c + 1], ws_it.get('%s%d' % (col, vr0 + j)))
    # ведомость удаляемых: перечень, под ним итоги
    WR = V['Удаляемые']
    F0 = BX.LFIRST
    rem = [r for r in res['rows'] if r['removed']]
    for i, r in enumerate(rem):
        rr = F0 + i
        chk('УД no %d' % rr, r['no'], WR.get('A%d' % rr))
        chk('УД title %d' % rr, r['title'], WR.get('B%d' % rr))
        chk('УД qty %d' % rr, r['qty'], WR.get('C%d' % rr))
        if E.is_num(r['rm']['Y']):
            chk('УД G %d' % rr, r['rm']['G'], WR.get('G%d' % rr))
        chk('УД прим %d' % rr, r['rm']['Hnote'], WR.get('H%d' % rr))
    chk('УД пустая строка', '', WR.get('A%d' % (F0 + len(rem))))
    for i, rowv in enumerate(res['removalReport']):
        for j, col in enumerate('ABCDEFGH'):
            rr = F0 + len(rem) + 1 + i
            chk('rep %s%d' % (col, rr), rowv[j], WR.get('%s%d' % (col, rr)))
    chk('УД после итогов', '', WR.get('B%d' % (F0 + len(rem) + 1 + len(res['removalReport']))))
    # древесина по позициям
    WD = V[BX.SH_VOL]
    for i, r in enumerate(rem):
        rr = F0 + i
        w = r['rm']['wood']
        if w['err']:
            continue
        num = lambda v, m=1: '' if v is None else v * m
        for col, val in (('G', num(w['BY'])), ('H', num(w['BW'])), ('I', num(w['BZ'], 0.2)), ('J', num(w['BX'], 0.2)),
                         ('K', num(w['BZ'])), ('L', num(w['BX'])), ('M', num(w['SBm3'])), ('N', num(w['SBt'])),
                         ('O', num(w['SSm3'])), ('P', num(w['SSt']))):
            chk('ДР %s%d' % (col, rr), val, WD.get('%s%d' % (col, rr)))
    vol = res['volume']
    tr0 = F0 + len(rem) + 1
    for col, val in (('G', vol['branchesM3']['all']), ('H', vol['branchesT']['all']), ('I', vol['stumpsM3']['all']), ('J', vol['stumpsT']['all']),
                     ('K', vol['stemsM3']['all']), ('L', vol['stemsT']['all']), ('M', vol['shrub']['branchesM3']), ('N', vol['shrub']['branchesT']),
                     ('O', vol['shrub']['stumpsM3']), ('P', vol['shrub']['stumpsT'])):
        chk('ДР итого %s' % col, val, WD.get('%s%d' % (col, tr0)))
    # сводные таблицы справа на листе «Объемы» (колонки S–W)
    sh = vol['shrub']
    for col, m3, t in (('S', E.xround(vol['stemsM3']['all'], 2), E.xround(vol['stemsT']['all'], 3)),
                       ('T', E.xround(vol['stumpsM3']['all'], 2), E.xround(vol['stumpsT']['all'], 2)),
                       ('U', E.xround(vol['branchesM3']['all'], 2), E.xround(vol['branchesT']['all'], 2)),
                       ('V', E.xround(sh['branchesM3'], 3), E.xround(sh['branchesT'], 3)), ('W', E.xround(sh['stumpsM3'], 3), E.xround(sh['stumpsT'], 3))):
        chk('ДР сводка м3 %s' % col, m3, WD.get('%s7' % col))
        chk('ДР сводка т %s' % col, t, WD.get('%s8' % col))
    for i, key in enumerate(['le50', 'gt50', 'stemsM3', 'stumpsM3', 'branchesM3', 'stemsT', 'stumpsT', 'branchesT']):
        d = vol[key]
        for j, col in enumerate('STUV'):
            v = d[j + 1] if j < 3 else d.get('all', d[1] + d[2] + d[3])
            chk('ДР типы %s %s' % (key, col), v, WD.get('%s%d' % (col, 13 + i)))
    for i, b in enumerate(vol['stemsByDiameter']):
        for j, col in enumerate('STUV'):
            chk('ДР стволы %s %s' % (b['label'], col), b[j + 1] if j < 3 else b['all'], WD.get('%s%d' % (col, 25 + i)))
    # «Баланс существующих деревьев и кустарников» для AutoCAD (лист «Объемы», колонки S–AA, строки 38–41)
    b = res['balance']
    for i, key in enumerate(['kept', 'transplanted', 'removed', 'total']):
        for j, f in enumerate(['trees', 'leafy', 'fruit', 'conif', 'shrubs', 'hedge1', 'hedge2', 'treePlots', 'shrubPlots']):
            col = ['S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z', 'AA'][j]
            chk('ДР баланс %s %s' % (key, f), b[key][f], WD.get('%s%d' % (col, 38 + i)))
    # существующие и сохраняемые
    WE, WK = V['Существующие'], V['Сохраняемые']
    kept = [r for r in res['rows'] if r['H'] == 'сохранить']
    for i, r in enumerate(res['rows']):
        chk('СУЩ no %d' % i, r['no'], WE.get('A%d' % (F0 + i)))
    for i, r in enumerate(kept):
        chk('СОХР title %d' % i, r['title'], WK.get('B%d' % (F0 + i)))
    kb = res['balance']['kept']
    for j, key in enumerate(['trees', 'leafy', 'fruit', 'conif', 'shrubs', 'treePlots', 'shrubPlots', 'hedge1', 'hedge2']):
        chk('СОХР итог %s' % key, kb[key], WK.get('C%d' % (F0 + len(kept) + 1 + j)))
    # пересаживаемые
    WT = V['Пересаживаемые']
    trs = [r for r in res['rows'] if r['transplanted']]
    for i, r in enumerate(trs):
        chk('ПЕР ком %d' % i, r['ball'], WT.get('G%d' % (F0 + i)))
    trt = res['transplant']
    for j, val in enumerate([trt['trees'], trt['byType']['хвой.'], trt['byType']['плод.'], trt['shrubs'], trt['hedge1'], trt['hedge2']]):
        chk('ПЕР итог %d' % j, val, WT.get('C%d' % (F0 + len(trs) + 1 + j)))
    # цветники, газоны, иной травяной покров: лист ввода
    G = res['greens']
    WG, WL = V[BX.SH_G], V[BX.SH_GL]
    GL = BX.letters(BX.greens_spec())
    for r in G['rows']:
        rr = BX.G_FIRST + r['idx']
        g = lambda name: WG.get('%s%d' % (GL[name], rr))
        chk('ЦГ № %d' % rr, r['no'], g('gno'))
        chk('ЦГ решение %d' % rr, r['decision'], g('gdec'))
        chk('ЦГ компенсация %d' % rr, r['comp'], g('gcomp'))
        chk('ЦГ выплата %d' % rr, r['value'], g('gv'))
        chk('ЦГ ошибка %d' % rr, r['error'], g('gerr'))
    # готовая ведомость (для AutoCAD): перечень, под ним итоги, справа баланс
    for i, r in enumerate(G['rows']):
        rr = F0 + i
        chk('ВЦГ № %d' % rr, r['no'], WL.get('A%d' % rr))
        chk('ВЦГ вид %d' % rr, r['title'], WL.get('B%d' % rr))
        chk('ВЦГ площадь %d' % rr, '' if r['area'] is None else r['area'], WL.get('C%d' % rr))
        chk('ВЦГ состояние %d' % rr, r['condShow'], WL.get('D%d' % rr))
        chk('ВЦГ решение %d' % rr, r['decision'], WL.get('E%d' % rr))
        chk('ВЦГ компенсация %d' % rr, r['comp'], WL.get('F%d' % rr))
    ng = len(G['rows'])
    chk('ВЦГ пустая строка', '', WL.get('B%d' % (F0 + ng)))
    for i, line in enumerate(res['greensReport']):
        for j, col in enumerate('ABCDEF'):
            chk('ВЦГ итог %s%d' % (col, i), line[j], WL.get('%s%d' % (col, F0 + ng + 1 + i)))
    chk('ВЦГ после итогов', '', WL.get('B%d' % (F0 + ng + 1 + len(res['greensReport']))))
    for i, key in enumerate(['kept', 'transplanted', 'removed', 'total']):
        for j, cat in enumerate(E.GREEN_CATS):
            chk('ВЦГ баланс %s %s' % (key, cat), G['balance'][cat][key], WL.get('%s%d' % ('IJK'[j], BX.HDR_ROW + 1 + i)))
    gt = G['totals']
    for nm, val in (('GZ_N', len(G['rows'])), ('GZ_ERR', gt['errors']), ('GZ_V', gt['payment']), ('GZ_PF', gt['plantFlower']),
                    ('GZ_PL', gt['plantLawn']), ('GZ_NM', gt['nMoney']), ('GZ_NP', gt['nPlant'])):
        chk(nm, val, named(nm))
    # имена для связи с AutoCAD: размер таблиц (строк × столбцов) — ровно шапка, позиции и итоги
    WI = V['Итоги']
    acad = {}
    for ref, v in WI.items():
        if ref.startswith('A') and ref[1:].isdigit() and isinstance(v, str) and v.startswith('АКАД_'):
            r = int(ref[1:])
            acad[v] = (WI.get('B%d' % r), WI.get('C%d' % r))
    ex_n, kept_n = len(res['rows']), len([r for r in res['rows'] if r['H'] == 'сохранить'])
    tr_n = len([r for r in res['rows'] if r['transplanted']])
    rep_n = len(res['removalReport'])
    exp = {'АКАД_Существующие': (1 + ex_n + 1 + 6, 7), 'АКАД_Существующие_перечень': (1 + ex_n, 7),
           'АКАД_Сохраняемые': (1 + kept_n + 1 + 9, 7), 'АКАД_Сохраняемые_перечень': (1 + kept_n, 7),
           'АКАД_Удаляемые': (1 + len(rem) + 1 + rep_n, 8), 'АКАД_Удаляемые_перечень': (1 + len(rem), 8),
           'АКАД_Пересаживаемые': (1 + tr_n + 1 + 6, 8), 'АКАД_Пересаживаемые_перечень': (1 + tr_n, 8),
           'АКАД_Отходы': (1 + len(rem) + 1 + 7, 16), 'АКАД_Отходы_перечень': (1 + len(rem), 16),
           'АКАД_Цветники': (1 + ng + 1 + 5, 6), 'АКАД_Цветники_перечень': (1 + ng, 6),
           'АКАД_ОбъемыОтходов': (3, 6), 'АКАД_ОбъемыПоТипам': (9, 5), 'АКАД_Стволы': (8, 5),
           'АКАД_Баланс': (8, 10 if res['existing']['shrubPlots'] > 0 else 9 if res['existing']['treePlots'] > 0 else 8),
           'АКАД_БалансЦветники': (6, 4)}
    chk('имена АКАД_ все на месте', sorted(exp), sorted(acad))
    for nm, (rows_e, cols_e) in exp.items():
        got = acad.get(nm, (None, None))
        chk('%s строк' % nm, rows_e, got[0])
        chk('%s столбцов' % nm, cols_e, got[1])
    shutil.rmtree(tmp, ignore_errors=True)
    print('%s: проверено %d значений, расхождений %d' % (vec_name, checked, len(diffs)))
    for d in diffs[:200 if verbose else 25]:
        print('   %-18s ядро=%r  excel=%r' % d)
    return checked, len(diffs)


def new_rules_project(money=True):
    """Набор на правила Таксации Лайт: К1–К4 по строке, пустая причина, пустое кол-во, поросль по строкам (в т.ч. «у дорог»,
    менее 10 м²), живая изгородь, пересадка без причины."""
    T = [
        {'a': '', 'noIn': '4-10', 'species': 'Береза бородавчатая', 'height': 21, 'diam': 22, 'cond': 'уд.', 'action': 'В', 'reason': ''},
        {'a': '', 'species': 'Береза бородавчатая', 'height': 21, 'diam': 22, 'cond': 'уд.', 'action': 'В', 'reason': 'СЕТИ', 'k4': ' да '},
        {'a': 3, 'species': 'Ель колючая', 'height': 15, 'diam': '30,24,14', 'cond': 'хор.', 'action': 'В', 'k1': 'да', 'k2': 'нет', 'pay': 'Посадками ', 'ptype': 'быстрорастущие лиственные деревья'},
        {'a': 20, 'species': 'Уч-к поросли дер.', 'height': 1, 'diam': '0-4', 'action': 'В', 'reason': 'СЕТИ'},
        {'a': 5, 'species': 'Уч-к поросли дер.', 'height': 1, 'diam': '0-4', 'action': 'В'},
        {'a': 12, 'species': '*Уч-к поросли дер. у дорог', 'height': 1, 'diam': '0-4', 'action': 'В', 'k3': 'да'},
        {'a': 15, 'species': 'Уч-к поросли куст.', 'height': 1, 'diam': '-', 'action': 'В', 'pay': 'посадками', 'ptype': 'однорядная живая изгородь'},
        {'a': 6, 'species': 'Уч-к поросли дер.', 'height': 1, 'diam': '0-4', 'action': 'В', 'pay': 'ВЫПЛАТАМИ'},
        {'a': 8, 'species': 'Однорядная живая изгородь', 'height': 1.2, 'diam': '-', 'action': 'В'},
        {'a': 1, 'species': 'Сирень обыкновенная', 'height': 2.5, 'diam': '-', 'action': 'В', 'note': 'V=50%', 'pay': 'посадками'},
        {'a': '', 'species': 'Дуб черешчатый', 'height': 18, 'diam': 56, 'cond': 'хор.', 'action': 'В', 'k1': ' нет ', 'pay': 'бюджет', 'ptype': 'красивоцветущие кусты'},
        {'a': '', 'species': 'Липа мелколистная', 'height': 8, 'diam': 14, 'cond': 'хор.', 'action': 'П'},
        {'a': 2, 'noIn': '50', 'species': 'Яблоня домашняя', 'height': 4, 'diam': 16, 'cond': 'уд.', 'action': ''},
        {'a': 4, 'noIn': '60-62', 'species': 'Яблоня домашняя', 'height': 4, 'diam': 16, 'cond': 'уд.', 'action': ''},
        {'a': '', 'noIn': '12а', 'species': 'Клен остролистный', 'height': 9, 'diam': 18, 'cond': 'хор.', 'action': 'В', 'k3': 'Да'},
        {'a': '', 'species': 'берез', 'height': 9, 'diam': 18, 'action': 'В'},
        {'a': 2, 'species': str(1 + [x['name'] for x in REF['special'] + REF['species']].index('Дуб черешчатый')), 'height': 16, 'diam': 40, 'action': 'В'},
        {'a': '', 'species': '9999', 'height': 9, 'diam': 18, 'action': 'В'},
        {'a': 6, 'species': '1', 'height': 1, 'diam': '0-4', 'action': 'В'},
        {'a': 4, 'species': 'Барбарис обыкновенный', 'height': 1.5, 'diam': '-', 'action': 'В', 'ptype': 'Красивоцветущие кусты'},
        {'a': '', 'species': 'береза повислая', 'height': 15, 'diam': '30,18', 'action': 'В', 'k2': 'да'},
        {'a': '', 'species': 'Сосна обыкновенная', 'height': 12, 'diam': 28, 'action': ''},
    ]
    S = {'date': '2026-03-01', 'k1': False, 'k2': True, 'k3': False, 'k4': False, 'money': money, 'unit': 'баз.в. (руб.)',
         'view': 'формула + итог', 'defaultCond': 'уд.', 'shrubMassKg': 5, 'flowerMode': 'метрами', 'showPlots': True, 'hedgePieces': True,
         'reasons': [{'name': 'СЕТИ', 'group': '', 'replace': ''}, {'name': 'БЛАГОУСТРОЙСТТВО', 'group': '', 'replace': ''},
                     {'name': 'АВАРИЯ', 'group': '', 'replace': 'без компенcации'}],
         'compMap': dict(E.default_settings(E.prepare_ref(REF))['compMap'], trees='' if money else 'медл.', shrubs='медл..' if money else ''),
         'bushArea': 1 if money else 1.5,
         'plotDensity': 'редкий' if money else 'густой'}
    return {'settings': S, 'trees': T, 'greens': GREENS_NEW}


# цветники, газоны, иной травяной покров: все виды, состояние (пусто = хор.; для иного покрова не учитывается), В/П/сохранить,
# причины (в т.ч. «без компенсации»), К1–К4 и способ компенсации в строке, свои номера, примечание, пустые строки и ошибки ввода
GREENS_NEW = [
    {'kind': 'Цветник из многолетников', 'area': 4, 'cond': 'хор.', 'action': 'В'},
    {'kind': 'Газон обыкновенный', 'area': 30, 'cond': 'уд.', 'action': 'В'},
    {'kind': 'Газон партерный', 'area': 12.5, 'action': 'В', 'pay': 'посадками'},
    {'kind': 'Цветник из однолетников и двулетников', 'area': 6, 'action': 'В', 'pay': 'Посадками '},
    {},
    {'kind': 'Иной травяной покров', 'area': 40, 'cond': 'плох.', 'action': 'В', 'k4': 'да'},
    {'kind': 'Иной травяной покров', 'area': 25, 'action': 'В', 'pay': 'посадками'},
    {'kind': 'Газон спортивный', 'area': 100, 'cond': 'хор.', 'action': 'П', 'reason': 'СЕТИ'},
    {'kind': 'Газон луговой', 'area': 200, 'cond': 'уд.'},
    {'noIn': 'Г-1', 'kind': 'газон мавританский', 'area': 15, 'cond': 'хор.', 'action': 'в', 'k1': 'да', 'k2': 'нет'},
    {'kind': 'Газон специального назначения', 'area': 8, 'cond': 'ненадл.', 'action': 'В'},
    {'kind': 'Клумба', 'area': 5, 'action': 'В'},
    {'kind': 'Газон партерный', 'action': 'В'},
    {'area': 7, 'action': 'В'},
    {'noIn': '20', 'kind': 'Газон обыкновенный', 'area': 'много', 'action': 'В'},
    {'kind': 'Газон обыкновенный', 'area': 0, 'action': 'В'},
    {'kind': 'Цветник из многолетников', 'area': 3, 'action': 'В', 'reason': 'АВАРИЯ'},
    {'kind': 'Газон обыкновенный', 'area': 9, 'action': 'В', 'reason': 'АВАРИЯ', 'pay': 'посадками'},
    {'noIn': '5', 'kind': 'Газон обыкновенный', 'area': 10.25, 'cond': 'хор.', 'action': 'В', 'pay': 'выплатами', 'note': 'у дома 3'},
    {'kind': 'Газон партерный', 'area': 50, 'action': 'В', 'reason': 'СЕТИ', 'k3': 'да'},
    {'kind': 'Цветник из многолетников', 'area': 2.5, 'cond': 'плох.', 'action': 'П'},
]


def deletion_check():
    total = fails = 0
    for first, cnt, own in ((BX.FIRST + 2, 3, False), (BX.FIRST, 1, False), (BX.FIRST + 6, 2, True)):
        # три строки в середине; первая строка данных; свои номера по плану (с пропусками) — должны сохраниться
        c, d = deletion_case(first, cnt, own)
        total += c
        fails += d
    for first, cnt in ((BX.G_FIRST + 1, 2), (BX.G_FIRST, 1), (BX.G_FIRST + 8, 3)):   # в середине; первая; вокруг своего номера «Г-1»
        c, d = greens_deletion_case(first, cnt)
        total += c
        fails += d
    return total, fails


def greens_deletion_case(first, cnt):
    """Удалить строки листа «Цветники и газоны»: ни одной ошибки формул; ведомость, итоги и баланс — как у ядра без этих строк."""
    tmp = tempfile.mkdtemp(prefix='taksg_')
    project = new_rules_project(True)
    tpl = BX.build(len(project['trees']) + 5, os.path.join(tmp, 'tpl.xlsx'))
    filled = os.path.join(tmp, 'case.xlsx')
    fill_workbook(tpl, filled, project)
    out = recalc(filled, delete=(first, cnt), sheet=BX.SH_G)
    gone = set(range(first - BX.G_FIRST, first - BX.G_FIRST + cnt))
    V, names = read_values(out, set(ALL_SHEETS))
    reduced = dict(project, greens=[g for i, g in enumerate(project['greens']) if i not in gone])
    res = E.compute(reduced, REF)
    diffs, checked = [], 0

    def chk(label, x, y):
        nonlocal checked
        checked += 1
        if not same(x, y):
            diffs.append((label, x, y))
    for sh in ALL_SHEETS:
        chk('ошибки формул на листе %s' % sh, [], sorted(r for r, v in V[sh].items() if isinstance(v, str) and v.startswith('#ERR'))[:5])
    G = res['greens']
    WL = V[BX.SH_GL]
    for i, r in enumerate(G['rows']):
        chk('ВЦГ № %d' % i, r['no'], WL.get('A%d' % (BX.LFIRST + i)))
        chk('ВЦГ компенсация %d' % i, r['comp'], WL.get('F%d' % (BX.LFIRST + i)))
    for i, line in enumerate(res['greensReport']):
        for j, col in enumerate('ABCDEF'):
            chk('ВЦГ итог %s%d' % (col, i), line[j], WL.get('%s%d' % (col, BX.LFIRST + len(G['rows']) + 1 + i)))
    for i, key in enumerate(['kept', 'transplanted', 'removed', 'total']):
        for j, cat in enumerate(E.GREEN_CATS):
            chk('ВЦГ баланс %s %s' % (key, cat), G['balance'][cat][key], WL.get('%s%d' % ('IJK'[j], BX.HDR_ROW + 1 + i)))
    shutil.rmtree(tmp, ignore_errors=True)
    print('удаление строк %d–%d листа «%s»: проверено %d значений, расхождений %d' % (first, first + cnt - 1, BX.SH_G, checked, len(diffs)))
    for d in diffs[:25]:
        print('   %-18s ядро=%r  excel=%r' % d)
    return checked, len(diffs)


def deletion_case(first, cnt, own=False):
    """Удалить строки «Ведомости» целиком: ни одной ошибки формул, итоги совпадают с ядром без этих деревьев."""
    tmp = tempfile.mkdtemp(prefix='taksd_')
    project = new_rules_project(True)
    if own:
        project['trees'] = [dict(t, noIn=str(10 + 3 * i)) for i, t in enumerate(project['trees'])]
    tpl = BX.build(len(project['trees']) + 5, os.path.join(tmp, 'tpl.xlsx'))
    filled = os.path.join(tmp, 'case.xlsx')
    fill_workbook(tpl, filled, project)
    out = recalc(filled, delete=(first, cnt))
    gone = set(range(first - BX.FIRST, first - BX.FIRST + cnt))
    all_sheets = set(ALL_SHEETS)
    V, names = read_values(out, all_sheets)
    reduced = dict(project, trees=[t for i, t in enumerate(project['trees']) if i not in gone])
    res = E.compute(reduced, REF)
    diffs, checked = [], 0

    def chk(label, x, y):
        nonlocal checked
        checked += 1
        if not same(x, y):
            diffs.append((label, x, y))
    for sh in sorted(all_sheets):
        chk('ошибки формул на листе %s' % sh, [], sorted(r for r, v in V[sh].items() if isinstance(v, str) and v.startswith('#ERR'))[:5])

    def named(nm):
        m = re.match(r"'?([^'!]+)'?!\$([A-Z]+)\$(\d+)$", names[nm])
        return V[m.group(1)].get(m.group(2) + m.group(3))
    T = res['removal']
    for nm, val in (('C_TREES', T['count']['trees']), ('AP_TOTAL', T['money']['total']), ('N_REM', sum(1 for r in res['rows'] if r['removed'])),
                    ('N_EX', len(res['rows'])), ('VOL_BZ_ALL', res['volume']['stemsM3']['all']), ('VOL_SBM3', res['volume']['shrub']['branchesM3'])):
        chk(nm, val, named(nm))
    L = BX.letters(BX.cols_spec())
    for i, r in enumerate(res['rows']):
        chk('№ строки %d' % i, r['no'], V['Ведомость'].get('%s%d' % (L['no'], BX.FIRST + i)))
    WR = V['Удаляемые']
    rem = [r for r in res['rows'] if r['removed']]
    for i, r in enumerate(rem):
        chk('Удаляемые № %d' % i, r['no'], WR.get('A%d' % (BX.LFIRST + i)))
        chk('Удаляемые порода %d' % i, r['title'], WR.get('B%d' % (BX.LFIRST + i)))
    for i, rowv in enumerate(res['removalReport']):
        chk('Удаляемые итоги %d' % i, rowv[6], WR.get('G%d' % (BX.LFIRST + len(rem) + 1 + i)))
    shutil.rmtree(tmp, ignore_errors=True)
    print('удаление строк %d–%d%s: проверено %d значений, расхождений %d' % (first, first + cnt - 1, ' (свои номера)' if own else '', checked, len(diffs)))
    for d in diffs[:25]:
        print('   %-18s ядро=%r  excel=%r' % d)
    return checked, len(diffs)


def random_project(seed, money):
    """Случайный набор на правила Таксации Лайт: все виды строк, номера и диапазоны, № породы и неполные названия, К1–К4,
    пустые причины и кол-во, поросль больше и меньше 10 м², изгороди, кусты, пересадка, ошибки ввода, цветники и газоны."""
    import random
    rnd = random.Random(seed)
    lib = REF['special'] + REF['species']
    trees = [s for s in REF['species'] if not s['shrub'] and s.get('hard')]
    shrubs = [s for s in REF['species'] if s['shrub']]
    T = []
    for i in range(140):
        kind = rnd.choices(['tree', 'multi', 'small', 'shrub', 'tplot', 'splot', 'hedge', 'num', 'partial', 'bad'],
                           [30, 12, 6, 14, 6, 5, 5, 8, 3, 2])[0]
        t = {}
        if kind in ('tree', 'multi', 'small'):
            sp = rnd.choice(trees)['name']
            t['diam'] = {'tree': lambda: rnd.randint(4, 80), 'multi': lambda: ','.join(str(rnd.randint(4, 45)) for _ in range(rnd.randint(2, 5))),
                         'small': lambda: rnd.choice(['до1', 'до4', '**до1', '**до4'])}[kind]()
            t['diamAsText'] = kind == 'tree' and rnd.random() < 0.3
            t['height'] = rnd.choice([None, rnd.randint(2, 30), round(rnd.uniform(1, 25), 1)])
        elif kind == 'shrub':
            sp, t['diam'], t['height'] = rnd.choice(shrubs)['name'], '-', rnd.choice([None, round(rnd.uniform(0.3, 4), 1)])
        elif kind == 'tplot':
            sp, t['diam'], t['height'] = rnd.choice(['Уч-к поросли дер.', '*Уч-к поросли дер. у дорог']), '0-4', 1
        elif kind == 'splot':
            sp, t['diam'], t['height'] = rnd.choice(['Уч-к поросли куст.', '*Уч-к поросли куст. у дорог']), '-', 1
        elif kind == 'hedge':
            sp, t['diam'], t['height'] = rnd.choice(['Однорядная живая изгородь', 'Двухрядная живая изгородь']), '-', round(rnd.uniform(0.5, 2), 1)
        elif kind == 'num':
            n = rnd.randint(7, len(lib))
            s0 = lib[n - 1]
            sp, t['diam'], t['height'] = str(n), ('-' if s0['shrub'] else rnd.randint(8, 50)), rnd.randint(1, 20)
        elif kind == 'partial':
            sp, t['diam'] = rnd.choice(['берез', 'дуб', 'липа', 'клен']), rnd.randint(8, 40)
        else:
            sp, t['diam'] = rnd.choice(['Неизвестная порода', '9999']), rnd.randint(8, 40)
        t['species'] = sp
        if kind in ('tplot', 'splot'):
            t['a'] = rnd.choice([rnd.randint(1, 9), rnd.randint(10, 60), round(rnd.uniform(2, 30), 1)])
        elif kind == 'hedge':
            t['a'] = rnd.randint(1, 30)
        else:
            t['a'] = rnd.choice(['', '', '', 1, rnd.randint(2, 6)])
        u = rnd.random()
        if u < 0.08:
            a = rnd.randint(1, 300)
            t['noIn'] = '%d-%d' % (a, a + rnd.randint(0, 5))
            t['a'] = ''
        elif u < 0.14:
            t['noIn'] = str(rnd.randint(1, 500))
        elif u < 0.16:
            t['noIn'] = rnd.choice(['12а', '7б'])
        t['cond'] = rnd.choice(['', '', 'хор.', 'уд.', 'плох.', 'ненадл.'])
        t['action'] = rnd.choices(['В', 'П', ''], [65, 10, 25])[0]
        t['reason'] = rnd.choice(['', '', 'СЕТИ', 'БЛАГОУСТРОЙСТТВО'])
        for k in range(1, 5):
            if rnd.random() < 0.12:
                t['k%d' % k] = rnd.choice(['да', 'нет'])
        if kind == 'shrub' and rnd.random() < 0.2:
            t['note'] = 'V=50%'
        if rnd.random() < 0.18:
            t['pay'] = rnd.choice(['выплатами', 'посадками'])
        T.append(t)
    S = {'date': rnd.choice(['2025-06-01', '2026-03-01']), 'k1': rnd.random() < 0.3, 'k2': rnd.random() < 0.6, 'k3': rnd.random() < 0.3,
         'k4': rnd.random() < 0.3, 'money': money, 'unit': rnd.choice(['баз.в.', 'баз.в. (руб.)', 'руб.']),
         'view': rnd.choice(['формула + итог', 'итог']), 'defaultCond': rnd.choice(['уд.', 'хор.']), 'shrubMassKg': 5,
         'flowerMode': rnd.choice(['метрами', 'стоимостью']), 'showPlots': True, 'hedgePieces': True,
         'reasons': [{'name': 'СЕТИ', 'group': '', 'replace': ''}, {'name': 'БЛАГОУСТРОЙСТТВО', 'group': '', 'replace': rnd.choice(['', '2'])}],
         'compMap': dict(E.default_settings(E.prepare_ref(REF))['compMap'], plotTree=rnd.choice(['хвой.', 'медл.', 'быст.'])),
         'bushArea': rnd.choice([1, 1.5]), 'plotDensity': rnd.choice(['редкий', 'средний', 'густой'])}
    fl = [{'kind': rnd.choice(list(REF['flowers'])[:2]), 'cond': rnd.choice(['хор.', 'уд.']), 'area': rnd.randint(1, 40), 'reason': rnd.choice(['', 'СЕТИ'])}
          for _ in range(rnd.randint(0, 4))]
    lw = [{'kind': rnd.choice(list(REF['lawns'])), 'cond': rnd.choice(['хор.', 'уд.', 'плох.']), 'area': rnd.randint(5, 200), 'reason': ''}
          for _ in range(rnd.randint(0, 5))]
    greens = []
    names = [g['name'] for g in REF['greens']]
    for _ in range(30):
        g = {'kind': rnd.choices([rnd.choice(names), rnd.choice(names).lower(), 'Клумба', ''], [80, 8, 2, 2])[0],
             'area': rnd.choices([rnd.randint(1, 300), round(rnd.uniform(0.5, 80), 2), None], [60, 38, 2])[0],
             'cond': rnd.choice(['', '', 'хор.', 'уд.', 'плох.', 'ненадл.']),
             'action': rnd.choices(['В', 'П', ''], [65, 10, 25])[0], 'reason': rnd.choice(['', '', 'СЕТИ', 'БЛАГОУСТРОЙСТТВО'])}
        for k in range(1, 5):
            if rnd.random() < 0.12:
                g['k%d' % k] = rnd.choice(['да', 'нет'])
        if rnd.random() < 0.3:
            g['pay'] = rnd.choice(['выплатами', 'посадками'])
        if rnd.random() < 0.1:
            g['noIn'] = rnd.choice([str(rnd.randint(1, 99)), 'Г-%d' % rnd.randint(1, 9)])
        if rnd.random() < 0.1:
            g['note'] = 'у дома %d' % rnd.randint(1, 30)
        greens.append(g)
    names_t = [x['name'] for x in REF['plantTypes'] if x['group'] == 'дерево']
    names_s = [x['name'] for x in REF['plantTypes'] if x['group'] == 'куст']
    for t in T:
        if rnd.random() < 0.15:
            t['ptype'] = rnd.choice(names_t + names_s)     # и подходящие, и не подходящие строке (тогда — как в настройках)
    S['compMap']['trees'] = rnd.choice(['', '', 'хвой.', 'медл.', 'быст.'])
    S['compMap']['shrubs'] = rnd.choice(['', '', 'лиан.', 'хвой..', 'крас.', 'медл..', 'быст..', 'ж.и.1', 'ж.и.2'])
    return {'settings': S, 'trees': T, 'flowers': fl, 'lawns': lw, 'grass': [], 'projected': {}, 'greens': greens}


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('-')]
    verbose = '-v' in sys.argv
    vdir = os.path.join(HERE, 'vectors')
    files = sorted(f for f in os.listdir(vdir) if f.endswith('.json'))
    if args:
        files = [f for f in files if any(a in f for a in args)]
    total = fails = 0
    cases = [(f, json.load(open(os.path.join(vdir, f), encoding='utf-8'))['project']) for f in files]
    if not args or any('new' in a for a in args):
        cases += [('new_rules_money', new_rules_project(True)), ('new_rules_planting', new_rules_project(False))]
    if not args or any('rand' in a for a in args):
        cases += [('random_%d_%s' % (sd, 'money' if m else 'planting'), random_project(sd, m)) for sd, m in ((1, True), (2, False), (3, True))]
    for f, project in cases:
        c, d = compare(f, project, verbose)
        total += c
        fails += d
    if not args or any('del' in a for a in args):
        c, d = deletion_check()
        total += c
        fails += d
    print('Итого: проверено %d, расхождений %d' % (total, fails))
    return 1 if fails else 0


if __name__ == '__main__':
    sys.exit(main())
