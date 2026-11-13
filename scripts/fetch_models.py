#!/usr/bin/env python3
"""Download and verify the offline AI models used by PDF Master.

Every file is pinned by SHA-256 in packaging/models.lock.json:

* neural OCR (ONNX): PP-OCRv6 text detector and 0/180 classifier from the
  RapidOCR 3.10.0 wheel on PyPI, and the PP-OCRv5 "eslav" recogniser
  (Russian, Belarusian, Ukrainian, English) from the RapidOCR model hub;
* offline translation: Argos Translate ru→en and en→ru packages.

Models are written to ``models/`` (or ``--target``). Network access is needed
only for this script; the program itself never downloads anything.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import shutil
import sys
import tempfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def digest_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def digest(path: Path) -> str:
    hasher = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            hasher.update(block)
    return hasher.hexdigest()


def download(url: str, sha256: str, cache: Path) -> Path:
    cache.mkdir(parents=True, exist_ok=True)
    path = cache / PurePosixPath(url.split("?")[0]).name
    if not path.is_file() or digest(path) != sha256:
        temporary = path.with_suffix(path.suffix + ".part")
        print("Downloading", url, flush=True)
        urllib.request.urlretrieve(url, temporary)
        if digest(temporary) != sha256:
            temporary.unlink()
            raise ValueError(f"SHA-256 mismatch: {url}")
        temporary.replace(path)
    return path


def safe_extract(archive: zipfile.ZipFile, target: Path) -> None:
    root = target.resolve()
    for info in archive.infolist():
        relative = PurePosixPath(info.filename)
        if relative.is_absolute() or ".." in relative.parts:
            raise ValueError(f"Unsafe archive member: {info.filename}")
        destination = (target / Path(*relative.parts)).resolve()
        if root not in destination.parents and destination != root:
            raise ValueError(f"Unsafe archive member: {info.filename}")
        if info.is_dir():
            destination.mkdir(parents=True, exist_ok=True)
            continue
        destination.parent.mkdir(parents=True, exist_ok=True)
        with archive.open(info) as source, destination.open("wb") as output:
            shutil.copyfileobj(source, output)


def install(record: dict, target: Path, cache: Path) -> None:
    archive = download(record["url"], record["sha256"], cache)
    if record["kind"] == "wheel_member":
        with zipfile.ZipFile(archive) as wheel:
            for member, spec in record["members"].items():
                data = wheel.read(member)
                if "sha256" in spec and digest_bytes(data) != spec["sha256"]:
                    raise ValueError(f"SHA-256 mismatch: {member}")
                destination = target / spec["target"]
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.write_bytes(data)
    elif record["kind"] == "file":
        destination = target / record["target"]
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(archive, destination)
    elif record["kind"] == "argos":
        destination = target / record["target"]
        with tempfile.TemporaryDirectory(dir=target) as staging:
            with zipfile.ZipFile(archive) as package:
                safe_extract(package, Path(staging))
            folders = [path for path in Path(staging).iterdir() if (path / "metadata.json").is_file()]
            if len(folders) != 1:
                raise ValueError(f"Unexpected Argos package layout: {record['url']}")
            if destination.exists():
                shutil.rmtree(destination)
            shutil.move(str(folders[0]), destination)
        # Stanza sentence models are only needed by the Argos library itself.
        shutil.rmtree(destination / "stanza", ignore_errors=True)
    else:
        raise ValueError(f"Unknown model kind: {record['kind']}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target", type=Path, default=ROOT / "models")
    parser.add_argument("--cache", type=Path, default=ROOT / ".build-cache" / "models")
    parser.add_argument("--ocr-only", action="store_true", help="Skip the ~350 MB translation models")
    args = parser.parse_args()
    records = json.loads((ROOT / "packaging/models.lock.json").read_text(encoding="utf-8"))
    args.target.mkdir(parents=True, exist_ok=True)
    for record in records:
        if args.ocr_only and record["kind"] == "argos":
            continue
        install(record, args.target, args.cache)
        print("Installed", record["name"], flush=True)
    notice = ROOT / "packaging" / "MODELS_NOTICE.txt"
    licenses = args.target / "licenses"
    licenses.mkdir(exist_ok=True)
    shutil.copy2(notice, licenses / "MODELS_NOTICE.txt")
    shutil.copy2(ROOT / "assets/licenses/Apache-2.0.txt", licenses / "Apache-2.0.txt")
    for folder in (args.target / "translate").glob("*"):
        readme = folder / "README.md"
        if readme.is_file():
            shutil.copy2(readme, licenses / f"Argos-{folder.name}-README.md")
    print("Models ready in", args.target)
    return 0


if __name__ == "__main__":
    sys.exit(main())
