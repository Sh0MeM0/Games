"""Supported input formats shared by the service, CLI and file chooser.

Extensions are routing hints only. The image reader also checks the detected
Pillow format, so renaming an unrelated file cannot produce a false success.
JPEG 2000 additionally requires the OpenJPEG decoder in the Pillow build.
"""
from types import MappingProxyType


_FORMAT_EXTENSIONS = {
    "TIFF": (".tif", ".tiff"),
    "JPEG": (".jpg", ".jpeg", ".jpe", ".jfif"),
    "PNG": (".png", ".apng"),
    "BMP": (".bmp",),
    "DIB": (".dib",),
    "WEBP": (".webp",),
    "AVIF": (".avif", ".avifs"),
    "GIF": (".gif",),
    "JPEG2000": (".jp2", ".j2k", ".jpc", ".jpf", ".jpx", ".j2c"),
    "PCX": (".pcx",),
    "TGA": (".tga",),
    "PPM": (".ppm", ".pgm", ".pbm", ".pnm"),
}

EXTENSION_FORMATS = MappingProxyType({
    extension: format_name
    for format_name, extensions in _FORMAT_EXTENSIONS.items()
    for extension in extensions
})
IMAGE_EXTENSIONS = frozenset(EXTENSION_FORMATS)
INPUT_EXTENSIONS = IMAGE_EXTENSIONS | {".pdf"}
IMAGE_FORMATS = frozenset(_FORMAT_EXTENSIONS)
