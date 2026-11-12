"""Сборка проекта макросов VBA (xl/vbaProject.bin) без Excel — по открытым спецификациям Microsoft:

* [MS-CFB] — составной файл (контейнер потоков);
* [MS-OVBA] — проект VBA: потоки PROJECT, PROJECTwm, VBA/dir, VBA/_VBA_PROJECT и модули, сжатие исходного кода (2.4.1),
  шифрование полей CMG/DPB/GC (2.4.3).

Исходный код модулей хранится сжатым; кэш скомпилированного кода не пишется (версия 0xFFFF в _VBA_PROJECT), поэтому
Excel при открытии компилирует модули из исходного кода.
"""
from __future__ import annotations

import struct

# ------------------------------------------------------------------ [MS-OVBA] 2.4.1 сжатие


def _copy_token_help(diff):
    bit_count = 4
    while (1 << bit_count) < diff:
        bit_count += 1
    length_mask = 0xFFFF >> bit_count
    return bit_count, length_mask, length_mask + 3


def _compress_chunk(data, start, end):
    out = bytearray()
    cur = start
    while cur < end:
        flag_pos = len(out)
        out.append(0)
        flag = 0
        for bit in range(8):
            if cur >= end:
                break
            bit_count, _mask, max_len = _copy_token_help(cur - start)
            best_len, best_off = 0, 0
            cand = cur - 1
            while cand >= start:
                ln = 0
                while cur + ln < end and ln < max_len and data[cand + ln] == data[cur + ln]:
                    ln += 1
                if ln > best_len:
                    best_len, best_off = ln, cur - cand
                cand -= 1
            if best_len >= 3:
                token = ((best_off - 1) << (16 - bit_count)) | (best_len - 3)
                out += struct.pack('<H', token)
                flag |= 1 << bit
                cur += best_len
            else:
                out.append(data[cur])
                cur += 1
        out[flag_pos] = flag
    return bytes(out)


def ovba_compress(data: bytes) -> bytes:
    """CompressedContainer: байт сигнатуры 0x01 и блоки по 4096 байт исходных данных."""
    out = bytearray(b'\x01')
    pos = 0
    while pos < len(data) or pos == 0:
        end = min(pos + 4096, len(data))
        body = _compress_chunk(data, pos, end)
        if len(body) + 2 <= 4098:
            out += struct.pack('<H', 0xB000 | (len(body) + 2 - 3)) + body       # сжатый блок (флаг 1, сигнатура 0b011)
        elif end - pos == 4096:
            out += struct.pack('<H', 0x3000 | (4098 - 3)) + data[pos:end]       # несжатый блок ровно 4096 байт
        else:
            raise ValueError('блок не сжимается: %d байт' % (end - pos))
        pos = end
        if not data:
            break
    return bytes(out)


def ovba_decompress(buf: bytes) -> bytes:
    """Обратное преобразование (для проверки)."""
    assert buf[0] == 1
    out = bytearray()
    pos = 1
    while pos < len(buf):
        hdr = struct.unpack_from('<H', buf, pos)[0]
        size = (hdr & 0x0FFF) + 3
        flag = hdr >> 15
        assert (hdr >> 12) & 0x7 == 0b011, 'сигнатура блока'
        chunk_end = pos + size
        pos += 2
        start = len(out)
        if not flag:
            out += buf[pos:pos + 4096]
            pos += 4096
            continue
        while pos < chunk_end:
            fl = buf[pos]
            pos += 1
            for bit in range(8):
                if pos >= chunk_end:
                    break
                if fl & (1 << bit):
                    token = struct.unpack_from('<H', buf, pos)[0]
                    pos += 2
                    bit_count, length_mask, _ = _copy_token_help(len(out) - start)
                    length = (token & length_mask) + 3
                    offset = (token >> (16 - bit_count)) + 1
                    for _ in range(length):
                        out.append(out[-offset])
                else:
                    out.append(buf[pos])
                    pos += 1
    return bytes(out)


# ------------------------------------------------------------------ [MS-OVBA] 2.4.3 шифрование полей CMG, DPB, GC


