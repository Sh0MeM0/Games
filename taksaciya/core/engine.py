"""Таксация Лайт — расчётное ядро (Python).

Построчный перенос core/engine.js: те же функции, те же ссылки на ячейки оригинала
«ТАКСАЦИЯ-АВТО 4.2». Обе версии проверяются одними и теми же эталонными векторами
(tests/vectors), полученными пересчётом оригинального файла.
Только стандартная библиотека Python 3.8+.
"""
from __future__ import annotations

import json
import math
import os
import re

TREE_TYPES = ['хвой.', 'медл.', 'быст.', 'плод.', 'малоц.']
SHRUB_TYPES = ['лиан.', 'хвой..', 'вечз.', 'крас.', 'медл..', 'быст..', 'плод..', 'ж.и.1', 'ж.и.2']
SP = {
    'treePlot': 'Уч-к поросли дер.', 'shrubPlot': 'Уч-к поросли куст.',
    'treePlotRoad': '*Уч-к поросли дер. у дорог', 'shrubPlotRoad': '*Уч-к поросли куст. у дорог',
    'hedge1': 'Однорядная живая изгородь', 'hedge2': 'Двухрядная живая изгородь',
}
NO_COMP = 'без компенcации'
NO_COMP_RE = re.compile(r'без\s*компен[сc]ации', re.I)
ERR_SHRUB_D = 'ОШИБКА: Ø куста = только "-"'
ERR_TREE_D = 'ОШИБКА: Ø дерева ≠ "-"'
LABELS = ['Д.хвой/вечз. ', 'Д.лис.м.р. ', 'Д.лис.б.р. ', 'К.древ.лиан. ', 'К.хвой/вечз. ',
          'К.лис.крас.цв. ', 'К.лис.м.р. ', 'К.л.б.р./плод. ', 'жив.изг.1р. ', 'жив.изг.2р. ']
LABELS_G = ['Д.хв./вчз. ', 'Д.мр. ', 'Д.бр. ', 'К.дл. ', 'К.хв./вчз. ', 'К.кц. ', 'К.мр. ',
            'К.бр./пл. ', 'ж.изг.1р. ', 'ж.изг.2р. ']
UNITS = ['баз.в.', 'баз.в. (руб.)', 'руб.']
NAN = float('nan')


# ------------------------------------------------------------------ utils
def lc(s):
    return '' if s is None else str(s).lower()


def is_blank(v):
    return v is None or v == ''


def eqi(a, b):
    return lc(a) == lc(b)


def has(s, sub):
    return str(sub) in ('' if s is None else str(s))


def is_num(v):
    return isinstance(v, (int, float)) and not isinstance(v, bool)


def _p15(x):
    return float('%.15g' % x)


def xround(v, n=0):
    p = 10 ** n
    s = -1 if v < 0 else 1
    return s * math.floor(_p15(abs(v) * p) + 0.5) / p


def xroundup(v, n=0):
    p = 10 ** n
    s = -1 if v < 0 else 1
    return s * math.ceil(_p15(abs(v) * p) - 1e-9) / p


def xnum(v):
    """Число → текст, как при склейке «&» в русском Excel."""
    if not is_num(v):
        return '' if v is None else str(v)
    if math.isnan(v) or math.isinf(v):
        return str(v)
    x = _p15(float(v))
    if x == int(x) and abs(x) < 1e21:
        s = str(int(x))
    else:
        s = repr(x)
        if 'e' in s or 'E' in s:
            s = ('%.12f' % x).rstrip('0').rstrip('.')
    return s.replace('.', ',')


def xfixed(v, n=2):
    r = xround(v, n)
    neg = r < 0
    s = '%.*f' % (n, abs(r))
    parts = s.split('.')
    ip = parts[0]
    groups = []
    while len(ip) > 3:
        groups.insert(0, ip[-3:])
        ip = ip[:-3]
    groups.insert(0, ip)
    parts[0] = ' '.join(groups)
    return ('-' if neg else '') + ','.join(parts)


_NUM_RE = re.compile(r'^[-+]?(\d+([.,]\d*)?|[.,]\d+)$')


def ru_value(t):
    """VALUE(text) в русской локали. NaN, если не число."""
    if is_num(t):
        return float(t)
    s = re.sub(r'[\s ]', '', '' if t is None else str(t))
    if s == '':
        return NAN
    pct = False
    if s.endswith('%'):
        pct = True
        s = s[:-1]
    if not _NUM_RE.match(s):
        return NAN
    v = float(s.replace(',', '.'))
    return v / 100 if pct else v


def in_num(v):
    if is_blank(v):
        return None
    x = ru_value(v)
    return None if math.isnan(x) else x


def cell_text(v):
    if is_num(v):
        return xnum(v)
    return '' if v is None else str(v)


def match_approx(arr, v):
    idx = -1
    for i, a in enumerate(arr):
        if a <= v:
            idx = i
        else:
            break
    return idx


def fsum(a):
    s = 0.0
    for x in a:
        s += x or 0
    return s


def _nf(v):
    """Число как в JS: целые float → int (для одинаковых итогов и вывода)."""
    return v


# ------------------------------------------------------------------ reference
def load_reference(path=None):
    path = path or os.path.join(os.path.dirname(os.path.abspath(__file__)), 'reference.json')
    with open(path, encoding='utf-8') as f:
        return json.load(f)


def prepare_ref(ref):
    if ref.get('_prepared'):
        return ref
    ref['_byName'] = {lc(s['name']): s for s in ref['species']}
    ref['_special'] = {lc(s['name']): {'name': s['name'], 'type': s['type'], 'pos': i + 1}
                       for i, s in enumerate(ref['special'])}
    ref['_typeK'] = {t['type']: t['k'] for t in ref['types']}
    ref['_prepared'] = True
    return ref


def species_info(ref, name):
    k = lc(name)
    sp = ref['_special'].get(k)
    lib = ref['_byName'].get(k)
    if sp:
        return {'found': True, 'special': True, 'type': sp['type'], 'lib': lib}
    if lib:
        return {'found': True, 'special': False, 'type': lib['type'], 'lib': lib}
    return {'found': False, 'special': False, 'type': '', 'lib': None}


def base_value_for(date, table):
    rows = [r for r in (table or []) if r and r.get('from') and not is_blank(r.get('rub'))]
    rows.sort(key=lambda r: r['from'])
    d = str(date or '')
    found = None
    for r in rows:
        if r['from'] <= d:
            found = r
    return found


def default_settings(ref):
    d = ref.get('defaults', {})
    comp = {t: '' for t in TREE_TYPES + SHRUB_TYPES}
    comp.update(ref.get('compMap', {}))
    return {
        'date': d.get('date') or __import__('datetime').date.today().isoformat(),
        'baseValue': None,
        'k1': d.get('K1') != 'нет', 'k2': d.get('K2') == 'да', 'k3': d.get('K3') == 'да', 'k4': d.get('K4') == 'да',
        'money': True,
        'unit': d.get('unit') or 'баз.в. (руб.)',
        'view': d.get('formulaView') or 'формула + итог',
        'defaultCond': d.get('defaultCondition') or 'уд.',
        'shrubMassKg': d.get('shrubMassKg', 5) if d.get('shrubMassKg') is not None else 5,
        'showPlots': d.get('showPlots') != 'нет', 'hedgePieces': d.get('hedgeAsPieces') != 'нет',
        'flowerMode': d.get('flowerOffset') or 'метрами',
        'reasons': [{'name': r['name'], 'group': r.get('group') or '', 'replace': r.get('replace') or ''}
                    for r in ref.get('reasons', [])],
        'compMap': comp,
    }


