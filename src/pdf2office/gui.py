"""Russian desktop interface. Conversion work never runs on the Qt UI thread."""

from __future__ import annotations

import json
import sys
import threading
from datetime import datetime
from pathlib import Path
from typing import Any

from PySide6.QtCore import QObject, QSettings, Qt, QThread, QUrl, Signal, Slot
from PySide6.QtGui import QCloseEvent, QDesktopServices, QDragEnterEvent, QDropEvent, QFontDatabase, QIcon
from PySide6.QtWidgets import (
    QAbstractItemView, QApplication, QCheckBox, QComboBox, QDialog,
    QDialogButtonBox, QDoubleSpinBox, QFileDialog, QFormLayout, QFrame,
    QHBoxLayout, QHeaderView, QLabel, QLineEdit, QListWidget, QListWidgetItem,
    QMainWindow, QMessageBox, QPlainTextEdit, QProgressBar, QPushButton,
    QScrollArea, QSizePolicy, QSpinBox, QStyle, QTableWidget, QTableWidgetItem,
    QTabWidget, QVBoxLayout, QWidget,
)


APP_NAME = "PDF Мастер"
INPUT_EXTENSIONS = {".pdf", ".tif", ".tiff"}

STYLE = """
QMainWindow, QDialog { background: #f3f5f9; color: #192841; }
QWidget#mainEditor { background: #f3f5f9; }
QWidget { font-family: 'Segoe UI', 'DejaVu Sans', sans-serif; font-size: 13px; }
QLabel { background: transparent; }
QLabel#title { font-size: 28px; font-weight: 700; color: #13243e; }
QLabel#subtitle { font-size: 13px; color: #60718b; }
QLabel#sectionTitle { font-size: 17px; font-weight: 650; color: #192841; }
QLabel#hint { color: #60718b; font-size: 12px; }
QLabel#badge { color: #2563eb; background: #e6edff; border-radius: 12px;
              padding: 7px 12px; font-weight: 600; }
QFrame#card { background: #ffffff; border: 1px solid #e1e7f0; border-radius: 12px; }
QPushButton { background: #ffffff; border: 1px solid #ccd5e3; border-radius: 7px;
              color: #263b58; padding: 8px 14px; min-height: 18px; }
QPushButton:hover { background: #edf3ff; border-color: #a1b8e0; }
QPushButton:pressed { background: #e1eaff; }
QPushButton:disabled { background: #f5f6f8; color: #9ba7b9; border-color: #e5e9ef; }
QPushButton#primary { background: #2563eb; color: white; border: 1px solid #2563eb;
                       font-weight: 650; padding: 11px 23px; }
QPushButton#primary:hover { background: #1d4ed8; }
QPushButton#primary:disabled { background: #9eb7ea; border-color: #9eb7ea; }
QPushButton#quiet { border: none; color: #526785; padding: 6px 9px; }
QPushButton#danger { color: #b44444; }
QPushButton#danger:disabled { color: #9ba7b9; }
QLineEdit, QComboBox, QDoubleSpinBox, QSpinBox { color: #1c304c; background: #ffffff;
    border: 1px solid #ccd5e3; border-radius: 6px; padding: 7px; min-height: 19px; }
QLineEdit:focus, QComboBox:focus, QDoubleSpinBox:focus, QSpinBox:focus { border: 1px solid #2563eb; }
QLineEdit:disabled, QComboBox:disabled, QDoubleSpinBox:disabled, QSpinBox:disabled { background: #f1f3f6; color: #97a1af; }
QComboBox QAbstractItemView { background: white; color: #192841; selection-background-color: #e5edff; }
QCheckBox { spacing: 8px; padding: 4px 0; color: #263b58; }
QCheckBox::indicator { width: 16px; height: 16px; }
QListWidget { background: #f9fbfe; color: #263b58; border: 1px dashed #b7c6dd;
              border-radius: 8px; padding: 8px; outline: none; }
QListWidget::item { padding: 9px 7px; border-radius: 5px; }
QListWidget::item:selected { background: #e5edff; color: #1d4ed8; }
QTableWidget { background: #ffffff; color: #263b58; border: 1px solid #dce3ee;
               border-radius: 6px; gridline-color: #e8edf5; selection-background-color: #e5edff; }
QHeaderView::section { background: #f5f7fb; color: #526785; padding: 8px; border: none;
                       border-bottom: 1px solid #dce3ee; font-weight: 600; }
QTableWidget QLineEdit, QTableWidget QComboBox { border-radius: 0; border: none; }
QTabWidget::pane { background: #ffffff; border: 1px solid #e1e7f0; border-radius: 10px; }
QTabBar::tab { background: #e9edf5; color: #60718b; padding: 11px 18px; margin-right: 4px;
              border-top-left-radius: 8px; border-top-right-radius: 8px; }
QTabBar::tab:selected { background: #ffffff; color: #1d4ed8; font-weight: 650; }
QScrollArea { border: none; background: transparent; }
QScrollArea > QWidget > QWidget { background: #ffffff; }
QProgressBar { border: none; background: #e7edf6; border-radius: 4px; min-height: 7px;
               max-height: 7px; color: transparent; }
QProgressBar::chunk { background: #2563eb; border-radius: 4px; }
QPlainTextEdit { color: #405571; background: #f8faff; border: 1px solid #e0e6f0;
                 border-radius: 6px; padding: 6px; font-family: 'Consolas', monospace;
                 font-size: 11px; }
QToolTip { background: #192841; color: white; padding: 6px; border: none; }
QSplitter::handle { background: transparent; }
"""


