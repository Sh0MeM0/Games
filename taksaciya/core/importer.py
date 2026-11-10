"""Импорт проекта из файла «ТАКСАЦИЯ-АВТО» (.xlsm/.xlsx) — тот же разбор, что в web/src/importer.js."""
from __future__ import annotations

import datetime
import html
import re
import zipfile

_ROW = re.compile(r'<row r="(\d+)"[^>]*?(?:/>|>(.*?)</row>)', re.S)
_CELL = re.compile(r'<c r="([A-Z]+)(\d+)"([^>]*?)(?:/>|>(.*?)</c>)', re.S)


def _unxml(s):
    return html.unescape(s)


def shared_strings(xml):
    out = []
    if not xml:
        return out
    for m in re.finditer(r'<si>(.*?)</si>', xml, re.S):
        body = re.sub(r'<rPh.*?</rPh>', '', m.group(1), flags=re.S)
        out.append(''.join(_unxml(t) for t in re.findall(r'<t[^>]*>(.*?)</t>', body, re.S)))
    return out


def sheet_cells(xml, ss, max_row, cols=None):
    cells = {}
    if not xml:
        return cells
    for m in _ROW.finditer(xml):
        r = int(m.group(1))
        if r > max_row:
            break
        if not m.group(2):
            continue
        for c in _CELL.finditer(m.group(2)):
            if cols and c.group(1) not in cols:
                continue
            attrs, inner = c.group(3), c.group(4) or ''
            t = re.search(r' t="([^"]+)"', attrs)
            t = t.group(1) if t else None
            vm = re.search(r'<v>(.*?)</v>', inner, re.S)
            v = None
            if t == 's' and vm:
                v = ss[int(vm.group(1))]
            elif t == 'inlineStr':
                im = re.search(r'<t[^>]*>(.*?)</t>', inner, re.S)
                v = _unxml(im.group(1)) if im else ''
            elif t in ('str', 'e'):
                v = _unxml(vm.group(1)) if vm else ''
            elif t == 'b':
                v = (vm.group(1) == '1') if vm else None
            elif vm:
                x = float(vm.group(1))
                v = int(x) if x == int(x) else x
            cells[c.group(1) + c.group(2)] = {'v': v, 'f': '<f' in inner}
    return cells


def sheet_map(files):
    wb = files.get('xl/workbook.xml', '')
    rels = files.get('xl/_rels/workbook.xml.rels', '')
    rel = {}
    for m in re.finditer(r'<Relationship ([^>]*?)/?>', rels):
        a = m.group(1)
        i = re.search(r'Id="([^"]+)"', a)
        t = re.search(r'Target="([^"]+)"', a)
        if i and t:
            rel[i.group(1)] = t.group(1)
    out = {}
    for m in re.finditer(r'<sheet ([^>]*?)/?>', wb):
        a = m.group(1)
        n = re.search(r'name="([^"]+)"', a)
        rid = re.search(r'r:id="([^"]+)"', a)
        if n and rid:
            tgt = re.sub(r'^/?xl/', '', rel.get(rid.group(1), '')).lstrip('/')
            out[_unxml(n.group(1))] = 'xl/' + tgt
    return out


def excel_date(serial):
    return (datetime.date(1899, 12, 30) + datetime.timedelta(days=int(round(serial)))).isoformat()


def _s(c):
    return '' if not c or c['v'] is None else c['v']


def _yes(c):
    return str(_s(c)).strip().lower() == 'да'


def read_files(path):
    """Читает из .xlsm только нужные листы (огромный скрытый лист УДд не трогается)."""
    with zipfile.ZipFile(path) as z:
        names = set(z.namelist())
        base = {n: z.read(n).decode('utf-8') for n in ('xl/workbook.xml', 'xl/_rels/workbook.xml.rels') if n in names}
        m = sheet_map(base)
        need = ['xl/sharedStrings.xml'] + [m[n] for n in ('СУЩд', 'имяПРОЕКТА', 'ЦвГаТр', 'базовая', 'БАЛАНСЫ', 'УДд1', 'Библ.ОРМ') if n in m]
        for n in need:
            if n in names:
                base[n] = z.read(n).decode('utf-8')
    return base


