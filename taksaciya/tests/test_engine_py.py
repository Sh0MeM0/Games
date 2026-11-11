"""Проверка Python-ядра по эталонным векторам (tests/vectors). Запуск: python3 tests/test_engine_py.py"""
import json, os, re, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'core'))
import engine as E


def cells_from_result(res):
    x = E.xnum
    c = {}
    n = len(res['rows'])
    for i, r in enumerate(res['rows']):
        row = 3 + i
        c[f'СУЩд!B{row}'] = r['no']; c[f'СУЩд!C{row}'] = r['title']; c[f'СУЩд!D{row}'] = r['qty']
        c[f'СУЩд!H{row}'] = r['H']; c[f'СУЩд!N{row}'] = r['N'] or ''
        if r['coef']: c[f'СУЩд!O{row}'] = r['O']
        if r['removed'] and r['rm']:
            m = r['rm']
            c[f'УДд!A{row}'] = r['no']; c[f'УДд!B{row}'] = r['title']; c[f'УДд!J{row}'] = m['J']
            if m['N'] is not None: c[f'УДд!N{row}'] = m['N']
            if m['AR'] is not None: c[f'УДд!AR{row}'] = m['AR']
            if E.is_num(m['Y']) or m['Y'] == '-':
                c[f'УДд!Y{row}'] = m['Y']; c[f'УДд!G{row}'] = m['G']; c[f'УДд!Z{row}'] = m['Z']
            c[f'УДд!AQ{row}'] = m['AQ']; c[f'УДд!M{row}'] = m['M']
            w = m['wood']
            c[f'УДд!CE{row}'] = w['ce'] or 0
            for k in ('BV', 'BW', 'BX', 'BY', 'BZ', 'CB', 'CC', 'CD'):
                c[f'УДд!{k}{row}'] = '' if w[k] is None else w[k]
        else:
            c[f'УДд!A{row}'] = ''
        if r['transplanted']: c[f'ПЕРЕд!G{row}'] = r['ball']
    it = 3 + n; ex = res['existing']
    c[f'СУЩд!C{it}'] = 'Итого:'
    c[f'СУЩд!D{it}'] = 'Дер. ' + x(ex['trees']); c[f'СУЩд!E{it}'] = 'Кус. ' + x(ex['shrubs'])
    c[f'СУЩд!F{it}'] = 'Дер.поросль ' + x(ex['treePlots']) + '_м²'
    c[f'СУЩд!G{it}'] = 'Кус.поросль ' + x(ex['shrubPlots']) + '_м²'
    c[f'СУЩд!H{it}'] = 'ж.изг.1ряд_' + x(ex['hedge1']) + '_п.м. ж.изг.2ряд_' + x(ex['hedge2']) + '_п.м.'
    T = res['removal']; AP, AN, C = T['money'], T['plant'], T['count']
    for r, v in [('2003', C['хвой.']), ('2004', C['медл.']), ('2005', C['быст.']), ('2006', C['плод.']), ('2007', C['малоц.']),
                 ('2008', C['treePlots']), ('2009', C['trees']), ('2011', AP['хвой.']), ('2012', AP['медл.']), ('2013', AP['быст.']),
                 ('2014', AP['плод.']), ('2015', AP['малоц.']), ('2016', AP['treePlots']), ('2017', AP['trees']),
                 ('2020', C['shrubTypes']['лиан.']), ('2021', C['shrubTypes']['хвой..']), ('2022', C['shrubTypes']['вечз.']),
                 ('2023', C['shrubTypes']['крас.']), ('2024', C['shrubTypes']['медл..']), ('2025', C['shrubTypes']['быст..']),
                 ('2026', C['shrubTypes']['плод..']), ('2027', C['hedge1']), ('2028', C['hedge2']), ('2030', C['shrubPlots']),
                 ('2033', AP['лиан.']), ('2034', AP['хвой..']), ('2035', AP['вечз.']), ('2036', AP['крас.']), ('2037', AP['медл..']),
                 ('2038', AP['быст..']), ('2039', AP['плод..']), ('2040', AP['ж.и.1']), ('2041', AP['ж.и.2']), ('2042', AP['shrubs']),
                 ('2043', AP['shrubPlots'])]:
        c[f'УДд!AP{r}'] = v
    for r, v in [('2011', AN['хвой.']), ('2012', AN['медл.']), ('2013', AN['быст.']), ('2017', AN['trees']), ('2033', AN['лиан.']),
                 ('2034', AN['хвой/вечз']), ('2035', AN['крас.']), ('2036', AN['медл..']), ('2037', AN['быст/плод']),
                 ('2040', AN['hedge1']), ('2041', AN['hedge2']), ('2042', AN['shrubs'])]:
        c[f'УДд!AN{r}'] = v
    c['УДд!AF2028'] = T['treePlots']['total']; c['УДд!AF2048'] = T['shrubPlots']['total']
    c['УДд!C2035'] = x(C['treesC2035']) + '+ ' + x(C['shrubsC2035'])
    c['УДд!D2035'] = x(C['plotsD']) + '+ ' + x(C['plotsK'])
    c['УДд!E2035'] = x(C['h1']) + '+ ' + x(C['h2'])
    for i, rowv in enumerate(res['removalReport']):
        for j, col in enumerate('ABCDEFGH'):
            c[f'УДд!{col}{2037 + i}'] = rowv[j]
    V = res['volume']
    for j, col in enumerate(['AF', 'AG', 'AH']):
        ce = j + 1
        c[f'УДд!{col}74'] = V['stemsM3'][ce]; c[f'УДд!{col}75'] = V['stemsT'][ce]
        c[f'УДд!{col}78'] = V['stumpsM3'][ce]; c[f'УДд!{col}79'] = V['stumpsT'][ce]
        c[f'УДд!{col}82'] = V['branchesM3'][ce]; c[f'УДд!{col}83'] = V['branchesT'][ce]
        c[f'УДд!{col}73'] = V['gt50'][ce]; c[f'УДд!{col}72'] = V['le50'][ce]
    for i, b in enumerate(V['stemsByDiameter']):
        for j, col in enumerate(['AK', 'AL', 'AM']):
            c[f'УДд!{col}{74 + i}'] = b[j + 1]
    c['УДд!BV1'] = V['shrubT']['all']; c['УДд!BW1'] = V['branchesT']['all']; c['УДд!BX1'] = V['stemsT']['all']
    c['УДд!BY1'] = V['branchesM3']['all']; c['УДд!BZ1'] = V['stemsM3']['all']
    for j, col in enumerate('KLMN'):
        c[f'БАЛАНСЫ!{col}9'] = V['table']['m3'][j]; c[f'БАЛАНСЫ!{col}10'] = V['table']['t'][j]
    for i, rowv in enumerate(res['balanceReport']):
        for j, col in enumerate('BCDEFGH'):
            c[f'БАЛАНСЫ!{col}{5 + i}'] = rowv[j + 1]
    tr = res['transplant']
    c['ПЕРЕд!K2003'] = tr['byType']['хвой.']; c['ПЕРЕд!K2006'] = tr['byType']['плод.']
    if tr['rows']:
        c['ПЕРЕд!C2004'] = tr['trees']; c['ПЕРЕд!D2004'] = tr['shrubs']; c['ПЕРЕд!E2004'] = tr['hedge1']; c['ПЕРЕд!F2004'] = tr['hedge2']
    for name, s, r0, tot in (('flowers', res['flowers'], 2, 15), ('lawns', res['lawns'], 36, 53), ('grass', res['grass'], 73, 90)):
        for i, r in enumerate(s['rows']):
            row = r0 + i
            c[f'ЦвГаТр!T{row}'] = r['T']; c[f'ЦвГаТр!U{row}'] = r['U']
            if r['coef']: c[f'ЦвГаТр!R{row}'] = r['coef']['str']
            if r['P'] is not None:
                c[f'ЦвГаТр!P{row}'] = r['P']; c[f'ЦвГаТр!Q{row}'] = r['Q']; c[f'ЦвГаТр!E{row}'] = r['E']
            elif r['transplant']:
                c[f'ЦвГаТр!E{row}'] = '-'
        c[f'ЦвГаТр!P{tot}'] = s['P']; c[f'ЦвГаТр!Q{tot}'] = s['Q']
    c['ЦвГаТр!R18'] = res['flowers']['projectedValue']; c['ЦвГаТр!R19'] = res['flowers']['rest']; c['ЦвГаТр!R20'] = res['flowers']['result']
    c['ЦвГаТр!R60'] = res['lawns']['projectedValue']; c['ЦвГаТр!R61'] = res['lawns']['rest']; c['ЦвГаТр!R62'] = res['lawns']['result']
    c['ЦвГаТр!R91'] = res['grass']['projectedValue']; c['ЦвГаТр!R92'] = res['grass']['rest']; c['ЦвГаТр!R93'] = res['grass']['result']
    return c