def _hint(text: str) -> QLabel:
    label = QLabel(text)
    label.setObjectName("hint")
    label.setWordWrap(True)
    return label


def _button(text: str, handler: Any, name: str = "") -> QPushButton:
    button = QPushButton(text)
    if name:
        button.setObjectName(name)
    button.clicked.connect(handler)
    return button


def _row(*widgets: QWidget) -> QWidget:
    row = QWidget()
    layout = QHBoxLayout(row)
    layout.setContentsMargins(0, 0, 0, 0)
    layout.setSpacing(8)
    for widget in widgets:
        layout.addWidget(widget)
    return row


def _create_settings() -> QSettings:
    """Keep portable preferences beside the program; installed apps use user settings."""
    from .runtime import resource_root

    root = resource_root()
    if (root / "portable.flag").is_file():
        return QSettings(str(root / "PDFMaster.ini"), QSettings.Format.IniFormat)
    return QSettings("PDFMaster", "PDFMaster")


class PdfList(QListWidget):
    """Accept PDF and TIFF files dropped from Explorer, without moving files."""

    files_dropped = Signal(list)

    def __init__(self) -> None:
        super().__init__()
        self.setAcceptDrops(True)
        self.setSelectionMode(QAbstractItemView.SelectionMode.ExtendedSelection)
        self.setMinimumHeight(48)
        self.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Ignored)
        self.setAccessibleName("Список PDF и TIFF для конвертации")

    def dragEnterEvent(self, event: QDragEnterEvent) -> None:
        if event.mimeData().hasUrls() and any(
            url.isLocalFile() and Path(url.toLocalFile()).suffix.lower() in INPUT_EXTENSIONS
            for url in event.mimeData().urls()
        ):
            event.acceptProposedAction()
        else:
            event.ignore()

    def dragMoveEvent(self, event: Any) -> None:
        self.dragEnterEvent(event)

    def dropEvent(self, event: QDropEvent) -> None:
        self.files_dropped.emit([
            url.toLocalFile() for url in event.mimeData().urls() if url.isLocalFile()
        ])
        event.acceptProposedAction()


class ConversionWorker(QObject):
    progress = Signal(str)
    completed = Signal(object)
    failed = Signal(str)
    finished = Signal()

    def __init__(self, request: Any, stop: threading.Event) -> None:
        super().__init__()
        self.request = request
        self.stop = stop

    @Slot()
    def run(self) -> None:
        try:
            from .service import convert

            results = convert(
                self.request, progress=self.progress.emit, cancelled=self.stop.is_set,
            )
            self.completed.emit(results)
        except Exception as exc:
            self.failed.emit(f"{type(exc).__name__}: {exc}")
        finally:
            self.finished.emit()


