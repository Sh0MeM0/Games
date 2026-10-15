#!/usr/bin/env python3
"""Verify bundled additions or restore their exact files from pinned Debian packages.

Verification only needs Python. ``--restore`` additionally needs dpkg-deb and
network access; it never installs packages or changes system fonts.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
import tempfile
import urllib.request
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
FONT_DIR = ROOT / "assets" / "fonts"


def check_digest(path: Path, expected: str) -> None:
    actual = hashlib.sha256(path.read_bytes()).hexdigest()
    if actual != expected:
        raise ValueError(f"SHA-256 mismatch: {path.name}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--restore", action="store_true", help="Download pinned packages and restore added fonts/licenses")
    parser.add_argument("--cache", type=Path, default=ROOT / "build" / "fonts-cache")
    args = parser.parse_args()
    manifest = json.loads((FONT_DIR / "SOURCES.json").read_text(encoding="utf-8"))
    if args.restore:
        extractor = shutil.which("dpkg-deb")
        if not extractor:
            parser.error("Restoring fonts needs dpkg-deb (Linux/Debian); verification works on all platforms.")
        args.cache.mkdir(parents=True, exist_ok=True)
        for package in manifest["packages"]:
            archive = args.cache / package["url"].rsplit("/", 1)[1]
            if not archive.exists():
                with urllib.request.urlopen(package["url"], timeout=120) as response:
                    data = response.read()
                if hashlib.sha256(data).hexdigest() != package["sha256"]:
                    raise ValueError(f"Download SHA-256 mismatch: {archive.name}")
                archive.write_bytes(data)
            check_digest(archive, package["sha256"])
            with tempfile.TemporaryDirectory(prefix="pdfmaster-fonts-") as directory:
                unpacked = Path(directory)
                subprocess.run([extractor, "-x", str(archive.resolve()), directory], check=True)
                for font in manifest["fonts"]:
                    if font["package"] == package["package"]:
                        source = unpacked / font["member"]
                        check_digest(source, font["sha256"])
                        shutil.copyfile(source, FONT_DIR / font["file"])
                source = unpacked / "usr/share/doc" / package["package"] / "copyright"
                check_digest(source, package["license_sha256"])
                shutil.copyfile(source, FONT_DIR / package["license_file"])
    for font in manifest["fonts"]:
        check_digest(FONT_DIR / font["file"], font["sha256"])
    for package in manifest["packages"]:
        check_digest(FONT_DIR / package["license_file"], package["license_sha256"])
    print(f"Verified {len(manifest['fonts'])} additional font files and their licenses.")


if __name__ == "__main__":
    main()