# ------------------------------------------------------------------ coefficients
def coefficients(settings, reason_name, k_repl):
    reason = None
    for r in settings.get('reasons') or []:
        if reason is None and eqi(r.get('name'), reason_name):
            reason = r
    rep = '' if reason is None or reason.get('replace') is None else str(reason.get('replace'))
    ind = '' if k_repl is None else (xnum(k_repl) if is_num(k_repl) else str(k_repl))
    if NO_COMP_RE.search(ind) or NO_COMP_RE.search(rep):
        return {'k': [0, 0, 0, 0], 'zero': True, 'str': '   0;   0;   0;   0', 'reason': reason}

    def same(dg):
        return has(ind, dg) == has(rep, dg)

    k1 = (2 if same(1) else 1) if settings['k1'] else (1 if same(1) else 2)
    k2 = (0.5 if same(2) else 1) if settings['k2'] else (1 if same(2) else 0.5)
    k3 = (0.5 if same(3) else 1) if settings['k3'] else (1 if same(3) else 0.5)
    k4 = (0.1 if same(4) else 1) if settings['k4'] else (1 if same(4) else 0.1)

    def f(v, last=False):
        t = '   1' if v == 1 else '   2' if v == 2 else ' 0,5' if v == 0.5 else ' 0,1'
        return t + ('' if last else ';')
    return {'k': [k1, k2, k3, k4], 'zero': False, 'str': f(k1) + f(k2) + f(k3) + f(k4, True), 'reason': reason}


# ------------------------------------------------------------------ stems
def parse_stems(diam):
    if is_num(diam):
        return [float(diam)] + [None] * 26
    s = cell_text(diam).replace(' ', '')
    letters = 'abcdefghijklmnopqrstuvwxyz'
    for ch in letters:
        s = s.replace(',', ch, 1)
    s = s.replace('-', '  ').replace('*', '').replace('до1', '0,8').replace('до4', '3,8')
    seps = list(letters) + ['.']
    out = []
    for k in range(27):
        if k == 0:
            p0 = s.find(seps[0])
            seg = s[:p0] if p0 >= 0 else s
        else:
            a = s.find(seps[k - 1])
            if a < 0:
                out.append(None)
                continue
            b = s.find(seps[k])
            if b >= 0 and b < a:
                out.append(None)
                continue
            seg = s[a + 1:b] if b >= 0 else s[a + 1:]
        v = ru_value(seg[-3:])
        out.append(None if math.isnan(v) else v)
    return out


def type_code(name, qty, diam_raw, N, shrub_row):
    lname = lc(name)
    E = cell_text(diam_raw).replace(' ', '')
    if not shrub_row:
        if has(lname, lc(SP['treePlot'])):
            return 60 if qty > 9.99 else 70
        numeric_e = is_num(diam_raw)
        if not has(E, 'до1') or (numeric_e and diam_raw > 0.99):
            if N == 'хвой.':
                return 10
            if N == 'медл.':
                return 20
            if N in ('быст.', 'плод.'):
                return 30
            if N == 'малоц.':
                t = E.replace('1,', '').replace('2,', '').replace('3,', '')
                v = float(diam_raw) if numeric_e else ru_value(t)
                if math.isnan(v):
                    v = 4
                if not has(E, 'до4') and v > 3.99:
                    return 40
                if has(E, '**до4'):
                    return 50
            return 1
        if N in ('хвой.', 'медл.', 'быст.', 'плод.') and has(E, '**до1'):
            return 50
        return 1
    if has(lname, lc(SP['shrubPlot'])):
        return 18 if qty > 9.99 else 19
    if has(lname, lc(SP['hedge1'])):
        return 16
    if has(lname, lc(SP['hedge2'])):
        return 17
    if N == 'лиан.':
        return 11
    if N in ('хвой..', 'вечз.'):
        return 12
    if N == 'крас.':
        return 13
    if N == 'медл..':
        return 14
    if N in ('быст..', 'плод..'):
        return 15
    return 1


# ------------------------------------------------------------------ tariffs
def tree_table(ref, J):
    t = ref['tariffTrees']
    return {10: t['хвой.'], 20: t['медл.'], 30: t['быст.'], 40: t['малоц.']}.get(J)


def tariff_lookup(table, d):
    i = match_approx([r[0] for r in table], d)
    return None if i < 0 else table[i][1]


SHRUB_H_KEY = {11: 'лиан.', 12: 'хвой/вечз', 13: 'крас.', 14: 'медл..', 15: 'быст/плод'}


def tariff(ref, J, stems, AQ, height, N):
    if J == 1:
        return {'v': 0, 'text': '0'}
    tbl = tree_table(ref, J)
    if AQ != '' and AQ > 1:
        if not tbl:
            return {'v': None, 'text': '===', 'err': 'многоствольное дерево вне таблицы'}
        parts, total = [], 0
        for d in stems:
            if d is None or d < 12:
                continue
            x = tariff_lookup(tbl, d)
            if x is None:
                continue
            total += x
            parts.append(xnum(x))
        return {'v': total, 'text': '+'.join(parts)}
    P = ref['plots']
    fixed = {60: P['treePlotPerM2'], 70: P['treePlotSmall'], 16: P['hedge1PerM'], 17: P['hedge2PerM'],
             18: P['shrubPlotPerM2'], 19: P['shrubPlotSmall']}
    if J in fixed:
        return {'v': fixed[J], 'text': xnum(fixed[J])}
    if J == 50:
        x = ref['plantedSmall'].get(N)
        return {'v': None, 'text': '___', 'err': 'нет тарифа'} if x is None else {'v': x, 'text': xnum(x)}
    if J in SHRUB_H_KEY:
        h = ref['tariffShrubs'][SHRUB_H_KEY[J]]
        if not is_num(height):
            return {'v': None, 'text': '___', 'err': 'не указана высота куста'}
        i = match_approx([r[0] for r in h], height)
        if i < 0:
            return {'v': None, 'text': '___', 'err': 'нет тарифа'}
        return {'v': h[i][1], 'text': xnum(h[i][1])}
    if tbl:
        mx = 0
        for s in stems:
            if s is not None and s > mx:
                mx = s
        x = tariff_lookup(tbl, mx)
        return {'v': None, 'text': '___', 'err': 'нет тарифа'} if x is None else {'v': x, 'text': xnum(x)}
    return {'v': None, 'text': '___', 'err': 'нет тарифа'}


def comp_type(settings, J, N, shrub_row):
    m = settings.get('compMap') or {}
    if J == 1:
        return N
    if J in (60, 70):
        return m.get('plotTree') or ''
    if J in (18, 19):
        return m.get('plotShrub') or ''
    if m.get(N):
        return m[N]
    if N in ('крас.', 'лиан.'):
        return N if shrub_row else 'медл.'
    return N


class _NoNorm(Exception):
    pass


def plant_cols(ref, J, M, K, L):
    c = [None] * 10
    pt = ref['plantTrees'].get(str(J))
    ps = ref['plantShrubs'].get(str(J))

    def tv(col):
        if not pt:
            raise _NoNorm()
        return pt[col]

    def sv(col):
        if not ps:
            raise _NoNorm()
        return ps[col]
    if K is not None:
        if M == 'хвой.':
            c[0] = 0 if J == 1 else K * tv('хвой.')
        if M == 'медл.':
            c[1] = 0 if J == 1 else K * tv('медл.')
        c[2] = (0 if J == 1 else (1 if J == 70 else K) * tv('быст.')) if M == 'быст.' else 0
    if L is not None:
        if M == 'лиан.':
            c[3] = 0 if J == 1 else L * sv('лиан.')
        if M in ('хвой..', 'вечз.'):
            c[4] = 0 if J == 1 else L * sv('хвой/вечз')
        if M == 'крас.':
            c[5] = 0 if J == 1 else L * sv('крас.')
        if M == 'медл..':
            c[6] = 0 if J == 1 else L * sv('медл..')
        if M in ('быст..', 'плод..'):
            c[7] = 0 if J == 1 else (1 if J == 19 else L) * sv('быст/плод')
        if M == 'ж.и.1':
            c[8] = 0 if J == 1 else L * sv('ж.и.1')
        if M == 'ж.и.2':
            c[9] = 0 if J == 1 else L * sv('ж.и.2')
    return c


