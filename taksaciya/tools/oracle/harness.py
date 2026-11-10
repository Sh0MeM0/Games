"""Oracle harness: feed a test case into the ORIGINAL workbook (trimmed + LO-compat), recalc in LibreOffice
(ru-RU) and dump the resulting cell values. Produces a test vector {project, cells} for the engines."""
import json, os, re, random, shutil, subprocess, sys, datetime, html
sys.path.insert(0, os.path.dirname(__file__))
import lo
from cmpvals import vals

WORK = os.path.abspath(os.environ.get('TAKS_ORACLE_WORK', 'oracle_work'))
TEMPLATE = f'{WORK}/trim'          # урезанная и совместимая с LibreOffice копия оригинала (распакованная)
REF = json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'core', 'reference.json'), encoding='utf-8'))
COLS = lambda c: sum((ord(ch) - 64) * 26 ** i for i, ch in enumerate(reversed(c)))

LIB_ROWS = {}
for _line in open(f'{WORK}/sheet7.tsv', encoding='utf-8'):
    _ref, _val, _f = _line.rstrip('\n').split('\t', 2)
    _m = re.match(r'B(\d+)$', _ref)
    if _m and int(_m.group(1)) >= 2: LIB_ROWS[int(_m.group(1))] = eval(_val)

def esc(s): return html.escape(str(s), quote=False)

def set_cells(xml, cells):
    """cells: {ref: ('n', v) | ('s', text) | ('f', formula) | ('clear',)}"""
    byrow = {}
    for ref, spec in cells.items():
        m = re.match(r'([A-Z]+)(\d+)$', ref); byrow.setdefault(int(m.group(2)), {})[m.group(1)] = spec
    starts = {int(m.group(1)): m.start() for m in re.finditer(r'<row r="(\d+)"', xml)}
    out = []; pos = 0
    for r in sorted(byrow):
        if r not in starts: raise RuntimeError(f'row {r} missing')
        st = starts[r]; en = xml.index('</row>', st) + 6
        head_end = xml.index('>', st) + 1
        if xml[head_end - 2] == '/':        # <row .../> empty
            rowhead = xml[st:head_end - 2] + '>'; body = ''; en = head_end
        else:
            rowhead = xml[st:head_end]; body = xml[head_end:en - 6]
        cellsxml = re.findall(r'<c r="([A-Z]+)\d+"([^>]*?)(?:/>|>(.*?)</c>)', body, re.S)
        cur = {}
        for col, attrs, inner in cellsxml:
            cur[col] = (attrs, inner)
        for col, spec in byrow[r].items():
            attrs, inner = cur.get(col, ('', None))
            if inner and '<f t="shared"' in inner and 'ref="' in inner:
                raise RuntimeError(f'overwriting shared master {col}{r}')
            sm = re.search(r' s="(\d+)"', attrs); s = f' s="{sm.group(1)}"' if sm else ''
            ref = f'{col}{r}'
            if spec[0] == 'n': cur[col] = (None, f'<c r="{ref}"{s}><v>{spec[1]}</v></c>')
            elif spec[0] == 's': cur[col] = (None, f'<c r="{ref}"{s} t="inlineStr"><is><t xml:space="preserve">{esc(spec[1])}</t></is></c>')
            elif spec[0] == 'f': cur[col] = (None, f'<c r="{ref}"{s} t="str"><f>{esc(spec[1])}</f><v></v></c>')
            else: cur[col] = (None, f'<c r="{ref}"{s}/>')
        parts = []
        for col in sorted(cur, key=COLS):
            attrs, inner = cur[col]
            if attrs is None: parts.append(inner)
            elif inner is None: parts.append(f'<c r="{col}{r}"{attrs}/>')
            else: parts.append(f'<c r="{col}{r}"{attrs}>{inner}</c>')
        out.append(xml[pos:st]); out.append(rowhead + ''.join(parts) + '</row>'); pos = en
    out.append(xml[pos:])
    return ''.join(out)

