"""Запись простой книги .xlsx (только стандартная библиотека)."""
from __future__ import annotations

import io
import zipfile
from xml.sax.saxutils import escape


def col_name(i):
    s = ''
    i += 1
    while i > 0:
        m = (i - 1) % 26
        s = chr(65 + m) + s
        i = (i - 1) // 26
    return s


def _clean(s):
    return ''.join(ch for ch in str(s) if ch in '\t\n\r' or ord(ch) >= 32)


def write_xlsx(path_or_buf, sheets, title='Таксация'):
    """sheets: [{'name', 'rows': [[cell]], 'widths': [..], 'bold': [индексы строк]}]; cell — str/число/None/{'v','b'}."""
    files = {}
    sheets_xml = rels = ctypes = ''
    for i, sh in enumerate(sheets, 1):
        rows_xml = []
        bold = set(sh.get('bold') or [])
        for r, row in enumerate(sh['rows']):
            cells = []
            for c, cell in enumerate(row or []):
                if cell is None or cell == '':
                    continue
                v, st = cell, 0
                if isinstance(cell, dict):
                    v, st = cell.get('v'), 1 if cell.get('b') else 0
                if r in bold:
                    st = 1
                ref = col_name(c) + str(r + 1)
                s = ' s="%d"' % st if st else ''
                if isinstance(v, (int, float)) and not isinstance(v, bool):
                    cells.append('<c r="%s"%s><v>%r</v></c>' % (ref, s, v))
                elif v not in (None, ''):
                    cells.append('<c r="%s"%s t="inlineStr"><is><t xml:space="preserve">%s</t></is></c>' % (ref, s, escape(_clean(v))))
            rows_xml.append('<row r="%d">%s</row>' % (r + 1, ''.join(cells)))
        cols = ''
        if sh.get('widths'):
            cols = '<cols>' + ''.join('<col min="%d" max="%d" width="%s" customWidth="1"/>' % (c + 1, c + 1, w)
                                      for c, w in enumerate(sh['widths'])) + '</cols>'
        files['xl/worksheets/sheet%d.xml' % i] = (
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetViews><sheetView workbookViewId="0"/></sheetViews>'
            + cols + '<sheetData>' + ''.join(rows_xml) + '</sheetData>'
            '<pageMargins left="0.5" right="0.5" top="0.6" bottom="0.6" header="0.3" footer="0.3"/>'
            '<pageSetup orientation="landscape" paperSize="9"/></worksheet>')
        safe = ''.join(' ' if ch in '\\/?*[]:' else ch for ch in str(sh['name']))[:31]
        sheets_xml += '<sheet name="%s" sheetId="%d" r:id="rId%d"/>' % (escape(safe, {'"': '&quot;'}), i, i)
        rels += ('<Relationship Id="rId%d" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" '
                 'Target="worksheets/sheet%d.xml"/>' % (i, i))
        ctypes += ('<Override PartName="/xl/worksheets/sheet%d.xml" '
                   'ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>' % i)
    k = len(sheets) + 1
    rels += ('<Relationship Id="rId%d" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" '
             'Target="styles.xml"/>' % k)
    files['[Content_Types].xml'] = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
        '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
        '<Default Extension="xml" ContentType="application/xml"/>'
        '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>'
        '<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>'
        '<Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>'
        + ctypes + '</Types>')
    files['_rels/.rels'] = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>'
        '<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>'
        '</Relationships>')
    files['docProps/core.xml'] = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><cp:coreProperties '
        'xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/">'
        '<dc:title>%s</dc:title><dc:creator>Таксация Лайт</dc:creator></cp:coreProperties>' % escape(_clean(title)))
    files['xl/workbook.xml'] = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" '
        'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>' + sheets_xml + '</sheets></workbook>')
    files['xl/_rels/workbook.xml.rels'] = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        + rels + '</Relationships>')
    files['xl/styles.xml'] = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
        '<fonts count="2"><font><sz val="10"/><name val="Arial"/></font><font><b/><sz val="10"/><name val="Arial"/></font></fonts>'
        '<fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>'
        '<borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>'
        '<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>'
        '<cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>'
        '<xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/></cellXfs>'
        '<cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>')
    order = ['[Content_Types].xml', '_rels/.rels', 'docProps/core.xml', 'xl/workbook.xml', 'xl/_rels/workbook.xml.rels', 'xl/styles.xml']
    order += [n for n in files if n not in order]
    own = isinstance(path_or_buf, (str, bytes)) or hasattr(path_or_buf, '__fspath__')
    with zipfile.ZipFile(path_or_buf, 'w', zipfile.ZIP_DEFLATED) as z:
        for n in order:
            z.writestr(n, files[n].encode('utf-8'))
    return path_or_buf if own else None
