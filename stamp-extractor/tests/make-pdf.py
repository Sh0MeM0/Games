#!/usr/bin/env python3
"""Creates the PDF test files in tests/fixtures/pdf (authored, no external content).

scan-2pages.pdf — two raster pages (examples/document.png, examples/failures-rectangular.png).
vector.pdf      — Helvetica text (non-embedded standard font), a JPEG stamp image
                  XObject and a blue Bézier signature drawn as vector paths.
"""
import io, zlib
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'tests' / 'fixtures' / 'pdf'; OUT.mkdir(parents=True, exist_ok=True)

pages = [Image.open(ROOT / 'examples' / 'document.png').convert('RGB'), Image.open(ROOT / 'examples' / 'failures-rectangular.png').convert('RGB')]
pages[0].save(OUT / 'scan-2pages.pdf', save_all=True, append_images=pages[1:], resolution=150, quality=88)

stamp = pages[0].crop((240, 520, 600, 880)).resize((240, 240))
buf = io.BytesIO(); stamp.save(buf, 'JPEG', quality=90); jpeg = buf.getvalue()
lines = ['BT /F1 18 Tf 60 780 Td (CERTIFICATE OF COMPLETION) Tj ET']
for i, text in enumerate(['This document was generated for automated tests only.', 'The stamp is a raster image; the signature is a vector path.',
                          'Printed text must not be exported as a signature.', 'Contract No. 12345, Minsk, 13.11.2026']):
    lines.append(f'BT /F1 11 Tf 60 {740 - i * 18} Td ({text}) Tj ET')
lines.append('BT /F1 11 Tf 60 520 Td (Director: ____________________ / A. B. Testov /) Tj ET')
lines.append('q 240 0 0 240 330 470 cm /Im1 Do Q')
lines.append('q 0.1 0.2 0.65 RG 1.6 w 1 J 1 j 130 525 m 140 580 165 600 158 548 c 150 505 175 530 190 560 c 205 590 215 520 230 545 c '
             '245 570 255 515 270 548 c 285 580 300 520 330 540 c S 120 515 m 200 505 290 512 350 530 c S Q')
content = '\n'.join(lines).encode('latin-1')
objects = [b'<< /Type /Catalog /Pages 2 0 R >>', b'<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
           b'<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> /XObject << /Im1 5 0 R >> >> /Contents 6 0 R >>',
           b'<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>',
           b'<< /Type /XObject /Subtype /Image /Width 240 /Height 240 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length %d >>\nstream\n' % len(jpeg) + jpeg + b'\nendstream',
           b'<< /Length %d /Filter /FlateDecode >>\nstream\n' % len(zlib.compress(content)) + zlib.compress(content) + b'\nendstream']
pdf = bytearray(b'%PDF-1.7\n%\xe2\xe3\xcf\xd3\n'); offsets = []
for i, body in enumerate(objects, 1):
    offsets.append(len(pdf)); pdf += b'%d 0 obj\n' % i + body + b'\nendobj\n'
xref = len(pdf)
pdf += b'xref\n0 %d\n0000000000 65535 f \n' % (len(objects) + 1) + b''.join(b'%010d 00000 n \n' % o for o in offsets)
pdf += b'trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n' % (len(objects) + 1, xref)
(OUT / 'vector.pdf').write_bytes(pdf)
print({p.name: p.stat().st_size for p in OUT.iterdir()})
