"""LibreOffice-compatibility rewrite of the trimmed original (semantics-preserving for Excel):
 - quote sheet name Библ.ОРМ in references;
 - replace whole-column lookup values MATCH(J:J,...) / MATCH(СУЩд!J:J,...) (Excel implicit intersection)
   by the same-row cell reference."""
import sys, re, glob
d = sys.argv[1]
pat = re.compile(r'MATCH\((СУЩд!)?J:J,')
for p in glob.glob(f'{d}/xl/worksheets/sheet*.xml'):
    s = open(p, encoding='utf-8').read()
    n0 = s.count('Библ.ОРМ!')
    s = s.replace("'Библ.ОРМ'!", 'Библ.ОРМ!').replace('Библ.ОРМ!', "'Библ.ОРМ'!")
    out = []; last = 0; cnt = 0
    for m in pat.finditer(s):
        cpos = s.rfind('<c r="', 0, m.start())
        ref = s[cpos + 6: s.index('"', cpos + 6)]
        row = re.sub(r'\D', '', ref)
        out.append(s[last:m.start()]); out.append(f'MATCH({m.group(1) or ""}J{row},'); last = m.end(); cnt += 1
    out.append(s[last:]); s = ''.join(out)
    open(p, 'w', encoding='utf-8').write(s)
    if n0 or cnt: print(p.split('/')[-1], 'quoted', n0, 'J:J fixed', cnt)
