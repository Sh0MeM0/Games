#!/usr/bin/env python3
"""Build an offline Windows x64 distribution on Linux or Windows.

Requires Python >=3.12, zstandard, GnuPG and MinGW-w64 GCC. NSIS is needed only
for installer builds. All downloaded runtime artifacts are pinned by SHA-256
in packaging/windows-assets.lock.json.
"""
from __future__ import annotations

import argparse
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import sys
import tarfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def digest(path: Path) -> str:
    hasher = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            hasher.update(block)
    return hasher.hexdigest()


def extract_zip(archive: Path, target: Path, prefix: str = "") -> None:
    with zipfile.ZipFile(archive) as zipped:
        for info in zipped.infolist():
            if not info.filename.startswith(prefix) or info.is_dir():
                continue
            relative = PurePosixPath(info.filename[len(prefix):])
            if relative.is_absolute() or ".." in relative.parts:
                raise ValueError(f"Unsafe archive member: {info.filename}")
            if not relative.parts:
                continue
            destination = target.joinpath(*relative.parts)
            destination.parent.mkdir(parents=True, exist_ok=True)
            with zipped.open(info) as source, destination.open("wb") as output:
                shutil.copyfileobj(source, output)


def extract_conda(archive: Path, target: Path) -> None:
    if archive.suffix == ".conda":
        import zstandard
        with zipfile.ZipFile(archive) as zipped:
            for name in zipped.namelist():
                if not name.endswith(".tar.zst"):
                    continue
                with zipped.open(name) as stream:
                    with zstandard.ZstdDecompressor().stream_reader(stream) as decoded:
                        with tarfile.open(fileobj=decoded, mode="r|") as tar:
                            tar.extractall(target, filter="data")
    else:
        with tarfile.open(archive) as tar:
            tar.extractall(target, filter="data")


def download(record: dict, cache: Path) -> Path:
    path = cache / record["filename"]
    if not path.exists():
        temporary = path.with_suffix(path.suffix + ".part")
        print("Downloading", record["filename"], flush=True)
        urllib.request.urlretrieve(record["url"], temporary)
        if digest(temporary) != record["sha256"]:
            temporary.unlink()
            raise ValueError(f"SHA-256 mismatch: {record['filename']}")
        temporary.replace(path)
    if digest(path) != record["sha256"]:
        raise ValueError(f"SHA-256 mismatch: {path}")
    return path


def copy_tree(source: Path, target: Path) -> None:
    shutil.copytree(source, target, dirs_exist_ok=True,
                    ignore=shutil.ignore_patterns("__pycache__", "*.pyc", ".pytest_cache"))


