from pathlib import Path

import pytest

from pdf2office.__main__ import main
from pdf2office.input_formats import IMAGE_EXTENSIONS


@pytest.mark.parametrize("disable_flag", ["--no-vectorize-images", "--no-vectorize-tiff"])
def test_cli_image_options_and_legacy_alias_reach_service(tmp_path, monkeypatch, disable_flag):
    received = []
    monkeypatch.setattr("pdf2office.service.convert", lambda request, **kwargs: received.append(request) or [])
    assert main(["convert", "scan.TIFF", "facade.JPG", "plan.PNG", "drawing.WebP", "-o", str(tmp_path), "--formats", "dwg",
                 "--no-scan-cleanup", "--no-scan-deskew", "--scan-dpi", "600",
                 disable_flag]) == 0
    request, = received
    assert request.inputs == [Path(name) for name in ("scan.TIFF", "facade.JPG", "plan.PNG", "drawing.WebP")]
    assert request.scan_dpi == 600
    assert not request.scan_cleanup and not request.scan_deskew and not request.vectorize_tiff
    assert not request.vectorize_images
    assert not request.vectorize_scans


def test_cli_defaults_enable_image_processing_without_changing_pdf_vectorization(tmp_path, monkeypatch):
    received = []
    monkeypatch.setattr("pdf2office.service.convert", lambda request, **kwargs: received.append(request) or [])
    assert main(["convert", "scan.png", "-o", str(tmp_path)]) == 0
    request, = received
    assert request.scan_cleanup and request.scan_deskew and request.vectorize_tiff
    assert request.vectorize_images
    assert request.scan_dpi == 0 and not request.vectorize_scans


def test_cli_help_lists_supported_image_formats_and_both_vectorization_flags(capsys):
    with pytest.raises(SystemExit) as exit_info:
        main(["convert", "--help"])
    assert exit_info.value.code == 0
    help_text = capsys.readouterr().out
    for extension in IMAGE_EXTENSIONS:
        assert extension[1:].upper() in help_text
    assert "--no-vectorize-images" in help_text
    assert "--no-vectorize-tiff" in help_text