def max_of(cols):
    mx = None
    for v in cols:
        if v is not None and (mx is None or v > mx):
            mx = v
    if mx is None:
        mx = 0
    pos = -1
    for i, v in enumerate(cols):
        if v is not None and v == mx:
            pos = i
            break
    return {'max': mx, 'pos': pos}


# ------------------------------------------------------------------ wood
def vol_row(ref, ce, d, H):
    V = ref['volumes']
    soft = V['soft']
    idx = match_approx([r[0] for r in soft], d)
    if idx < 0:
        return None

    def cell(r, c):
        v = r[c - 1] if c - 1 < len(r) else None
        return 0 if v is None else v

    def le(a, h):
        return True if not is_num(h) else a <= h
    if ce == 1:
        row = soft[idx]
        if le(cell(row, 2), H):
            t, s = cell(row, 4), cell(row, 5)
        else:
            t, s = cell(row, 8), cell(row, 9)
    elif ce == 2:
        row = V['conif'][idx]
        if le(cell(row, 1), H):
            t, s = cell(row, 3), cell(row, 4)
        elif le(cell(row, 5), H):
            t, s = cell(row, 7), cell(row, 8)
        else:
            t, s = cell(row, 11), cell(row, 12)
    else:
        row = V['hard'][idx]
        if le(cell(row, 1), H):
            t, s = cell(row, 3), cell(row, 4)
        else:
            t, s = cell(row, 7), cell(row, 8)
    return {'total': t, 'stem': s}


def hardness_index(info):
    if info['special']:
        return 1
    h = info['lib']['hard'] if info['lib'] else ''
    return {'мяг.': 1, 'хвн.': 2, 'твд.': 3}.get(h)


def percent_from_note(note):
    s = '' if note is None else str(note)
    m = re.search('v', s, re.I)
    if not m:
        return 1
    i = m.start()
    p = s.find('%', i)
    if p < 0:
        return 1
    ln = p - i - 1
    if ln < 0:
        return 1
    v = ru_value(s[i + 2:i + 2 + ln])
    return 1 if math.isnan(v) else v


def wood(ref, settings, info, row, K, L, stems, H, qty):
    res = {'ce': 0, 'BV': None, 'BW': None, 'BX': None, 'BY': None, 'BZ': None, 'CA': 0,
           'CB': None, 'CC': None, 'CD': None, 'perStem': [], 'err': None}
    CA = 0
    for d in stems:
        if d is not None and d < 8:
            CA += d
    res['CA'] = CA
    hh = row['height']
    if (0.1 < CA < 8) or L == 1:
        h = hh if is_num(hh) else 0
        hk = h + (h - 1) / 3 if h > 1 else h
        res['BV'] = hk * (settings['shrubMassKg'] * percent_from_note(row['note'])) * qty / 1000
    if K is None:
        return res
    ce = hardness_index(info)
    if ce is None:
        res['err'] = 'у породы не задан тип твёрдости древесины'
        return res
    res['ce'] = ce
    dn = ref['volumes']['density']
    dens = dn['soft'] if ce == 1 else dn['conif'] if ce == 2 else dn['hard']
    totals = stems_v = bz15 = bz11 = bz3 = 0
    for d in stems:
        if d is None:
            res['perStem'].append(None)
            continue
        v = vol_row(ref, ce, d, H)
        res['perStem'].append({'d': d, 'total': v['total'], 'stem': v['stem']} if v else {'d': d, 'total': None, 'stem': None})
        if not v:
            continue
        totals += v['total']
        stems_v += v['stem']
        if d >= 15:
            bz15 += v['stem']
        elif d >= 11:
            bz11 += v['stem']
        elif d >= 3:
            bz3 += v['stem']
    extra = 0
    if CA >= 8:
        vc = vol_row(ref, ce, CA, H)
        extra = vc['total'] - vc['stem']
    res['CC'] = totals + extra
    res['CD'] = stems_v
    res['CB'] = None if res['CC'] < res['CD'] else res['CC'] - res['CD']
    res['BX'] = res['CD'] * dens * K / 1000
    res['BW'] = None if res['CB'] is None else res['CB'] * dens * K / 1000
    res['BY'] = None if res['CB'] is None else (res['CB'] / 0.1) * K
    res['BZ'] = K * (bz15 * (1.49 if ce == 2 else 1.47) + bz11 / 0.67 + bz3 / 0.6)
    return res


# ------------------------------------------------------------------ numbering
def column_a(row):
    if not is_blank(row.get('a')):
        return row['a']
    c = '' if is_blank(row.get('count')) else row.get('count')
    s = '' if is_blank(row.get('start')) else row.get('start')
    if s != '':
        return (cell_text(c) if c != '' else '') + '№' + cell_text(s)
    return c


def qty_from_a(A):
    if is_blank(A):
        return 1
    t = cell_text(A)
    if t.startswith('№'):
        return 1
    p = t.find('№')
    v = ru_value(t[:p] if p >= 0 else t)
    return None if math.isnan(v) else v


def number_from_a(A, prev_b):
    t = cell_text(A)
    p = t.find('№')
    if not is_blank(A) and p >= 0:
        st = t[p + 1:p + 11]
        stv = ru_value(st)
        cnt = ru_value(t[:p])
        tail = ('-' + xnum(cnt - 1 + stv)) if not (math.isnan(stv) or math.isnan(cnt)) else ''
        return st + tail
    pb = cell_text(prev_b)
    q = pb.find('-')
    prev_end = ru_value(pb[q + 1:q + 11] if q >= 0 else pb)
    first = 1 if math.isnan(prev_end) else prev_end + 1
    if is_blank(A):
        return xnum(first)
    av = ru_value(A)
    return xnum(first) + ('' if math.isnan(av) else '-' + xnum(av - 1 + first))


def _norm_num(v):
    """Целые значения — int, чтобы xnum и сравнения вели себя как в JS."""
    if is_num(v) and float(v) == int(v) and abs(v) < 1e15:
        return int(v)
    return v


# ------------------------------------------------------------------ trees
def compute_trees(project, ref, S):
    rows = []
    prev_b = 'Номер по плану'
    for i, src in enumerate(project.get('trees') or []):
        if is_blank(src.get('species')):
            continue
        r = {'idx': i, 'src': src, 'errors': [], 'warnings': []}
        A = column_a(src)
        r['A'] = A
        q = qty_from_a(A)
        if q is None:
            r['errors'].append('количество не распознано')
            q = 0
        r['qty'] = _norm_num(q)
        r['no'] = number_from_a(A, prev_b)
        prev_b = r['no']
        r['species'] = str(src['species']).strip()
        r['info'] = species_info(ref, r['species'])
        r['N'] = r['info']['type']
        if not r['info']['found']:
            r['errors'].append('породы нет в справочнике')
        h = in_num(src.get('height'))
        r['height'] = _norm_num(h) if h is not None else None
        diam = src.get('diam')
        if isinstance(diam, str) and re.match(r'^\s*\d+(\.\d+)?\s*$', diam):
            diam = _norm_num(ru_value(diam))
        elif is_num(diam):
            diam = _norm_num(diam)
        r['diam'] = diam
        r['diamText'] = cell_text(diam)
        r['cond'] = S['defaultCond'] if is_blank(src.get('cond')) else src.get('cond')
        r['action'] = lc(src.get('action'))
        r['note'] = src.get('note') or ''
        r['reason'] = src.get('reason') or ''
        r['kRepl'] = '' if src.get('kRepl') is None else src.get('kRepl')
        shrub_type = r['N'] in SHRUB_TYPES
        title = r['species']
        nst = -1 if is_num(diam) else r['diamText'].find(',', 1)
        if nst >= 1:
            title += ' (' + str(r['diamText'].count(',') + 1) + ' ств.' + (', ' + r['note'] if r['note'] else '') + ')'
        elif r['note']:
            title += ' (' + r['note'] + ')'
        if has(r['species'], 'поросли'):
            title += ', м2 '
        elif has(r['species'], 'рядная живая'):
            title += ', п.м. '
        r['title'] = title
        if shrub_type and r['diamText'] != '-':
            r['H'] = ERR_SHRUB_D
        elif not shrub_type and r['diamText'] == '-':
            r['H'] = ERR_TREE_D
        else:
            r['H'] = ('вырубить (' + r['reason'] + ')' if r['action'] == 'в' else
                      'пересадить (' + r['reason'] + ')' if r['action'] == 'п' else 'сохранить')
        if r['H'] in (ERR_SHRUB_D, ERR_TREE_D):
            r['errors'].append(r['H'])
        r['removed'] = lc(r['H']).startswith('вырубить')
        r['transplanted'] = lc(r['H']).startswith('пересадить')
        r['coef'] = None
        if not is_blank(r['reason']) and r['action'] == 'в' and r['info']['found']:
            r['coef'] = coefficients(S, r['reason'], r['kRepl'])
        r['O'] = r['coef']['str'] if r['coef'] else ''
        if r['removed'] and not r['coef']:
            r['errors'].append('не указана причина вырубки')
        r['rm'] = None
        rows.append(r)
    return rows


