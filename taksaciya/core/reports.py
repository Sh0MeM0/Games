"""Ведомости (те же, что в веб-версии): данные для таблиц и выгрузки в Excel."""
from __future__ import annotations

from . import engine as E

AREA_SECTIONS = [('flowers', 'Цветники'), ('lawns', 'Газоны'), ('grass', 'Иной травяной покров')]


def xn(v):
    if v is None:
        return ''
    return E.xnum(v) if E.is_num(v) else str(v)


def _header(project):
    m = project.get('meta') or {}
    parts = [m.get('org'), m.get('objectNo'), m.get('objectName'), ('стадия ' + m['stage']) if m.get('stage') else '']
    return ' · '.join(str(p) for p in parts if p)


def area_result_text(s, res):
    if not s['rows']:
        return '—'
    if s['allTransplanted']:
        return 'не требуется'
    if s['rest'] <= 0 and s['P'] > 0:
        return 'компенсировано проектируемым озеленением'
    return E.unit_text(s['result'], s['result'] * res['baseValue'], res['settings'])


def rep_existing(res, project):
    ex = res['existing']
    rows = []
    for r in res['rows']:
        cond = '-' if (r['removed'] and r['rm'] and r['rm']['isPlot']) else (r['cond'] or '-')
        h = '' if r['height'] is None else r['height']
        rows.append([r['no'], r['title'], r['qty'], h, r['diamText'], cond, r['H']])
    return {'id': 'existing', 'title': 'Ведомость существующих деревьев и кустарников', 'sub': _header(project),
            'parts': [{'head': ['Номер по плану', 'Порода, вид', 'Кол-во, шт.', 'Высота, м', 'Диаметр ствола, см',
                                'Качественное состояние', 'Примечание'], 'rows': rows,
                       'totals': [['Итого:', 'Дер. ' + xn(ex['trees']), 'Кус. ' + xn(ex['shrubs']),
                                   'Дер.поросль ' + xn(ex['treePlots']) + '_м²', 'Кус.поросль ' + xn(ex['shrubPlots']) + '_м²',
                                   'ж.изг.1ряд_' + xn(ex['hedge1']) + '_п.м.', 'ж.изг.2ряд_' + xn(ex['hedge2']) + '_п.м.']]}],
            'widths': [12, 36, 10, 10, 16, 14, 26]}


def plot_notes(res):
    out = []
    T = res['removal']
    for name, grp in (('Поросль деревьев', T['treePlots']), ('Поросль кустарников', T['shrubPlots'])):
        for g in grp['groups']:
            out.append('%s: %s м² × %s × (%s) = %s' % (name, xn(g['area']), xn(g['norm']), ' '.join(g['str'].split()),
                                                    xn(E.xround(g['value'], 6))))
    return out


def rep_removal(res, project):
    S = res['settings']
    rows = []
    for r in res['rows']:
        if not r['removed']:
            continue
        m = r['rm'] or {}
        rows.append([r['no'], r['title'], r['qty'], '' if r['height'] is None else r['height'], m.get('E'), m.get('F'), m.get('G'), m.get('Hnote')])
    tot = [row for row in res['removalReport'] if any(c not in ('', None) for c in row)]
    sub = _header(project) + ' · ' + (('компенсационные выплаты, ' + S['unit']) if S['money'] else 'компенсационные посадки') + \
        ' · базовая величина ' + xn(res['baseValue']) + ' руб.'
    return {'id': 'removal', 'title': 'Ведомость удаляемых деревьев и кустарников', 'sub': sub,
            'parts': [{'head': ['Номер по плану', 'Порода, вид', 'Кол-во, шт.', 'Высота, м', 'Диаметр ствола, см', 'Качественное состояние',
                                'Компенсационные посадки (выплаты)', 'Примечание'], 'rows': rows, 'totals': tot}],
            'notes': plot_notes(res), 'widths': [12, 36, 10, 10, 16, 14, 40, 18]}