def make_source_archive(destination: Path) -> None:
    entries = ["src", "assets", "docs", "packaging", "scripts", "tests", "examples",
               "README.md", "LICENSE", "THIRD_PARTY_NOTICES.md", "pyproject.toml",
               "requirements.txt", "requirements-dev.txt", ".gitignore"]
    with zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for entry in entries:
            path = ROOT / entry
            if not path.exists():
                continue
            paths = path.rglob("*") if path.is_dir() else [path]
            for file in sorted(paths):
                if not file.is_file() or any(part in {"__pycache__", ".pytest_cache"} for part in file.parts):
                    continue
                if file.suffix != ".pyc":
                    archive.write(file, Path("PDFMaster-Source") / file.relative_to(ROOT))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cache", type=Path, default=ROOT / ".build-cache" / "windows")
    parser.add_argument("--cc", default=os.environ.get("MINGW_CC", "x86_64-w64-mingw32-gcc"))
    parser.add_argument("--windres", default=os.environ.get("MINGW_WINDRES"))
    parser.add_argument("--makensis", default=os.environ.get("MAKENSIS", "makensis"))
    parser.add_argument("--stage-only", action="store_true", help="Prepare files and launchers, without archives/installer")
    parser.add_argument("--portable-only", action="store_true", help="Build portable and source ZIPs without invoking NSIS or including an installer checksum")
    args = parser.parse_args()
    records = json.loads((ROOT / "packaging/windows-assets.lock.json").read_text())
    args.cache.mkdir(parents=True, exist_ok=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
        archives = list(pool.map(lambda record: download(record, args.cache), records))
    keyring = args.cache / "gnupg"
    keyring.mkdir(exist_ok=True, mode=0o700)
    subprocess.run(["gpg", "--batch", "--homedir", str(keyring), "--import",
                    str(ROOT / "packaging/keys/libredwg-maintainer.asc")], check=True)
    libredwg_archive = next(path for record, path in zip(records, archives) if record["kind"] == "libredwg")
    subprocess.run(["gpg", "--batch", "--homedir", str(keyring), "--verify",
                    str(ROOT / "packaging/keys/libredwg-0.14-win64.zip.sig"), str(libredwg_archive)], check=True)
    dist = ROOT / "dist"
    stage = dist / "PDFMaster-Portable-x64"
    # Only delete the dedicated build staging directory, never a user-selected folder.
    if stage.exists():
        shutil.rmtree(stage)
    stage.mkdir(parents=True)
    (stage / "portable.flag").write_text("Store PDFMaster.ini next to the portable application.\n", encoding="utf-8")
    runtime = stage / "runtime"
    site = runtime / "Lib/site-packages"
    licenses = stage / "licenses"
    licenses.mkdir()
    tess = stage / "vendor/tesseract"
    tess.mkdir(parents=True)
    for record, archive in zip(records, archives):
        kind = record["kind"]
        if kind == "python":
            extract_zip(archive, runtime, "tools/")
            # Runtime is offline and intentionally has no package installer.
            for unused in list(site.glob("pip*")) + list(site.glob("setuptools*")) + [runtime / "Scripts", runtime / "include", runtime / "libs"]:
                if unused.is_dir():
                    shutil.rmtree(unused)
                elif unused.exists():
                    unused.unlink()
            shutil.copy2(runtime / "LICENSE.txt", licenses / "CPython-LICENSE.txt")
        elif kind == "wheel":
            extract_zip(archive, site)
        elif kind == "libredwg":
            vendor = stage / "vendor/libredwg"
            extract_zip(archive, vendor)
            # Keep converter, read-back tools and libraries. Examples aren't runtime dependencies.
            for unused in [vendor / "examples", vendor / "include", vendor / "lib"]:
                if unused.exists():
                    shutil.rmtree(unused)
        elif kind == "conda":
            extracted = args.cache / "extracted" / record["name"]
            if not (extracted / ".extracted").exists():
                extracted.mkdir(parents=True, exist_ok=True)
                extract_conda(archive, extracted)
                (extracted / ".extracted").write_text(record["sha256"])
            elif (extracted / ".extracted").read_text() != record["sha256"]:
                shutil.rmtree(extracted)
                extracted.mkdir()
                extract_conda(archive, extracted)
                (extracted / ".extracted").write_text(record["sha256"])
            for dll in extracted.rglob("*.dll"):
                if "info" not in dll.relative_to(extracted).parts:
                    destination = tess / dll.name
                    if destination.exists() and digest(destination) != digest(dll):
                        raise ValueError(f"Native DLL collision: {dll.name}")
                    shutil.copy2(dll, destination)
            if record["name"] == "tesseract":
                shutil.copy2(next(extracted.rglob("tesseract.exe")), tess / "tesseract.exe")
                data = tess / "tessdata"
                data.mkdir(exist_ok=True)
                for language in ["eng", "rus", "osd"]:
                    shutil.copy2(next(extracted.rglob(language + ".traineddata")), data / (language + ".traineddata"))
                for folder in ["configs", "tessconfigs"]:
                    source = extracted / "share/tessdata" / folder
                    if source.exists():
                        copy_tree(source, data / folder)
            info = extracted / "info"
            for directory in ["licenses", "recipe"]:
                if (info / directory).exists():
                    copy_tree(info / directory, licenses / "conda" / record["name"] / directory)
            if (info / "index.json").exists():
                destination = licenses / "conda" / record["name"]
                destination.mkdir(parents=True, exist_ok=True)
                shutil.copy2(info / "index.json", destination / "index.json")

    (runtime / "python312._pth").write_text(".\nLib\nDLLs\nLib/site-packages\n../app\nimport site\n", encoding="utf-8")
    copy_tree(ROOT / "src/pdf2office", stage / "app/pdf2office")
    copy_tree(ROOT / "assets", stage / "assets")
    if (ROOT / "docs").exists():
        copy_tree(ROOT / "docs", stage / "docs")
    if (ROOT / "examples").exists():
        copy_tree(ROOT / "examples", stage / "examples")
    for name in ["LICENSE", "THIRD_PARTY_NOTICES.md", "README.md"]:
        shutil.copy2(ROOT / name, stage / name)
    shutil.copy2(ROOT / "packaging/windows-assets.lock.json", stage / "DEPENDENCIES.json")
    # Copy exact third-party license text next to application licenses as well as
    # retaining the original .dist-info notices in the installed Python packages.
    for info in site.glob("*.dist-info"):
        for file in info.rglob("*"):
            if file.is_file() and any(word in file.name.lower() for word in ["license", "copying", "notice"]):
                target = licenses / "python" / info.name / file.relative_to(info)
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(file, target)
    (licenses / "general").mkdir(parents=True, exist_ok=True)
    for license_file in (ROOT / "assets/licenses").glob("*.txt"):
        shutil.copy2(license_file, licenses / "general" / license_file.name)
    windres = args.windres or str(Path(args.cc).with_name("x86_64-w64-mingw32-windres"))
    resource = args.cache / "launcher-resource.o"
    subprocess.run([windres, "--preprocessor", args.cc, "--preprocessor-arg=-E",
                    "--preprocessor-arg=-xc", "--preprocessor-arg=-DRC_INVOKED",
                    "-i", "launcher.rc", "-o", str(resource)],
                   cwd=ROOT / "packaging", check=True)
    for cli in [False, True]:
        output = stage / ("PDFMaster-CLI.exe" if cli else "PDFMaster.exe")
        command = [args.cc, "-std=c11", "-Os", "-s", "-municode", "-static-libgcc", "-finput-charset=UTF-8"]
        if cli:
            command.append("-DCLI_LAUNCHER")
        else:
            command.append("-mwindows")
        command += [str(ROOT / "packaging/launcher.c"), str(resource), "-o", str(output), "-luser32"]
        subprocess.run(command, check=True)
    uninstall = []
    for file in sorted(stage.rglob("*")):
        if file.is_file():
            relative = str(file.relative_to(stage)).replace("/", "\\").replace("$", "$$")
            uninstall.append(f'  Delete "$INSTDIR\\{relative}"')
    for directory in sorted((p for p in stage.rglob("*") if p.is_dir()), key=lambda p:len(p.parts), reverse=True):
        relative = str(directory.relative_to(stage)).replace("/", "\\").replace("$", "$$")
        uninstall.append(f'  RMDir "$INSTDIR\\{relative}"')
    (stage / "uninstall-files.nsh").write_text("\n".join(uninstall) + "\n", encoding="utf-8")
    if args.stage_only:
        print(stage)
        return 0
    outputs = []
    if not args.portable_only:
        flag = "/" if os.name == "nt" else "-"
        installer = dist / "PDFMaster-Setup-x64.exe"
        subprocess.run([args.makensis, f"{flag}V2", f"{flag}DPAYLOAD={stage}",
                        f"{flag}DOUTPUT={installer}",
                        str(ROOT / "packaging/installer.nsi")], check=True)
        outputs.append(installer)
    shutil.make_archive(str(dist / "PDFMaster-Portable-x64"), "zip", dist, stage.name)
    make_source_archive(dist / "PDFMaster-Source.zip")
    outputs += [dist / "PDFMaster-Portable-x64.zip", dist / "PDFMaster-Source.zip"]
    (dist / "SHA256SUMS.txt").write_text("\n".join(f"{digest(path)}  {path.name}" for path in outputs) + "\n")
    for path in outputs:
        print(f"Built {path} ({path.stat().st_size / 1024 / 1024:.1f} MiB)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