class FontDialog(QDialog):
    """Show editable substitution rules and user-owned font directories."""

    def __init__(self, directories: list[Path], overrides: dict[str, str], parent: QWidget) -> None:
        super().__init__(parent)
        self.setWindowTitle("Шрифты и подстановки")
        self.resize(700, 610)
        layout = QVBoxLayout(self)
        layout.setSpacing(12)
        layout.addWidget(_hint(
            "Программа использует доступные системные и дополнительные шрифты. "
            "Для точного оформления укажите свои лицензированные шрифты, включая ГОСТ. "
            "Подстановка применяется к имени исходного шрифта. "
            "Для всех текстов, включая распознанные, укажите * в исходном имени."
        ))
        layout.addWidget(QLabel("Дополнительные папки со шрифтами TTF / OTF / TTC"))
        self.directory_list = QListWidget()
        self.directory_list.setMaximumHeight(110)
        for directory in directories:
            self.directory_list.addItem(str(directory))
        layout.addWidget(self.directory_list)
        buttons = QHBoxLayout()
        buttons.addWidget(_button("Добавить папку…", self._add_directory))
        buttons.addWidget(_button("Убрать", self._remove_directory, "quiet"))
        buttons.addStretch()
        layout.addLayout(buttons)
        layout.addWidget(QLabel("Подстановка шрифтов"))
        self.table = QTableWidget(0, 2)
        self.table.setHorizontalHeaderLabels(["Исходное имя шрифта", "Использовать шрифт"])
        self.table.horizontalHeader().setSectionResizeMode(QHeaderView.ResizeMode.Stretch)
        self.table.verticalHeader().setVisible(False)
        self.table.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        self.table.setMinimumHeight(170)
        self._font_families = self._families()
        for source, target in overrides.items():
            self._add_rule(source, target)
        layout.addWidget(self.table)
        rule_buttons = QHBoxLayout()
        rule_buttons.addWidget(_button("Добавить правило", lambda: self._add_rule()))
        rule_buttons.addWidget(_button("Удалить правило", self._remove_rule, "quiet"))
        rule_buttons.addStretch()
        layout.addLayout(rule_buttons)
        layout.addWidget(_hint(
            "Недоступные шрифты заменяются ближайшим доступным; замены перечисляются в журнале. "
            "Включить в поставку все существующие и коммерческие шрифты невозможно. "
            "Текст, уже превращённый в контуры внутри PDF, требует распознавания."
        ))
        dialog_buttons = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel,
        )
        dialog_buttons.button(QDialogButtonBox.StandardButton.Ok).setText("Применить")
        dialog_buttons.button(QDialogButtonBox.StandardButton.Cancel).setText("Отмена")
        dialog_buttons.accepted.connect(self._accept_rules)
        dialog_buttons.rejected.connect(self.reject)
        layout.addWidget(dialog_buttons)

    def directories(self) -> list[Path]:
        return [Path(self.directory_list.item(i).text()) for i in range(self.directory_list.count())]

    def overrides(self) -> dict[str, str]:
        rules = {}
        for row in range(self.table.rowCount()):
            source_item = self.table.item(row, 0)
            target_box = self.table.cellWidget(row, 1)
            source = source_item.text().strip() if source_item else ""
            target = target_box.currentText().strip() if isinstance(target_box, QComboBox) else ""
            if source and target:
                rules[source] = target
        return rules

    def _families(self) -> list[str]:
        families = set(QFontDatabase.families())
        try:
            from .fonts import FontCatalog

            catalog_families = FontCatalog(extra_dirs=self.directories()).families
            families.update(catalog_families() if callable(catalog_families) else catalog_families)
        except (ImportError, OSError, TypeError):
            # Qt families remain usable even if optional font metadata cannot be read.
            pass
        return sorted(families, key=str.casefold)

    def _add_directory(self) -> None:
        selected = QFileDialog.getExistingDirectory(self, "Папка с дополнительными шрифтами")
        if not selected:
            return
        if selected not in {str(path) for path in self.directories()}:
            self.directory_list.addItem(selected)
        self._font_families = self._families()
        for row in range(self.table.rowCount()):
            box = self.table.cellWidget(row, 1)
            if isinstance(box, QComboBox):
                previous = box.currentText()
                box.clear()
                box.addItems(self._font_families)
                box.setCurrentText(previous)

    def _remove_directory(self) -> None:
        for item in self.directory_list.selectedItems():
            self.directory_list.takeItem(self.directory_list.row(item))

    def _add_rule(self, source: str = "", target: str = "") -> None:
        row = self.table.rowCount()
        self.table.insertRow(row)
        self.table.setItem(row, 0, QTableWidgetItem(source))
        box = QComboBox()
        box.setEditable(True)
        box.addItems(self._font_families)
        box.setCurrentText(target)
        box.setToolTip("Семейство TTF / OTF, например osifont; можно ввести имя вручную")
        self.table.setCellWidget(row, 1, box)
        self.table.setRowHeight(row, 38)

    def _remove_rule(self) -> None:
        rows = sorted({index.row() for index in self.table.selectedIndexes()}, reverse=True)
        for row in rows:
            self.table.removeRow(row)

    def _accept_rules(self) -> None:
        sources = set()
        for row in range(self.table.rowCount()):
            item = self.table.item(row, 0)
            source = item.text().strip() if item else ""
            box = self.table.cellWidget(row, 1)
            target = box.currentText().strip() if isinstance(box, QComboBox) else ""
            if not source and not target:
                continue
            if not source or not target:
                QMessageBox.warning(self, "Правило не заполнено", "Укажите оба имени шрифтов или удалите пустое правило.")
                return
            if source.casefold() in sources:
                QMessageBox.warning(self, "Повторяющееся правило", f"Шрифт «{source}» указан более одного раза.")
                return
            sources.add(source.casefold())
        self.accept()


