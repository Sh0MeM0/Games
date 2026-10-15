#!/usr/bin/env python3
"""Build the patched Windows DXF importer from the bundled, signed GPL source.

Run on Linux/WSL with Python 3.12+, GnuPG, GNU make/patch and MinGW-w64. Nothing
is downloaded here. The exact source, signature, patch and flags are pinned in
packaging/libredwg-patch.lock.json and retained in both distribution archives.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tarfile

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "packaging/libredwg-patch.lock.json"


def digest(path: Path) -> str:
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def output(command: list[str], **kwargs) -> str:
    return subprocess.run(command, check=True, text=True, capture_output=True,
                          **kwargs).stdout.strip()


def build_backend(cache: Path, cc: str, jobs: int = 2) -> tuple[Path, Path]:
    if os.name == "nt":
        raise RuntimeError("Build the patched LibreDWG importer on Linux or WSL with MinGW-w64.")
    if jobs < 1:
        raise ValueError("Build jobs must be positive.")
    record = json.loads(MANIFEST.read_text(encoding="utf-8"))
    for name in ("source", "signature", "patch"):
        path = ROOT / record[name]["path"]
        if digest(path) != record[name]["sha256"]:
            raise ValueError(f"SHA-256 mismatch: {path}")
    compiler_name = shutil.which(cc)
    if not compiler_name:
        raise RuntimeError(f"MinGW compiler not found: {cc}")
    compiler = Path(compiler_name).resolve()
    target = output([str(compiler), "-dumpmachine"])
    if target != record["target"]:
        raise ValueError(f"Expected {record['target']} compiler; got {target}")
    tool_dir = compiler.parent
    native_tools = {}
    for name in ("ar", "ranlib", "objdump"):
        filename = f"{target}-{name}"
        found = tool_dir / filename
        if not found.is_file():
            located = shutil.which(filename)
            if not located:
                raise RuntimeError(f"MinGW tool not found: {filename}")
            found = Path(located)
        native_tools[name] = found.resolve()
    for name in ("bash", "make", "patch", "gpg"):
        if not shutil.which(name):
            raise RuntimeError(f"Build tool not found: {name}")

    base = Path(cache).resolve() / "libredwg-pdfmaster"
    base.mkdir(parents=True, exist_ok=True)
    keyring = base / "gnupg"
    keyring.mkdir(exist_ok=True, mode=0o700)
    gpg = ["gpg", "--batch", "--homedir", str(keyring)]
    output(gpg + ["--import", str(ROOT / record["signature"]["key"])])
    signature_status = output(gpg + ["--status-fd", "1", "--verify",
                                    str(ROOT / record["signature"]["path"]),
                                    str(ROOT / record["source"]["path"])])
    marker = "[GNUPG:] VALIDSIG " + record["signature"]["fingerprint"] + " "
    if marker not in signature_status:
        raise ValueError("The source signature does not match the pinned LibreDWG key.")

    inputs = {
        "manifest": record,
        "recipe_sha256": digest(Path(__file__)),
        "compiler": output([str(compiler), "--version"]).splitlines()[0],
        "compiler_sha256": digest(compiler),
        "binutils": output([str(native_tools["ar"]), "--version"]).splitlines()[0],
    }
    fingerprint = hashlib.sha256(json.dumps(inputs, sort_keys=True).encode()).hexdigest()
    work = base / fingerprint[:24]
    executable = work / "build/programs/dxf2dwg.exe"
    provenance = work / "PDFMASTER_BUILD.json"
    if executable.is_file() and provenance.is_file():
        previous = json.loads(provenance.read_text(encoding="utf-8"))
        if (previous.get("fingerprint") == fingerprint
                and previous.get("executable_sha256") == digest(executable)):
            print("Verified cached patched LibreDWG importer", flush=True)
            return executable, provenance
    # This path is always our dedicated, fingerprint-named build directory.
    if work.exists():
        shutil.rmtree(work)
    work.mkdir()
    with tarfile.open(ROOT / record["source"]["path"]) as archive:
        archive.extractall(work, filter="data")
    source = work / "libredwg-0.14"
    build = work / "build"
    build.mkdir()
    environment = os.environ.copy()
    environment.update(
        PATH=str(tool_dir) + os.pathsep + environment.get("PATH", ""),
        CC=str(compiler), AR=str(native_tools["ar"]), RANLIB=str(native_tools["ranlib"]),
        CFLAGS=record["cflags"], LDFLAGS=record["ldflags"],
        PKG_CONFIG_LIBDIR=str(work / "empty-pkgconfig"), PKG_CONFIG_PATH="",
        SOURCE_DATE_EPOCH="1782576000",
    )

    def run(command: list[str], cwd: Path, name: str) -> None:
        print(f"LibreDWG: {name}", flush=True)
        log = work / (name + ".log")
        with log.open("wb") as stream:
            result = subprocess.run(command, cwd=cwd, env=environment,
                                    stdout=stream, stderr=subprocess.STDOUT)
        if result.returncode:
            raise RuntimeError(f"LibreDWG {name} failed ({result.returncode}); see {log}")

    run(["patch", "--batch", "--forward", "--fuzz=0", "-p1", "--input",
         str(ROOT / record["patch"]["path"])], source, "apply-patch")
    run(["bash", "../libredwg-0.14/configure", *record["configure"]], build, "configure")
    run(["make", "-C", "src", f"-j{jobs}"], build, "compile-library")
    # Libtool needs -all-static here; -static alone still imports libssp DLL.
    run(["make", "-C", "programs", "dxf2dwg.exe", f"-j{jobs}",
         "LDFLAGS=" + record["program_ldflags"]], build, "link-importer")
    imports = re.findall(r"DLL Name:\s*(\S+)", output([str(native_tools["objdump"]), "-p", str(executable)]))
    if {name.lower() for name in imports} != {name.lower() for name in record["expected_imports"]}:
        raise ValueError(f"Unexpected native dependencies in patched importer: {imports}")
    provenance.write_text(json.dumps({"fingerprint": fingerprint, "inputs": inputs,
                                     "executable_sha256": digest(executable),
                                     "dll_imports": imports}, ensure_ascii=False, indent=2) + "\n",
                          encoding="utf-8")
    print("Built patched LibreDWG importer: " + digest(executable), flush=True)
    return executable, provenance


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cache", type=Path, default=ROOT / ".build-cache/windows")
    parser.add_argument("--cc", default=os.environ.get("MINGW_CC", "x86_64-w64-mingw32-gcc"))
    parser.add_argument("--jobs", type=int, default=2)
    args = parser.parse_args()
    executable, provenance = build_backend(args.cache, args.cc, args.jobs)
    print(executable)
    print(provenance)


if __name__ == "__main__":
    main()
