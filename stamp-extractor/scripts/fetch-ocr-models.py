#!/usr/bin/env python3
"""Re-fetch pinned, SHA256-verified offline models. Python 3, standard library only."""
from hashlib import sha256
from pathlib import Path
from urllib.request import urlopen

REVISION = '87416418657359cb625c412a48b6e1d6d41c29bd'
BASE = f'https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/{REVISION}/'
FILES = {
    'eng.traineddata': '7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2',
    'rus.traineddata': 'e16e5e036cce1d9ec2b00063cf8b54472625b9e14d893a169e2b0dedeb4df225',
    'LICENSE': 'cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30',
}
destination = Path(__file__).resolve().parent.parent / 'resources' / 'ocr'
destination.mkdir(parents=True, exist_ok=True)
for name, expected in FILES.items():
    with urlopen(BASE + name, timeout=60) as response:
        content = response.read()
    if sha256(content).hexdigest() != expected:
        raise SystemExit(f'SHA256 mismatch: {name}')
    output = destination / ('LICENSE-tessdata.txt' if name == 'LICENSE' else name)
    temporary = output.with_suffix(output.suffix + '.download')
    temporary.write_bytes(content)
    temporary.replace(output)
    print(f'{output.name}: verified, {len(content)} bytes')
