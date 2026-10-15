"""Exercise Qt's real event loop without a display or a GUI-testing plugin."""

from __future__ import annotations

import os
import threading
import time
from pathlib import Path

import pytest

# Set the platform before importing/constructing any Qt application.
os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtCore import QEventLoop, QSettings, QTimer  # noqa: E402
from PySide6.QtWidgets import QApplication, QMessageBox, QPushButton  # noqa: E402

from pdf2office import gui, runtime, service  # noqa: E402
from pdf2office.gui import MainWindow, STYLE  # noqa: E402
from pdf2office.service import ConversionResult  # noqa: E402


def pump_until(app: QApplication, condition, timeout: float = 5.0) -> None:
    """Process queued worker/UI signals, failing instead of hanging on a regression."""
    deadline = time.monotonic() + timeout
    while not condition():
        app.processEvents(QEventLoop.ProcessEventsFlag.AllEvents, 20)
        if time.monotonic() >= deadline:
            pytest.fail("Qt operation did not complete before the test timeout")
        time.sleep(0.002)
    app.processEvents()


@pytest.fixture(scope="session")
def qt_app(tmp_path_factory):
    root = tmp_path_factory.mktemp("qt-config")
    previous = {key: os.environ.get(key) for key in ("XDG_CONFIG_HOME", "XDG_CACHE_HOME")}
    os.environ["XDG_CONFIG_HOME"] = str(root / "config")
    os.environ["XDG_CACHE_HOME"] = str(root / "cache")
    Path(os.environ["XDG_CONFIG_HOME"]).mkdir()
    Path(os.environ["XDG_CACHE_HOME"]).mkdir()
    # Explicit INI paths also keep tests isolated on Windows, where the default
    # backend otherwise writes the registry instead of XDG_CONFIG_HOME.
    default_format = QSettings.defaultFormat()
    QSettings.setDefaultFormat(QSettings.Format.IniFormat)
    QSettings.setPath(QSettings.Format.IniFormat, QSettings.Scope.UserScope, str(root))
    app = QApplication.instance() or QApplication([])
    app.setQuitOnLastWindowClosed(False)
    yield app
    app.processEvents()
    QSettings.setDefaultFormat(default_format)
    for key, value in previous.items():
        if value is None:
            os.environ.pop(key, None)
        else:
            os.environ[key] = value


@pytest.fixture
def window(qt_app, tmp_path, monkeypatch):
    # Supply actual isolated INI-backed settings rather than mocking persistence.
    # Portable/installed selection is exercised separately below.
    settings_path = tmp_path / "settings" / "PDFMaster.ini"
    monkeypatch.setattr(
        gui, "_create_settings",
        lambda: QSettings(str(settings_path), QSettings.Format.IniFormat),
    )
    dialogs = []
    monkeypatch.setattr(QMessageBox, "critical", lambda *args: dialogs.append(args[1:]))
    widget = MainWindow()
    widget._test_dialogs = dialogs
    widget.output_dir.setText(str(tmp_path / "converted"))
    widget.dwg.setChecked(True)
    widget.docx.setChecked(True)
    widget.dxf.setChecked(False)
    widget.password.clear()
    widget.show()
    qt_app.processEvents()
    yield widget
    if widget._thread is not None:
        widget._stop.set()
        pump_until(qt_app, lambda: widget._thread is None)
    widget.close()
    widget.deleteLater()
    qt_app.processEvents()


def add_inputs(window: MainWindow, tmp_path: Path, count: int = 1) -> list[Path]:
    paths = [tmp_path / f"Чертёж {index + 1}.pdf" for index in range(count)]
    for path in paths:
        path.write_bytes(b"%PDF-1.7\n")
    window._add_files([str(path) for path in paths])
    return paths


