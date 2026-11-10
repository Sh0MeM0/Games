import sys, re
from lxml import etree
NS='{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'
def vals(unz, sheet, maxrow=2100):
    ss=[]
    try:
        for ev, el in etree.iterparse(f'{unz}/xl/sharedStrings.xml', tag=NS+'si'):
            ss.append(''.join(el.itertext())); el.clear()
    except OSError: pass
    d={}
    for ev, el in etree.iterparse(f'{unz}/xl/worksheets/{sheet}.xml', tag=NS+'row', huge_tree=True):
        r=int(el.get('r'))
        if r>maxrow: break
        for c in el.findall(NS+'c'):
            t=c.get('t'); v=c.find(NS+'v'); val=v.text if v is not None else None
            if t=='s' and val is not None: val=ss[int(val)]
            is_=c.find(NS+'is')
            if is_ is not None: val=''.join(is_.itertext())
            if t=='e': val='#ERR:'+str(val)
            if val not in (None,''): d[c.get('r')]=val
        el.clear()
    return d
def same(a,b):
    if a==b: return True
    try: return abs(float(a)-float(b))<=1e-9*max(1,abs(float(a)))
    except: return False
if __name__=='__main__':
    A,B=sys.argv[1],sys.argv[2]; sheets=sys.argv[3].split(','); cols=sys.argv[4] if len(sys.argv)>4 else None
    for sh in sheets:
        a=vals(A,sh); b=vals(B,sh); keys=sorted(set(a)|set(b), key=lambda k:(int(re.sub(r'\D','',k)), len(re.sub(r'\d','',k)), k))
        if cols: keys=[k for k in keys if re.match(cols,k)]
        diff=[k for k in keys if not same(a.get(k),b.get(k))]
        print(f'== {sh}: cells {len(keys)} diffs {len(diff)}')
        for k in diff[:40]: print('  ',k,'| excel:',repr(a.get(k))[:80],'| lo:',repr(b.get(k))[:80])
