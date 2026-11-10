"""Make a trimmed copy of the original package: drop empty styled rows of УДд (sheet10) after row 2100,
drop calcChain. Formulas/values are untouched."""
import sys, os, re, shutil
src, dst = sys.argv[1], sys.argv[2]
if os.path.exists(dst): shutil.rmtree(dst)
shutil.copytree(src, dst)
p = f'{dst}/xl/worksheets/sheet10.xml'
b = open(p, 'rb').read()
i = b.find(b'<row r="2101"'); j = b.find(b'</sheetData>')
assert i > 0 and j > i
b = b[:i] + b[j:]
b = re.sub(rb'<dimension ref="[^"]*"/>', b'<dimension ref="A1:EG2100"/>', b, count=1)
open(p, 'wb').write(b)
os.remove(f'{dst}/xl/calcChain.xml')
rels = f'{dst}/xl/_rels/workbook.xml.rels'
t = open(rels, encoding='utf-8').read()
t = re.sub(r'<Relationship Id="[^"]+" Type="[^"]+/calcChain" Target="calcChain.xml"/>', '', t)
open(rels, 'w', encoding='utf-8').write(t)
ct = f'{dst}/[Content_Types].xml'
t = open(ct, encoding='utf-8').read()
t = re.sub(r'<Override PartName="/xl/calcChain.xml"[^>]*/>', '', t)
open(ct, 'w', encoding='utf-8').write(t)
print('sheet10 bytes', len(b))
