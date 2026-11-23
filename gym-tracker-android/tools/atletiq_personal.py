#!/usr/bin/env python3
"""PERSONAL USE ONLY: downloads the exercise reference of atletiq.com for a personal build.

The owner of this app asked for a build with the atletiq.com exercises for his own phone only.
Everything this script produces goes to private/ (git-ignored): it must not be committed,
published or shared. atletiq.com keeps all rights to its photos and texts.

What it does
  1. reads the list of exercises (/uprazhneniya/) and every exercise page (cached in private/atletiq/cache),
  2. takes the name, muscles, equipment, technique steps and the YouTube video id,
  3. stores two photos per exercise as small WebP files (private/assets/extra/img/<id>_0|1.webp),
  4. skips exercises that the built-in reference already has (same words in the name),
  5. writes private/assets/extra/catalog.json, read by the app at start (see Extras.java).

Requests are paced (about one every 0.7 s) and retried politely on errors.
Usage: python3 tools/atletiq_personal.py
Then:  ./build.sh --personal
"""
import hashlib
import html
import io
import json
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PRIVATE = os.path.join(ROOT, 'private')
CACHE = os.path.join(PRIVATE, 'atletiq', 'cache')
OUT = os.path.join(PRIVATE, 'assets', 'extra')
BASE = 'https://atletiq.com'
UA = 'Mozilla/5.0 (compatible; personal-gym-log/1.0)'
WIDTH, QUALITY = 280, 50
DELAY = 0.7

# site muscle id -> app group
GROUP = {'5': 'legs', '4': 'abs', '3': 'shoulders', '2': 'chest', '6': 'legs', '1': 'triceps', '17': 'back',
         '9': 'glutes', '15': 'biceps', '16': 'back', '7': 'calves', '8': 'back', '14': 'forearms', '18': 'traps',
         '19': 'legs', '20': 'glutes', '21': 'traps'}
# site equipment id -> app bucket (machine, cable, barbell, dumbbell, body, other)
EQUIP = {'2': 'body', '1': 'barbell', '10': 'dumbbell', '6': 'other', '5': 'cable', '3': 'machine', '9': 'dumbbell',
         '11': 'other', '4': 'other', '17': 'other', '16': 'barbell', '18': 'other', '12': 'other', '8': 'machine',
         '7': 'barbell', '15': 'barbell', '14': 'other'}
SITE_EQUIP = {'2': 'С весом тела', '1': 'Штанга', '10': 'Гантели', '6': 'Другое', '5': 'Тросовые тренажеры',
              '3': 'Тренажер', '9': 'Гири', '11': 'Эспандер-жгут', '4': 'Фитбол', '17': 'Босу', '16': 'Бодибар',
              '18': 'Мини-петли', '12': 'Медбол', '8': 'Машина Смита', '7': 'Силовая рама',
              '15': 'Штанга с EZ грифом', '14': 'Роллер'}

_last = [0.0]


def fetch(url, binary=False):
    key = hashlib.sha1(url.encode()).hexdigest()
    path = os.path.join(CACHE, key)
    if os.path.exists(path):
        data = open(path, 'rb').read()
        return data if binary else data.decode('utf-8', 'replace')
    for attempt in range(6):
        wait = _last[0] + DELAY - time.time()
        if wait > 0:
            time.sleep(wait)
        _last[0] = time.time()
        try:
            req = urllib.request.Request(url, headers={'User-Agent': UA})
            with urllib.request.urlopen(req, timeout=60) as r:
                data = r.read()
            break
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return None
            if e.code not in (429, 500, 502, 503, 504) or attempt == 5:
                raise
            time.sleep(min(120, 10 * 2 ** attempt))
        except (urllib.error.URLError, TimeoutError, ConnectionError):
            if attempt == 5:
                raise
            time.sleep(10 * (attempt + 1))
    os.makedirs(CACHE, exist_ok=True)
    open(path, 'wb').write(data)
    return data if binary else data.decode('utf-8', 'replace')


