import sys, re
from lxml import etree
NS = '{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'
unz = sys.argv[1]; sheet = sys.argv[2]; maxrow = int(sys.argv[3]) if len(sys.argv)>3 else 10**9
minrow = int(sys.argv[4]) if len(sys.argv)>4 else 1
# shared strings
ss=[]
for ev, el in etree.iterparse(f'{unz}/xl/sharedStrings.xml', tag=NS+'si'):
    ss.append(''.join(el.itertext())); el.clear()
out = sys.stdout
for ev, el in etree.iterparse(f'{unz}/xl/worksheets/{sheet}.xml', tag=NS+'row', huge_tree=True):
    r = int(el.get('r'))
    if r < minrow: el.clear(); continue
    if r > maxrow: break
    for c in el.findall(NS+'c'):
        ref=c.get('r'); t=c.get('t'); f=c.find(NS+'f'); v=c.find(NS+'v')
        val = v.text if v is not None else None
        if t=='s' and val is not None: val = ss[int(val)]
        is_=c.find(NS+'is')
        if is_ is not None: val=''.join(is_.itertext())
        fs=''
        if f is not None:
            fs = (f.text or '')
            if f.get('t')=='shared': fs = f'[sh{f.get("si")}{" ref="+f.get("ref") if f.get("ref") else ""}] ' + fs
            if f.get('t')=='array': fs = f'[arr {f.get("ref")}] ' + fs
        if val is None and not fs: continue
        out.write(f'{ref}\t{val!r}\t{fs}\n')
    el.clear()