def rep_transplant(res, project):
    t = res['transplant']
    rows = [[r['no'], r['title'], r['qty'], '' if r['height'] is None else r['height'], r['diamText'], r['cond'], r['ball'],
             '(' + r['reason'] + ')'] for r in t['rows']]
    tot = []
    if t['rows']:
        tot.append(['', 'Итого пересадить по объекту:', 'деревья, шт.: ' + xn(t['trees']), 'кусты, шт.: ' + xn(t['shrubs']),
                    'жив.изг.1р., п.м.: ' + xn(t['hedge1']), 'жив.изг.2р., п.м.: ' + xn(t['hedge2']), '', ''])
        for g in t['groups']:
            tot.append(['', 'в т.ч. ' + g['group'], xn(g['trees']), xn(g['shrubs']), xn(g['hedge1']), xn(g['hedge2']), '', ''])
    return {'id': 'transplant', 'title': 'Ведомость пересаживаемых деревьев и кустарников', 'sub': _header(project),
            'parts': [{'head': ['Номер по плану', 'Порода, вид', 'Кол-во, шт.', 'Высота, м', 'Диаметр ствола, см',
                                'Качественное состояние', 'Размер кома, м', 'Примечание'], 'rows': rows, 'totals': tot}],
            'widths': [12, 36, 10, 10, 16, 14, 16, 18]}


def rep_areas(res, project):
    parts = []
    for key, title in AREA_SECTIONS:
        s = res[key]
        if not s['rows']:
            continue
        rows = [[i + 1, r['kind'], r['cond'], r['area'], '-' if r['transplant'] else ('; '.join(r['errors']) if r['errors'] else r['E']), r['F']]
                for i, r in enumerate(s['rows'])]
        pv = ('учтено проектируемых: ' + xn(E.xround(s['projectedValue'], 3)) + ' б.в.') if s['projectedValue'] else ''
        parts.append({'caption': title, 'head': ['Поз.', 'Вид', 'Качественное состояние', 'Площадь, м²',
                                                 'Компенсационные посадки (выплаты)', 'Примечание'],
                      'rows': rows, 'totals': [['', 'Итого по объекту:', '', s['area'], area_result_text(s, res), pv]]})
    return {'id': 'areas', 'title': 'Ведомость удаляемых цветников, газонов и травяного покрова', 'sub': _header(project),
            'parts': parts, 'widths': [6, 30, 14, 12, 46, 24], 'empty': 'Цветники, газоны и травяной покров не внесены.'}


def _num(v):
    try:
        return float(str(v).replace(',', '.'))
    except (TypeError, ValueError):
        return 0.0