def text_lines(h):
    body = h[h.find('<main'):h.find('</main>')] if '<main' in h else h
    body = re.sub(r'<script.*?</script>|<style.*?</style>', '', body, flags=re.S)
    t = re.sub(r'<[^>]+>', '\n', body)
    return [html.unescape(re.sub(r'\s+', ' ', x)).strip() for x in t.split('\n') if x.strip()]


def section(lines, start, stops):
    if start not in lines:
        return []
    i = lines.index(start) + 1
    out = []
    while i < len(lines) and lines[i] not in stops:
        out.append(lines[i])
        i += 1
    return out


def parse_page(h, site_id):
    lines = text_lines(h)
    muscles = section(lines, 'Какие мышцы работают', {'Приложение AtletIQ', 'Как правильно делать', 'Похожие упражнения'})
    main, helpers, cur = [], [], None
    for x in muscles:
        if x == 'Основная нагрузка':
            cur = main
        elif x == 'Помогают':
            cur = helpers
        elif cur is not None:
            cur.append(x)
    steps = []
    if 'Как правильно делать' in lines:
        i = lines.index('Как правильно делать') + 1
        m = re.match(r'(\d+)\s+шаг', lines[i]) if i < len(lines) else None
        if m:
            steps = lines[i + 1:i + 1 + int(m.group(1))]
    video = re.search(r'ytimg\.com/vi/([\w-]{11})', h)
    # names vary: male_1_v3.jpg, male_1.jpg, female_1.jpg, img_male_1.jpg, .jpeg/.png
    photos = list(dict.fromkeys(re.findall(
        r'content/exercises/%s/((?:img_)?(?:male|female)_[12](?:_v\d+)?\.(?:jpg|jpeg|png|webp))' % site_id, h)))
    p1 = next((p for p in photos if re.search(r'_1(?:_v\d+)?\.', p)), None)
    # the end photo of the same person and version as the start photo
    p2 = None
    if p1:
        want = re.sub(r'_1((?:_v\d+)?\.)', r'_2\1', p1)
        p2 = want if want in photos else next((p for p in photos if re.search(r'_2(?:_v\d+)?\.', p)), None)
    return {'main': main, 'helpers': helpers, 'steps': steps, 'video': video.group(1) if video else None,
            'p1': p1, 'p2': p2}


def save_webp(raw, path):
    from PIL import Image
    img = Image.open(io.BytesIO(raw)).convert('RGB')
    h = round(img.height * WIDTH / img.width)
    img.resize((WIDTH, h), Image.LANCZOS).save(path, 'WEBP', quality=QUALITY, method=6)


# --- same word stems as Matcher.java, to skip exercises the built-in reference already has ---
STOP = set('на в во с со для из за к ко по и от до под над а the on with of a машине упражнение кг'.split())
ENDINGS = ['ями', 'ами', 'ого', 'его', 'ому', 'ему', 'ыми', 'ими', 'ией', 'ой', 'ей', 'ий', 'ый', 'ая', 'яя',
           'ое', 'ее', 'ые', 'ие', 'ов', 'ев', 'ам', 'ям', 'ах', 'ях', 'ом', 'ем', 'ую', 'юю', 'ия', 'ья', 'ью',
           'а', 'я', 'о', 'е', 'ы', 'и', 'у', 'ю', 'ь', 'й']


def stem(w):
    if len(w) <= 3 or not re.search('[а-я]', w):
        return w[:-1] if len(w) > 4 and w.endswith('s') else w
    for e in ENDINGS:
        if w.endswith(e) and len(w) - len(e) >= 3:
            return w[:-len(e)]
    return w


def key_of(text):
    t = text.lower().replace('ё', 'е').replace('э', 'е')
    t = ''.join(c if c.isalnum() else ' ' for c in t)
    words = [stem(w) for w in t.split() if w not in STOP]
    return tuple(sorted(w for w in words if w not in STOP))