def val_spec(v):
    if v is None or v == '': return ('clear',)
    if isinstance(v, (int, float)) and not isinstance(v, bool): return ('n', repr(float(v)) if isinstance(v, float) else str(v))
    return ('s', str(v))

def serial(date):
    d = datetime.date.fromisoformat(date); return (d - datetime.date(1899, 12, 30)).days

TYPES_T = ['хвой.', 'медл.', 'быст.', 'плод.', 'малоц.', None, 'лиан.', 'хвой..', 'вечз.', 'крас.', 'медл..', 'быст..', 'плод..', 'ж.и.1', 'ж.и.2']

def write_case(case, outpkg):
    if os.path.exists(outpkg): shutil.rmtree(outpkg)
    shutil.copytree(TEMPLATE, outpkg)
    S = case['settings']; trees = case['trees']; n = len(trees)
    assert n >= 16
    ws = f'{outpkg}/xl/worksheets'
    # ---- СУЩд (sheet2)
    c = {}
    yn = lambda b: 'да' if b else 'нет'
    c['N1'] = ('s', yn(S['k1'])); c['P1'] = ('s', yn(S['k2'])); c['N2'] = ('s', yn(S['k3'])); c['P2'] = ('s', yn(S['k4']))
    c['I1'] = ('n', str(serial(S['date']))); c['J1'] = ('s', S['defaultCond']); c['L1'] = ('s', S['unit']); c['M1'] = ('s', S['view'])
    for i in range(16):
        r = 4 + i
        rs = S['reasons'][i] if i < len(S['reasons']) else None
        c[f'P{r}'] = val_spec(rs['name'] if rs else None); c[f'Q{r}'] = val_spec(rs['group'] if rs else None)
        c[f'S{r}'] = val_spec(rs['replace'] if rs else None)
    for i, t in enumerate(TYPES_T):
        if t is None: continue
        c[f'U{3 + i}'] = val_spec(S['compMap'].get(t) or None)
    c['U19'] = val_spec(S['compMap']['plotTree']); c['U20'] = val_spec(S['compMap']['plotShrub'])
    for i, t in enumerate(trees):
        r = 3 + i
        a = t.get('a', '')
        c[f'A{r}'] = val_spec(a if a != '' else None)
        c[f'E{r}'] = val_spec(t.get('height'))
        d = t.get('diam')
        if isinstance(d, str) and re.fullmatch(r'\d+', d): d = int(d)
        c[f'F{r}'] = val_spec(d)
        if t.get('cond'): c[f'G{r}'] = ('s', t['cond'])
        c[f'I{r}'] = val_spec(t.get('action') or None)
        c[f'J{r}'] = ('s', t['species'])
        c[f'K{r}'] = val_spec(t.get('note') or None)
        c[f'L{r}'] = val_spec(t.get('reason') or None)
        kr = t.get('kRepl')
        if isinstance(kr, str) and re.fullmatch(r'\d+', kr): kr = int(kr)
        c[f'M{r}'] = val_spec(kr if kr not in ('', None) else None)
    r = 3 + n
    c[f'J{r}'] = ('f', '" "')
    for k in 'AIKLM': c[f'{k}{r}'] = ('clear',)      # строка «Итого:» — формулы D..H остаются
    for rr in range(r + 1, 19):
        c[f'J{rr}'] = ('f', '""&""')
        for k in 'AEFIKLM': c[f'{k}{rr}'] = ('clear',)
    p = f'{ws}/sheet2.xml'; s = open(p, encoding='utf-8').read(); s = set_cells(s, c); open(p, 'w', encoding='utf-8').write(s)
    # ---- Библ.ОРМ (sheet7): рабочий список = все породы, используемые в тесте
    used = set(t['species'] for t in trees)
    c7 = {}
    for r, name in LIB_ROWS.items():
        c7[f'A{r}'] = ('n', '1') if name in used else ('clear',)
    p = f'{ws}/sheet7.xml'; s = open(p, encoding='utf-8').read(); s = set_cells(s, c7); open(p, 'w', encoding='utf-8').write(s)
    # ---- УДд1 (sheet8)
    p = f'{ws}/sheet8.xml'; s = open(p, encoding='utf-8').read()
    s = set_cells(s, {'L1': ('s', yn(S['money'])), 'J1': ('s', 'да')}); open(p, 'w', encoding='utf-8').write(s)
    # ---- БАЛАНСЫ (sheet5)
    p = f'{ws}/sheet5.xml'; s = open(p, encoding='utf-8').read()
    s = set_cells(s, {'L12': ('n', str(S['shrubMassKg'])), 'F12': ('s', yn(S['showPlots'])), 'H12': ('s', yn(S['hedgePieces']))})
    open(p, 'w', encoding='utf-8').write(s)
    # ---- ЦвГаТр (sheet3)
    c = {'T1': ('s', S['flowerMode'])}
    def area_rows(rows, r0, cnt, with_kind=True):
        for i in range(cnt):
            r = r0 + i; x = rows[i] if i < len(rows) else None
            if with_kind: c[f'B{r}'] = val_spec(x['kind'] if x else None)
            c[f'C{r}'] = val_spec(x.get('cond') if x else None)
            c[f'D{r}'] = val_spec(x.get('area') if x else None)
            c[f'G{r}'] = val_spec(('п' if x.get('transplant') else None) if x else None)
            c[f'H{r}'] = val_spec(x.get('reason') if x else None)
            c[f'I{r}'] = val_spec(x.get('note') if x else None)
            kr = x.get('kRepl') if x else None
            if isinstance(kr, str) and re.fullmatch(r'\d+', kr): kr = int(kr)
            c[f'S{r}'] = val_spec(kr if kr not in ('', None) else None)
    area_rows(case.get('flowers', []), 2, 13)
    area_rows(case.get('lawns', []), 36, 17)
    area_rows(case.get('grass', []), 73, 17, with_kind=False)
    pr = case.get('projected', {})
    c['G17'] = val_spec(pr.get('perennial')); c['G19'] = val_spec(pr.get('annual'))
    pl = pr.get('lawns', {})
    for ref, k in [('G55', 'Спортивный'), ('G57', 'Партерный'), ('G59', 'Обыкновенный'), ('G61', 'Луговой'), ('G63', 'Мавританский'), ('G65', 'Специального назначения')]:
        c[ref] = val_spec(pl.get(k))
    p = f'{ws}/sheet3.xml'; s = open(p, encoding='utf-8').read(); s = set_cells(s, c); open(p, 'w', encoding='utf-8').write(s)

def run_case(case, name):
    os.makedirs(f'{WORK}/cases', exist_ok=True)
    pkg = f'{WORK}/cases/{name}'
    write_case(case, pkg)
    xl = pkg + '.xlsm'
    if os.path.exists(xl): os.remove(xl)
    subprocess.run(['zip', '-q', '-r', '-X', xl, '[Content_Types].xml', '_rels', 'docProps', 'xl', 'customXml'], cwd=pkg, check=True)
    out, secs, log = lo.recalc(xl)
    if not out: raise RuntimeError('LO failed: ' + log)
    unz = pkg + '_out'
    if os.path.exists(unz): shutil.rmtree(unz)
    os.makedirs(unz); subprocess.run(['unzip', '-q', out], cwd=unz, check=True)
    cells = {}
    for sheet, label in [('sheet2', 'СУЩд'), ('sheet10', 'УДд'), ('sheet5', 'БАЛАНСЫ'), ('sheet11', 'ПЕРЕд'), ('sheet3', 'ЦвГаТр'), ('sheet8', 'УДд1')]:
        for k, v in vals(unz, sheet).items():
            cells[f'{label}!{k}'] = v
    shutil.rmtree(pkg); os.remove(xl)
    return cells, secs
