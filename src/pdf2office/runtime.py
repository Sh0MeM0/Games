"""Locate retained resources and native tools without depending on the cwd."""
from __future__ import annotations

import os
from pathlib import Path
import shutil
import sys


def resource_root() -> Path:
    if root := os.environ.get("PDF2OFFICE_HOME"):
        return Path(root).resolve()
    # Both source/src/pdf2office and portable/app/pdf2office have this depth.
    return Path(__file__).resolve().parents[2]


def tool_path(name: str) -> Path | None:
    if name not in {"tesseract", "dxf2dwg", "dwgread", "dwg2dxf"}:
        raise ValueError(f"Unknown bundled tool: {name}")
    suffix = ".exe" if sys.platform == "win32" else ""
    folder = "tesseract" if name == "tesseract" else "libredwg"
    executable = name + suffix
    for path in (
        resource_root() / "vendor" / folder / executable,
        resource_root() / "vendor" / folder / "bin" / executable,
    ):
        if path.is_file():
            return path
    found = shutil.which(executable)
    return Path(found) if found else None


def setup_runtime() -> None:
    """Use offline OCR data and DLLs from the distribution, if present."""
    root = resource_root()
    for tessdata in (
        root / "vendor" / "tesseract" / "tessdata",
        root / "vendor" / "tessdata",
    ):
        if tessdata.is_dir():
            os.environ["TESSDATA_PREFIX"] = str(tessdata)
            break
    if sys.platform == "win32":
        os.environ["PATH"] = os.pathsep.join(
            [str(root / "vendor" / "tesseract"),
             str(root / "vendor" / "libredwg"), os.environ.get("PATH", "")]
        )
        preload_msvc_runtime(root)


_PRELOADED: list = []


def preload_msvc_runtime(root: Path | None = None) -> list[str]:
    """Load the newest bundled MSVC C++ runtime before Qt or ONNX Runtime.

    PySide6 ships msvcp140.dll 14.39 next to Qt, while ONNX Runtime and
    SentencePiece are built with a newer toolset. Windows reuses an already
    loaded DLL with the same module name for every later import, so loading
    the portable's 14.5x runtime first makes all extensions share it.
    Returns the loaded paths; a missing file is simply skipped.
    """
    if sys.platform != "win32":
        return []
    import ctypes

    root = root or resource_root()
    loaded = []
    for name in ("msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll"):
        path = root / "runtime" / name
        if path.is_file():
            try:
                _PRELOADED.append(ctypes.WinDLL(str(path)))
                loaded.append(str(path))
            except OSError:
                pass
    return loaded
