"""Build the partial CUIX «Картограмма» as an Open Packaging Conventions ZIP.

Adapted from the Profiles module (same repository, branch profiles-v0.8.8).

AutoCAD loads this menu directly from PackageContents.xml, even when its ribbon
is closed. Only standard CUI schema elements are used; no third-party commands,
images, workspaces or startup scripts are bundled.
"""
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile

GROUP = 'SH0MEM0_KZM'
MENU_UID = 'PMU_SH0MEM0_KZM'
MENU_NAME = 'Картограмма'
COMMANDS = (
    ('KZM', 'Панель КЗМ', 'Открыть панель: поверхности, точки, картограммы, настройки'),
    ('KZM_IMPORT', 'Импорт отметок из текстов', 'Открыть панель на форме импорта отметок'),
    ('KZM_POINT', 'Точки вручную', 'Указать точку и ввести отметку; Enter — повторить отметку'),
    ('KZM_CARTOGRAM', 'Новая картограмма', 'Граница работ, поверхности, сетка квадратов, оформление'),
    ('KZM_UPDATE', 'Пересчитать картограммы', 'Пересчитать устаревшие картограммы чертежа'),
    ('KZM_SETTINGS', 'Настройки чертежа', 'Знаки после запятой, масштаб новых картограмм'),
    ('KZM_CLEARMARKS', 'Убрать пометки проверки', 'Удалить пометки со слоя КЗМ_ПРОВЕРКА'),
    ('KZM_HELP', 'Справка по командам', 'Вывести список команд в командную строку'),
    ('KZM_MENU', 'Показать меню «Картограмма»', 'Восстановить меню в текущем рабочем пространстве'),
)
ROOTS = (
    'AcceleratorRoot', 'OverrideRoot', 'MouseButtonRoot', 'ToolbarRoot',
    'DoubleClickRoot', 'QuickPropertiesRoot', 'RolloverTooltipRoot',
    'QuickAccessToolbarRoot', 'ToolPanelRoot', 'PanelSetRoot', 'ScreenMenuRoot',
    'ImageMenuRoot', 'TabletMenuRoot', 'DigitizerButtonRoot', 'LSPFiles',
)

def rev(parent, tag='ModifiedRev'):
    # Oldest supported release (AutoCAD 2021 = R24.0); newer releases read older CUIX parts.
    return ET.SubElement(parent, tag, MajorVersion='24', MinorVersion='0', UserVersion='1')

def xml(element):
    ET.indent(element)
    return ET.tostring(element, encoding='utf-8', xml_declaration=True)

def build_classic_menu(destination):
    destination = Path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    parts = {}
    header = ET.Element('CustSection')
    ET.SubElement(header, 'FileVersion', MajorVersion='0', MinorVersion='6', IncrementalVersion='1', UserVersion='1')
    rev(ET.SubElement(ET.SubElement(ET.SubElement(header, 'Header'), 'CommonConfiguration'), 'CommonItems'))
    parts['Header.cui'] = xml(header)
    workspace = ET.Element('WorkspaceRoot')
    ET.SubElement(workspace, 'WorkspaceConfigRoot')
    parts['WorkspaceRoot.cui'] = xml(workspace)
    group = ET.Element('MenuGroup', Name=GROUP, DisplayName=MENU_NAME)
    macros = ET.SubElement(group, 'MacroGroup', Name=GROUP)
    menus = ET.Element('PopMenuRoot')
    popup = ET.SubElement(menus, 'PopMenu', hasDiesel='false', UID=MENU_UID)
    rev(popup)
    ET.SubElement(popup, 'Alias').text = 'POP1'
    ET.SubElement(popup, 'Name', xlate='true', UID='XLS_SH0MEM0_KZM').text = MENU_NAME
    for command, label, description in COMMANDS:
        macro_id = 'MMU_' + command
        macro = ET.SubElement(ET.SubElement(macros, 'MenuMacro', UID=macro_id), 'Macro', type='Any')
        rev(macro, 'Revision')
        rev(macro)
        ET.SubElement(macro, 'Name', xlate='true', UID='XLS_NAME_' + command).text = label
        ET.SubElement(macro, 'Command').text = '^C^C' + command + ' '
        ET.SubElement(macro, 'HelpString', xlate='true', UID='XLS_HELP_' + command).text = description
        ET.SubElement(macro, 'SmallImage', Name='')
        ET.SubElement(macro, 'LargeImage', Name='')
        item = ET.SubElement(popup, 'PopMenuItem', IsSeparator='false', hasDiesel='false', UID='PMIU_' + command)
        rev(item)
        ET.SubElement(item, 'NameRef', xlate='true', UID='XLS_ITEM_' + command).text = label
        ET.SubElement(ET.SubElement(item, 'MenuItem'), 'MacroRef', MenuMacroID=macro_id)
    parts['MenuGroup.cui'] = xml(group)
    parts['PopMenuRoot.cui'] = xml(menus)
    ribbon = ET.Element('RibbonRoot')
    ET.SubElement(ribbon, 'RibbonPanelSourceCollection')
    ET.SubElement(ribbon, 'RibbonTabSourceCollection')
    parts['RibbonRoot.cui'] = xml(ribbon)
    for name in ROOTS:
        parts[name + '.cui'] = xml(ET.Element(name))
    metadata = ET.Element('MenuPackageParts')
    relationships = ET.Element('Relationships', xmlns='http://schemas.openxmlformats.org/package/2006/relationships')
    for number, name in enumerate(parts, 1):
        ET.SubElement(metadata, 'PartData', PartData_Name='/' + name, PartData_Modified='2026-11-12T00:00:00Z')
        ET.SubElement(relationships, 'Relationship', Type='CUI', Target='/' + name, Id='RKzm' + str(number))
    parts['Menu_Package_Info.xml'] = xml(metadata)
    parts['_rels/.rels'] = xml(relationships)
    types = ET.Element('Types', xmlns='http://schemas.openxmlformats.org/package/2006/content-types')
    for extension, content in [('cui', 'text/xml'), ('xml', 'text/xml'), ('rels', 'application/vnd.openxmlformats-package.relationships+xml')]:
        ET.SubElement(types, 'Default', Extension=extension, ContentType=content)
    parts['[Content_Types].xml'] = xml(types)
    with zipfile.ZipFile(destination, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
        for name, data in parts.items():
            info = zipfile.ZipInfo(name, date_time=(2026, 11, 12, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, data)
