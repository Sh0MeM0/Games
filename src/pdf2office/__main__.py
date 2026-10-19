"""Desktop entry point and scriptable offline conversion CLI."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys

from . import __version__
from .input_formats import IMAGE_EXTENSIONS
from .runtime import setup_runtime, tool_path


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="pdf-master", description="PDF Мастер: PDF и изображения → DWG / DXF / DOCX")
    parser.add_argument("--version", action="version", version=__version__)
    sub = parser.add_subparsers(dest="command")
    sub.add_parser("gui", help="Открыть графический интерфейс")
    sub.add_parser("doctor", help="Проверить наличие модулей и встроенных конвертеров")
    cli = sub.add_parser(
        "convert", help="Преобразовать PDF и изображения без графического интерфейса",
        epilog="Изображения: " + ", ".join(extension[1:].upper() for extension in sorted(IMAGE_EXTENSIONS))
               + ". Тонкие линии и штриховки восстанавливаются приближённо; проверяйте размеры и текст.",
    )
    cli.add_argument("inputs", nargs="+", type=Path, help="PDF или изображения чертежей")
    cli.add_argument("-o", "--output", type=Path, required=True)
    cli.add_argument("--formats", nargs="+", choices=["dwg", "dxf", "docx"], default=["dwg", "docx"])
    cli.add_argument("--pages", default="", help="Страницы PDF или кадры изображений; например: 1-3,5")
    cli.add_argument("--ocr", choices=["auto", "always", "off"], default="auto")
    cli.add_argument("--language", default="rus+eng")
    cli.add_argument("--vectorize-scans", action="store_true", help="Восстанавливать линии сканов внутри PDF")
    cli.add_argument("--no-scan-cleanup", action="store_true", help="Отключить очистку фона и шума изображений")
    cli.add_argument("--no-scan-deskew", action="store_true", help="Отключить выравнивание изображений")
    cli.add_argument("--scan-dpi", type=int, default=0,
                     help="DPI изображений: 0 — из файла (при отсутствии 300); вручную 72–2400. Влияет на физический размер")
    cli.add_argument("--no-vectorize-images", "--no-vectorize-tiff", dest="no_vectorize_images",
                     action="store_true", help="Отключить восстановление линий изображений (включая TIFF)")
    cli.add_argument("--docx-mode", choices=["layout", "flow"], default="layout")
    cli.add_argument("--scale", type=float, default=1.0, help="Масштаб CAD; 1 = размер страницы в мм")
    cli.add_argument("--font-dir", action="append", type=Path, default=[])
    cli.add_argument("--font-map", type=Path, help="JSON: исходное имя шрифта → установленное семейство")
    cli.add_argument("--ask-password", action="store_true", help="Безопасно запросить пароль PDF")
    cli.add_argument("--overwrite", action="store_true")
    args = parser.parse_args(argv)
    setup_runtime()
    if args.command in (None, "gui"):
        from .gui import main as gui_main
        return gui_main()
    if args.command == "doctor":
        from importlib.metadata import version
        info = {package: version(package) for package in
                ("PyMuPDF", "ezdxf", "python-docx", "PySide6-Essentials", "Pillow", "opencv-python-headless")}
        info.update({name: str(tool_path(name) or "MISSING") for name in ("dxf2dwg", "tesseract")})
        print(json.dumps(info, ensure_ascii=False, indent=2))
        return 0 if all(tool_path(name) for name in ("dxf2dwg", "tesseract")) else 1
    from .service import ConversionRequest, convert
    try:
        overrides = json.loads(args.font_map.read_text(encoding="utf-8")) if args.font_map else {}
        if not isinstance(overrides, dict) or not all(isinstance(k, str) and isinstance(v, str) for k, v in overrides.items()):
            raise ValueError("Карта шрифтов должна быть JSON-объектом из строк.")
        password = ""
        if args.ask_password:
            from getpass import getpass
            password = getpass("Пароль PDF: ")
        results = convert(ConversionRequest(
            inputs=args.inputs, output_dir=args.output, formats=tuple(args.formats), pages=args.pages,
            password=password, ocr_mode=args.ocr, ocr_language=args.language,
            vectorize_scans=args.vectorize_scans, docx_mode=args.docx_mode, scale=args.scale,
            font_overrides=overrides, font_dirs=args.font_dir, overwrite=args.overwrite,
            scan_cleanup=not args.no_scan_cleanup, scan_deskew=not args.no_scan_deskew,
            scan_dpi=args.scan_dpi, vectorize_images=not args.no_vectorize_images,
            vectorize_tiff=not args.no_vectorize_images,
        ), progress=lambda line: print(line, file=sys.stderr))
        print(json.dumps([{"input": str(r.input), "outputs": [str(p) for p in r.outputs],
                           "warnings": r.warnings, "error": r.error} for r in results],
                         ensure_ascii=False, indent=2))
        return 1 if any(r.error for r in results) else 0
    except (OSError, ValueError, RuntimeError) as exc:
        print(f"Ошибка: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