def rep_balance(res, project):
    v = res['volume']
    S = res['settings']
    area_bal = []
    totals = project.get('areaTotals') or {}
    for key, _ in AREA_SECTIONS:
        s = res[key]
        tr = E.xround(sum(r['area'] for r in s['rows'] if r['transplant']), 6)
        rem = s['area'] - tr
        if rem < 0.1:
            rem = 0
        total = _num(totals.get(key)) if totals.get(key) not in (None, '') else 0
        area_bal.append({'tr': tr, 'rem': rem, 'total': total, 'kept': total - tr - rem})

    def r4(x):
        return E.xround(x, 4)
    t = v['table']
    parts = [
        {'caption': 'Баланс существующих деревьев и кустарников',
         'head': ['Проектные предложения', 'Деревья всего, шт. + поросль, м²' if S['showPlots'] else 'Деревья всего, шт.',
                  'в т.ч. лиственные декоративные', 'плодовые', 'хвойные',
                  'Кустарники, шт. + поросль, м²' if S['showPlots'] else 'Кустарники, шт.',
                  'Живая изгородь однорядная, м / шт.' if S['hedgePieces'] else 'Живая изгородь однорядная, м',
                  'двухрядная, м / шт.' if S['hedgePieces'] else 'двухрядная, м'],
         'rows': [list(r) for r in res['balanceReport']]},
        {'caption': 'Баланс цветников, газонов, иного травяного покрова, м²', 'head': ['', 'Цветники', 'Газоны', 'Иной травяной покров'],
         'rows': [['Сохраняемые'] + [a['kept'] for a in area_bal], ['Пересаживаемые'] + [a['tr'] for a in area_bal],
                  ['Удаляемые'] + [a['rem'] for a in area_bal], ['Итого'] + [a['total'] for a in area_bal]],
         'note': 'Строка «Итого» — общая площадь на участке (вводится на вкладке «Цветники, газоны»).'},
        {'caption': 'Объёмы выхода древесины и древесных отходов', 'head': ['Единицы измерения', 'Стволы', 'Пни', 'Ветви', 'Кустарник'],
         'rows': [['складочный объём, м³'] + list(t['m3']), ['масса, тонн'] + list(t['t'])]},
        {'caption': 'По типам твёрдости древесины', 'head': ['', 'мягкие', 'хвойные', 'твёрдые', 'итого'],
         'rows': [
             ['Стволы, м³ скл. (Ø ≤ 50 см)', r4(v['le50'][1]), r4(v['le50'][2]), r4(v['le50'][3]), r4(v['le50'][1] + v['le50'][2] + v['le50'][3])],
             ['Стволы, м³ скл. (Ø > 50 см)', r4(v['gt50'][1]), r4(v['gt50'][2]), r4(v['gt50'][3]), r4(v['gt50'][1] + v['gt50'][2] + v['gt50'][3])],
             ['Стволы, м³ скл. (все)', r4(v['stemsM3'][1]), r4(v['stemsM3'][2]), r4(v['stemsM3'][3]), r4(v['stemsM3']['all'])],
             ['Пни, м³ скл.', r4(v['stumpsM3'][1]), r4(v['stumpsM3'][2]), r4(v['stumpsM3'][3]), r4(v['stumpsM3']['all'])],
             ['Ветви, м³ скл.', r4(v['branchesM3'][1]), r4(v['branchesM3'][2]), r4(v['branchesM3'][3]), r4(v['branchesM3']['all'])],
             ['Стволы, т', r4(v['stemsT'][1]), r4(v['stemsT'][2]), r4(v['stemsT'][3]), r4(v['stemsT']['all'])],
             ['Пни, т', r4(v['stumpsT'][1]), r4(v['stumpsT'][2]), r4(v['stumpsT'][3]), r4(v['stumpsT']['all'])],
             ['Ветви, т', r4(v['branchesT'][1]), r4(v['branchesT'][2]), r4(v['branchesT'][3]), r4(v['branchesT']['all'])]]},
        {'caption': 'Количество стволов по диаметрам, шт.', 'head': ['Диаметр, см', 'мяг.', 'хвн.', 'твд.', 'итого'],
         'rows': [[b['label'].strip(), b[1], b[2], b[3], b['all']] for b in v['stemsByDiameter']]},
    ]
    return {'id': 'balance', 'title': 'Баланс деревьев и кустарников, объёмы древесины', 'sub': _header(project), 'parts': parts,
            'widths': [30, 18, 18, 14, 14, 18, 22, 18]}


REPORTS = [('Существующие', rep_existing), ('Удаляемые', rep_removal), ('Пересаживаемые', rep_transplant),
           ('Цветники и газоны', rep_areas), ('Баланс и древесина', rep_balance)]


def report_sheet(rep, name):
    rows = [[{'v': rep['title'], 'b': True}], [rep.get('sub') or ''], []]
    bold = [0]
    for pt in rep['parts']:
        if pt.get('caption'):
            bold.append(len(rows))
            rows.append([pt['caption']])
        bold.append(len(rows))
        rows.append(list(pt['head']))
        for r in pt['rows'] + pt.get('totals', []):
            rows.append([c if E.is_num(c) else xn(c) for c in r])
        rows.append([])
    for n in rep.get('notes') or []:
        rows.append([n])
    return {'name': name, 'rows': rows, 'bold': bold, 'widths': rep.get('widths') or [22] * 8}


def all_sheets(res, project):
    return [report_sheet(fn(res, project), name) for name, fn in REPORTS]