def test_conversion_runs_in_worker_and_ui_remains_responsive(window, qt_app, tmp_path, monkeypatch):
    paths = add_inputs(window, tmp_path)
    entered = threading.Event()
    release = threading.Event()
    observations = {}
    ui_thread_id = threading.get_ident()

    def convert(request, progress, cancelled):
        observations["thread_id"] = threading.get_ident()
        observations["inputs"] = request.inputs
        entered.set()
        progress("Обработка контрольного PDF")
        while not release.wait(0.01):
            if cancelled():
                return []
        return [ConversionResult(paths[0], [tmp_path / "result.dwg", tmp_path / "result.docx"])]

    monkeypatch.setattr(service, "convert", convert)
    try:
        window._start()
        pump_until(qt_app, entered.is_set)
        assert observations["thread_id"] != ui_thread_id
        assert observations["inputs"] == paths
        assert not window.editor.isEnabled()
        assert not window.convert_button.isEnabled()

        # A main-thread timer must still fire while the converter is blocked.
        heartbeat = []
        QTimer.singleShot(0, lambda: heartbeat.append(threading.get_ident()))
        pump_until(qt_app, lambda: bool(heartbeat))
        assert heartbeat == [ui_thread_id]
        assert window._thread is not None and window._thread.isRunning()
        assert "Обработка контрольного PDF" in window.log.toPlainText()
    finally:
        release.set()

    pump_until(qt_app, lambda: window._thread is None)
    assert "Обработано PDF: 1" in window.status_label.text()
    assert "создано файлов: 2" in window.status_label.text()
    assert window.editor.isEnabled()
    assert window.convert_button.isEnabled()
    assert not window._test_dialogs


def test_cancel_is_cooperative_and_clears_password(window, qt_app, tmp_path, monkeypatch):
    paths = add_inputs(window, tmp_path, count=2)
    entered = threading.Event()
    cancellation_observed = threading.Event()

    def convert(request, progress, cancelled):
        assert request.password == "one-time-PDF-password"
        entered.set()
        while not cancelled():
            time.sleep(0.005)
        cancellation_observed.set()
        return [ConversionResult(paths[0], [tmp_path / "completed.docx"])]

    monkeypatch.setattr(service, "convert", convert)
    window.password.setText("one-time-PDF-password")
    window._start()
    pump_until(qt_app, entered.is_set)
    window._cancel()
    assert not window.cancel_button.isEnabled()
    pump_until(qt_app, lambda: window._thread is None)

    assert cancellation_observed.is_set()
    assert "Остановлено" in window.status_label.text()
    assert "PDF: 1 из 2" in window.status_label.text()
    assert "создано файлов: 1" in window.status_label.text()
    assert not window.password.text()
    assert window._worker is None
    assert window._active_request is None
    assert window.editor.isEnabled()
    assert window.convert_button.isEnabled()
    assert not window._test_dialogs


def test_password_and_destructive_overwrite_are_not_persisted(window, tmp_path):
    password = "private-never-store-this-credential"
    window.password.setText(password)
    window.overwrite.setChecked(True)
    window.scale.setValue(100.0)
    window._save_settings()

    assert window.settings.status() == QSettings.Status.NoError
    assert not window.settings.contains("password")
    assert not window.settings.contains("overwrite")
    settings_path = Path(window.settings.fileName())
    assert settings_path.is_relative_to(tmp_path)
    assert password not in settings_path.read_text(encoding="utf-8")

    reopened = MainWindow()
    try:
        assert reopened.output_dir.text() == window.output_dir.text()
        assert reopened.scale.value() == 100.0
        assert not reopened.password.text()
        assert not reopened.overwrite.isChecked()
    finally:
        reopened.close()
        reopened.deleteLater()


def test_portable_settings_travel_with_folder_without_storing_password(qt_app, tmp_path, monkeypatch):
    portable_root = tmp_path / "portable"
    portable_root.mkdir()
    (portable_root / "portable.flag").touch()
    monkeypatch.setattr(runtime, "resource_root", lambda: portable_root)
    password = "portable-password-must-not-be-written"

    original = MainWindow()
    try:
        original.output_dir.setText(str(tmp_path / "results"))
        original.scale.setValue(50.0)
        original.password.setText(password)
        original._save_settings()
        assert original.settings.format() == QSettings.Format.IniFormat
        assert Path(original.settings.fileName()) == portable_root / "PDFMaster.ini"
        assert original.settings.status() == QSettings.Status.NoError
        assert not original.settings.contains("password")
    finally:
        original.close()
        original.deleteLater()
        qt_app.processEvents()

    assert password not in (portable_root / "PDFMaster.ini").read_text(encoding="utf-8")
    moved_root = tmp_path / "moved-portable"
    portable_root.rename(moved_root)
    portable_root = moved_root
    moved = MainWindow()
    try:
        assert moved.scale.value() == 50.0
        assert moved.output_dir.text() == str(tmp_path / "results")
        assert not moved.password.text()
        assert Path(moved.settings.fileName()) == moved_root / "PDFMaster.ini"
    finally:
        moved.close()
        moved.deleteLater()
        qt_app.processEvents()

    # Removing the distribution marker restores installed-mode user settings.
    (moved_root / "portable.flag").unlink()
    installed_settings = gui._create_settings()
    assert installed_settings.format() == QSettings.Format.NativeFormat
    assert installed_settings.fileName() != str(moved_root / "PDFMaster.ini")


