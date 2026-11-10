"""Generate randomized test cases, run them through the original workbook (LibreOffice) and save vectors."""
import json, random, sys, os, time
sys.path.insert(0, os.path.dirname(__file__))
import harness

REF = harness.REF
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'tests', 'vectors')
os.makedirs(OUT, exist_ok=True)
SHRUB_T = ['лиан.', 'хвой..', 'вечз.', 'крас.', 'медл..', 'быст..', 'плод..', 'ж.и.1', 'ж.и.2']
trees_pool = [s for s in REF['species'] if s['type'] not in SHRUB_T and s['hard']]
shrub_pool = [s for s in REF['species'] if s['type'] in SHRUB_T]
REASONS = [
    {'name': 'СЕТИ', 'group': '', 'replace': ''},
    {'name': 'БЛАГОУСТРОЙСТТВО', 'group': '', 'replace': ''},
    {'name': 'ЗДАНИЕ', 'group': 'Группа А', 'replace': '1'},
    {'name': 'ДОРОГА', 'group': 'Группа Б', 'replace': '23'},
    {'name': 'АВАРИЯ', 'group': '', 'replace': 'без компенcации'},
    {'name': 'ОЗЕЛЕНЕНИЕ', 'group': 'Группа А', 'replace': '4'},
]
HEIGHTS = [None, 0.5, 0.8, 1, 1.05, 1.1, 1.5, 2, 2.5, 3, 4, 4.5, 5, 6, 8, 10, 11, 12, 14, 15, 17, 18, 20, 23, 25, 28, 30, 35]

def diam_tree(rng):
    k = rng.random()
    if k < 0.55: return str(rng.choice([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 28, 30, 31, 32, 35, 40, 45, 48, 50, 52, 55, 60, 64, 70, 76, 80, 84, 90, 100]))
    if k < 0.85:
        n = rng.choice([2, 2, 3, 3, 4, 5, 7])
        return ','.join(str(rng.choice([3, 5, 7, 8, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 25, 30, 36, 44, 52])) for _ in range(n))
    return rng.choice(['до1', '**до1', 'до4', '**до4', 'до 1', '** до 4', '0-4', '12-14'])

def gen_trees(rng, n, mix):
    out = []
    for i in range(n):
        k = rng.random()
        t = {}
        if k < mix.get('plotT', 0.05):
            t['species'] = rng.choice(['Уч-к поросли дер.', '*Уч-к поросли дер. у дорог'])
            t['diam'] = '0-4'; t['a'] = rng.choice([3, 5, 8, 9.5, 10, 12, 25, 40])
        elif k < mix.get('plotT', 0.05) + mix.get('plotS', 0.05):
            t['species'] = rng.choice(['Уч-к поросли куст.', '*Уч-к поросли куст. у дорог'])
            t['diam'] = '-'; t['a'] = rng.choice([2, 6, 9, 10, 15, 30])
        elif k < mix.get('plotT', 0.05) + mix.get('plotS', 0.05) + mix.get('hedge', 0.05):
            t['species'] = rng.choice(['Однорядная живая изгородь', 'Двухрядная живая изгородь'])
            t['diam'] = '-'; t['a'] = rng.choice([1, 3, 5, 12.5, 20])
        elif k < 0.15 + mix.get('shrub', 0.2):
            s = rng.choice(shrub_pool); t['species'] = s['name']
            t['diam'] = '-' if rng.random() > mix.get('bad', 0.0) else '12'
            t['a'] = rng.choice(['', 1, 1, 2, 3, 5, 10])
        else:
            s = rng.choice(trees_pool); t['species'] = s['name']
            t['diam'] = diam_tree(rng) if rng.random() > mix.get('bad', 0.0) else '-'
            t['a'] = rng.choice(['', 1, 1, 1, 2, 3, 4, 5, 7, 10, 15, '3№200', '№50'])
        t['height'] = rng.choice(HEIGHTS)
        if rng.random() < 0.6: t['cond'] = rng.choice(['хор.', 'уд.', 'плох.', 'ненадл.'])
        t['action'] = rng.choices(['В', 'П', '', 'в'], [70, 15, 10, 5])[0]
        t['reason'] = rng.choice(REASONS)['name'] if (t['action'] or rng.random() < 0.3) else ''
        if rng.random() < mix.get('noreason', 0): t['reason'] = ''
        if rng.random() < 0.15: t['kRepl'] = rng.choice(['1', '2', '3', '4', '12', '34', '1234', 'без компенcации'])
        if rng.random() < 0.1: t['note'] = rng.choice(['V=50%', 'аварийное', 'V=120%', 'сухостой'])
        out.append(t)
    return out

