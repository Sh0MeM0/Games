"""Проверка варианта с защитой доступа (excel/Таксация-Лайт-*-защита.xlsm).

    python3 tests/test_protect.py

1. Сжатие и шифрование полей проекта VBA ([MS-OVBA] 2.4.1, 2.4.3) — туда и обратно.
2. Код недели: эталон на Python совпадает с числами, которые даёт макрос (их посчитал LibreOffice).
3. Книга .xlsm: тип содержимого, связь с vbaProject.bin, кодовые имена листов, видимость листов.
4. Проект VBA: потоки, исходный код модулей (нужен пакет olefile; без него шаг пропускается).
5. LibreOffice (нужны soffice и python-uno; иначе шаг пропускается): проект импортируется со всеми модулями,
   логика доступа (код сегодняшний, 6 и 7 дней назад, завтрашний, чужой; скрытие и показ листов) работает.
Формулы защищённой книги проверяет tests/test_excel.py с TAKS_PROTECT=1.
"""
from __future__ import annotations

import os
import random
import shutil
import re
import subprocess
import sys
import tempfile
import time
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, os.path.join(ROOT, 'tools'))
import build_excel as BX  # noqa: E402
import vba_project as VP  # noqa: E402

P = 2147483647
FAILS = []


def check(label, cond, detail=''):
    print(('  ok  ' if cond else '  ОШИБКА  ') + label + (('  ' + str(detail)) if detail and not cond else ''))
    if not cond:
        FAILS.append(label)


def tk_secret(login, password):
    """То же, что TK_Secret в tools/vba/TK.bas (по кодовым единицам UTF-16, как AscW)."""
    s = (login + '|' + password).encode('utf-16-le')
    h1, h2 = 7, 13
    for i in range(0, len(s), 2):
        c = s[i] | (s[i + 1] << 8)
        h1 = (h1 * 131 + c + 1) % P
        h2 = (h2 * 1031 + c * 7 + 3) % P
    return (h1 % 100000) * 100000 + (h2 % 100000) + 1


def tk_code(secret, d):
    """То же, что TK_Code: номер дня от 01.01.2025 и 6 цифр."""
    x, y = secret % P, secret // P
    r = (x * 48271 + d * 16807 + y * 69621 + 12345) % P
    r = (r * 69621 + 7) % P
    r = (r * 48271 + d) % P
    return '%04d-%06d' % (d, r % 1000000)


def part_codec():
    print('1. Сжатие и шифрование полей проекта VBA')
    rnd = random.Random(7)
    tk = open(os.path.join(ROOT, 'tools', 'vba', 'TK.bas'), encoding='utf-8').read().encode('cp1251')
    cases = [b'a', b'abc' * 2000, bytes(rnd.randrange(256) for _ in range(6000)), tk, b'\x00' * 10000]
    check('сжатие туда и обратно (%d наборов)' % len(cases), all(VP.ovba_decompress(VP.ovba_compress(c)) == c for c in cases))
    pid = '{00000000-0000-0000-0000-000000000000}'
    ok = all(VP.ovba_decrypt(VP.ovba_encrypt(d, pid, sd)) == (2, sum(pid.encode()) & 0xFF, d)
             for sd in range(256) for d in (b'\x00\x00\x00\x00', b'\x00', b'\xff'))
    check('шифрование CMG/DPB/GC туда и обратно (768 проверок)', ok)


