from pdf2office.__main__ import main


def test_cli_tiff_options_reach_service(tmp_path, monkeypatch):
    received = []
    monkeypatch.setattr("pdf2office.service.convert", lambda request, **kwargs: received.append(request) or [])
    assert main(["convert", "scan.TIFF", "-o", str(tmp_path), "--formats", "dwg",
                 "--no-scan-cleanup", "--no-scan-deskew", "--scan-dpi", "600",
                 "--no-vectorize-tiff"]) == 0
    request, = received
    assert request.scan_dpi == 600
    assert not request.scan_cleanup and not request.scan_deskew and not request.vectorize_tiff
    assert not request.vectorize_scans


def test_cli_defaults_enable_tiff_processing_without_changing_pdf_vectorization(tmp_path, monkeypatch):
    received = []
    monkeypatch.setattr("pdf2office.service.convert", lambda request, **kwargs: received.append(request) or [])
    assert main(["convert", "scan.tif", "-o", str(tmp_path)]) == 0
    request, = received
    assert request.scan_cleanup and request.scan_deskew and request.vectorize_tiff
    assert request.scan_dpi == 0 and not request.vectorize_scans
