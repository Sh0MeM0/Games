"""LibreOffice headless recalculation with ru-RU locale (as the original was authored)."""
import os, sys, subprocess, pathlib, time
def get_soffice_env():
    env = os.environ.copy()
    env['SAL_USE_VCLPLUGIN'] = 'svp'
    return env
PROFILE = pathlib.Path(os.environ.get('LO_PROFILE', os.path.join(os.environ.get('TAKS_ORACLE_WORK', 'oracle_work'), 'lo_profile'))).absolute()
MACRO = """<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE script:module PUBLIC "-//OpenOffice.org//DTD OfficeDocument 1.0//EN" "module.dtd">
<script:module xmlns:script="http://openoffice.org/2000/script" script:name="Module1" script:language="StarBasic">
Sub RecalcSave()
  ThisComponent.calculateAll()
  Dim a(0) As New com.sun.star.beans.PropertyValue
  a(0).Name = "FilterName"
  a(0).Value = "Calc MS Excel 2007 XML"
  ThisComponent.storeToURL(ThisComponent.getURL() &amp; ".out.xlsx", a())
  ThisComponent.close(True)
End Sub
</script:module>"""
XCU = """<?xml version="1.0" encoding="UTF-8"?>
<oor:items xmlns:oor="http://openoffice.org/2001/registry" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
<item oor:path="/org.openoffice.Setup/L10N"><prop oor:name="ooSetupSystemLocale" oor:op="fuse"><value>ru-RU</value></prop></item>
<item oor:path="/org.openoffice.Setup/L10N"><prop oor:name="ooLocale" oor:op="fuse"><value>ru-RU</value></prop></item>
<item oor:path="/org.openoffice.Office.Calc/Formula/Load"><prop oor:name="OOXMLRecalcMode" oor:op="fuse"><value>0</value></prop></item>
<item oor:path="/org.openoffice.Office.Calc/Formula/Load"><prop oor:name="ODFRecalcMode" oor:op="fuse"><value>0</value></prop></item>
<item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>0</value></prop></item>
</oor:items>"""
def setup():
    if (PROFILE / 'user' / 'basic' / 'Standard' / 'Module1.xba').exists():
        return
    env = get_soffice_env()
    subprocess.run(['soffice', '--headless', '--terminate_after_init', f'-env:UserInstallation={PROFILE.as_uri()}'], env=env, capture_output=True, timeout=120)
    (PROFILE / 'user' / 'basic' / 'Standard' / 'Module1.xba').write_text(MACRO)
    reg = PROFILE / 'user' / 'registrymodifications.xcu'
    t = reg.read_text()
    inner = XCU.split('xsi="http://www.w3.org/2001/XMLSchema-instance">')[1].replace('</oor:items>', '')
    t = t.replace('</oor:items>', inner + '</oor:items>')
    reg.write_text(t)
def recalc(path, timeout=1800):
    setup()
    path = os.path.abspath(path)
    out = path + '.out.xlsx'
    if os.path.exists(out): os.remove(out)
    t0 = time.time()
    r = subprocess.run(['timeout', str(timeout), 'soffice', '--headless', '--norestore', f'-env:UserInstallation={PROFILE.as_uri()}',
                        'vnd.sun.star.script:Standard.Module1.RecalcSave?language=Basic&location=application', path],
                       env=get_soffice_env(), capture_output=True, text=True)
    return out if os.path.exists(out) else None, time.time() - t0, r.stdout[-500:] + r.stderr[-500:]
if __name__ == '__main__':
    print(recalc(sys.argv[1]))
