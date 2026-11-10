"""Сборка веб-приложения в один HTML-файл (работает без интернета, открывается двойным щелчком).

    python3 tools/build_web.py

Результат: web/taksaciya.html (полный документ). С ключом --artifact ПУТЬ дополнительно пишется
вариант без <html>/<head> для публикации как страницы.
"""
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def read(*p):
    with open(os.path.join(ROOT, *p), encoding='utf-8') as f:
        return f.read()


def main():
    tpl = read('web', 'src', 'index.html')
    ref = json.loads(read('core', 'reference.json'))
    ref.pop('sources', None)
    parts = {
        '/*CSS*/': read('web', 'src', 'app.css'),
        '/*ENGINE*/': read('core', 'engine.js'),
        '/*REF*/': 'window.TAKS_REF = ' + json.dumps(ref, ensure_ascii=False, separators=(',', ':')) + ';',
        '/*XLSXIO*/': read('web', 'src', 'xlsxio.js'),
        '/*IMPORTER*/': read('web', 'src', 'importer.js'),
        '/*APP*/': read('web', 'src', 'app.js'),
    }
    body = tpl
    for k, v in parts.items():
        assert k in body, k
        body = body.replace(k, v.replace('</script', '<\\/script'))
    artifact = body.replace('<!--META-->', '<meta name="claude:viewport-fit-sides" content="cover">')
    full = ('<!doctype html>\n<html lang="ru">\n<head>\n<meta charset="utf-8">\n'
            '<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">\n'
            + body.replace('<!--META-->', '').replace('<header', '</head>\n<body>\n<header', 1) + '\n</body>\n</html>\n')
    with open(os.path.join(ROOT, 'web', 'taksaciya.html'), 'w', encoding='utf-8') as f:
        f.write(full)
    if len(sys.argv) > 2 and sys.argv[1] == '--artifact':
        with open(sys.argv[2], 'w', encoding='utf-8') as f:
            f.write(artifact)
    print('web/taksaciya.html', len(full.encode('utf-8')) // 1024, 'КБ')


if __name__ == '__main__':
    main()
