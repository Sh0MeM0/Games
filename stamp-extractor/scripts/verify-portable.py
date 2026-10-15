#!/usr/bin/env python3
"""Verify a built portable's payload and source without claiming Windows runtime.

Run from any directory after building:
    python3 scripts/verify-portable.py
For an older build whose original win-unpacked is still present:
    python3 scripts/verify-portable.py --exe release/StampExtractor-Portable-2.0.0.exe --skip-source

7-Zip discovery: --seven-zip, SEVEN_ZIP, PATH, then Electron Builder's cache.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def sha256(filename: Path) -> str:
    digest = hashlib.sha256()
    with filename.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


def run(arguments: list[str | Path]) -> str:
    process = subprocess.run([str(value) for value in arguments], capture_output=True, text=True)
    require(process.returncode == 0, f'Command failed ({process.returncode}): {arguments[0]}\n{process.stdout}\n{process.stderr}')
    return process.stdout


def find_seven_zip(argument: str | None, project: Path) -> Path:
    requested = argument or os.environ.get('SEVEN_ZIP')
    if requested:
        found = shutil.which(requested) or requested
        require(Path(found).is_file(), f'7-Zip executable does not exist: {found}')
        return Path(found)
    for command in ['7zz', '7z', '7za']:
        if found := shutil.which(command):
            return Path(found)
    caches = []
    if configured := os.environ.get('ELECTRON_BUILDER_CACHE'):
        caches.append(Path(configured))
    caches.extend([Path.home() / '.cache/electron-builder', project.parent.parent / '.cache/electron-builder'])
    if local := os.environ.get('LOCALAPPDATA'):
        caches.append(Path(local) / 'electron-builder/Cache')
    patterns = ['7zip*/**/bin/7zz', '7zip*/**/7za.exe', '7zip*/**/7z.exe']
    for cache in caches:
        for pattern in patterns:
            matches = sorted(path for path in cache.glob(pattern) if path.is_file())
            if matches:
                return matches[-1]
    raise RuntimeError('7-Zip was not found. Install 7-Zip or set --seven-zip /path/to/7zz (or SEVEN_ZIP).')


def inspect_pe(filename: Path) -> dict:
    with filename.open('rb') as stream:
        dos = stream.read(64)
        require(len(dos) == 64 and dos[:2] == b'MZ', f'Not a Windows PE executable: {filename}')
        pe_offset = struct.unpack_from('<I', dos, 0x3c)[0]
        stream.seek(pe_offset)
        header = stream.read(24)
        require(len(header) == 24 and header[:4] == b'PE\0\0', f'Invalid PE header: {filename}')
        machine = struct.unpack_from('<H', header, 4)[0]
        optional_size = struct.unpack_from('<H', header, 20)[0]
        optional = stream.read(optional_size)
        require(len(optional) >= 2, f'Missing PE optional header: {filename}')
        magic = struct.unpack_from('<H', optional)[0]
        require(magic in (0x10b, 0x20b), f'Unknown PE type: {filename}')
        certificate_index = (96 if magic == 0x10b else 112) + 4 * 8
        require(len(optional) >= certificate_index + 8, f'Missing PE certificate directory: {filename}')
        offset, size = struct.unpack_from('<II', optional, certificate_index)
        require((offset == 0) == (size == 0), f'Inconsistent PE certificate directory: {filename}')
        require(offset + size <= filename.stat().st_size, f'PE certificate extends beyond executable: {filename}')
    return {'machine': {0x14c: 'x86', 0x8664: 'x64', 0xaa64: 'arm64'}.get(machine, hex(machine)),
            'certificate_present': bool(size), 'certificate_offset': offset, 'certificate_bytes': size,
            'authenticode': 'certificate present; trust/signature not validated' if size else 'unsigned'}


def verify(args: argparse.Namespace) -> dict:
    project = Path(__file__).resolve().parent.parent
    package = json.loads((project / 'package.json').read_text(encoding='utf-8'))
    version = package['version']
    exe = Path(args.exe).resolve() if args.exe else project / f'release/StampExtractor-Portable-{version}.exe'
    unpacked = Path(args.unpacked).resolve() if args.unpacked else project / 'release/win-unpacked'
    require(exe.is_file(), f'Portable executable does not exist: {exe}')
    require(unpacked.is_dir(), f'Unpacked build does not exist: {unpacked}')
    seven_zip = find_seven_zip(args.seven_zip, project)
    node = shutil.which(args.node)
    require(bool(node), f'Node.js executable was not found: {args.node}')
    outer_pe = inspect_pe(exe)
    with tempfile.TemporaryDirectory(prefix='stamp-portable-verification-') as temporary:
        workspace = Path(temporary)
        run([seven_zip, 'x', '-y', f'-o{workspace}', exe, '$PLUGINSDIR/app-64.7z'])
        inner = workspace / '$PLUGINSDIR/app-64.7z'
        require(inner.is_file(), 'NSIS portable does not contain $PLUGINSDIR/app-64.7z')
        run([seven_zip, 't', inner])
        payload = workspace / 'payload'
        run([seven_zip, 'x', '-y', f'-o{payload}', inner])
        actual = {path.relative_to(payload).as_posix(): path for path in payload.rglob('*') if path.is_file()}
        expected = {path.relative_to(unpacked).as_posix(): path for path in unpacked.rglob('*') if path.is_file()}
        require(actual.keys() == expected.keys(), f'Payload file tree differs from win-unpacked: missing={sorted(expected.keys() - actual.keys())}, extra={sorted(actual.keys() - expected.keys())}')
        for name in sorted(expected):
            require(actual[name].stat().st_size == expected[name].stat().st_size and sha256(actual[name]) == sha256(expected[name]), f'Payload file differs from win-unpacked: {name}')
        main_exe = f"{package['build']['productName']}.exe"
        require(main_exe in actual, f'Windows application is missing: {main_exe}')
        application_pe = inspect_pe(actual[main_exe])
        require(application_pe['machine'] == 'x64', f'Inner Windows application is not x64: {application_pe}')
        require('resources/app.asar' in actual, 'Application ASAR is missing')
        ocr = {name: {'bytes': file.stat().st_size, 'sha256': sha256(file)} for name, file in actual.items() if name.startswith('resources/ocr/')}
        for language in ('eng', 'rus'):
            require(f'resources/ocr/{language}.traineddata' in ocr, f'Missing offline OCR model: {language}')
        require(any(name.startswith('resources/app.asar.unpacked/node_modules/tesseract.js/') for name in actual), 'Tesseract.js runtime is missing')
        require(any(name.endswith('.wasm') and 'tesseract.js-core/' in name for name in actual), 'Offline Tesseract WASM is missing')
        asar_command = [node, project / 'scripts/verify-asar.cjs', actual['resources/app.asar'], project]
        if args.skip_source:
            asar_command.append('--skip-source')
        asar_report = json.loads(run(asar_command))
        if not args.skip_source:
            for path in (project / 'resources/ocr').rglob('*'):
                if path.is_file():
                    name = f'resources/ocr/{path.relative_to(project / "resources/ocr").as_posix()}'
                    require(name in actual and sha256(path) == sha256(actual[name]), f'OCR resource differs from current checkout: {name}')
        report = {'file': exe.name, 'packaged_version': asar_report['version'], 'bytes': exe.stat().st_size, 'sha256': sha256(exe),
                  'portable_pe': outer_pe, 'application_pe': application_pe, 'inner_archive_test': 'passed',
                  'matched_payload_files': len(actual), 'matched_payload_bytes': sum(path.stat().st_size for path in actual.values()),
                  'ocr_resources': ocr, 'asar': asar_report, 'windows_runtime_test': 'not performed'}
    destination = Path(args.report).resolve() if args.report else project / f'test-results/portable-{asar_report["version"]}-verification.json'
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    return {**report, 'report': str(destination)}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--exe', help='Portable EXE (default: release artifact for package.json version)')
    parser.add_argument('--unpacked', help='Matching win-unpacked directory from the same build')
    parser.add_argument('--seven-zip', help='Path/name of 7-Zip executable; also accepts SEVEN_ZIP')
    parser.add_argument('--node', default='node', help='Node.js executable (default: node)')
    parser.add_argument('--report', help='JSON output path (default: test-results/portable-VERSION-verification.json)')
    parser.add_argument('--skip-source', action='store_true', help='Verify an older payload without comparing against the current source/resources')
    args = parser.parse_args()
    try:
        report = verify(args)
    except (OSError, RuntimeError, ValueError, KeyError) as error:
        print(f'Portable verification failed: {error}', file=sys.stderr)
        return 1
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