def removal_row(r, ref, S):
    shrub_row = r['diamText'] == '-'
    K = None if shrub_row else r['qty']
    L = r['qty'] if shrub_row else None
    J = type_code(r['species'], r['qty'], r['diam'], r['N'], shrub_row)
    is_plot = J in (60, 70, 18, 19)
    F = '-' if is_plot else r['cond']
    lname = lc(r['species'])
    if has(lname, lc(SP['treePlotRoad'])) or has(lname, lc(SP['shrubPlotRoad'])):
        Nc = 0.1
    elif eqi(F, 'хор.') or F == '-':
        Nc = 1
    elif eqi(F, 'уд.'):
        Nc = 0.75
    elif eqi(F, 'плох.'):
        Nc = 0.5
    elif eqi(F, 'ненадл.'):
        Nc = 0.25
    else:
        Nc = None
        r['errors'].append('неизвестное качественное состояние')
    M = comp_type(S, J, r['N'], shrub_row)
    stems = [_norm_num(x) if x is not None else None for x in parse_stems(r['diam'])]
    AQ = max(1, len([d for d in stems if d is not None and d > 11.9])) if K is not None else ''
    AO = r['N'] if (K is not None and not has(lname, 'ч-к поросли')) else ''
    invasive = bool(r['info']['lib'] and r['info']['lib'].get('invasive') and not r['info']['special'])
    Hn = 0 if r['height'] is None else r['height']
    tr = tariff(ref, J, stems, AQ, Hn, r['N'])
    try:
        cols = plant_cols(ref, J, M, K, L)
    except _NoNorm:
        cols = None
    mx = max_of(cols) if cols else {'max': None, 'pos': -1}
    k = r['coef']['k'] if r['coef'] else None
    Y, yerr = None, None
    if k and not r['coef']['zero'] and is_plot:
        Y = '-'
    elif invasive:
        Y = 0
    elif not k or Nc is None:
        Y, yerr = None, 'нет коэффициентов'
    elif S['money']:
        if tr['v'] is None:
            Y, yerr = None, tr.get('err') or 'нет тарифа'
        else:
            Y = xround(tr['v'] * (r['qty'] if max(r['qty'], 0) > 1 else 1) * Nc * k[0] * k[1] * k[2] * k[3], 3)
    else:
        if not cols:
            Y, yerr = None, 'нет нормы посадок'
        else:
            Y = xround(mx['max'] * (1 if len(str(AO)) < 2 else AQ) * Nc * k[0] * k[1] * k[2] * k[3], 3)
    if is_num(Y):
        Y = _norm_num(Y)
    if yerr:
        r['errors'].append(yerr)
    Z = (LABELS[mx['pos']] if mx['pos'] >= 0 else '') + ('' if Y is None else cell_text(Y))
    if Y == 0 and is_num(Y):
        G = 'Без компенсации'
    elif Y is None:
        G = 'ОШИБКА: ' + (yerr or '')
    elif S['view'] == 'итог':
        G = '(' + xnum(tr['v']) + ') баз.в.' if S['money'] else Z
    elif S['money']:
        G = ('(' + tr['text'] + ') баз.в. ' + (' х' + xnum(r['qty']) + 'шт. ' if max(r['qty'], 0) > 1 else '') +
             'х' + xnum(Nc) + 'х' + xnum(k[0]) + 'х' + xnum(k[1]) + 'х' + xnum(k[2]) + 'х' + xnum(k[3]) +
             ' =' + (' + др. уч-ки поросли' if Y == '-' else xnum(Y)))
    else:
        G = (xnum(mx['max']) + ' ' + (LABELS_G[mx['pos']] if mx['pos'] >= 0 else '') +
             ('х' + str(AQ) + 'ств.' if AQ != '' and AQ >= 2 else '') +
             'х' + xnum(Nc) + 'х' + xnum(k[0]) + 'х' + xnum(k[1]) + 'х' + xnum(k[2]) + 'х' + xnum(k[3]) +
             ' =' + (' + др. уч-ки поросли' if Y == '-' else xnum(Y)))
    w = wood(ref, S, r['info'], r, K, L, stems, Hn, r['qty'])
    if w['err']:
        r['warnings'].append(w['err'])
    return {'J': J, 'K': K, 'L': L, 'F': F, 'N': Nc, 'M': M, 'isPlot': is_plot, 'AO': AO, 'AQ': AQ,
            'invasive': invasive, 'stems': stems, 'AR': tr['v'], 'AS': tr['text'], 'cols': cols,
            'maxPlant': mx['max'], 'Y': Y, 'Z': Z, 'G': G,
            'E': '-' if r['diamText'] == '0-4' else r['diamText'], 'Hnote': '(' + r['reason'] + ')', 'wood': w}


def plot_groups(rows, S, ref, shrub):
    groups, order = {}, []
    pattern = 'уч-к поросли куст' if shrub else 'уч-к поросли дер'
    for r in rows:
        if r['action'] != 'в' or not r['coef']:
            continue
        if not has(lc(r['species']), pattern):
            continue
        key = r['coef']['str']
        if key not in groups:
            groups[key] = {'str': key, 'k': r['coef']['k'], 'area': 0}
            order.append(key)
        groups[key]['area'] += r['qty']
    AG = (S['compMap'].get('plotShrub') if shrub else S['compMap'].get('plotTree')) or ''
    total = 0
    lst = []
    for key in order:
        g = groups[key]
        if S['money']:
            if shrub:
                AH = ref['plots']['shrubPlotPerM2'] if g['area'] > 9.99 else ref['plots']['shrubPlotSmall']
            else:
                AH = ref['plots']['treePlotPerM2'] if g['area'] > 9.99 else ref['plots']['treePlotSmall']
        elif not shrub:
            col = {'хвой.': 'хвой.', 'медл.': 'медл.', 'быст.': 'быст.'}.get(AG)
            AH = ref['plantTrees']['60' if g['area'] > 9.99 else '70'][col] if col else None
        else:
            mp = {'лиан.': 'лиан.', 'хвой..': 'хвой/вечз', 'вечз.': 'хвой/вечз', 'крас.': 'крас.', 'медл..': 'медл..',
                  'быст..': 'быст/плод', 'плод..': 'быст/плод', 'ж.и.1': 'ж.и.1', 'ж.и.2': 'ж.и.2'}.get(AG)
            AH = ref['plantShrubs']['18' if g['area'] > 9.99 else '19'][mp] if mp else None
        AF = 0 if AH is None else g['area'] * AH * g['k'][0] * g['k'][1] * g['k'][2] * g['k'][3]
        total += AF
        lst.append({'str': g['str'], 'area': g['area'], 'norm': AH, 'value': AF})
    return {'type': AG, 'groups': lst, 'total': total}