def ovba_encrypt(data: bytes, project_id: str, seed: int) -> str:
    proj_key = sum(project_id.encode('ascii')) & 0xFF
    version = 2
    out = bytearray([seed, seed ^ version, seed ^ proj_key])
    u1, e1, e2 = proj_key, seed ^ proj_key, seed ^ version
    plain = bytearray()
    for _ in range((seed & 6) // 2):
        plain.append(0)                                   # «пустые» байты, значение не важно
    plain += struct.pack('<I', len(data)) + data
    for b in plain:
        be = b ^ ((e2 + u1) & 0xFF)
        out.append(be)
        e2, e1, u1 = e1, be, b
    return out.hex().upper()


def ovba_decrypt(hexs: str):
    b = bytes.fromhex(hexs)
    seed, venc, penc = b[0], b[1], b[2]
    u1, e1, e2 = seed ^ penc, penc, venc
    res = []
    for be in b[3:]:
        x = be ^ ((e2 + u1) & 0xFF)
        e2, e1, u1 = e1, be, x
        res.append(x)
    ign = (seed & 6) // 2
    n = struct.unpack('<I', bytes(res[ign:ign + 4]))[0]
    return seed ^ venc, seed ^ penc, bytes(res[ign + 4:ign + 4 + n])


# ------------------------------------------------------------------ [MS-CFB] составной файл, версия 3

FREESECT, ENDOFCHAIN, FATSECT, NOSTREAM = 0xFFFFFFFF, 0xFFFFFFFE, 0xFFFFFFFD, 0xFFFFFFFF


def _cfb_key(name):
    return (len(name), name.upper())


def cfb_write(streams: dict) -> bytes:
    """streams: {'PROJECT': b'..', 'VBA/dir': b'..', …} → байты составного файла (сектор 512 байт, мини-поток для < 4096)."""
    # дерево каталогов
    nodes = [{'name': 'Root Entry', 'type': 5, 'children': [], 'data': b''}]
    index = {'': 0}
    for path in sorted(streams):
        parts = path.split('/')
        for i in range(len(parts) - 1):
            p = '/'.join(parts[:i + 1])
            if p not in index:
                index[p] = len(nodes)
                nodes.append({'name': parts[i], 'type': 1, 'children': [], 'data': b''})
                nodes[index['/'.join(parts[:i])]]['children'].append(index[p])
        index[path] = len(nodes)
        nodes.append({'name': parts[-1], 'type': 2, 'children': [], 'data': streams[path]})
        nodes[index['/'.join(parts[:-1])]]['children'].append(index[path])
    for n in nodes:
        n['left'] = n['right'] = n['child'] = NOSTREAM
        n['color'] = 1
    # красно-чёрное дерево из отсортированных детей: сбалансировано, нижний неполный уровень — красный
    for n in nodes:
        kids = sorted(n['children'], key=lambda k: _cfb_key(nodes[k]['name']))
        if not kids:
            continue
        depth = {}

        def build(lo, hi, d):
            if lo > hi:
                return NOSTREAM
            mid = (lo + hi) // 2
            k = kids[mid]
            depth[k] = d
            nodes[k]['left'] = build(lo, mid - 1, d + 1)
            nodes[k]['right'] = build(mid + 1, hi, d + 1)
            return k
        n['child'] = build(0, len(kids) - 1, 0)
        maxd = max(depth.values())
        perfect = len(kids) == (1 << (maxd + 1)) - 1
        for k, d in depth.items():
            nodes[k]['color'] = 0 if (d == maxd and not perfect) else 1
    # мини-поток
    mini = bytearray()
    minifat = []
    for n in nodes:
        if n['type'] == 2 and len(n['data']) < 4096:
            data = n['data']
            if not data:
                n['start'], n['size'] = ENDOFCHAIN, 0
                continue
            first = len(mini) // 64
            cnt = (len(data) + 63) // 64
            mini += data + b'\x00' * (cnt * 64 - len(data))
            for i in range(cnt):
                minifat.append(first + i + 1 if i < cnt - 1 else ENDOFCHAIN)
            n['start'], n['size'] = first, len(data)
    # раскладка секторов: FAT | каталог | мини-FAT | мини-поток | большие потоки
    big = [n for n in nodes if n['type'] == 2 and len(n['data']) >= 4096]
    sec = lambda nbytes: (nbytes + 511) // 512
    n_dir = sec(len(nodes) * 128)
    n_minifat = sec(len(minifat) * 4)
    n_mini = sec(len(mini))
    n_big = sum(sec(len(n['data'])) for n in big)
    n_fat = 1
    while n_fat * 128 < n_fat + n_dir + n_minifat + n_mini + n_big:
        n_fat += 1
    assert n_fat <= 109, 'нужен DIFAT'
    fat = [FATSECT] * n_fat
    cur = n_fat

    def chain(count):
        nonlocal cur
        if count == 0:
            return ENDOFCHAIN
        first = cur
        for i in range(count):
            fat.append(cur + 1 if i < count - 1 else ENDOFCHAIN)
            cur += 1
        return first
    dir_start = chain(n_dir)
    minifat_start = chain(n_minifat)
    mini_start = chain(n_mini)
    for n in big:
        n['start'] = chain(sec(len(n['data'])))
        n['size'] = len(n['data'])
    nodes[0]['start'] = mini_start if mini else ENDOFCHAIN
    nodes[0]['size'] = len(mini)
    fat += [FREESECT] * (n_fat * 128 - len(fat))
    # заголовок
    hdr = bytearray(512)
    hdr[0:8] = bytes.fromhex('D0CF11E0A1B11AE1')
    struct.pack_into('<HHHHH', hdr, 24, 0x003E, 0x0003, 0xFFFE, 9, 6)
    struct.pack_into('<IIIIIIII', hdr, 40, 0, n_fat, dir_start, 0, 4096,
                     minifat_start if n_minifat else ENDOFCHAIN, n_minifat, ENDOFCHAIN)
    struct.pack_into('<I', hdr, 72, 0)          # число секторов DIFAT
    difat = list(range(n_fat)) + [FREESECT] * (109 - n_fat)
    struct.pack_into('<109I', hdr, 76, *difat)
    # каталог
    dirb = bytearray()
    for n in nodes:
        e = bytearray(128)
        nm = n['name'].encode('utf-16-le')
        assert len(nm) <= 62, n['name']
        e[0:len(nm)] = nm
        struct.pack_into('<HBB', e, 64, len(nm) + 2, n['type'], n['color'])
        struct.pack_into('<III', e, 68, n['left'], n['right'], n['child'])
        start = n.get('start', 0)
        if n['type'] == 1:
            start = 0
        struct.pack_into('<IQ', e, 116, start, n.get('size', 0))
        dirb += e
    dirb += b'\x00' * (n_dir * 512 - len(dirb))
    for i in range(len(nodes), n_dir * 4):                       # пустые записи: ссылки NOSTREAM
        struct.pack_into('<III', dirb, i * 128 + 68, NOSTREAM, NOSTREAM, NOSTREAM)
    body = bytearray()
    body += struct.pack('<%dI' % len(fat), *fat)
    body += dirb
    mf = minifat + [FREESECT] * (n_minifat * 128 - len(minifat))
    body += struct.pack('<%dI' % len(mf), *mf)
    body += mini + b'\x00' * (n_mini * 512 - len(mini))
    for n in big:
        body += n['data'] + b'\x00' * (sec(len(n['data'])) * 512 - len(n['data']))
    return bytes(hdr + body)


# ------------------------------------------------------------------ [MS-OVBA] проект


def _rec(rid, payload=b''):
    return struct.pack('<HI', rid, len(payload)) + payload


DOC_BASE = {'workbook': '0{00020819-0000-0000-C000-000000000046}', 'sheet': '0{00020820-0000-0000-C000-000000000046}'}


def module_source(name, kind, code):
    """Исходный текст модуля с атрибутами, как их пишет редактор VBA. kind: 'workbook', 'sheet' или 'std'."""
    lines = ['Attribute VB_Name = "%s"' % name]
    if kind in DOC_BASE:
        lines += ['Attribute VB_Base = "%s"' % DOC_BASE[kind], 'Attribute VB_GlobalNameSpace = False',
                  'Attribute VB_Creatable = False', 'Attribute VB_PredeclaredId = True', 'Attribute VB_Exposed = True',
                  'Attribute VB_TemplateDerived = False', 'Attribute VB_Customizable = True']
    body = code.replace('\r\n', '\n').rstrip('\n').split('\n') if code.strip() else []
    return '\r\n'.join(lines + body) + '\r\n'


def build_vba_project(modules, codepage=1251, project_name='VBAProject', seed=0x5A):
    """modules: [(имя, вид, код)] — вид 'workbook' (ЭтаКнига), 'sheet' (модуль листа) или 'std' (обычный модуль).
    Имена модулей — латиницей. Возвращает байты vbaProject.bin."""
    enc = 'cp%d' % codepage
    pid = '{00000000-0000-0000-0000-000000000000}'
    streams = {}
    # VBA/dir
    d = bytearray()
    d += _rec(0x0001, struct.pack('<I', 1))                       # PROJECTSYSKIND: Win32
    d += _rec(0x0002, struct.pack('<I', 0x0409))                  # PROJECTLCID
    d += _rec(0x0014, struct.pack('<I', 0x0409))                  # PROJECTLCIDINVOKE
    d += _rec(0x0003, struct.pack('<H', codepage))                # PROJECTCODEPAGE
    d += _rec(0x0004, project_name.encode(enc))                   # PROJECTNAME
    d += _rec(0x0005) + _rec(0x0040)                              # PROJECTDOCSTRING (+ Unicode)
    d += _rec(0x0006) + _rec(0x003D)                              # PROJECTHELPFILEPATH
    d += _rec(0x0007, struct.pack('<I', 0))                       # PROJECTHELPCONTEXT
    d += _rec(0x0008, struct.pack('<I', 0))                       # PROJECTLIBFLAGS
    d += struct.pack('<HIIH', 0x0009, 4, 1, 0)                    # PROJECTVERSION: Reserved=4, Major=1, Minor=0
    d += _rec(0x000C) + _rec(0x003C)                              # PROJECTCONSTANTS
    # ссылка на stdole (OLE Automation), как в новой книге Excel
    libid = b'*\\G{00020430-0000-0000-C000-000000000046}#2.0#0#C:\\Windows\\System32\\stdole2.tlb#OLE Automation'
    d += _rec(0x0016, b'stdole') + _rec(0x003E, 'stdole'.encode('utf-16-le'))
    d += _rec(0x000D, struct.pack('<I', len(libid)) + libid + struct.pack('<IH', 0, 0))
    d += _rec(0x000F, struct.pack('<H', len(modules)))            # PROJECTMODULES
    d += _rec(0x0013, struct.pack('<H', 0xFFFF))                  # PROJECTCOOKIE
    for name, kind, code in modules:
        nb, nu = name.encode(enc), name.encode('utf-16-le')
        d += _rec(0x0019, nb) + _rec(0x0047, nu)                  # MODULENAME (+ Unicode)
        d += _rec(0x001A, nb) + _rec(0x0032, nu)                  # MODULESTREAMNAME (+ Unicode)
        d += _rec(0x001C) + _rec(0x0048)                          # MODULEDOCSTRING
        d += _rec(0x0031, struct.pack('<I', 0))                   # MODULEOFFSET: исходный код с начала потока
        d += _rec(0x001E, struct.pack('<I', 0))                   # MODULEHELPCONTEXT
        d += _rec(0x002C, struct.pack('<H', 0xFFFF))              # MODULECOOKIE
        d += _rec(0x0021 if kind == 'std' else 0x0022)            # MODULETYPE: обычный / документ
        d += _rec(0x002B)                                         # конец модуля
        streams['VBA/' + name] = ovba_compress(module_source(name, kind, code).encode(enc))
    d += _rec(0x0010)                                             # конец каталога
    streams['VBA/dir'] = ovba_compress(bytes(d))
    streams['VBA/_VBA_PROJECT'] = bytes([0xCC, 0x61, 0xFF, 0xFF, 0x00, 0x00, 0x00])
    # PROJECT
    lines = ['ID="%s"' % pid]
    for name, kind, code in modules:
        lines.append(('Document=%s/&H00000000' % name) if kind != 'std' else ('Module=%s' % name))
    lines += ['Name="%s"' % project_name, 'HelpContextID="0"', 'VersionCompatible32="393222000"',
              'CMG="%s"' % ovba_encrypt(struct.pack('<I', 0), pid, seed),           # защиты нет
              'DPB="%s"' % ovba_encrypt(b'\x00', pid, (seed + 1) & 0xFF),             # пароля нет
              'GC="%s"' % ovba_encrypt(b'\xFF', pid, (seed + 2) & 0xFF),              # проект видим
              '', '[Host Extender Info]', '&H00000001={3832D640-CF90-11CF-8E43-00A0C911005A};VBE;&H00000000', '',
              '[Workspace]']
    lines += ['%s=0, 0, 0, 0, C' % name for name, _k, _c in modules]
    streams['PROJECT'] = ('\r\n'.join(lines) + '\r\n').encode(enc)
    wm = bytearray()
    for name, _k, _c in modules:
        wm += name.encode(enc) + b'\x00' + name.encode('utf-16-le') + b'\x00\x00'
    streams['PROJECTwm'] = bytes(wm) + b'\x00\x00'
    return cfb_write(streams)