def part_code():
    print('2. Код недели: эталон на Python против чисел макроса (LibreOffice)')
    # значения получены вызовом функций макроса TK.bas в LibreOffice (TK_SelfTest и отдельные вызовы)
    expected = {'secret owner/Pa55-слово': 7216708305, 'code 1234567890/680': '0680-670961',
                'code Иванов/qwerty/1': '0001-668297', 'code 9999999999/9999': '9999-119280',
                'secret a/b': 3488902494, 'code 12345/7': '0007-132769', 'secret owner/pass': 6866177523}
    got = {'secret owner/Pa55-слово': tk_secret('owner', 'Pa55-слово'), 'code 1234567890/680': tk_code(1234567890, 680),
           'code Иванов/qwerty/1': tk_code(tk_secret('Иванов', 'qwerty'), 1), 'code 9999999999/9999': tk_code(9999999999, 9999),
           'secret a/b': tk_secret('a', 'b'), 'code 12345/7': tk_code(12345, 7), 'secret owner/pass': tk_secret('owner', 'pass')}
    for k, v in expected.items():
        check(k, got[k] == v, (got[k], v))
    # все промежуточные произведения в макросе меньше 2^53 — точны в числах Double
    check('произведения в TK_Code меньше 2^53', (P - 1) * 69621 + 9999 * 16807 + 4 * 69621 + 12345 < 2 ** 53)


def part_book(path):
    print('3. Книга .xlsm')
    z = zipfile.ZipFile(path)
    ct = z.read('[Content_Types].xml').decode()
    check('тип книги с макросами', 'application/vnd.ms-excel.sheet.macroEnabled.main+xml' in ct)
    check('тип проекта VBA', 'PartName="/xl/vbaProject.bin" ContentType="application/vnd.ms-office.vbaProject"' in ct)
    rels = z.read('xl/_rels/workbook.xml.rels').decode()
    check('связь книги с vbaProject.bin', 'relationships/vbaProject" Target="vbaProject.bin"' in rels)
    wb = z.read('xl/workbook.xml').decode()
    check('кодовое имя книги ThisWorkbook', '<workbookPr codeName="ThisWorkbook"/>' in wb)
    sheets = re.findall(r'<sheet [^>]*name="([^"]+)"[^>]*state="(\w+)"', wb)
    check('первый лист «Доступ» виден', sheets[0] == (BX.SH_ACCESS, 'visible'), sheets[0])
    check('остальные листы полностью скрыты', all(st == 'veryHidden' for _n, st in sheets[1:]), sheets[1:3])
    check('служебный лист «TK» есть', any(n == BX.SH_STORE for n, _s in sheets))
    check('активный лист — «Доступ»', 'activeTab="0"' in wb)
    cns = []
    for n in sorted((n for n in z.namelist() if re.match(r'xl/worksheets/sheet\d+\.xml$', n)),
                    key=lambda n: int(re.search(r'(\d+)\.xml$', n).group(1))):
        m = re.search(r'<sheetPr[^>]*codeName="([^"]+)"', z.read(n).decode())
        cns.append(m.group(1) if m else None)
    check('кодовые имена листов', cns[0] == 'SheetTK' and cns[-1] == 'SheetTKD' and None not in cns and len(set(cns)) == len(cns), cns)
    return z.read('xl/vbaProject.bin'), cns


def part_vba(vba, cns):
    print('4. Проект VBA')
    try:
        import olefile
    except ImportError:
        print('  пропущено: нет пакета olefile (pip install olefile)')
        return
    import io
    f = olefile.OleFileIO(io.BytesIO(vba))
    streams = {'/'.join(e) for e in f.listdir()}
    need = {'PROJECT', 'PROJECTwm', 'VBA/dir', 'VBA/_VBA_PROJECT', 'VBA/ThisWorkbook', 'VBA/TK'} | {'VBA/' + c for c in cns}
    check('все потоки на месте', need <= streams, sorted(need - streams))
    rd = lambda fn: open(os.path.join(ROOT, 'tools', 'vba', fn), encoding='utf-8').read()
    for name, kind, code in [('ThisWorkbook', 'workbook', rd('ThisWorkbook.cls')), ('TK', 'std', rd('TK.bas'))] + [(c, 'sheet', '') for c in cns]:
        src = VP.ovba_decompress(f.openstream('VBA/' + name).read()).decode('cp1251')
        if name in ('ThisWorkbook', 'TK', cns[0]):
            check('исходный код модуля %s' % name, src == VP.module_source(name, kind, code))
    proj = f.openstream('PROJECT').read().decode('cp1251')
    pid = re.search(r'ID="([^"]+)"', proj).group(1)
    vals = {k: VP.ovba_decrypt(re.search(r'%s="([0-9A-F]+)"' % k, proj).group(1)) for k in ('CMG', 'DPB', 'GC')}
    check('CMG: защиты проекта нет', vals['CMG'] == (2, sum(pid.encode()) & 0xFF, b'\x00\x00\x00\x00'))
    check('DPB: пароля проекта нет', vals['DPB'][2] == b'\x00')
    check('GC: проект видим', vals['GC'][2] == b'\xff')
    check('модуль ЭтаКнига обрабатывает открытие и сохранение',
          all(x in rd('ThisWorkbook.cls') for x in ('Workbook_Open', 'Workbook_BeforeSave', 'Workbook_AfterSave')))


