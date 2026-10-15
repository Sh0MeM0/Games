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
