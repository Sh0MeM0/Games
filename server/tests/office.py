"""Small OOXML fixtures with stable timestamps, suitable for Word/Excel."""

import io
import zipfile
from xml.sax.saxutils import escape

REL = "http://schemas.openxmlformats.org/package/2006/relationships"
OFFICE = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"


def document(text="Первая версия", ext=".docx"):
    value = escape(text)
    if ext == ".docx":
        main = "word/document.xml"
        files = {
            main: '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">'
            f"<w:body><w:p><w:r><w:t>{value}</w:t></w:r></w:p><w:sectPr/></w:body></w:document>"
        }
        overrides = '<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>'
    else:
        main = "xl/workbook.xml"
        files = {
            main: '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" '
            f'xmlns:r="{OFFICE}"><sheets><sheet name="Лист1" sheetId="1" r:id="rId1"/></sheets></workbook>',
            "xl/_rels/workbook.xml.rels": f'<Relationships xmlns="{REL}"><Relationship Id="rId1" '
            f'Type="{OFFICE}/worksheet" Target="worksheets/sheet1.xml"/></Relationships>',
            "xl/worksheets/sheet1.xml": '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
            f'<sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>{value}</t></is></c></row></sheetData></worksheet>',
        }
        overrides = '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>'
        overrides += '<Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>'
    files["[Content_Types].xml"] = (
        '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
        + '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/>'
        + overrides
        + "</Types>"
    )
    files["_rels/.rels"] = (
        f'<Relationships xmlns="{REL}"><Relationship Id="rId1" Type="{OFFICE}/officeDocument" Target="{main}"/></Relationships>'
    )
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w") as archive:
        for name, content in files.items():
            archive.writestr(
                zipfile.ZipInfo(name, date_time=(2020, 1, 1, 0, 0, 0)),
                content.encode(),
                compress_type=zipfile.ZIP_DEFLATED,
            )
    return output.getvalue()
