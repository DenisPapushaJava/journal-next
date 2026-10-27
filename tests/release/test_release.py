import importlib.util
import json
import re
from pathlib import Path
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('next_release', ROOT / 'scripts/release.py')
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class ReleaseTests(unittest.TestCase):
    def test_alpha_four_notes_are_exactly_the_three_approved_points(self):
        self.assertEqual(
            '1. Задание в цех: копирование текста по составу заявки.\n'
            '2. Папка «ГИБКА»: автоматическое создание после сохранения отметки.\n'
            '3. Служебные файлы и папки проектов скрываются в Windows.\n',
            release.release_notes('2026.1.0-alpha.4'))

    def test_hotfix_notes_are_one_point_and_do_not_replace_previous_release(self):
        notes = release.release_notes('2026.1.0-alpha.3')
        self.assertEqual(['1'], re.findall(r'^(\d+)\. ', notes, re.MULTILINE))
        self.assertIn('дробные толщины и цены', notes)
        self.assertNotIn('**', notes)
        self.assertNotIn('#', notes)
        previous = release.release_notes('2026.1.0-alpha.2')
        self.assertEqual([str(n) for n in range(1, 10)], re.findall(r'^(\d+)\. ', previous, re.MULTILINE))

    def test_notes_preserve_the_approved_nine_points_without_bell_markup(self):
        notes = release.release_notes('2026.1.0-alpha.2')
        self.assertEqual([str(n) for n in range(1, 10)], re.findall(r'^(\d+)\. ', notes, re.MULTILINE))
        self.assertNotIn('**', notes)
        self.assertNotIn('#', notes)
        self.assertIn('Настройки → Хранилище', notes)
        self.assertEqual('', release.release_notes('workflow-v2026.1.0-alpha.1'))
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); (root / 'docs').mkdir(); (root / 'docs/release-notes.md').write_text('# Empty\n', encoding='utf-8')
            with self.assertRaises(ValueError): release.release_notes('2026.1.0-alpha.2', root)

    def test_first_release_identity_is_independent_and_windows_only(self):
        p = release.plan('workflow-v2026.1.0-alpha.1')
        self.assertEqual('DenisPapusha.JournalWorkflow', p['productId'])
        self.assertEqual('workflow-win-x64', p['channel'])
        self.assertEqual('https://github.com/DenisPapushaJava/journal-next', p['repository'])
        for runtime in ['linux-x64', 'win-x86', 'osx-arm64']:
            with self.assertRaises(ValueError): release.plan('2026.1.0-alpha.1', runtime)

    def test_invalid_or_legacy_tags_are_rejected(self):
        for v in ['v2026.1.0', '2026.1.0-alpha.0', '2026.1.0-alpha.1;echo x', '../2026.1.0', '2026.1.0.1']:
            with self.assertRaises(ValueError): release.plan(v)

    def test_repository_or_identity_cannot_be_changed_by_manifest(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            original = json.loads((ROOT / 'workflow-product.json').read_text(encoding='utf-8'))
            for key, value in [('productId', 'DenisPapusha.Journal'), ('updatesRepository', 'https://github.com/DenisPapushaJava/journal-v2.0'), ('publicationEnabled', False)]:
                (root / 'workflow-product.json').write_text(json.dumps(original | {key: value}))
                with self.assertRaises(ValueError): release.product(root)

    def test_legacy_entrypoint_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            p = Path(temp)
            for name in ['Jornal.Workflow.Desktop.exe', 'Jornal.Workflow.Desktop.dll', 'Jornal.Workflow.Desktop.deps.json', 'Jornal.Workflow.Desktop.runtimeconfig.json', 'AccessHost/Jornal.AccessHost.exe']:
                f = p / name; f.parent.mkdir(parents=True, exist_ok=True); f.write_bytes(b'fixture')
            release.verify_publish(p)
            (p / 'Jornal.Desktop.exe').write_bytes(b'old')
            with self.assertRaises(ValueError): release.verify_publish(p)

    def test_feed_hashes_package_identity_and_exact_file_set(self):
        with tempfile.TemporaryDirectory() as temp:
            p = Path(temp); v = '2026.1.0-alpha.2'
            package = p / f'{release.PACKAGE}-{v}-workflow-win-x64-full.nupkg'
            with zipfile.ZipFile(package, 'w') as z:
                z.writestr('test.nuspec', f'<package><metadata><id>{release.PACKAGE}</id><version>{v}</version></metadata></package>')
                z.writestr('lib/app/' + release.EXECUTABLE, b'fixture')
            setup = p / f'JournalNext-{v}-win-x64-OfflineSetup.exe'; setup.write_bytes(b'setup')
            feed = p / f'releases.{release.CHANNEL}.json'
            asset = dict(PackageId=release.PACKAGE, Version=v, Type='Full', FileName=package.name, Size=package.stat().st_size,
                         SHA256=release.sha(package), NotesMarkdown=release.release_notes(v))
            def hashes():
                (p / 'SHA256SUMS').write_text(''.join(f'{release.sha(f)}  {f.name}\n' for f in [package, setup, feed]))
            feed.write_text(json.dumps({'Assets': [asset]})); hashes(); release.verify_release(p, v)
            for key, value in [('PackageId', 'DenisPapusha.Journal'), ('Version', '2026.2.0'), ('SHA256', '0' * 64),
                               ('FileName', '../old.nupkg'), ('NotesMarkdown', ''), ('NotesMarkdown', 'Wrong notes')]:
                feed.write_text(json.dumps({'Assets': [asset | {key: value}]})); hashes()
                with self.assertRaises(ValueError): release.verify_release(p, v)
            feed.write_text(json.dumps({'Assets': [asset]})); hashes()
            (p / 'old-release.exe').write_bytes(b'old')
            with self.assertRaises(ValueError): release.verify_release(p, v)


if __name__ == '__main__': unittest.main()