def removal_totals(rows, S, ref, base):
    rem = [r for r in rows if r['removed']]

    def ysum(pred):
        return fsum(r['rm']['Y'] for r in rem if pred(r) and is_num(r['rm']['Y']))

    def csum(pred):
        return fsum(r['qty'] for r in rem if pred(r))
    tp = plot_groups(rows, S, ref, False)
    sp = plot_groups(rows, S, ref, True)
    t = {'treePlots': tp, 'shrubPlots': sp}

    def af_t(ty):
        return tp['total'] if tp['type'] == ty else 0

    def af_s(ty):
        return sp['total'] if sp['type'] == ty else 0
    AP = {}
    for ty in ('хвой.', 'медл.', 'быст.'):
        AP[ty] = ysum(lambda r, ty=ty: r['rm']['AO'] == ty) + af_t(ty)
    for ty in ('плод.', 'малоц.'):
        AP[ty] = ysum(lambda r, ty=ty: r['rm']['AO'] == ty)
    AP['treePlots'] = tp['total']
    AP['trees'] = AP['хвой.'] + AP['медл.'] + AP['быст.'] + AP['плод.'] + AP['малоц.']
    for ty in ('лиан.', 'хвой..', 'вечз.', 'крас.', 'медл..', 'быст..', 'плод..', 'ж.и.1', 'ж.и.2'):
        AP[ty] = ysum(lambda r, ty=ty: r['N'] == ty) + af_s(ty)
    AP['shrubs'] = sum(AP[x] for x in ('лиан.', 'хвой..', 'вечз.', 'крас.', 'медл..', 'быст..', 'плод..'))
    AP['shrubPlots'] = sp['total']
    AP['total'] = AP['treePlots'] + AP['trees'] + AP['ж.и.1'] + AP['ж.и.2'] + AP['shrubs'] + AP['shrubPlots']
    t['money'] = AP

    def zsum(prefix):
        return ysum(lambda r: r['rm']['Z'].startswith(prefix))
    AN = {}
    AN['хвой.'] = zsum('Д.хв') + af_t('хвой.')
    AN['медл.'] = zsum('Д.лис.м') + af_t('медл.')
    AN['быст.'] = zsum('Д.лис.б.р') + af_t('быст.')
    AN['trees'] = AN['хвой.'] + AN['медл.'] + AN['быст.']
    AN['лиан.'] = zsum('К.древ.лиан') + af_s('лиан.')
    AN['хвой/вечз'] = zsum('К.хв') + af_s('хвой..') + af_s('вечз.')
    AN['крас.'] = zsum('К.лис.кр') + af_s('крас.')
    AN['медл..'] = zsum('К.лис.м.р') + af_s('медл..')
    AN['быст/плод'] = zsum('К.л.б.р./плод') + af_s('быст..') + af_s('плод..')
    AN['hedge1'] = zsum('жив.изг.1р')
    AN['hedge2'] = zsum('жив.изг.2р')
    AN['shrubs'] = AN['лиан.'] + AN['хвой/вечз'] + AN['крас.'] + AN['медл..'] + AN['быст/плод']
    AN['shrubsAll'] = AN['hedge1'] + AN['hedge2'] + AN['shrubs']
    AN['hedge1Pcs'] = xroundup(AN['hedge1'] * 3, 3)
    AN['hedge2Pcs'] = xroundup(AN['hedge2'] * 5, 3)
    t['plant'] = AN
    C = {}
    for ty in ('хвой.', 'медл.', 'быст.', 'плод.', 'малоц.'):
        C[ty] = csum(lambda r, ty=ty: r['rm']['AO'] == ty)
    C['trees'] = C['хвой.'] + C['медл.'] + C['быст.'] + C['плод.'] + C['малоц.']
    C['leafy'] = C['trees'] - C['хвой.'] - C['плод.']
    C['treePlots'] = csum(lambda r: has(lc(r['species']), 'уч-к поросли дер') and r['rm']['K'] is not None)
    C['shrubPlots'] = csum(lambda r: has(lc(r['species']), 'уч-к поросли куст.') and r['rm']['L'] is not None)
    C['hedge1'] = csum(lambda r: r['N'] == 'ж.и.1')
    C['hedge2'] = csum(lambda r: r['N'] == 'ж.и.2')
    sumK = csum(lambda r: r['rm']['K'] is not None)
    sumL = csum(lambda r: r['rm']['L'] is not None)
    plotsD = csum(lambda r: has(lc(r['species']), 'ч-к поросли дер') and r['rm']['K'] is not None)
    plotsK = csum(lambda r: has(lc(r['species']), 'ч-к поросли куст') and r['rm']['L'] is not None)
    h1 = csum(lambda r: has(lc(r['species']), 'днорядная живая изгород') and r['rm']['L'] is not None)
    h2 = csum(lambda r: has(lc(r['species']), 'вухрядная живая изгород') and r['rm']['L'] is not None)
    C['treesC2035'] = sumK - plotsD
    C['shrubsC2035'] = sumL - plotsK - h1 - h2
    C['plotsD'], C['plotsK'], C['h1'], C['h2'] = plotsD, plotsK, h1, h2
    C['shrubTypes'] = {ty: csum(lambda r, ty=ty: r['N'] == ty)
                       for ty in ('лиан.', 'хвой..', 'вечз.', 'крас.', 'медл..', 'быст..', 'плод..')}
    t['count'] = C
    t['unit'] = S['unit']
    t['base'] = base
    return t


def money_text(v, S, base, unit=None):
    u = S['unit'] if unit is None else unit
    if u == 'руб.':
        return xnum(v * base)
    if u == 'баз.в. (руб.)':
        return xnum(v) + ' (' + xnum(v * base) + ')'
    return xnum(v)


