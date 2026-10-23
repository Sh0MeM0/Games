"""Regression checks for the classic-workspace menu missing in release 0.1.0."""
from pathlib import Path
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from build import package_xml, TARGETS
from menu_package import build_classic_menu


class ClassicMenuPackageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.bundle = Path(self.temporary.name)
        build_classic_menu(self.bundle / 'Contents/Profiles.cuix')
        package_xml(self.bundle)
        self.package = ET.parse(self.bundle / 'PackageContents.xml').getroot()
        self.archive = zipfile.ZipFile(self.bundle / 'Contents/Profiles.cuix')
        self.addCleanup(self.archive.close)

    def part(self, name):
        return ET.fromstring(self.archive.read(name))

    def test_classic_menu_autoload_is_independent_of_managed_ribbon(self):
        groups = [g for g in self.package.findall('Components')
                  if any(e.get('ModuleName', '').endswith('.cuix') for e in g.findall('ComponentEntry'))]
        self.assertEqual(1, len(groups))
        menu = groups[0].find('ComponentEntry')
        self.assertEqual('./Contents/Profiles.cuix', menu.get('ModuleName'))
        self.assertEqual('True', menu.get('LoadOnAutoCADStartup'))
        self.assertFalse(any(e.get('ModuleName', '').endswith('.dll') for e in groups[0].findall('ComponentEntry')))
        self.assertEqual('R23.1', groups[0].find('RuntimeRequirements').get('SeriesMin'))
        self.assertEqual('R24.3', groups[0].find('RuntimeRequirements').get('SeriesMax'))

    def test_top_level_menu_has_russian_label_not_just_a_ribbon(self):
        menu = self.part('PopMenuRoot.cui').find('PopMenu')
        self.assertIsNotNone(menu)
        self.assertEqual('Профиля', menu.findtext('Name'))
        alias = menu.findtext('Alias')
        self.assertTrue(alias.startswith('POP'))
        self.assertTrue(1 <= int(alias[3:]) < 500, 'A context-only POP alias cannot create a menu-bar item')
        self.assertEqual(4, len(menu.findall('PopMenuItem')))
        self.assertEqual('SH0MEM0_PROFILES', self.part('MenuGroup.cui').get('Name'))

    def test_menu_actions_are_available_in_each_autocad_version(self):
        macros = {m.get('UID'): m.findtext('Macro/Command').strip()
                  for m in self.part('MenuGroup.cui').findall('.//MenuMacro')}
        commands = set()
        for reference in self.part('PopMenuRoot.cui').findall('.//MacroRef'):
            command = macros[reference.get('MenuMacroID')]
            self.assertTrue(command.startswith('^C^C'))
            commands.add(command[4:])
        self.assertEqual({'PROFILI', 'PROFILI_LONG', 'PROFILI_CROSS', 'PROFILI_MENU'}, commands)
        managed = [e for e in self.package.findall('.//ComponentEntry') if e.get('ModuleName', '').endswith('.dll')]
        self.assertEqual(5, len(managed))
        for entry in managed:
            self.assertEqual(commands, {c.get('Global') for c in entry.findall('Commands/Command')})
            self.assertEqual('True', entry.get('LoadOnCommandInvocation'))
        source = (ROOT / 'src/Profiles.AutoCAD/Commands.cs').read_text()
        for command in commands:
            self.assertIn('[CommandMethod("' + command + '"', source)

    def test_opc_relationships_and_metadata_cover_all_cui_parts(self):
        names = set(self.archive.namelist())
        self.assertEqual(len(names), len(self.archive.namelist()))
        targets = set()
        for relationship in self.part('_rels/.rels'):
            self.assertEqual('CUI', relationship.get('Type'))
            target = relationship.get('Target').lstrip('/')
            self.assertIn(target, names)
            targets.add(target)
        expected = {n for n in names if n.endswith('.cui')}
        self.assertEqual(expected, targets)
        metadata = {p.get('PartData_Name').lstrip('/') for p in self.part('Menu_Package_Info.xml')}
        self.assertEqual(expected, metadata)
        for name in names:
            self.part(name)  # Every part must be well-formed UTF-8 XML.
        self.assertIsNone(self.archive.testzip())

    def test_no_workspace_or_user_menu_replacement_is_packaged(self):
        self.assertEqual([], self.part('WorkspaceRoot.cui').findall('Workspace'))
        self.assertEqual([], list(self.part('LSPFiles.cui')))
        self.assertEqual([], self.part('RibbonRoot.cui').findall('.//RibbonTabSource'))
        self.assertFalse(any(n.endswith(('.dll', '.lsp', '.exe')) for n in self.archive.namelist()))
        versions = self.part('MenuGroup.cui').findall('.//ModifiedRev')
        self.assertTrue(versions)
        self.assertTrue(all(int(v.get('MajorVersion')) <= 23 for v in versions))

    def test_menu_package_is_reproducible(self):
        second = self.bundle / 'again.cuix'
        build_classic_menu(second)
        self.assertEqual((self.bundle / 'Contents/Profiles.cuix').read_bytes(), second.read_bytes())


if __name__ == '__main__':
    unittest.main()
