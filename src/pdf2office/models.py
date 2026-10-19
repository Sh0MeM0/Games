"""Interchange objects: PDF points, top-left origin, displayed page rotation."""

from __future__ import annotations

from dataclasses import dataclass, field

Point = tuple[float, float]
Color = tuple[int, int, int]
BBox = tuple[float, float, float, float]


@dataclass
class TextSpan:
    text: str
    bbox: BBox
    font: str
    size: float
    color: Color
    bold: bool = False
    italic: bool = False
    origin: Point | None = None
    angle: float = 0.0
    from_ocr: bool = False
    # Baseline advance in PDF points, independent of the axis-aligned bounding
    # box, so rotated text keeps its original length.
    advance: float | None = None


@dataclass
class Segment:
    kind: str
    points: tuple[Point, ...]


@dataclass
class Drawing:
    segments: list[Segment]
    stroke: Color | None = None
    fill: Color | None = None
    width: float = 1.0
    closed: bool = False
    dashes: tuple[float, ...] = ()
    dash_offset: float = 0.0


@dataclass
class RasterImage:
    bbox: BBox
    data: bytes


@dataclass
class PdfPage:
    number: int
    width: float
    height: float
    texts: list[TextSpan]
    drawings: list[Drawing]
    images: list[RasterImage]
    ocr_used: bool = False
    warnings: list[str] = field(default_factory=list)
    # Full-page raster inputs have a reference image, unlike photos in a PDF.
    raster_source: bool = False