def removal_report(res):
    S, T = res['settings'], res['removal']
    AP, AN, C, base = T['money'], T['plant'], T['count'], res['baseValue']
    plant = not S['money']
    H2035 = '' if plant else ('' if AN['shrubs'] + AN['trees'] == 0 else S['unit'])
    unit_lbl = S['unit']

    def m(v):
        return money_text(v, S, base, H2035)

    def nz(v):
        return '' if v == 0 else v
    AF28, AF48 = T['treePlots']['total'], T['shrubPlots']['total']
    AG27, AG48 = T['treePlots']['type'], T['shrubPlots']['type']
    rows = []

    def blank(v):
        return v is None or v == ''

    def row(A, B, Cc, D, E, F, G, H):
        ce, ge = blank(Cc), blank(G)
        rows.append(['' if ce else A, '' if ce else B, '' if ce else Cc, '' if ce else D,
                     '' if ge else E, '' if ge else F, '' if ge else G, '' if ge else H])
    rows.append(['-------', '-- Удаление --', '-------', '-------', '-------',
                 '--- Компенсация ' if plant else '-------',
                 '--------------' if plant else 'Компенсация  выплатами -----', '--------------'])

    def pnote(t):
        return ' (' + xnum(AF28) + ')' if AG27 == t else ''

    def pnote_s(ts):
        return ' (' + xnum(AF48) + ')' if AG48 in ts else ''
    row('Итого', '_________деревьев:', nz(C['trees']), 'шт.', 'Итого', '_______деревьев:' if plant else 'за деревья:',
        nz(AN['trees']) if plant else ('' if AP['trees'] == 0 else m(AP['trees'])),
        'шт. саженцев' if plant else unit_lbl)
    lv = AP['медл.'] + AP['быст.'] + AP['малоц.']
    row('в т.ч.', 'лиственные декор-е:', nz(C['trees'] - C['хвой.'] - C['плод.']), 'шт.',
        'в т.ч.' if plant else '--- в т.ч. за', 'хвойн./ вечнозел.:' if plant else 'лиственные декор-е:',
        ('' if AN['хвой.'] == 0 else xnum(AP['хвой.']) + pnote('хвой.')) if plant else ('' if lv == 0 else m(lv)),
        ('шт. ' + ('(в т.ч. за поросль)' if AG27 == 'хвой.' else '')) if plant else unit_lbl)
    row('в т.ч.', 'хвойные и вечнозел.:', nz(C['хвой.']), 'шт.', 'в т.ч.' if plant else '--- в т.ч. за',
        'листв. медл-раст-х:' if plant else 'хвойные и вечнозел.:',
        ('' if AN['медл.'] == 0 else xnum(AN['медл.']) + pnote('медл.')) if plant else ('' if AP['хвой.'] == 0 else m(AP['хвой.'])),
        ('шт. ' + ('(в т.ч. за поросль)' if AG27 == 'медл.' else '')) if plant else unit_lbl)
    row('в т.ч.', 'плодовые:', nz(C['плод.']), 'шт.', 'в т.ч.' if plant else '--- в т.ч. за',
        'листв. быст-раст-х:' if plant else 'плодовые:',
        ('' if AN['быст.'] == 0 else xnum(AN['быст.']) + pnote('быст.')) if plant else ('' if AP['плод.'] == 0 else m(AP['плод.'])),
        ('шт. ' + ('(в т.ч. за поросль)' if AG27 == 'быст.' else '')) if plant else unit_lbl)
    c42 = nz(C['treePlots'])
    row('Итого', 'уч-ки поросли дер-ев:', c42, 'м²', 'Итого' if plant else '--- Итого за',
        '_____кустарников:' if plant else 'уч-ки поросли дер-ев:',
        ('' if AN['shrubsAll'] == 0 else xnum(AN['shrubsAll'])) if plant else ('' if c42 == '' else m(AP['treePlots'])),
        'шт. (в т.ч. в жив. изг.)' if plant else unit_lbl)
    row('', '', '', '', 'в т.ч.' if plant else '', 'древесных лиан:' if plant else '',
        ('' if AP['лиан.'] + AN['лиан.'] == 0 else xnum(AN['лиан.']) + pnote_s(['лиан.'])) if plant else '',
        'шт. ' if plant else '')
    shrubs_cnt = C['hedge1'] + C['hedge2'] + C['shrubPlots'] + sum(C['shrubTypes'].values())
    row('Итого', '_______кустарников:', '' if shrubs_cnt == 0 else C['shrubsC2035'], 'шт.',
        'в т.ч.' if plant else '--- Итого за', 'хвойных/вечноз-х:' if plant else 'кустарники:',
        ('' if AN['хвой/вечз'] == 0 else xnum(AN['хвой/вечз']) + pnote_s(['хвой..', 'вечз.'])) if plant else ('' if AP['shrubs'] == 0 else m(AP['shrubs'])),
        'шт. ' if plant else unit_lbl)
    row('Итого', 'уч-ки поросли куст-ов:', nz(C['shrubPlots']), 'м²', 'в т.ч.' if plant else '--- Итого за',
        'красивоцветущих:' if plant else 'поросль куст.',
        ('' if AN['крас.'] == 0 else xnum(AN['крас.']) + pnote_s(['крас.'])) if plant else ('' if AP['shrubPlots'] == 0 else m(AP['shrubPlots'])),
        'шт. ' if plant else unit_lbl)
    row('Итого', '1-рядн. жив. изгородь:', nz(C['hedge1']), 'п.м.', 'в т.ч.' if plant else '--- Итого за',
        'медл-растущих:' if plant else '1-рядн. ж.изг.',
        ('' if AN['медл..'] == 0 else xnum(AN['медл..']) + pnote_s(['медл..'])) if plant else ('' if AP['ж.и.1'] == 0 else m(AP['ж.и.1'])),
        'шт. ' if plant else unit_lbl)
    row('Итого', '2-рядн. жив. изгородь:', nz(C['hedge2']), 'п.м.', 'в т.ч.' if plant else '--- Итого за',
        'быстр-х/плод-х:' if plant else '2-рядн. ж.изг.',
        ('' if AN['быст/плод'] == 0 else xnum(AN['быст/плод']) + pnote_s(['быст..', 'плод..'])) if plant else ('' if AP['ж.и.2'] == 0 else m(AP['ж.и.2'])),
        'шт. ' if plant else unit_lbl)
    g48 = ('' if AN['hedge1'] == 0 else xnum(AN['hedge1Pcs']) + pnote_s(['ж.и.1'])) if plant else m(AP['total'])
    rows.append(['', '', '', '', ('' if g48 == '' else 'в т.ч.') if plant else 'ИТОГО всех',
                 ('' if g48 == '' else '1-рядн. жив. изг.:') if plant else 'выплат:', g48,
                 '' if g48 == '' else ('шт. ' if plant else unit_lbl)])
    g49 = ('' if AN['hedge2'] == 0 else xnum(AN['hedge2Pcs']) + pnote_s(['ж.и.2'])) if plant else ''
    rows.append(['', '', '', '', '' if g49 == '' else 'в т.ч.', '' if g49 == '' else '2-рядн. жив. изг.:', g49,
                 '' if g49 == '' else 'шт. '])
    return rows


def volume_totals(rows, ref):
    rem = [r for r in rows if r['removed']]

    def acc(key):
        o = {1: 0, 2: 0, 3: 0, 'all': 0}
        for r in rem:
            w = r['rm']['wood']
            v = w[key]
            if is_num(v):
                o['all'] += v
                if w['ce']:
                    o[w['ce']] += v
        return o
    BZ, BX, BY, BW, BV = acc('BZ'), acc('BX'), acc('BY'), acc('BW'), acc('BV')
    big = {1: 0, 2: 0, 3: 0}
    for r in rem:
        w = r['rm']['wood']
        if not w['ce']:
            continue
        for p in w['perStem']:
            if p and p['d'] > 50 and is_num(p['stem']):
                big[w['ce']] += p['stem']
    big[1] *= 1.47
    big[2] *= 1.49
    big[3] *= 1.47
    buckets = [('до 12', None, 12), (' 12-16', 12, 16), (' 16-20', 16, 20), (' 20-24', 20, 24), (' 24-28', 24, 28),
               (' 28-32', 28, 32), ('более 32', 32, None)]
    table = []
    for bi, (label, lo, hi) in enumerate(buckets):
        o = {'label': label, 1: 0, 2: 0, 3: 0}
        for r in rem:
            w = r['rm']['wood']
            if not w['ce'] or r['rm']['K'] is None:
                continue
            for si, d in enumerate(r['rm']['stems']):
                if d is None:
                    continue
                if bi == 0 and si == 0 and not (r['rm']['J'] < 59):
                    continue
                if (lo is None or d > lo) and (hi is None or d <= hi):
                    o[w['ce']] += r['rm']['K']
        o['all'] = o[1] + o[2] + o[3]
        table.append(o)
    dens = ref['volumes']['density']['soft']
    return {
        'stemsM3': BZ, 'stemsT': BX, 'branchesM3': BY, 'branchesT': BW, 'shrubT': BV,
        'stumpsM3': {k: v * 0.2 for k, v in BZ.items()}, 'stumpsT': {k: v * 0.2 for k, v in BX.items()},
        'shrubM3': ((BV['all'] * 1000) / dens) / 0.1,
        'le50': {c: BZ[c] - big[c] for c in (1, 2, 3)}, 'gt50': big, 'stemsByDiameter': table,
        'table': {'m3': [xround(BZ['all'], 2), xround(BZ['all'] * 0.2, 2), xround(BY['all'], 2),
                         xround(((BV['all'] * 1000) / dens) / 0.1, 2)],
                  't': [xround(BX['all'], 3), xround(BX['all'] * 0.2, 2), xround(BW['all'], 2), xround(BV['all'], 2)]},
    }