def builtin_phrases():
    """Titles and synonyms of the built-in reference (dumped by a tiny Java program)."""
    tmp = os.path.join(PRIVATE, 'atletiq', 'dump')
    os.makedirs(tmp, exist_ok=True)
    src = os.path.join(tmp, 'Dump.java')
    open(src, 'w').write('import app.gymlog.*;\npublic class Dump { public static void main(String[] a) {\n'
                         '  for (Catalog.Item it : Catalog.all()) { System.out.println(it.title);\n'
                         '    for (String s : it.synonyms) System.out.println(s); } } }\n')
    gym = os.path.join(ROOT, 'src', 'app', 'gymlog')
    subprocess.check_call(['javac', '-nowarn', '-encoding', 'UTF-8', '-d', tmp,
                           os.path.join(gym, 'Catalog.java'), os.path.join(gym, 'Matcher.java'),
                           os.path.join(gym, 'PhotoData.java'), src], stderr=subprocess.DEVNULL)
    out = subprocess.check_output(['java', '-Dfile.encoding=UTF-8', '-Dstdout.encoding=UTF-8', '-cp', tmp, 'Dump'],
                                  stderr=subprocess.DEVNULL).decode('utf-8')
    return {key_of(x) for x in out.splitlines() if x.strip()}


def main():
    listing = fetch(BASE + '/uprazhneniya/')
    items = []
    for p in listing.split('<li class="ex-item"')[1:]:
        p = p[:p.find('</li>')]
        attr = lambda k: (re.search(r'%s="([^"]*)"' % k, p) or [None, None])[1]
        items.append({'m': attr('data-m'), 'q': attr('data-q'), 'href': attr('href'),
                      'thumb': re.search(r'src="(/img/t/[^"]+)"', p).group(1),
                      'site_id': re.search(r'/img/t/(\d+)-', p).group(1),
                      'name': html.unescape(re.search(r'class="ex-t">([^<]*)<', p).group(1)).strip()})
    print('listed:', len(items), flush=True)
    known = builtin_phrases()
    img_dir = os.path.join(OUT, 'img')
    os.makedirs(img_dir, exist_ok=True)
    out, skipped, seen = [], 0, set()
    for n, it in enumerate(items):
        k = key_of(it['name'])
        if k in known or k in seen:
            skipped += 1
            continue
        page = fetch(BASE + it['href'])
        if not page:
            continue
        info = parse_page(page, it['site_id'])
        sid = it['site_id']
        f0, f1 = os.path.join(img_dir, sid + '_0.webp'), os.path.join(img_dir, sid + '_1.webp')
        if not os.path.exists(f0):
            # the full photo compresses better than the site's already compressed preview
            raw = fetch('%s/content/exercises/%s/%s' % (BASE, sid, info['p1']), binary=True) if info['p1'] else None
            raw = raw or fetch(BASE + it['thumb'], binary=True)
            if raw:
                save_webp(raw, f0)
        if not os.path.exists(f1) and info['p2']:
            raw = fetch('%s/content/exercises/%s/%s' % (BASE, sid, info['p2']), binary=True)
            if raw:
                save_webp(raw, f1)
        if not os.path.exists(f0):
            continue
        seen.add(k)
        groups = [GROUP.get(it['m'], 'abs')]
        muscles = [x.lower() for x in info['main'] + info['helpers']]
        out.append({
            'id': sid, 'name': it['name'], 'groups': groups,
            'equipment': EQUIP.get(it['q'], 'other'), 'equipmentTitle': SITE_EQUIP.get(it['q'], 'Другое'),
            'bodyweight': it['q'] == '2', 'muscles': ', '.join(dict.fromkeys(muscles)),
            'steps': info['steps'], 'video': info['video'], 'frames': 2 if os.path.exists(f1) else 1,
            'url': BASE + it['href'],
        })
        if n % 50 == 0:
            print('%d/%d, kept %d, skipped %d' % (n, len(items), len(out), skipped), flush=True)
    with open(os.path.join(OUT, 'catalog.json'), 'w', encoding='utf-8') as f:
        json.dump({'source': 'atletiq.com', 'personalUseOnly': True, 'exercises': out}, f, ensure_ascii=False)
    size = sum(os.path.getsize(os.path.join(img_dir, x)) for x in os.listdir(img_dir))
    print('done: %d exercises, %d already in the app, images %.1f MB' % (len(out), skipped, size / 1e6))


if __name__ == '__main__':
    main()