def gen_areas(rng, kinds, n):
    out = []
    for i in range(n):
        out.append({'kind': rng.choice(kinds), 'cond': rng.choice(['хор.', 'уд.', 'плох.', 'ненадл.']),
                    'area': rng.choice([1, 5, 12.5, 20, 40, 75, 100, 250]), 'transplant': rng.random() < 0.15,
                    'reason': rng.choice(REASONS)['name'], 'note': rng.choice(['', 'клумба', '']),
                    'kRepl': rng.choice(['', '', '', '1', '24'])})
    return out

def settings(rng, **kw):
    s = {'date': rng.choice(['2026-09-02', '2025-06-01', '2024-03-15', '2019-07-01']),
         'k1': rng.random() < 0.3, 'k2': rng.random() < 0.5, 'k3': rng.random() < 0.3, 'k4': rng.random() < 0.3,
         'money': True, 'unit': rng.choice(['баз.в.', 'баз.в. (руб.)', 'руб.']),
         'view': rng.choice(['формула + итог', 'итог']), 'defaultCond': rng.choice(['уд.', 'хор.', 'плох.']),
         'shrubMassKg': rng.choice([5, 3, 8]), 'showPlots': rng.random() < 0.8, 'hedgePieces': rng.random() < 0.8,
         'flowerMode': 'метрами', 'reasons': REASONS,
         'compMap': {'плод.': 'быст.', 'малоц.': 'быст.', 'plotTree': rng.choice(['быст.', 'быст.', 'хвой.', 'медл.']),
                     'plotShrub': rng.choice(['быст..', 'быст..', 'хвой..', 'крас.', 'медл..'])}}
    s.update(kw)
    return s

def make(seed, n, mix=None, **kw):
    rng = random.Random(seed)
    case = {'settings': settings(rng, **kw), 'trees': gen_trees(rng, n, mix or {})}
    case['flowers'] = gen_areas(rng, ['Композиция из многолетников', 'Композиция из 1-2-летников'], rng.randint(0, 6))
    case['lawns'] = gen_areas(rng, list(REF['lawns'].keys()), rng.randint(0, 8))
    case['grass'] = [{k: v for k, v in g.items() if k != 'kind'} for g in gen_areas(rng, ['Иной травяной покров'], rng.randint(0, 5))]
    case['projected'] = {'perennial': rng.choice([None, 10, 50]), 'annual': rng.choice([None, 5, 30]),
                         'lawns': {k: rng.choice([None, None, 20, 100]) for k in REF['lawns']}}
    return case

PLAN = {
    'c01_money_default': dict(seed=1, n=200),
    'c02_money_mix': dict(seed=2, n=300, mix={'plotT': 0.08, 'plotS': 0.06, 'hedge': 0.06, 'shrub': 0.25}),
    'c03_planting': dict(seed=3, n=300, mix={'plotT': 0.08, 'plotS': 0.06, 'hedge': 0.06, 'shrub': 0.25}, money=False),
    'c04_planting_k': dict(seed=4, n=250, money=False, k1=True, k2=False, k3=True, k4=True),
    'c05_cost_mode': dict(seed=5, n=60, flowerMode='стоимостью'),
    'c06_errors': dict(seed=6, n=120, mix={'bad': 0.1, 'noreason': 0.1}),
    'c07_money_k_all': dict(seed=7, n=250, k1=True, k2=True, k3=True, k4=True, unit='руб.'),
    'c08_small': dict(seed=8, n=20, mix={'plotT': 0.3, 'plotS': 0.2, 'hedge': 0.2}),
}

if __name__ == '__main__':
    names = sys.argv[1:] or list(PLAN)
    for name in names:
        p = dict(PLAN[name]); seed = p.pop('seed'); n = p.pop('n'); mix = p.pop('mix', None)
        case = make(seed, n, mix, **p)
        t0 = time.time()
        cells, secs = harness.run_case(case, name)
        json.dump({'name': name, 'project': case, 'cells': cells}, open(f'{OUT}/{name}.json', 'w', encoding='utf-8'), ensure_ascii=False)
        print(name, 'rows', n, 'cells', len(cells), f'{secs:.0f}s', flush=True)