LO_TEST_SUB = r'''
Public Sub TK_FlowTest()
    Dim f As Integer, s As Double, today As Long, sh As Object, vis As String
    f = FreeFile
    Open "OUTFILE" For Output As #f
    On Error GoTo Fail
    s = TK_Secret("owner", "pass")
    SetName "TK_S", Format$(s, "0"), False
    Print #f, "secret=" & Format$(Secret(), "0")
    today = DayNo()
    SetName "TK_K", Digits(TK_Code(s, today)), True
    Print #f, "today=" & TK_Unlocked()
    SetName "TK_K", Digits(TK_Code(s, today - 6)), True
    Print #f, "minus6=" & TK_Unlocked()
    SetName "TK_K", Digits(TK_Code(s, today - 7)), True
    Print #f, "minus7=" & TK_Unlocked()
    SetName "TK_K", Digits(TK_Code(s, today + 1)), True
    Print #f, "plus1=" & TK_Unlocked()
    SetName "TK_K", Digits(TK_Code(s + 1, today)), True
    Print #f, "other=" & TK_Unlocked()
    Print #f, "looks=" & LooksLikeCode("0680-123456") & "," & LooksLikeCode("owner")
    TK_HideData
    vis = ""
    For Each sh In ThisWorkbook.Sheets
        vis = vis & sh.CodeName & ":" & sh.Visible & " "
    Next sh
    Print #f, "hide=" & Trim$(vis)
    SetName "TK_K", Digits(TK_Code(s, today)), True
    TK_AfterSave
    vis = ""
    For Each sh In ThisWorkbook.Sheets
        vis = vis & sh.CodeName & ":" & sh.Visible & " "
    Next sh
    Print #f, "aftersave=" & Trim$(vis)
    SetName "TK_K", "", True
    Print #f, "empty=" & TK_Unlocked()
    Close #f
    Exit Sub
Fail:
    Print #f, "ERROR " & Err.Number & ": " & Err.Description
    Close #f
End Sub
'''