def ball_size(ref, info, height):
    lib = info['lib']
    if not lib:
        return ''
    h = height if is_num(height) else (0 if is_blank(height) else float('inf'))
    col = 6 if h > 5 else 5 if h > 4 else 4 if h > 2 else 3 if h > 1.5 else 2 if h > 0.99 else 1
    F, G = lib.get('ballD'), lib.get('ballH')
    if col == 1:
        return 'Ø' + xnum('' if F is None else F) + '; h' + xnum('' if G is None else G)
    k = ref['_typeK'].get(lib['type'])
    if not k:
        return ''
    kd, kh = k[(col - 2) * 2], k[(col - 2) * 2 + 1]
    return 'Ø' + xnum(_norm_num(xround((F or 0) * kd, 1))) + '; h' + xnum(_norm_num(xround((G or 0) * kh, 1)))


def work_group(S, reason):
    for r in S.get('reasons') or []:
        if eqi(r.get('name'), reason):
            return r.get('group') or ''
    return ''


def transplant_totals(rows, ref, S):
    tr = [r for r in rows if r['transplanted']]
    for r in tr:
        r['ball'] = ball_size(ref, r['info'], r['height'])
        r['group'] = work_group(S, r['reason'])
        r['trType'] = r['N'] if (r['action'] == 'п' and r['diamText'] != '-' and not has(r['species'], 'ч-к поросли')) else ''

    def calc(lst):
        def s(pred):
            return fsum(r['qty'] for r in lst if pred(r))
        h1 = s(lambda r: has(lc(r['species']), 'днорядная живая изгород'))
        h2 = s(lambda r: has(lc(r['species']), 'вухрядная живая изгород'))
        shrubs = s(lambda r: r['diamText'] == '-') - h1 - h2
        trees = (s(lambda r: True) - shrubs - h1 - h2 - s(lambda r: has(lc(r['species']), 'ч-к поросли дер'))
                 - s(lambda r: has(lc(r['species']), 'ч-к поросли куст')))
        return {'trees': trees, 'shrubs': shrubs, 'hedge1': h1, 'hedge2': h2}
    tot = calc(tr)
    tot['byType'] = {ty: fsum(r['qty'] for r in tr if r['trType'] == ty) for ty in TREE_TYPES}
    groups = []
    for rs in S.get('reasons') or []:
        g = rs.get('group')
        if not g or any(x['group'] == g for x in groups):
            continue
        lst = [r for r in tr if r['group'] == g]
        if lst:
            c = calc(lst)
            c['group'] = g
            groups.append(c)
    tot['groups'] = groups
    tot['rows'] = tr
    return tot


def existing_totals(rows):
    def s(pred):
        return fsum(r['qty'] for r in rows if pred(r))
    plotsD = s(lambda r: has(lc(r['species']), 'ч-к поросли дер'))
    plotsK = s(lambda r: has(lc(r['species']), 'ч-к поросли куст'))
    dash = s(lambda r: r['diamText'] == '-')
    hedges = s(lambda r: has(lc(r['species']), 'рядная живая'))
    return {'trees': s(lambda r: True) - dash - plotsD, 'shrubs': dash - plotsK - hedges,
            'treePlots': plotsD, 'shrubPlots': plotsK,
            'hedge1': s(lambda r: has(lc(r['species']), 'днорядная живая')),
            'hedge2': s(lambda r: has(lc(r['species']), 'вухрядная живая'))}


def balance(rows, ex, rmT, trT, S):
    kept = [r for r in rows if r['action'] == '']

    def kept_of(ty):
        return fsum(r['qty'] for r in kept if r['N'] == ty and r['qty'] > 0.5 and r['diamText'] != '-')
    C = rmT['count']
    total = {k: ex[k] for k in ('trees', 'treePlots', 'shrubs', 'shrubPlots', 'hedge1', 'hedge2')}
    removed = {'trees': C['treesC2035'], 'treePlots': C['plotsD'], 'shrubs': C['shrubsC2035'],
               'shrubPlots': C['plotsK'], 'hedge1': C['h1'], 'hedge2': C['h2'], 'conif': C['хвой.'], 'fruit': C['плод.']}
    removed['leafy'] = C['trees'] - removed['fruit'] - removed['conif']
    trans = {'trees': trT['trees'], 'treePlots': 0, 'shrubs': trT['shrubs'], 'shrubPlots': 0,
             'hedge1': trT['hedge1'], 'hedge2': trT['hedge2'], 'conif': trT['byType']['хвой.'], 'fruit': trT['byType']['плод.']}
    trans['leafy'] = trT['trees'] - trans['fruit'] - trans['conif']
    kb = {k: total[k] - trans[k] - removed[k] for k in ('trees', 'treePlots', 'shrubs', 'shrubPlots', 'hedge1', 'hedge2')}
    kb['conif'] = kept_of('хвой.')
    kb['fruit'] = kept_of('плод.')
    kb['leafy'] = kb['trees'] - kb['conif'] - kb['fruit']
    total['conif'] = kb['conif'] + trans['conif'] + removed['conif']
    total['fruit'] = kb['fruit'] + trans['fruit'] + removed['fruit']
    total['leafy'] = kb['leafy'] + trans['leafy'] + removed['leafy']
    return {'kept': kb, 'transplanted': trans, 'removed': removed, 'total': total,
            'showPlots': S['showPlots'], 'hedgePieces': S['hedgePieces']}


def balance_report(res):
    b = res['balance']
    P, Hp = b['showPlots'], b['hedgePieces']

    def tr(o):
        return xnum(o['trees']) + ' + ' + xnum(o['treePlots']) if P else xnum(o['trees'])

    def sh(o):
        return xnum(o['shrubs']) + ' + ' + xnum(o['shrubPlots']) if P else xnum(o['shrubs'])

    def h1(o):
        return xnum(o['hedge1']) + ' / ' + xnum(o['hedge1'] * 3) if Hp else xnum(o['hedge1'])

    def h2(o):
        return xnum(o['hedge2']) + ' / ' + xnum(o['hedge2'] * 5) if Hp else xnum(o['hedge2'])
    out = []
    for name, key in (('Сохраняемые', 'kept'), ('Пересаживаемые', 'transplanted'), ('Удаляемые', 'removed'), ('Итого', 'total')):
        o = b[key]
        out.append([name, tr(o), o['leafy'], o['fruit'], o['conif'], sh(o), h1(o), h2(o)])
    return out


# ------------------------------------------------------------------ flowers / lawns / grass
def quality_k2(c):
    return 1 if eqi(c, 'хор.') else 0.75 if eqi(c, 'уд.') else 0.5 if eqi(c, 'плох.') else 0


def unit_text(bv, rub, S):
    if S['unit'] == 'баз.в.':
        return xfixed(bv, 0) + ' баз.в.'
    if S['unit'] == 'руб.':
        return xfixed(rub) + ' руб.'
    return xfixed(bv, 0) + ' баз.в.(' + xfixed(rub) + ' руб.)'


