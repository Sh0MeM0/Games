"""Checks of the autoloader manifest and the classic top menu «Картограмма»."""
from pathlib import Path
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from build import package_xml, TARGETS  # noqa: E402
from menu_package import build_classic_menu, COMMANDS  # noqa: E402

COMMAND_NAMES = {command for command, _, _ in COMMANDS}


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.bundle = Path(self.temporary.name)
        build_classic_menu(self.bundle / 'Contents/Kzm.cuix')
        package_xml(self.bundle)
        self.package = ET.parse(self.bundle / 'PackageContents.xml').getroot()
        self.archive = zipfile.ZipFile(self.bundle / 'Contents/Kzm.cuix')
        self.addCleanup(self.archive.close)

    def part(self, name):
        return ET.fromstring(self.archive.read(name))

    def test_one_managed_build_per_autocad_release_2021_to_2026(self):
        managed = {}
        for group in self.package.findall('Components'):
            entry = group.find('ComponentEntry')
            if not entry.get('ModuleName', '').endswith('.dll'):
                continue
            requirement = group.find('RuntimeRequirements')
            self.assertEqual(requirement.get('SeriesMin'), requirement.get('SeriesMax'))
            managed[requirement.get('SeriesMin')] = entry.get('ModuleName')
        self.assertEqual({'R24.0', 'R24.1', 'R24.2', 'R24.3', 'R25.0', 'R25.1'}, set(managed))
        self.assertEqual('./Contents/2022/Kzm.AutoCAD.dll', managed['R24.1'])
        self.assertEqual('./Contents/2026/Kzm.AutoCAD.dll', managed['R25.1'])

    def test_classic_menu_is_loaded_independently_of_the_ribbon(self):
        groups = [g for g in self.package.findall('Components')
                  if any(e.get('ModuleName', '').endswith('.cuix') for e in g.findall('ComponentEntry'))]
        self.assertEqual(1, len(groups))
        requirement = groups[0].find('RuntimeRequirements')
        self.assertEqual('R24.0', requirement.get('SeriesMin'))
        self.assertEqual('R25.1', requirement.get('SeriesMax'))
        self.assertEqual('True', groups[0].find('ComponentEntry').get('LoadOnAutoCADStartup'))

    def test_top_menu_has_russian_title_and_all_commands(self):
        menu = self.part('PopMenuRoot.cui').find('PopMenu')
        self.assertEqual('Картограмма', menu.findtext('Name'))
        alias = menu.findtext('Alias')
        self.assertTrue(alias.startswith('POP') and 1 <= int(alias[3:]) < 500)
        macros = {m.get('UID'): m.findtext('Macro/Command').strip()
                  for m in self.part('MenuGroup.cui').findall('.//MenuMacro')}
        commands = set()
        for reference in self.part('PopMenuRoot.cui').findall('.//MacroRef'):
            command = macros[reference.get('MenuMacroID')]
            self.assertTrue(command.startswith('^C^C'))
            commands.add(command[4:])
        self.assertEqual(COMMAND_NAMES, commands)
        self.assertEqual('SH0MEM0_KZM', self.part('MenuGroup.cui').get('Name'))

    def test_demand_loading_knows_every_command(self):
        for entry in self.package.findall('.//ComponentEntry'):
            if not entry.get('ModuleName', '').endswith('.dll'):
                continue
            names = {c.get('Global') for c in entry.findall('Commands/Command')}
            self.assertEqual(COMMAND_NAMES, names)

    def test_commands_in_menu_exist_in_plugin_source(self):
        source = '\n'.join(p.read_text(encoding='utf-8') for p in (ROOT / 'src/Kzm.AutoCAD').rglob('*.cs'))
        for command in COMMAND_NAMES:
            self.assertIn('[CommandMethod("' + command + '"', source)

    def test_targets_cover_net48_and_net8(self):
        self.assertEqual('net48', TARGETS['2021'][1])
        self.assertEqual('net48', TARGETS['2024'][1])
        self.assertEqual('net8.0-windows', TARGETS['2025'][1])
        self.assertEqual('net8.0-windows', TARGETS['2026'][1])


if __name__ == '__main__':
    unittest.main()