NUM = re.compile(r'^[-+]?\d*\.?\d+(e[-+]?\d+)?$', re.I)


def same(a, b):
    blank = lambda v: v is None or v == ''
    if blank(a) and blank(b): return True
    if blank(a) or blank(b):
        o = b if blank(a) else a
        return o == 0 or o == '0'
    def tonum(v):
        if isinstance(v, bool): return None
        if isinstance(v, (int, float)): return float(v)
        if NUM.match(str(v)): return float(v)
        if re.match(r'^[-+]?\d+,\d+$', str(v)): return float(str(v).replace(',', '.'))
        return None
    na, nb = tonum(a), tonum(b)
    if na is not None and nb is not None:
        return abs(na - nb) <= 1e-9 * max(1, abs(na))
    return str(a) == str(b)


def mixed_pay_check(ref):
    """Колонка «Компенсация»: итоги ведомости, где строки поочерёдно «выплатами» и «посадками»,
    равны итогам двух отдельных ведомостей — только из строк с выплатами и только из строк с посадками."""
    import copy, math
    d = os.path.join(HERE, 'vectors'); fail = checked = 0
    for f in ('c01_money_default.json', 'c02_money_mix.json', 'c03_planting.json', 'c04_planting_k.json', 'c07_money_k_all.json'):
        base = json.load(open(os.path.join(d, f), encoding='utf-8'))['project']
        for t in base['trees']: t.pop('pay', None)
        n = len(base['trees'])

        def run(money, idx, pays=None):
            p = copy.deepcopy(base); p['settings']['money'] = money
            p['trees'] = [copy.deepcopy(base['trees'][i]) for i in idx]
            for t, i in zip(p['trees'], idx):
                if pays and pays[i]: t['pay'] = pays[i]
            return E.compute(p, ref)['removal']
        ev = [i for i in range(n) if i % 2 == 0]; od = [i for i in range(n) if i % 2]
        for money in (True, False):
            other = 'посадками' if money else 'выплатами'
            X = run(money, list(range(n)), [other if i % 2 else '' for i in range(n)])
            A, B = run(money, ev), run(not money, od)       # чётные — способом из настроек, нечётные — другим
            M, P = (A, B) if money else (B, A)
            pairs = [('выплаты: итого', X['money']['total'], M['money']['total'])]
            pairs += [('посадки: ' + k, X['plant'][k], P['plant'][k]) for k in X['plant']]
            pairs += [('поросль посадками: ' + k, X['plotsPlant'][k], P['plotsPlant'][k]) for k in X['plotsPlant']]
            bad = [(k, a, b) for k, a, b in pairs if not math.isclose(a, b, rel_tol=1e-12, abs_tol=1e-9)]
            checked += len(pairs); fail += len(bad)
            print(f'смешанная компенсация, {f}, в настройках {"выплатами" if money else "посадками"}: '
                  f'{len(pairs)} итогов, расхождений: {len(bad)}')
            for k, a, b in bad[:10]: print(f'   {k}: смешанная={a!r} раздельно={b!r}')
    return checked, fail