def area_section(lst, tariffs, projected_area, projected_value, S, base, leftover_start=None):
    rows = []
    tprev = None
    P15 = Q15 = 0
    for i, src in enumerate(lst or []):
        kind = src.get('kind')
        if is_blank(kind) and is_blank(src.get('area')) and is_blank(src.get('cond')):
            continue
        r = {'idx': i, 'src': src, 'kind': kind, 'errors': []}
        r['area'] = _norm_num(in_num(src.get('area')) or 0)
        r['cond'] = src.get('cond') or ''
        r['transplant'] = bool(src.get('transplant'))
        r['reason'] = src.get('reason') or ''
        r['group'] = work_group(S, r['reason'])
        tf = tariffs.get(kind)
        if tf is None:
            r['errors'].append('вид не найден в справочнике')
        if leftover_start is not None:
            T = r['area'] - leftover_start if tprev is None else (r['area'] if tprev > 0 else r['area'] + tprev)
        elif tprev is None:
            T = (0 if r['transplant'] else r['area']) - projected_area
        else:
            T = r['area'] if tprev > 0 else (tprev if r['transplant'] else r['area'] + tprev)
        tprev = T
        r['T'] = T
        r['U'] = r['area'] if T < 0 else (0 if T == r['area'] else r['area'] - T)
        r['F'] = ('Пересадить (' + r['reason'] + ')') if r['transplant'] else ('(' + r['reason'] + ') ' + (src.get('note') or ''))
        r['coef'] = None if is_blank(r['reason']) else coefficients(S, r['reason'], src.get('kRepl'))
        if not r['coef'] and not r['transplant']:
            r['errors'].append('не указана причина')
        r['q'] = quality_k2(r['cond'])
        if r['transplant']:
            r['P'] = r['Q'] = None
            r['E'] = '-'
        elif not r['coef'] or tf is None:
            r['P'] = r['Q'] = None
            r['E'] = 'ОШИБКА'
        else:
            paid = r['area'] if S['flowerMode'] == 'стоимостью' else max(T, 0)
            k = r['coef']['k']
            r['P'] = paid * tf * r['q'] * k[0] * k[1] * k[2] * k[3]
            r['Q'] = r['P'] * base
            O = unit_text(r['P'], r['Q'], S)
            if S['view'] == 'итог':
                r['E'] = O
            else:
                r['E'] = (O + ' = ' + xnum(r['area']) + ('' if S['flowerMode'] == 'стоимостью' else
                          ('-' + xnum(_norm_num(r['U'])) if r['U'] > 0 else '')) +
                          'м² x' + xnum(tf) + 'б.в. x' + xnum(r['q']) + 'x' + xnum(k[0]) + 'х' + xnum(k[1]) +
                          'х' + xnum(k[2]) + 'х' + xnum(k[3]))
            P15 += r['P']
            Q15 += r['Q']
        rows.append(r)
    R18 = projected_value if S['flowerMode'] == 'стоимостью' else 0
    R19 = P15 - R18
    R20 = R19 if R19 > 0 else 0
    groups = []
    for rs in S.get('reasons') or []:
        g = rs.get('group')
        if not g or any(x['group'] == g for x in groups):
            continue
        lst2 = [r for r in rows if r['group'] == g]
        if not lst2:
            continue
        share = 0 if P15 == 0 else fsum(r['P'] or 0 for r in lst2) * ((P15 - R18) / P15)
        groups.append({'group': g, 'area': fsum(r['area'] for r in lst2), 'value': share})
    return {'rows': rows, 'area': fsum(r['area'] for r in rows), 'P': P15, 'Q': Q15, 'projectedValue': R18,
            'rest': R19, 'result': R20, 'allTransplanted': bool(rows) and all(r['transplant'] for r in rows),
            'groups': groups, 'lastT': tprev}


LAWN_KINDS = ['Спортивный', 'Партерный', 'Обыкновенный', 'Луговой', 'Мавританский', 'Специального назначения']


def compute_areas(project, ref, S, base):
    pr = project.get('projected') or {}
    fl, lw = ref['flowers'], ref['lawns']
    pr_per = in_num(pr.get('perennial')) or 0
    pr_ann = in_num(pr.get('annual')) or 0
    flowers = area_section(project.get('flowers'), fl, pr_per + pr_ann,
                           pr_per * fl['Композиция из многолетников'] + pr_ann * fl['Композиция из 1-2-летников'], S, base)
    prl = pr.get('lawns') or {}
    pr_lawn_area = pr_lawn_value = 0
    for k in LAWN_KINDS:
        a = in_num(prl.get(k)) or 0
        pr_lawn_area += a
        pr_lawn_value += a * lw[k]
    lawns = area_section(project.get('lawns'), lw, pr_lawn_area, pr_lawn_value, S, base)
    lawn_ts = [r['T'] for r in lawns['rows']]
    n_empty = max(0, 17 - len(lawns['rows']))
    last_t = lawns['lastT'] if lawns['rows'] else -pr_lawn_area
    lawn_ts += [last_t] * n_empty
    any_pos = any(t > 0 for t in lawn_ts)
    extra = [-float('inf')] if lawns['rows'] else [-pr_lawn_area]
    leftover = 0 if any_pos else -max(lawn_ts + extra)
    if leftover == 0:
        leftover = 0
    glist = []
    for g in project.get('grass') or []:
        o = dict(g)
        o['kind'] = '' if (is_blank(g.get('area')) and is_blank(g.get('cond'))) else 'Иной травяной покров'
        glist.append(o)
    grass = area_section(glist, {'Иной травяной покров': ref['grass']['Иной травяной покров']}, 0, 0, S, base,
                         0 if S['flowerMode'] == 'стоимостью' else leftover)
    R61 = lawns['P'] - lawns['projectedValue']
    R91 = 0 if -R61 < 0 else -R61
    if S['flowerMode'] == 'стоимостью':
        grass['projectedValue'] = R91
        grass['rest'] = grass['P'] - R91
        grass['result'] = grass['rest'] if grass['rest'] > 0 else 0
    grass['lawnLeftoverArea'] = leftover
    return {'flowers': flowers, 'lawns': lawns, 'grass': grass}


# ------------------------------------------------------------------ public compute
def _merged_ref(project, ref):
    extra = project.get('customSpecies') or []
    if not extra:
        return prepare_ref(ref)
    key = json.dumps(extra, ensure_ascii=False, sort_keys=True)
    cache = ref.setdefault('_mergeCache', {})
    if key not in cache:
        r2 = {k: v for k, v in ref.items() if not k.startswith('_')}
        names = {lc(s['name']) for s in ref['species']}
        r2['species'] = list(ref['species']) + [s for s in extra if s.get('name') and lc(s['name']) not in names]
        cache.clear()
        cache[key] = prepare_ref(r2)
    return cache[key]


def compute(project, ref):
    ref0 = prepare_ref(ref)
    ref = _merged_ref(project, ref0)
    d = default_settings(ref)
    S = dict(d)
    ps = project.get('settings') or {}
    for k, v in ps.items():
        if v is not None:
            S[k] = v
    S['compMap'] = dict(d['compMap'])
    S['compMap'].update(ps.get('compMap') or {})
    bv_row = base_value_for(S['date'], project.get('baseValues') or ref['baseValues'])
    base = float(S['baseValue']) if not is_blank(S.get('baseValue')) else (bv_row['rub'] if bv_row else 0)
    base = _norm_num(base)
    rows = compute_trees(project, ref, S)
    for r in rows:
        if r['removed']:
            r['rm'] = removal_row(r, ref, S)
    rmT = removal_totals(rows, S, ref, base)
    vol = volume_totals(rows, ref)
    trT = transplant_totals(rows, ref, S)
    ex = existing_totals(rows)
    bal = balance(rows, ex, rmT, trT, S)
    areas = compute_areas(project, ref, S, base)
    errors = []
    for r in rows:
        for e in r['errors']:
            errors.append({'section': 'trees', 'row': r['idx'], 'no': r['no'], 'msg': e})
    for sec in ('flowers', 'lawns', 'grass'):
        for r in areas[sec]['rows']:
            for e in r['errors']:
                errors.append({'section': sec, 'row': r['idx'], 'msg': e})
    res = {'settings': S, 'baseValue': base, 'baseValueRow': bv_row, 'rows': rows, 'existing': ex, 'removal': rmT,
           'volume': vol, 'transplant': trT, 'balance': bal, 'flowers': areas['flowers'], 'lawns': areas['lawns'],
           'grass': areas['grass'], 'errors': errors}
    res['removalReport'] = removal_report(res)
    res['balanceReport'] = balance_report(res)
    return res