def part_lo(path, cns):
    print('5. LibreOffice: импорт проекта и логика доступа')
    try:
        import uno
        from com.sun.star.beans import PropertyValue
        from com.sun.star.document.MacroExecMode import NEVER_EXECUTE, ALWAYS_EXECUTE_NO_WARN
    except ImportError:
        print('  пропущено: нет python-uno')
        return
    tmp = tempfile.mkdtemp(prefix='taksp_')
    tk = open(os.path.join(ROOT, 'tools', 'vba', 'TK.bas'), encoding='utf-8').read()
    out = os.path.join(tmp, 'flow.txt')
    # тестовая копия: те же листы и модуль TK + проверочная процедура; события книги отключены (без диалогов)
    mods = [('ThisWorkbook', 'workbook', '')] + [(c, 'sheet', '') for c in cns] + [('TK', 'std', tk + LO_TEST_SUB.replace('OUTFILE', out))]
    with zipfile.ZipFile(path) as z:
        files = {n: z.read(n) for n in z.namelist()}
    files['xl/vbaProject.bin'] = VP.build_vba_project(mods)
    test_path = os.path.join(tmp, 'flow.xlsm')
    with zipfile.ZipFile(test_path, 'w', zipfile.ZIP_DEFLATED) as z:
        for n, b in files.items():
            z.writestr(n, b)
    pipe = 'taksprot%d' % os.getpid()
    env = dict(os.environ, SAL_USE_VCLPLUGIN='svp')
    proc = subprocess.Popen(['soffice', '--headless', '--norestore', '--nologo', '--nodefault', '-env:UserInstallation=file://' + tmp + '/prof',
                             '--accept=pipe,name=%s;urp;StarOffice.ComponentContext' % pipe], env=env, stderr=subprocess.DEVNULL)
    try:
        local = uno.getComponentContext()
        resolver = local.ServiceManager.createInstanceWithContext('com.sun.star.bridge.UnoUrlResolver', local)
        ctx = None
        for _ in range(90):
            try:
                ctx = resolver.resolve('uno:pipe,name=%s;urp;StarOffice.ComponentContext' % pipe)
                break
            except Exception:
                time.sleep(1)
        desktop = ctx.ServiceManager.createInstanceWithContext('com.sun.star.frame.Desktop', ctx)

        def pv(n, v):
            x = PropertyValue()
            x.Name, x.Value = n, v
            return x
        doc = desktop.loadComponentFromURL(uno.systemPathToFileUrl(path), '_blank', 0, (pv('Hidden', True), pv('MacroExecutionMode', NEVER_EXECUTE)))
        libs = doc.BasicLibraries
        libs.loadLibrary('VBAProject')
        lib = libs.getByName('VBAProject')
        names = set(lib.getElementNames())
        check('LibreOffice импортировал все модули', names == {'ThisWorkbook', 'TK'} | set(cns), sorted(names))
        check('русские сообщения прочитаны верно', 'Доступ к расчёту закрыт' in lib.getByName('TK'))
        doc.close(True)
        doc = desktop.loadComponentFromURL(uno.systemPathToFileUrl(test_path), '_blank', 0, (pv('Hidden', False), pv('MacroExecutionMode', ALWAYS_EXECUTE_NO_WARN)))
        sc = doc.getScriptProvider().getScript('vnd.sun.star.script:VBAProject.TK.TK_FlowTest?language=Basic&location=document')
        sc.invoke((), (), ())
        doc.close(True)
        try:
            desktop.terminate()
        except Exception:
            pass
    finally:
        try:
            proc.wait(timeout=60)
        except Exception:
            proc.kill()
    res = dict(line.split('=', 1) for line in open(out, encoding='utf-8', errors='replace').read().splitlines() if '=' in line)
    hidden = ' '.join('%s:%s' % (c, -1 if c == 'SheetTK' else 2) for c in cns)
    shown = ' '.join('%s:%s' % (c, 2 if c in ('SheetTK', 'SheetTKD') else -1) for c in cns)
    exp = {'secret': str(tk_secret('owner', 'pass')), 'today': 'True', 'minus6': 'True', 'minus7': 'False', 'plus1': 'False',
           'other': 'False', 'looks': 'True,False', 'hide': hidden, 'aftersave': shown, 'empty': 'False'}
    for k, v in exp.items():
        check('логика доступа: %s' % k, res.get(k) == v, (res.get(k), v))
    shutil.rmtree(tmp, ignore_errors=True)


def main():
    tmp = tempfile.mkdtemp(prefix='taksp_')
    path = BX.build(30, os.path.join(tmp, 'book.xlsm'), protect=True)
    part_codec()
    part_code()
    vba, cns = part_book(path)
    part_vba(vba, cns)
    part_lo(path, cns)
    shutil.rmtree(tmp, ignore_errors=True)
    print('Итого ошибок: %d' % len(FAILS))
    return 1 if FAILS else 0


if __name__ == '__main__':
    sys.exit(main())
