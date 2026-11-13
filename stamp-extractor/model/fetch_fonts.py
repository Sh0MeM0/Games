#!/usr/bin/env python3
"""Downloads the handwriting fonts used by synth.py into model/fonts/hand.

All fonts are from github.com/google/fonts (SIL Open Font License 1.1). They are
only used to render training images and are not distributed with the program.
Each file is verified against the SHA-256 recorded when the model was trained.
"""
import hashlib, urllib.request
from pathlib import Path

BASE = 'https://raw.githubusercontent.com/google/fonts/main/ofl/'
FONTS = [
    ('AlexBrush-Regular.ttf', 'alexbrush/AlexBrush-Regular.ttf', 'df702038d8e27797230c77959c139eeea38cac0caf53e19ea5b513d3b0d3362d'),
    ('Allura-Regular.ttf', 'allura/Allura-Regular.ttf', '9c142b2e515832c0dfc4ff8b8ea18f40314943bf937b72e2b23c4661bac14cc6'),
    ('AmaticSC-Regular.ttf', 'amaticsc/AmaticSC-Regular.ttf', '513a76f96d39a5366bd27c258674aaea35ee51c42b8f3060387f74649ee8c8aa'),
    ('BadScript-Regular.ttf', 'badscript/BadScript-Regular.ttf', '40ec737eee65ec4efbc24ae636fa31d7d84d1ccca83ab54cd555edb72cb3eaf0'),
    ('Ballet.ttf', 'ballet/Ballet%5Bopsz%5D.ttf', '6500b6abb6dd31c3389e8f32a21ae3c66e3257d0afefecf7688aa23f742cb677'),
    ('Caveat.ttf', 'caveat/Caveat%5Bwght%5D.ttf', '0bdb6b660482d31531b3945849fba5916b3ef8695da7024a9e6b9ee3c4157988'),
    ('Comforter-Regular.ttf', 'comforter/Comforter-Regular.ttf', '2852da85b2fbfd37ccd87387f3578a251cb9a8814fbab22c4487f792936a2267'),
    ('ComforterBrush-Regular.ttf', 'comforterbrush/ComforterBrush-Regular.ttf', '7bd357dc8bf83d71f27fd5155ece5c8c100a2042d4530e4487635473c5836f95'),
    ('DancingScript.ttf', 'dancingscript/DancingScript%5Bwght%5D.ttf', '21808625578fe8d8cd10cb684be546dca077b27cd03a53a2f1ec11dc743c924c'),
    ('Gaegu-Regular.ttf', 'gaegu/Gaegu-Regular.ttf', 'aa52c98336f7c62e2896fc8b12b56a75d5b476d88a2f104b0980f4f7ce0adfc3'),
    ('GreatVibes-Regular.ttf', 'greatvibes/GreatVibes-Regular.ttf', '8d509802186f1b51572531ecf313e8098f9a5bfdfaca93f0c9b34467f9982d15'),
    ('KaushanScript-Regular.ttf', 'kaushanscript/KaushanScript-Regular.ttf', '6d8d379d9bba98178bee476d68114c8f83812c18005ecccf679e70f60e03d8f6'),
    ('Lobster-Regular.ttf', 'lobster/Lobster-Regular.ttf', 'd6568e697fd50cedc0be04d8aae4127fe95add607e7bff954ca88604be80c205'),
    ('MarckScript-Regular.ttf', 'marckscript/MarckScript-Regular.ttf', '504de8cc6f919163bc9afb67b9bc208d258230c5e8d00cb82ec52474f9fafc3b'),
    ('MrDafoe-Regular.ttf', 'mrdafoe/MrDafoe-Regular.ttf', 'a42a3c66a709927f358243fdfcbc437bd8eb188e2ee6a680c52f5be12c670fd2'),
    ('Neucha.ttf', 'neucha/Neucha.ttf', '7927bd6ce090fa032857dcbc3ad0e8b765c462d0a72a8779068132496b4e087d'),
    ('NothingYouCouldDo.ttf', 'nothingyoucoulddo/NothingYouCouldDo.ttf', '1daf8cf79076bf59c5a9117b5efd6ecea35e57a05ef127fe4f95b072b8a5245d'),
    ('Pacifico-Regular.ttf', 'pacifico/Pacifico-Regular.ttf', '5b6c0d5334a7bf77dea52b975c5a0c408878c0f7115ed5b6fb151f634b7bf701'),
    ('Pangolin-Regular.ttf', 'pangolin/Pangolin-Regular.ttf', 'cf4f0a46704c7859d4943320fce6c6d06cc7dc450ef971c3676ef36f9f482299'),
    ('Parisienne-Regular.ttf', 'parisienne/Parisienne-Regular.ttf', 'bc9ee17f022e20bc700797e5f557d14bfa43af0c98d9e6c9c5c1ca4ec7aacd57'),
    ('Sacramento-Regular.ttf', 'sacramento/Sacramento-Regular.ttf', '9341fda10adbfeb7efc94302b34507a3e227d7e7f5c432df3f5ac8753ff73d24'),
    ('ShantellSans.ttf', 'shantellsans/ShantellSans%5BBNCE,INFM,SPAC,wght%5D.ttf', 'd98a7913853f78d71aa0b86daf66b5eddb5a3266c07bfd51ef70632b88c41084')
]

def main():
    out = Path(__file__).resolve().parent / 'fonts' / 'hand'; out.mkdir(parents=True, exist_ok=True)
    for name, path, digest in FONTS:
        target = out / name
        if not target.exists():
            target.write_bytes(urllib.request.urlopen(BASE + path, timeout=60).read())
        actual = hashlib.sha256(target.read_bytes()).hexdigest()
        if actual != digest:
            raise SystemExit(f'SHA-256 mismatch for {name}: {actual} (upstream changed?)')
    print(f'{len(FONTS)} fonts verified in {out}')

if __name__ == '__main__':
    main()