class MainWindow(QMainWindow):
    def __init__(self) -> None:
        super().__init__()
        self.setWindowTitle(f"{APP_NAME} — PDF / TIFF → DWG / DOCX")
        self.resize(1120, 850)
        self.setMinimumSize(900, 700)
        self.settings = _create_settings()
        self._thread: QThread | None = None
        self._worker: ConversionWorker | None = None
        self._stop = threading.Event()
        self._close_when_finished = False
        self._font_dirs: list[Path] = []
        self._font_overrides: dict[str, str] = {}
        self._active_request: Any = None
        self._build_ui()
        self._load_settings()

    def _build_ui(self) -> None:
        central = QWidget()
        outer = QVBoxLayout(central)
        outer.setContentsMargins(24, 20, 24, 20)
        outer.setSpacing(17)
        header = QHBoxLayout()
        headings = QVBoxLayout()
        title = QLabel(APP_NAME)
        title.setObjectName("title")
        headings.addWidget(title)
        subtitle = QLabel("Чертежи и документы из PDF и TIFF — для редактирования")
        subtitle.setObjectName("subtitle")
        headings.addWidget(subtitle)
        header.addLayout(headings)
        header.addStretch()
        badge = QLabel("DWG  /  DXF  /  DOCX")
        badge.setObjectName("badge")
        header.addWidget(badge, 0, Qt.AlignmentFlag.AlignTop)
        outer.addLayout(header)

        self.editor = QWidget()
        self.editor.setObjectName("mainEditor")
        columns = QHBoxLayout(self.editor)
        columns.setContentsMargins(0, 0, 0, 0)
        columns.setSpacing(16)
        left = QFrame()
        left.setObjectName("card")
        left.setMinimumWidth(320)
        # Wrapped hints need more height than Qt's unwrapped minimum-size hint.
        # The outer scroll area keeps this card reachable on small screens.
        left.setMinimumHeight(380)
        files = QVBoxLayout(left)
        files.setContentsMargins(17, 17, 17, 17)
        files.setSpacing(11)
        title_row = QHBoxLayout()
        file_title = QLabel("1. Исходные файлы")
        file_title.setObjectName("sectionTitle")
        title_row.addWidget(file_title)
        title_row.addStretch()
        self.file_count = _hint("0 файлов")
        title_row.addWidget(self.file_count)
        files.addLayout(title_row)
        files.addWidget(_hint("Добавьте PDF или TIFF либо перетащите их в список ниже."))
        self.file_list = PdfList()
        self.file_list.files_dropped.connect(self._add_files)
        files.addWidget(self.file_list, 1)
        file_buttons = QHBoxLayout()
        file_buttons.addWidget(_button("+ Добавить файлы", self._choose_files))
        file_buttons.addStretch()
        file_buttons.addWidget(_button("Убрать", self._remove_files, "quiet"))
        file_buttons.addWidget(_button("Очистить", self._clear_files, "quiet"))
        files.addLayout(file_buttons)
        files.addSpacing(5)
        files.addWidget(QLabel("Папка результата"))
        self.output_dir = QLineEdit()
        self.output_dir.setPlaceholderText("Выберите папку для новых файлов")
        output_button = _button("…", self._choose_output)
        output_button.setMaximumWidth(42)
        output_button.setToolTip("Выбрать папку результата")
        output_button.setAccessibleName("Выбрать папку результата")
        files.addWidget(_row(self.output_dir, output_button))
        self.overwrite = QCheckBox("Заменять существующие результаты")
        self.overwrite.setToolTip("По умолчанию совпадающее имя получает числовой суффикс.")
        files.addWidget(self.overwrite)
        files.addWidget(_hint("Исходные файлы сохраняются. Обработка выполняется локально."))
        columns.addWidget(left, 5)

        self.tabs = QTabWidget()
        self.tabs.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Ignored)
        self.tabs.addTab(self._conversion_tab(), "2. Конвертация")
        self.tabs.addTab(self._advanced_tab(), "Точность и оформление")
        columns.addWidget(self.tabs, 6)
        # The main form must remain reachable on 1366×768 laptop screens and
        # when long result summaries wrap. A scroll area respects the cards'
        # minimum size instead of compressing their controls into each other.
        self.editor_scroll = QScrollArea()
        self.editor_scroll.setWidgetResizable(True)
        self.editor_scroll.setFrameShape(QFrame.Shape.NoFrame)
        self.editor_scroll.setWidget(self.editor)
        outer.addWidget(self.editor_scroll, 1)

        bottom = QFrame()
        bottom.setObjectName("card")
        footer = QVBoxLayout(bottom)
        footer.setContentsMargins(17, 13, 17, 13)
        footer.setSpacing(9)
        action_row = QHBoxLayout()
        status_column = QVBoxLayout()
        self.status_label = QLabel("Готово к работе")
        self.status_label.setWordWrap(True)
        self.status_label.setStyleSheet("font-weight: 600; color: #263b58;")
        status_column.addWidget(self.status_label)
        self.progress = QProgressBar()
        self.progress.setRange(0, 100)
        self.progress.setValue(0)
        self.progress.setTextVisible(False)
        status_column.addWidget(self.progress)
        action_row.addLayout(status_column, 1)
        action_row.addSpacing(15)
        self.open_button = _button("Открыть папку", self._open_output)
        action_row.addWidget(self.open_button)
        self.cancel_button = _button("Остановить", self._cancel, "danger")
        self.cancel_button.setEnabled(False)
        action_row.addWidget(self.cancel_button)
        self.convert_button = _button("Конвертировать", self._start, "primary")
        action_row.addWidget(self.convert_button)
        footer.addLayout(action_row)
        log_header = QHBoxLayout()
        log_title = _hint("Журнал обработки")
        log_title.setWordWrap(False)
        log_header.addWidget(log_title)
        log_header.addStretch()
        log_header.addWidget(_button("Сохранить журнал…", self._save_log, "quiet"))
        footer.addLayout(log_header)
        self.log = QPlainTextEdit()
        self.log.setReadOnly(True)
        self.log.setMaximumBlockCount(10000)
        self.log.setFixedHeight(80)
        self.log.setPlaceholderText("Здесь появятся этапы обработки, предупреждения о шрифтах и результаты.")
        footer.addWidget(self.log)
        outer.addWidget(bottom)
        self.setCentralWidget(central)
        self.menuBar().addMenu("Справка").addAction("О программе", self._about)

    def _conversion_tab(self) -> QWidget:
        content = QWidget()
        layout = QVBoxLayout(content)
        layout.setContentsMargins(19, 18, 19, 18)
        layout.setSpacing(12)
        title = QLabel("Форматы результата")
        title.setObjectName("sectionTitle")
        layout.addWidget(title)
        self.dwg = QCheckBox("DWG — чертёж AutoCAD и совместимых САПР")
        self.dwg.setChecked(True)
        self.docx = QCheckBox("DOCX — редактируемый документ Word")
        self.docx.setChecked(True)
        self.dxf = QCheckBox("DXF — открытый обменный формат чертежей")
        layout.addWidget(self.dwg)
        layout.addWidget(self.docx)
        layout.addWidget(self.dxf)
        layout.addWidget(_hint(
            "Линии и текст из векторных PDF переносятся как объекты. "
            "Размеры, связи и исходные слои САПР восстанавливаются не всегда."
        ))
        layout.addSpacing(6)
        form = QFormLayout()
        form.setSpacing(12)
        form.setLabelAlignment(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter)
        self.pages = QLineEdit()
        self.pages.setPlaceholderText("Все страницы; например: 1-3, 5, 8")
        self.pages.setToolTip("Нумерация с 1. Поддерживаются многостраничные PDF и TIFF. Укажите страницы и диапазоны через запятую.")
        form.addRow("Страницы", self.pages)
        self.ocr_mode = QComboBox()
        self.ocr_mode.addItem("Автоматически для сканов", "auto")
        self.ocr_mode.addItem("Распознавать все страницы", "always")
        self.ocr_mode.addItem("Не распознавать", "off")
        form.addRow("Распознавание", self.ocr_mode)
        self.ocr_language = QComboBox()
        self.ocr_language.addItem("Русский + английский", "rus+eng")
        self.ocr_language.addItem("Русский", "rus")
        self.ocr_language.addItem("Английский", "eng")
        form.addRow("Язык текста", self.ocr_language)
        self.docx_mode = QComboBox()
        self.docx_mode.addItem("Сохранить расположение на странице", "layout")
        self.docx_mode.addItem("Потоковый текст для редактирования", "flow")
        form.addRow("Оформление DOCX", self.docx_mode)
        for combo in (self.ocr_mode, self.ocr_language, self.docx_mode):
            combo.setSizeAdjustPolicy(QComboBox.SizeAdjustPolicy.AdjustToMinimumContentsLengthWithIcon)
            combo.setMinimumContentsLength(12)
        layout.addLayout(form)
        layout.addWidget(_hint(
            "Для сканов доступно OCR. Качество распознавания зависит от исходного изображения; "
            "проверяйте числа, обозначения и таблицы после конвертации."
        ))
        layout.addStretch()
        self.ocr_mode.currentIndexChanged.connect(
            lambda: self.ocr_language.setEnabled(self.ocr_mode.currentData() != "off")
        )
        self.docx.toggled.connect(self.docx_mode.setEnabled)
        return self._scroll(content)

    def _advanced_tab(self) -> QWidget:
        content = QWidget()
        layout = QVBoxLayout(content)
        layout.setContentsMargins(19, 18, 19, 18)
        layout.setSpacing(13)
        title = QLabel("Чертежи и масштаб")
        title.setObjectName("sectionTitle")
        layout.addWidget(title)
        form = QFormLayout()
        self.scale = QDoubleSpinBox()
        self.scale.setDecimals(4)
        self.scale.setRange(0.0001, 1000000.0)
        self.scale.setValue(1.0)
        self.scale.setSingleStep(0.5)
        self.scale.setSuffix(" ×")
        self.scale.setToolTip("1×: размеры листа в миллиметрах. Для TIFF размер определяется по DPI. Для чертежа 1:100 задайте 100×.")
        form.addRow("Масштаб CAD", self.scale)
        layout.addLayout(form)
        layout.addWidget(_hint(
            "При 1× один миллиметр листа становится одним миллиметром чертежа. "
            "Для масштаба исходного чертежа 1:100 укажите 100×."
        ))
        self.vectorize = QCheckBox("Векторизация сканов PDF (экспериментально)")
        layout.addWidget(self.vectorize)
        layout.addWidget(_hint(
            "Создаёт редактируемые контуры из растрового изображения. "
            "Линии могут требовать очистки; размеры и геометрическую точность нужно проверить."
        ))
        layout.addSpacing(5)
        scan_title = QLabel("Подготовка TIFF")
        scan_title.setObjectName("sectionTitle")
        layout.addWidget(scan_title)
        self.scan_deskew = QCheckBox("Выравнивать наклон скана")
        self.scan_deskew.setChecked(True)
        layout.addWidget(self.scan_deskew)
        self.scan_cleanup = QCheckBox("Очищать фон и мелкий шум")
        self.scan_cleanup.setChecked(True)
        layout.addWidget(self.scan_cleanup)
        self.vectorize_tiff = QCheckBox("Восстанавливать линии TIFF")
        self.vectorize_tiff.setChecked(True)
        layout.addWidget(self.vectorize_tiff)
        scan_form = QFormLayout()
        self.scan_dpi = QSpinBox()
        self.scan_dpi.setRange(0, 2400)
        self.scan_dpi.setSingleStep(100)
        self.scan_dpi.setSpecialValueText("Авто")
        self.scan_dpi.setSuffix(" DPI")
        self.scan_dpi.setToolTip(
            "Авто (0): разрешение из TIFF; если оно отсутствует — 300 DPI. Вручную: 72–2400 DPI. "
            "Задайте разрешение сканера вручную, чтобы сохранить физические размеры листа."
        )
        scan_form.addRow("Разрешение TIFF", self.scan_dpi)
        layout.addLayout(scan_form)
        layout.addWidget(_hint(
            "Выравнивание и очистка применяются к копии изображения. "
            "Текст распознаётся по настройкам на вкладке «Конвертация». "
            "Проверяйте мелкие обозначения и геометрию после обработки."
        ))
        layout.addSpacing(5)
        font_title = QLabel("Шрифты и ГОСТ")
        font_title.setObjectName("sectionTitle")
        layout.addWidget(font_title)
        self.font_summary = _hint("Системные шрифты и шрифты из комплекта программы")
        layout.addWidget(self.font_summary)
        layout.addWidget(_button("Настроить шрифты и подстановки…", self._configure_fonts))
        layout.addWidget(_hint(
            "Доступны шрифты из комплекта и установленные в системе. "
            "Дополнительные шрифты ГОСТ подключаются из вашей папки. "
            "Соответствие стандарту зависит от выбранного шрифта и оформления."
        ))
        self.password = QLineEdit()
        self.password.setEchoMode(QLineEdit.EchoMode.Password)
        self.password.setPlaceholderText("Только для защищённых PDF")
        self.password.setToolTip("Пароль применяется к выбранным файлам и не сохраняется в настройках.")
        layout.addSpacing(5)
        layout.addWidget(QLabel("Пароль PDF"))
        layout.addWidget(self.password)
        layout.addStretch()
        return self._scroll(content)

    @staticmethod
    def _scroll(content: QWidget) -> QScrollArea:
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setWidget(content)
        scroll.setFrameShape(QFrame.Shape.NoFrame)
        return scroll

    def _load_settings(self) -> None:
        settings = self.settings
        self.output_dir.setText(str(settings.value("output_dir", "")))
        for key, box, default in (("dwg", self.dwg, True), ("docx", self.docx, True), ("dxf", self.dxf, False)):
            box.setChecked(settings.value(key, default, type=bool))
        for key, box, default in (
            ("ocr_mode", self.ocr_mode, "auto"),
            ("ocr_language", self.ocr_language, "rus+eng"),
            ("docx_mode", self.docx_mode, "layout"),
        ):
            index = box.findData(settings.value(key, default))
            box.setCurrentIndex(max(0, index))
        self.scale.setValue(settings.value("scale", 1.0, type=float))
        self.vectorize.setChecked(settings.value("vectorize", False, type=bool))
        self.scan_cleanup.setChecked(settings.value("scan_cleanup", True, type=bool))
        self.scan_deskew.setChecked(settings.value("scan_deskew", True, type=bool))
        self.vectorize_tiff.setChecked(settings.value("vectorize_tiff", True, type=bool))
        self.scan_dpi.setValue(settings.value("scan_dpi", 0, type=int))
        # Destructive replacement must be deliberately enabled for each session.
        self.overwrite.setChecked(False)
        try:
            dirs = json.loads(str(settings.value("font_dirs", "[]")))
            overrides = json.loads(str(settings.value("font_overrides", "{}")))
            if isinstance(dirs, list):
                self._font_dirs = [Path(path) for path in dirs if isinstance(path, str)]
            if isinstance(overrides, dict):
                self._font_overrides = {k: v for k, v in overrides.items() if isinstance(k, str) and isinstance(v, str)}
        except (ValueError, TypeError):
            pass
        self._update_font_summary()
        geometry = settings.value("geometry")
        if geometry is not None:
            self.restoreGeometry(geometry)

    def _save_settings(self) -> None:
        values = {
            "output_dir": self.output_dir.text().strip(),
            "dwg": self.dwg.isChecked(), "docx": self.docx.isChecked(), "dxf": self.dxf.isChecked(),
            "ocr_mode": self.ocr_mode.currentData(), "ocr_language": self.ocr_language.currentData(),
            "docx_mode": self.docx_mode.currentData(), "scale": self.scale.value(),
            "vectorize": self.vectorize.isChecked(),
            "scan_cleanup": self.scan_cleanup.isChecked(),
            "scan_deskew": self.scan_deskew.isChecked(),
            "vectorize_tiff": self.vectorize_tiff.isChecked(),
            "scan_dpi": self.scan_dpi.value(),
            "font_dirs": json.dumps([str(path) for path in self._font_dirs]),
            "font_overrides": json.dumps(self._font_overrides, ensure_ascii=False),
            "geometry": self.saveGeometry(),
        }
        for key, value in values.items():
            self.settings.setValue(key, value)
        self.settings.sync()

    def _choose_files(self) -> None:
        paths, _ = QFileDialog.getOpenFileNames(
            self, "Добавить PDF или TIFF", "",
            "PDF и TIFF (*.pdf *.PDF *.tif *.TIF *.tiff *.TIFF);;PDF (*.pdf *.PDF);;TIFF (*.tif *.TIF *.tiff *.TIFF)",
        )
        self._add_files(paths)

    @Slot(list)
    def _add_files(self, paths: list[str]) -> None:
        if self._thread is not None:
            return
        known = {self.file_list.item(i).data(Qt.ItemDataRole.UserRole) for i in range(self.file_list.count())}
        skipped = []
        for path_string in paths:
            path = Path(path_string).expanduser().resolve()
            if not path.is_file() or path.suffix.lower() not in INPUT_EXTENSIONS:
                skipped.append(path.name)
                continue
            if str(path) in known:
                continue
            item = QListWidgetItem(path.name)
            item.setData(Qt.ItemDataRole.UserRole, str(path))
            item.setToolTip(str(path))
            item.setIcon(self.style().standardIcon(QStyle.StandardPixmap.SP_FileIcon))
            self.file_list.addItem(item)
            known.add(str(path))
        self._update_file_count()
        if not self.output_dir.text().strip() and self.file_list.count():
            first = Path(self.file_list.item(0).data(Qt.ItemDataRole.UserRole))
            self.output_dir.setText(str(first.parent / "PDF Master"))
        if skipped:
            self._append_log("Пропущены неподдерживаемые или недоступные файлы: " + ", ".join(skipped))

    def _remove_files(self) -> None:
        for item in self.file_list.selectedItems():
            self.file_list.takeItem(self.file_list.row(item))
        self._update_file_count()

    def _clear_files(self) -> None:
        self.file_list.clear()
        self._update_file_count()

    def _update_file_count(self) -> None:
        self.file_count.setText(f"Файлов: {self.file_list.count()}")

    def _choose_output(self) -> None:
        directory = QFileDialog.getExistingDirectory(self, "Папка для результатов", self.output_dir.text())
        if directory:
            self.output_dir.setText(directory)

    def _configure_fonts(self) -> None:
        dialog = FontDialog(self._font_dirs, self._font_overrides, self)
        if dialog.exec() == QDialog.DialogCode.Accepted:
            self._font_dirs = dialog.directories()
            self._font_overrides = dialog.overrides()
            self._update_font_summary()
            self._save_settings()

    def _update_font_summary(self) -> None:
        self.font_summary.setText(
            "Системные шрифты и комплект программы. "
            f"Дополнительных папок: {len(self._font_dirs)}; правил подстановки: {len(self._font_overrides)}."
        )

    def _start(self) -> None:
        if self._thread is not None:
            return
        inputs = [Path(self.file_list.item(i).data(Qt.ItemDataRole.UserRole)) for i in range(self.file_list.count())]
        formats = tuple(name for name, widget in (("dwg", self.dwg), ("docx", self.docx), ("dxf", self.dxf)) if widget.isChecked())
        if not inputs:
            QMessageBox.information(self, "Добавьте файлы", "Добавьте хотя бы один PDF или TIFF в список.")
            return
        if not formats:
            QMessageBox.information(self, "Выберите формат", "Выберите хотя бы один формат результата: DWG, DXF или DOCX.")
            return
        directory = self.output_dir.text().strip()
        if not directory:
            QMessageBox.information(self, "Выберите папку", "Укажите папку для сохранения результатов.")
            return
        try:
            from .service import ConversionRequest

            request = ConversionRequest(
                inputs=inputs, output_dir=Path(directory).expanduser(), formats=formats,
                pages=self.pages.text().strip(), password=self.password.text(),
                ocr_mode=self.ocr_mode.currentData(), ocr_language=self.ocr_language.currentData(),
                vectorize_scans=self.vectorize.isChecked(), docx_mode=self.docx_mode.currentData(),
                scan_cleanup=self.scan_cleanup.isChecked(), scan_deskew=self.scan_deskew.isChecked(),
                vectorize_tiff=self.vectorize_tiff.isChecked(), scan_dpi=self.scan_dpi.value(),
                scale=self.scale.value(), font_overrides=dict(self._font_overrides),
                font_dirs=list(self._font_dirs), overwrite=self.overwrite.isChecked(),
            )
        except Exception as exc:
            QMessageBox.critical(self, "Не удалось начать обработку", str(exc))
            return
        self._save_settings()
        self._active_request = request
        self._stop = threading.Event()
        self._thread = QThread(self)
        self._worker = ConversionWorker(request, self._stop)
        self._worker.moveToThread(self._thread)
        self._thread.started.connect(self._worker.run)
        self._worker.progress.connect(self._on_progress)
        self._worker.completed.connect(self._completed)
        self._worker.failed.connect(self._failed)
        self._worker.finished.connect(self._thread.quit)
        self._worker.finished.connect(self._worker.deleteLater)
        self._thread.finished.connect(self._thread_finished)
        self._thread.finished.connect(self._thread.deleteLater)
        self.editor.setEnabled(False)
        self.convert_button.setEnabled(False)
        self.cancel_button.setEnabled(True)
        self.progress.setRange(0, 0)
        self.status_label.setText("Подготовка конвертации…")
        self._append_log(f"Начало обработки: файлов {len(inputs)}; форматы: {', '.join(formats).upper()}.")
        self._thread.start()

    @Slot(str)
    def _append_log(self, message: str) -> None:
        self.log.appendPlainText(f"{datetime.now():%H:%M:%S}  {message}")
        bar = self.log.verticalScrollBar()
        bar.setValue(bar.maximum())

    @Slot(str)
    def _on_progress(self, message: str) -> None:
        self._append_log(message)
        if not self._stop.is_set():
            self.status_label.setText(message[:160])

    @Slot(object)
    def _completed(self, results: list[Any]) -> None:
        full_success = 0
        partial = 0
        failed = 0
        output_count = 0
        warning_count = 0
        requested = set(self._active_request.formats)
        for result in results:
            produced = {Path(path).suffix.lstrip(".").lower() for path in result.outputs}
            output_count += len(result.outputs)
            warning_count += len(result.warnings)
            if not result.error and requested.issubset(produced):
                full_success += 1
            elif result.outputs:
                partial += 1
            else:
                failed += 1
            for output in result.outputs:
                self._append_log(f"Сохранено: {output}")
            for warning in result.warnings:
                self._append_log(f"{result.input.name}: предупреждение — {warning}")
            if result.error:
                self._append_log(f"{result.input.name}: ошибка — {result.error}")
        if self._stop.is_set():
            summary = f"Остановлено. Обработано файлов: {len(results)} из {len(self._active_request.inputs)}; создано файлов: {output_count}."
        elif partial or failed:
            summary = f"Завершено: успешно {full_success}, частично {partial}, с ошибкой {failed}. Создано файлов: {output_count}."
        else:
            summary = f"Готово. Обработано файлов: {full_success}; создано файлов: {output_count}."
        if warning_count:
            summary += f" Предупреждений: {warning_count} — см. журнал."
        self.status_label.setText(summary)
        self._append_log(summary)
        self.progress.setRange(0, 100)
        self.progress.setValue(100 if not self._stop.is_set() else 0)

    @Slot(str)
    def _failed(self, error: str) -> None:
        self._append_log("Обработка прервана: " + error)
        self.status_label.setText("Не удалось завершить обработку. Подробности в журнале.")
        self.progress.setRange(0, 100)
        self.progress.setValue(0)
        if not self._close_when_finished:
            QMessageBox.critical(self, "Ошибка конвертации", error)

    @Slot()
    def _thread_finished(self) -> None:
        self._thread = None
        self._worker = None
        self._active_request = None
        self.password.clear()
        self.editor.setEnabled(True)
        self.convert_button.setEnabled(True)
        self.cancel_button.setEnabled(False)
        if self._close_when_finished:
            self.close()

    def _cancel(self) -> None:
        self._stop.set()
        self.cancel_button.setEnabled(False)
        self.status_label.setText("Остановка…")
        self._append_log("Запрошена остановка. Уже созданные файлы сохраняются.")

    def _open_output(self) -> None:
        directory = Path(self.output_dir.text().strip()).expanduser()
        if not self.output_dir.text().strip() or not directory.is_dir():
            QMessageBox.information(self, "Папка результата", "Папка появится после начала конвертации. Можно выбрать существующую папку.")
            return
        if not QDesktopServices.openUrl(QUrl.fromLocalFile(str(directory.resolve()))):
            QMessageBox.warning(self, "Не удалось открыть папку", str(directory.resolve()))

    def _save_log(self) -> None:
        path, _ = QFileDialog.getSaveFileName(self, "Сохранить журнал", "PDF-Master-log.txt", "Текстовый файл (*.txt)")
        if path:
            try:
                Path(path).write_text(self.log.toPlainText(), encoding="utf-8")
            except OSError as exc:
                QMessageBox.warning(self, "Не удалось сохранить журнал", str(exc))

    def _about(self) -> None:
        from . import __version__

        QMessageBox.about(
            self, f"О программе {APP_NAME}",
            f"<h3>{APP_NAME} {__version__}</h3>"
            "<p>Локальная конвертация PDF и TIFF в DWG, DXF и DOCX. "
            "Векторная геометрия, извлечение текста и OCR на русском и английском.</p>"
            "<p>PDF хранит вид страницы. Исходные зависимости чертежа, параметры объектов "
            "и точное оформление Word восстановить автоматически удаётся не всегда. "
            "Результаты распознавания и векторизации требуют проверки.</p>"
            "<p>Используйте дополнительные шрифты в соответствии с их лицензиями. "
            "Сведения о сторонних компонентах находятся в документации поставки.</p>",
        )

    def closeEvent(self, event: QCloseEvent) -> None:
        if self._thread is not None:
            if not self._close_when_finished:
                answer = QMessageBox.question(
                    self, "Идёт конвертация",
                    "Остановить обработку и закрыть программу после завершения текущей операции?",
                    QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
                    QMessageBox.StandardButton.No,
                )
                if answer == QMessageBox.StandardButton.Yes:
                    self._close_when_finished = True
                    self._cancel()
            event.ignore()
            return
        self._save_settings()
        event.accept()


def main() -> int:
    """Start the desktop application; optional arguments are PDF or TIFF paths."""
    app = QApplication(sys.argv)
    app.setApplicationName(APP_NAME)
    app.setOrganizationName("PDFMaster")
    app.setStyle("Fusion")
    app.setStyleSheet(STYLE)
    try:
        from .runtime import resource_root

        icon = resource_root() / "assets" / "app.svg"
    except ImportError:
        icon = Path(__file__).resolve().parents[2] / "assets" / "app.svg"
    if icon.is_file():
        app.setWindowIcon(QIcon(str(icon)))
    window = MainWindow()
    window.show()
    files = [argument for argument in sys.argv[1:] if not argument.startswith("-")]
    if files:
        window._add_files(files)
    return app.exec()


if __name__ == "__main__":
    raise SystemExit(main())
