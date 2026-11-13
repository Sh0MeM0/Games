#!/usr/bin/env python3
"""Produce a new portable release from a verified previous portable ZIP.

The native parts of the portable (CPython runtime, LibreDWG importer built
from patched source, Tesseract, launchers) do not change between 0.3.0 and
0.4.0. Rebuilding them needs MinGW and several hosts; this script instead
takes the previous, checksum-verified ``PDFMaster-Portable-x64.zip`` and:

* replaces ``app/pdf2office``, documentation and examples with this checkout;
* adds the pinned Windows wheels for ONNX Runtime and SentencePiece
  (packaging/windows-assets.lock.json) to the runtime;
* adds a newer MSVC C++ runtime (msvcp140*.dll from the bundled conda
  vc14_runtime package) to ``runtime/``;
* adds the offline AI models (scripts/fetch_models.py, pinned by SHA-256);
* writes the ZIP, 45 MiB parts, manifest.json, SHA256SUMS.txt and the
  source archive.

Every downloaded file is verified against its pinned SHA-256.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))

from build_windows import download, make_source_archive  # noqa: E402

PREFIX = "PDFMaster-Portable-x64/"
PART_SIZE = 45 * 1024 * 1024
NEW_WHEELS = ("onnxruntime", "sentencepiece")
REPLACED = ("app/pdf2office/", "docs/", "examples/", "README.md", "THIRD_PARTY_NOTICES.md",
            "LICENSE", "DEPENDENCIES.json", "uninstall-files.nsh")


def digest(path: Path) -> str:
    hasher = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            hasher.update(block)
    return hasher.hexdigest()


def add_tree(archive: zipfile.ZipFile, source: Path, target: str, written: set[str]) -> None:
    for file in sorted(source.rglob("*")):
        if not file.is_file() or "__pycache__" in file.parts or file.suffix == ".pyc":
            continue
        name = PREFIX + target + file.relative_to(source).as_posix()
        archive.write(file, name)
        written.add(name)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", type=Path, required=True, help="Previous PDFMaster-Portable-x64.zip")
    parser.add_argument("--base-sha256", required=True, help="Expected SHA-256 of --base")
    parser.add_argument("--models", type=Path, default=ROOT / "models")
    parser.add_argument("--cache", type=Path, default=ROOT / ".build-cache" / "windows")
    parser.add_argument("--output", type=Path, default=ROOT / "dist")
    args = parser.parse_args()
    if digest(args.base) != args.base_sha256.lower():
        raise SystemExit("Base portable SHA-256 mismatch.")
    if not (args.models / "ocr").is_dir() or not (args.models / "translate").is_dir():
        subprocess.run([sys.executable, str(ROOT / "scripts/fetch_models.py"), "--target", str(args.models)], check=True)
    records = json.loads((ROOT / "packaging/windows-assets.lock.json").read_text(encoding="utf-8"))
    wheels = [record for record in records if record["kind"] == "wheel" and record["name"] in NEW_WHEELS]
    if len(wheels) != len(NEW_WHEELS):
        raise SystemExit("Missing new wheel records in windows-assets.lock.json.")
    args.output.mkdir(parents=True, exist_ok=True)
    portable = args.output / "PDFMaster-Portable-x64.zip"
    temporary = portable.with_suffix(".zip.part")
    written: set[str] = set()
    with zipfile.ZipFile(args.base) as base, zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as out:
        names = base.namelist()
        for info in base.infolist():
            relative = info.filename[len(PREFIX):] if info.filename.startswith(PREFIX) else None
            if relative is None:
                raise SystemExit(f"Unexpected member outside {PREFIX}: {info.filename}")
            if any(relative == item or relative.startswith(item) for item in REPLACED):
                continue
            if info.is_dir():
                continue
            # A fresh ZipInfo: writestr() rewrites offsets of the object it gets,
            # and the base archive's own entries must stay intact.
            copy = zipfile.ZipInfo(info.filename, date_time=info.date_time)
            copy.external_attr = info.external_attr
            copy.compress_type = zipfile.ZIP_DEFLATED
            with base.open(info) as source:
                out.writestr(copy, source.read(), compresslevel=6)
            written.add(info.filename)
        # Newer MSVC C++ runtime from the bundled conda vc14_runtime package.
        for name in ("msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll"):
            member = PREFIX + "vendor/tesseract/" + name
            if member not in names:
                raise SystemExit(f"Base portable lacks {member}")
            out.writestr(PREFIX + "runtime/" + name, base.read(member))
            written.add(PREFIX + "runtime/" + name)
        # New Python wheels.
        for record in wheels:
            wheel = download(record, args.cache)
            with zipfile.ZipFile(wheel) as package:
                for info in package.infolist():
                    relative = PurePosixPath(info.filename)
                    if relative.is_absolute() or ".." in relative.parts or info.is_dir():
                        continue
                    name = PREFIX + "runtime/Lib/site-packages/" + info.filename
                    out.writestr(name, package.read(info))
                    written.add(name)
                    lowered = relative.name.lower()
                    if relative.parts[0].endswith(".dist-info") and any(word in lowered for word in ("license", "copying", "notice")):
                        license_name = PREFIX + "licenses/python/" + info.filename
                        out.writestr(license_name, package.read(info))
                        written.add(license_name)
        # Application, documentation, examples and models.
        add_tree(out, ROOT / "src/pdf2office", "app/pdf2office/", written)
        add_tree(out, ROOT / "docs", "docs/", written)
        add_tree(out, ROOT / "examples", "examples/", written)
        add_tree(out, args.models / "ocr", "models/ocr/", written)
        add_tree(out, args.models / "translate", "models/translate/", written)
        add_tree(out, args.models / "licenses", "licenses/models/", written)
        for name in ("README.md", "THIRD_PARTY_NOTICES.md", "LICENSE"):
            out.write(ROOT / name, PREFIX + name)
            written.add(PREFIX + name)
        out.write(ROOT / "packaging/windows-assets.lock.json", PREFIX + "DEPENDENCIES.json")
        out.write(ROOT / "packaging/models.lock.json", PREFIX + "DEPENDENCIES-MODELS.json")
        written.update({PREFIX + "DEPENDENCIES.json", PREFIX + "DEPENDENCIES-MODELS.json"})
        # Installer uninstall list mirrors every shipped file.
        files = sorted(name[len(PREFIX):] for name in written)
        lines = [f'  Delete "$INSTDIR\\\\{name.replace("/", chr(92)).replace("$", "$$")}"' for name in files]
        directories = sorted({str(PurePosixPath(name).parent) for name in files if "/" in name},
                             key=lambda item: len(PurePosixPath(item).parts), reverse=True)
        lines += [f'  RMDir "$INSTDIR\\\\{name.replace("/", chr(92)).replace("$", "$$")}"' for name in directories]
        out.writestr(PREFIX + "uninstall-files.nsh", "\n".join(lines) + "\n")
    temporary.replace(portable)
    source_zip = args.output / "PDFMaster-Source.zip"
    make_source_archive(source_zip)
    # Parts for GitHub's 100 MB per-file limit, restored by RESTORE.cmd.
    release = args.output / "release"
    if release.exists():
        shutil.rmtree(release)
    release.mkdir()
    parts = []
    with portable.open("rb") as stream:
        number = 1
        while chunk := stream.read(PART_SIZE):
            part = release / f"{portable.name}.part{number:02d}"
            part.write_bytes(chunk)
            parts.append({"filename": part.name, "size": len(chunk), "sha256": hashlib.sha256(chunk).hexdigest()})
            number += 1
    manifest = {"filename": portable.name, "size": portable.stat().st_size, "sha256": digest(portable), "parts": parts}
    (release / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    shutil.copy2(source_zip, release / source_zip.name)
    shutil.copy2(ROOT / "packaging/RESTORE.cmd", release / "RESTORE.cmd")
    shutil.copy2(ROOT / "LICENSE", release / "LICENSE.txt")
    shutil.copy2(ROOT / "THIRD_PARTY_NOTICES.md", release / "THIRD_PARTY_NOTICES.md")
    (release / "SHA256SUMS.txt").write_text(
        f"{manifest['sha256']}  {portable.name}\n{digest(source_zip)}  {source_zip.name}\n", encoding="utf-8")
    print(json.dumps({"portable": str(portable), "size": manifest["size"], "sha256": manifest["sha256"],
                      "parts": len(parts), "source_sha256": digest(source_zip)}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