def test_partial_failures_are_counted_separately_and_logged(window, qt_app, tmp_path, monkeypatch):
    paths = add_inputs(window, tmp_path, count=3)

    def convert(request, progress, cancelled):
        return [
            ConversionResult(paths[0], [tmp_path / "one.dwg", tmp_path / "one.docx"]),
            ConversionResult(paths[1], [tmp_path / "two.docx"], ["Заменён шрифт"], "Ошибка DWG"),
            ConversionResult(paths[2], [], [], "PDF повреждён"),
        ]

    monkeypatch.setattr(service, "convert", convert)
    window._start()
    pump_until(qt_app, lambda: window._thread is None)

    summary = window.status_label.text()
    assert "успешно 1, частично 1, с ошибкой 1" in summary
    assert "Создано файлов: 3" in summary
    assert "Предупреждений: 1" in summary
    log = window.log.toPlainText()
    assert "Заменён шрифт" in log
    assert "Ошибка DWG" in log
    assert "PDF повреждён" in log
    assert "one.dwg" in log
    assert window.editor.isEnabled()


def test_worker_exception_restores_controls_and_reports_error(window, qt_app, tmp_path, monkeypatch):
    add_inputs(window, tmp_path)

    def convert(request, progress, cancelled):
        raise ValueError("Неверный диапазон страниц")

    monkeypatch.setattr(service, "convert", convert)
    window._start()
    pump_until(qt_app, lambda: window._thread is None)

    assert "Не удалось завершить обработку" in window.status_label.text()
    assert "Неверный диапазон страниц" in window.log.toPlainText()
    assert window._test_dialogs == [("Ошибка конвертации", "ValueError: Неверный диапазон страниц")]
    assert window.editor.isEnabled()
    assert window.convert_button.isEnabled()
    assert not window.cancel_button.isEnabled()


@pytest.mark.parametrize("width", [900, 1000])
def test_small_laptop_window_keeps_controls_reachable_without_overlap(window, qt_app, width):
    window.setStyleSheet(STYLE)
    window.resize(width, 700)
    qt_app.processEvents()
    add_button = next(button for button in window.findChildren(QPushButton) if button.text() == "+ Добавить PDF")
    list_bottom = window.file_list.mapToGlobal(window.file_list.rect().bottomLeft()).y()
    button_top = add_button.mapToGlobal(add_button.rect().topLeft()).y()
    assert list_bottom < button_top, "PDF list overlaps file-selection buttons"
    assert window.editor_scroll.horizontalScrollBar().maximum() == 0

    # Fixed actions stay visible; fields farther down are reachable by scrolling.
    assert window.rect().contains(window.convert_button.mapTo(window, window.convert_button.rect().center()))
    window.editor_scroll.ensureWidgetVisible(window.output_dir, 0, 0)
    qt_app.processEvents()
    viewport = window.editor_scroll.viewport()
    assert viewport.rect().contains(window.output_dir.mapTo(viewport, window.output_dir.rect().center()))

    conversion_scroll = window.tabs.widget(0)
    conversion_scroll.ensureWidgetVisible(window.docx_mode, 0, 0)
    qt_app.processEvents()
    viewport = conversion_scroll.viewport()
    assert viewport.rect().contains(window.docx_mode.mapTo(viewport, window.docx_mode.rect().center()))
    assert conversion_scroll.horizontalScrollBar().maximum() == 0

    window.tabs.setCurrentIndex(1)
    qt_app.processEvents()
    advanced_scroll = window.tabs.widget(1)
    # Scroll to the bottom as a user would. For QLineEdit, ensureWidgetVisible
    # uses the input-method cursor rectangle, not the full field rectangle.
    advanced_scroll.verticalScrollBar().setValue(advanced_scroll.verticalScrollBar().maximum())
    qt_app.processEvents()
    viewport = advanced_scroll.viewport()
    assert viewport.rect().contains(window.password.mapTo(viewport, window.password.rect().center()))