def main():
    ref = E.load_reference()
    d = os.path.join(HERE, 'vectors'); fail = checked = orig_err = 0
    for f in sorted(os.listdir(d)):
        if not f.endswith('.json'): continue
        vec = json.load(open(os.path.join(d, f), encoding='utf-8'))
        res = E.compute(vec['project'], ref, compat=True)
        exp = cells_from_result(res)
        known = vec.get('knownDiffs') or {}
        diffs = []
        for k, v in exp.items():
            checked += 1
            o = vec['cells'].get(k)
            if isinstance(o, str) and o.startswith('#ERR'):
                if res['errors']: orig_err += 1; continue
                diffs.append((k, v, o)); continue
            if not same(v, o) and k not in known: diffs.append((k, v, o))
        print(f'{f}: {len(exp)} ячеек, расхождений: {len(diffs)}')
        for k, a, b in diffs[:15]: print(f'   {k}  ядро={a!r}  оригинал={b!r}')
        fail += len(diffs)
    print(f'Итого проверено ячеек: {checked}, расхождений: {fail}' +
          (f'; ячеек, где оригинал выдаёт #ЗНАЧ!/#Н/Д из-за ошибок ввода: {orig_err}' if orig_err else ''))
    mc, mf = mixed_pay_check(ref)
    print(f'Смешанная компенсация: проверено итогов {mc}, расхождений {mf}')
    return 1 if fail or mf else 0


if __name__ == '__main__':
    sys.exit(main())
