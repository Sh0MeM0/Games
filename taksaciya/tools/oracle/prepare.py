"""Подготовка оригинального файла к пересчёту в LibreOffice.

    python3 prepare.py ПУТЬ/к/оригиналу.xlsm      (рабочая папка — $TAKS_ORACLE_WORK, по умолчанию ./oracle_work)

Шаги: распаковка; выгрузка библиотеки пород (sheet7.tsv); удаление пустых отформатированных строк
листа «УДд» (после 2100-й) и calcChain; правки совместимости с LibreOffice (lofix.py), не меняющие
логику формул. Затем gen.py прогоняет тестовые наборы через оригинал и пишет tests/vectors/*.json.
Нужны Python 3 с lxml и LibreOffice.
"""
import os, subprocess, sys, zipfile, shutil
HERE = os.path.dirname(os.path.abspath(__file__))
WORK = os.path.abspath(os.environ.get('TAKS_ORACLE_WORK', 'oracle_work'))
src = sys.argv[1]
unz = os.path.join(WORK, 'unz')
if os.path.exists(unz):
    shutil.rmtree(unz)
os.makedirs(unz)
zipfile.ZipFile(src).extractall(unz)
with open(os.path.join(WORK, 'sheet7.tsv'), 'w', encoding='utf-8') as f:
    subprocess.run([sys.executable, os.path.join(HERE, 'dump.py'), unz, 'sheet7'], stdout=f, check=True)
subprocess.run([sys.executable, os.path.join(HERE, 'trim.py'), unz, os.path.join(WORK, 'trim')], check=True)
subprocess.run([sys.executable, os.path.join(HERE, 'lofix.py'), os.path.join(WORK, 'trim')], check=True)
print('Готово. Теперь: python3 gen.py')
