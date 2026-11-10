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
XCU = """<?xml version="1.0" encoding="UTF-8"?>
<oor:items xmlns:oor="http://openoffice.org/2001/registry" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
<item oor:path="/org.openoffice.Setup/L10N"><prop oor:name="ooSetupSystemLocale" oor:op="fuse"><value>ru-RU</value></prop></item>
<item oor:path="/org.openoffice.Setup/L10N"><prop oor:name="ooLocale" oor:op="fuse"><value>ru-RU</value></prop></item>
<item oor:path="/org.openoffice.Office.Calc/Formula/Load"><prop oor:name="OOXMLRecalcMode" oor:op="fuse"><value>0</value></prop></item>
</oor:items>"""


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
        rows = re.findall(r'(<row r="(\d+)".*?(?:/>|</row>))', xml, re.S)
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
            'S_MASS': S['shrubMassKg'], 'S_FMODE': S['flowerMode'], 'S_PLOTS': yn(S['showPlots']), 'S_HEDGE': yn(S['hedgePieces']),
            'S_PLOTTREE': S['compMap']['plotTree'], 'S_PLOTSHRUB': S['compMap']['plotShrub']}
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
        put(sh, 'C%d' % (r1 + i), S['compMap'].get(t) or None)
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
        put('Ведомость', L['sp'] + str(r), t['species'])
        put('Ведомость', L['h'] + str(r), t.get('height'))
        put('Ведомость', L['d'] + str(r), num_or_text(t.get('diam')) if t.get('diam') not in (None, '') else None)
        put('Ведомость', L['cond'] + str(r), t.get('cond') or None)
        put('Ведомость', L['act'] + str(r), t.get('action') or None)
        put('Ведомость', L['rsn'] + str(r), t.get('reason') or None)
        kr = t.get('kRepl')
        put('Ведомость', L['kr'] + str(r), num_or_text(kr) if kr not in (None, '') else None)
        put('Ведомость', L['note'] + str(r), t.get('note') or None)
        for k in range(1, 5):
            put('Ведомость', L['k%din' % k] + str(r), t.get('k%d' % k) or None)
    # цветники
    area_rows = {}
    for key, title, n, tk in BX.AREA_BLOCKS:
        nm = '%s_AREA' % tk
        sh, ref = cell_of(nm)
        tot_row = int(re.sub(r'\D', '', ref))
        area_rows[key] = tot_row - n
    srcs = {'fl': project.get('flowers', []), 'lw': project.get('lawns', []), 'gr': project.get('grass', [])}
    for key, title, n, tk in BX.AREA_BLOCKS:
        r0 = area_rows[key]
        for i in range(n):
            x = srcs[key][i] if i < len(srcs[key]) else None
            r = r0 + i
            if key != 'gr':
                put('Цветники и газоны', 'B%d' % r, x.get('kind') if x else None)
            put('Цветники и газоны', 'C%d' % r, (x.get('cond') or None) if x else None)
            put('Цветники и газоны', 'D%d' % r, x.get('area') if x else None)
            put('Цветники и газоны', 'E%d' % r, ('п' if x.get('transplant') else None) if x else None)
            put('Цветники и газоны', 'F%d' % r, (x.get('reason') or None) if x else None)
            put('Цветники и газоны', 'G%d' % r, (x.get('note') or None) if x else None)
            kr = x.get('kRepl') if x else None
            put('Цветники и газоны', 'H%d' % r, num_or_text(kr) if kr not in (None, '') else None)
    pr = project.get('projected', {})
    for nm, v in [('PR_PER', pr.get('perennial')), ('PR_ANN', pr.get('annual'))] + \
                 [('PR_L%d' % i, (pr.get('lawns') or {}).get(k)) for i, k in enumerate(REF['lawns'])]:
        sh, ref = cell_of(nm)
        put(sh, ref, v)
    for sh, cells in edits.items():
        path = sheets[sh]
        files[path] = set_cells(files[path].decode('utf-8'), cells).encode('utf-8')
    with zipfile.ZipFile(dst, 'w', zipfile.ZIP_DEFLATED) as z:
        for n, b in files.items():
            z.writestr(n, b)
    return sheets, names, area_rows


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
</script:module>"""


def lo_profile():
    prof = os.path.join(tempfile.gettempdir(), 'taks_lo_profile')
    if not os.path.exists(os.path.join(prof, 'user', 'basic', 'Standard', 'Module1.xba')):
        subprocess.run(['soffice', '--headless', '--terminate_after_init', '-env:UserInstallation=file://' + prof], capture_output=True, timeout=180)
        open(os.path.join(prof, 'user', 'basic', 'Standard', 'Module1.xba'), 'w').write(MACRO)
        reg = os.path.join(prof, 'user', 'registrymodifications.xcu')
        t = open(reg).read()
        inner = XCU.split('xsi="http://www.w3.org/2001/XMLSchema-instance">')[1].replace('</oor:items>', '')
        open(reg, 'w').write(t.replace('</oor:items>', inner + '</oor:items>'))
    return prof


def recalc(path):
    prof = lo_profile()
    out = path + '.out.xlsx'
    if os.path.exists(out):
        os.remove(out)
    env = dict(os.environ, SAL_USE_VCLPLUGIN='svp')
    subprocess.run(['soffice', '--headless', '--norestore', '-env:UserInstallation=file://' + prof,
                    'vnd.sun.star.script:Standard.Module1.RecalcSave?language=Basic&location=application', path],
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


def same(a, b):
    a, b = norm(a), norm(b)
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
    tpl = BX.build(max(n + 2, 20), os.path.join(tmp, 'tpl.xlsx'))
    filled = os.path.join(tmp, 'case.xlsx')
    fill_workbook(tpl, filled, project)
    out = recalc(filled)
    V, names = read_values(out, {'Ведомость', 'Итоги', 'Удаляемые', 'Баланс', 'Цветники и газоны', 'Пересаживаемые', 'Настройки',
                                 'Существующие', 'Сохраняемые', 'Древесина'})
    res = E.compute(project, REF)
    L = BX.letters(BX.cols_spec())
    diffs = []
    checked = 0

    def chk(label, x, y):
        nonlocal checked
        checked += 1
        if not same(x, y):
            diffs.append((label, x, y))
    W = V['Ведомость']
    rows_by_idx = {r['idx']: r for r in res['rows']}
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
                for k in ('BV', 'BW', 'BX', 'BY', 'BZ', 'CB', 'CC', 'CD'):
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
             ('VOL_BV', res['volume']['shrubT']['all']), ('BASE', res['baseValue'])]
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
    WD = V['Древесина']
    for i, r in enumerate(rem):
        rr = F0 + i
        w = r['rm']['wood']
        if w['err']:
            continue
        num = lambda v, m=1: '' if v is None else v * m
        for col, val in (('G', num(w['BZ'])), ('H', num(w['BX'])), ('I', num(w['BZ'], 0.2)), ('J', num(w['BX'], 0.2)),
                         ('K', num(w['BY'])), ('L', num(w['BW'])), ('M', num(w['BV']))):
            chk('ДР %s%d' % (col, rr), val, WD.get('%s%d' % (col, rr)))
    vol = res['volume']
    tr0 = F0 + len(rem) + 1
    for col, val in (('G', vol['stemsM3']['all']), ('H', vol['stemsT']['all']), ('I', vol['stumpsM3']['all']), ('J', vol['stumpsT']['all']),
                     ('K', vol['branchesM3']['all']), ('L', vol['branchesT']['all']), ('M', vol['shrubT']['all']), ('N', vol['shrubM3'])):
        chk('ДР итого %s' % col, val, WD.get('%s%d' % (col, tr0)))
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
    # баланс
    WB = V['Баланс']
    for i, rowv in enumerate(res['balanceReport']):
        for j, col in enumerate('BCDEFGH'):
            chk('bal %s%d' % (col, 5 + i), rowv[j + 1], WB.get('%s%d' % (col, 5 + i)))
    for j, col in enumerate('BCDE'):
        chk('vtab m3 %s' % col, vol['table']['m3'][j], WB.get('%s13' % col))
        chk('vtab t %s' % col, vol['table']['t'][j], WB.get('%s14' % col))
    # цветники
    WA = V['Цветники и газоны']
    for key, title, nn, tk in BX.AREA_BLOCKS:
        sec = res[{'fl': 'flowers', 'lw': 'lawns', 'gr': 'grass'}[key]]
        m = re.match(r"'?([^'!]+)'?!\$([A-Z]+)\$(\d+)$", names['%s_AREA' % tk])
        r0 = int(m.group(3)) - nn
        for x in sec['rows']:
            rr = r0 + x['idx']
            chk('%s T %d' % (key, rr), x['T'], WA.get('L%d' % rr))
            chk('%s U %d' % (key, rr), x['U'], WA.get('M%d' % rr))
            if x['P'] is not None:
                chk('%s P %d' % (key, rr), x['P'], WA.get('T%d' % rr))
                chk('%s E %d' % (key, rr), x['E'], WA.get('I%d' % rr))
        chk('%s total P' % key, sec['P'], named('%s_P' % tk))
        chk('%s result' % key, sec['result'], named('%s_RES' % tk))
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
        {'a': '', 'species': 'Береза бородавчатая', 'height': 21, 'diam': 22, 'cond': 'уд.', 'action': 'В', 'reason': 'СЕТИ', 'k4': 'да'},
        {'a': 3, 'species': 'Ель колючая', 'height': 15, 'diam': '30,24,14', 'cond': 'хор.', 'action': 'В', 'k1': 'да', 'k2': 'нет'},
        {'a': 20, 'species': 'Уч-к поросли дер.', 'height': 1, 'diam': '0-4', 'action': 'В', 'reason': 'СЕТИ'},
        {'a': 5, 'species': 'Уч-к поросли дер.', 'height': 1, 'diam': '0-4', 'action': 'В'},
        {'a': 12, 'species': '*Уч-к поросли дер. у дорог', 'height': 1, 'diam': '0-4', 'action': 'В', 'k3': 'да'},
        {'a': 15, 'species': 'Уч-к поросли куст.', 'height': 1, 'diam': '-', 'action': 'В'},
        {'a': 8, 'species': 'Однорядная живая изгородь', 'height': 1.2, 'diam': '-', 'action': 'В'},
        {'a': 1, 'species': 'Сирень обыкновенная', 'height': 2.5, 'diam': '-', 'action': 'В', 'note': 'V=50%'},
        {'a': '', 'species': 'Дуб черешчатый', 'height': 18, 'diam': 56, 'cond': 'хор.', 'action': 'В', 'k1': 'нет'},
        {'a': '', 'species': 'Липа мелколистная', 'height': 8, 'diam': 14, 'cond': 'хор.', 'action': 'П'},
        {'a': 2, 'noIn': '50', 'species': 'Яблоня домашняя', 'height': 4, 'diam': 16, 'cond': 'уд.', 'action': ''},
        {'a': 4, 'noIn': '60-62', 'species': 'Яблоня домашняя', 'height': 4, 'diam': 16, 'cond': 'уд.', 'action': ''},
        {'a': '', 'noIn': '12а', 'species': 'Клен остролистный', 'height': 9, 'diam': 18, 'cond': 'хор.', 'action': 'В'},
        {'a': '', 'species': 'Сосна обыкновенная', 'height': 12, 'diam': 28, 'action': ''},
    ]
    S = {'date': '2026-03-01', 'k1': False, 'k2': True, 'k3': False, 'k4': False, 'money': money, 'unit': 'баз.в. (руб.)',
         'view': 'формула + итог', 'defaultCond': 'уд.', 'shrubMassKg': 5, 'flowerMode': 'метрами', 'showPlots': True, 'hedgePieces': True,
         'reasons': [{'name': 'СЕТИ', 'group': '', 'replace': ''}, {'name': 'БЛАГОУСТРОЙСТТВО', 'group': '', 'replace': ''}],
         'compMap': dict(E.default_settings(E.prepare_ref(REF))['compMap'])}
    return {'settings': S, 'trees': T, 'flowers': [{'kind': 'Композиция из многолетников', 'cond': 'хор.', 'area': 4}],
            'lawns': [{'kind': 'Обыкновенный', 'cond': 'уд.', 'area': 30, 'reason': ''}], 'grass': [], 'projected': {}}


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
    for f, project in cases:
        c, d = compare(f, project, verbose)
        total += c
        fails += d
    print('Итого: проверено %d, расхождений %d' % (total, fails))
    return 1 if fails else 0


if __name__ == '__main__':
    sys.exit(main())