def parse(files, ref=None):
    m = sheet_map(files)
    if 'СУЩд' not in m:
        raise ValueError('В книге нет листа «СУЩд» — это не файл программы ТАКСАЦИЯ-АВТО.')
    ss = shared_strings(files.get('xl/sharedStrings.xml'))
    S = sheet_cells(files.get(m['СУЩд']), ss, 2010, list('ACEFGIJKLMNPQSU'))
    warnings = []
    st = {'k1': str(_s(S.get('N1'))).strip().lower() != 'нет', 'k2': _yes(S.get('P1')), 'k3': _yes(S.get('N2')),
          'k4': _yes(S.get('P2')), 'defaultCond': _s(S.get('J1')) or 'уд.', 'unit': _s(S.get('L1')) or 'баз.в. (руб.)',
          'view': _s(S.get('M1')) or 'формула + итог'}
    if isinstance(_s(S.get('I1')), (int, float)):
        st['date'] = excel_date(_s(S.get('I1')))
    reasons = []
    for r in range(4, 20):
        nm = _s(S.get('P%d' % r))
        if nm != '':
            reasons.append({'name': str(nm), 'group': str(_s(S.get('Q%d' % r)) or ''), 'replace': str(_s(S.get('S%d' % r)) or '')})
    st['reasons'] = reasons
    types = ['хвой.', 'медл.', 'быст.', 'плод.', 'малоц.', None, 'лиан.', 'хвой..', 'вечз.', 'крас.', 'медл..', 'быст..', 'плод..', 'ж.и.1', 'ж.и.2']
    comp = {t: str(_s(S.get('U%d' % (3 + i))) or '') for i, t in enumerate(types) if t}
    comp['plotTree'] = str(_s(S.get('U19')) or '')
    comp['plotShrub'] = str(_s(S.get('U20')) or '')
    st['compMap'] = comp
    trees = []
    for r in range(3, 2003):
        if _s(S.get('C%d' % r)) == 'Итого:':
            break
        jc = S.get('J%d' % r)
        sp = _s(jc)
        if sp == '' or (jc and jc['f']):
            break
        row = {'species': str(sp).strip()}
        a = _s(S.get('A%d' % r))
        if a != '':
            t = str(a)
            if '№' in t:
                p = t.index('№')
                row['count'], row['start'] = t[:p], t[p + 1:]
            else:
                row['count'] = a
        row['height'] = _s(S.get('E%d' % r))
        row['diam'] = _s(S.get('F%d' % r))
        g = S.get('G%d' % r)
        if g and not g['f'] and _s(g) != '':
            row['cond'] = _s(g)
        row['action'] = str(_s(S.get('I%d' % r)) or '').upper()
        row['note'] = str(_s(S.get('K%d' % r)) or '')
        row['reason'] = str(_s(S.get('L%d' % r)) or '')
        kr = _s(S.get('M%d' % r))
        row['kRepl'] = '' if kr == '' else str(kr)
        trees.append(row)
    project = {'settings': st, 'trees': trees, 'flowers': [], 'lawns': [], 'grass': [], 'projected': {'lawns': {}}, 'meta': {}}
    if 'имяПРОЕКТА' in m:
        I = sheet_cells(files.get(m['имяПРОЕКТА']), ss, 30)
        project['meta'] = {'org': _s(I.get('B2')), 'objectNo': _s(I.get('B5')), 'objectName': _s(I.get('B8')),
                           'stage': _s(I.get('D11')),
                           'author': ' '.join(str(x) for x in (_s(I.get('C14')), _s(I.get('C15')), _s(I.get('C16'))) if x not in ('', '…'))}
    if 'УДд1' in m:
        U = sheet_cells(files.get(m['УДд1']), ss, 1)
        st['money'] = str(_s(U.get('L1'))).lower() != 'нет'
    if 'БАЛАНСЫ' in m:
        B = sheet_cells(files.get(m['БАЛАНСЫ']), ss, 13)
        if isinstance(_s(B.get('L12')), (int, float)):
            st['shrubMassKg'] = _s(B.get('L12'))
        st['showPlots'] = str(_s(B.get('F12'))).lower() != 'нет'
        st['hedgePieces'] = str(_s(B.get('H12'))).lower() != 'нет'
    if 'ЦвГаТр' in m:
        C = sheet_cells(files.get(m['ЦвГаТр']), ss, 90)
        st['flowerMode'] = _s(C.get('T1')) or 'метрами'

        def area(r0, cnt, with_kind):
            out = []
            for i in range(cnt):
                rr = r0 + i
                kind = _s(C.get('B%d' % rr)) if with_kind else 'Иной травяной покров'
                av, cond = _s(C.get('D%d' % rr)), _s(C.get('C%d' % rr))
                if (with_kind and kind == '') or (not with_kind and av == '' and cond == ''):
                    continue
                kr = _s(C.get('S%d' % rr))
                out.append({'kind': kind, 'cond': cond, 'area': av, 'transplant': str(_s(C.get('G%d' % rr))).lower() == 'п',
                            'reason': str(_s(C.get('H%d' % rr)) or ''), 'note': str(_s(C.get('I%d' % rr)) or ''),
                            'kRepl': '' if kr == '' else str(kr)})
            return out
        project['flowers'] = area(2, 13, True)
        project['lawns'] = area(36, 17, True)
        project['grass'] = [{k: v for k, v in g.items() if k != 'kind'} for g in area(73, 17, False)]
        project['projected'] = {'perennial': _s(C.get('G17')), 'annual': _s(C.get('G19')), 'lawns': {
            'Спортивный': _s(C.get('G55')), 'Партерный': _s(C.get('G57')), 'Обыкновенный': _s(C.get('G59')),
            'Луговой': _s(C.get('G61')), 'Мавританский': _s(C.get('G63')), 'Специального назначения': _s(C.get('G65'))}}
    if 'базовая' in m:
        Z = sheet_cells(files.get(m['базовая']), ss, 400, ['A', 'B', 'C'])
        bv = []
        for r in range(2, 401):
            a = _s(Z.get('A%d' % r))
            b = _s(Z.get('B%d' % r))
            if not isinstance(a, (int, float)) or b == '':
                continue
            bv.append({'from': excel_date(a), 'rub': b, 'act': str(_s(Z.get('C%d' % r)) or '')})
        if bv:
            project['baseValues'] = bv
    if 'Библ.ОРМ' in m and ref:
        L = sheet_cells(files.get(m['Библ.ОРМ']), ss, 1200, list('BCDEFGHI'))
        known = {s['name'].lower() for s in ref['species']} | {s['name'].lower() for s in ref.get('special', [])}
        custom = []
        for r in range(2, 1201):
            n2 = _s(L.get('B%d' % r))
            if n2 == '' or str(n2).lower() in known:
                continue
            f, g2 = _s(L.get('F%d' % r)), _s(L.get('G%d' % r))
            h = _s(L.get('H%d' % r))
            custom.append({'name': str(n2), 'alt': str(_s(L.get('C%d' % r)) or ''), 'shrub': _s(L.get('D%d' % r)) == 1,
                           'type': str(_s(L.get('E%d' % r)) or ''), 'ballD': None if f == '' else f, 'ballH': None if g2 == '' else g2,
                           'invasive': bool(h) and h != 0, 'hard': str(_s(L.get('I%d' % r)) or ''), 'inList': False, 'own': None})
        if custom:
            project['customSpecies'] = custom
            warnings.append('Добавлено собственных пород из файла: %d.' % len(custom))
    return {'project': project, 'warnings': warnings, 'count': len(trees)}


def import_file(path, ref=None):
    return parse(read_files(path), ref)
