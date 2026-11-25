import hashlib
import importlib.util
import io
import os
from pathlib import Path
import tarfile
import tempfile
import unittest
import zipfile
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('java_agent', Path(__file__).parents[1] / 'PCLCS/Server/Scripts/agent.py')
agent = importlib.util.module_from_spec(spec)
spec.loader.exec_module(agent)


class JavaArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name).resolve()
        self.staging = self.root / 'stage'
        self.staging.mkdir()
        self.archive = self.root / 'java.tar.gz'

    def tearDown(self):
        self.temporary.cleanup()

    def tar(self, entries):
        with tarfile.open(self.archive, 'w:gz') as bundle:
            for name, kind, value in entries:
                entry = tarfile.TarInfo(name)
                entry.type = kind
                entry.mode = 0o755 if name.endswith('/java') else 0o644
                if kind == tarfile.REGTYPE:
                    data = value.encode()
                    entry.size = len(data)
                    bundle.addfile(entry, io.BytesIO(data))
                else:
                    entry.linkname = value
                    bundle.addfile(entry)

    def test_internal_license_symlink_and_hardlink_are_materialized(self):
        self.tar([
            ('jre/bin/java', tarfile.REGTYPE, 'executable'),
            ('jre/legal/java.base/LICENSE', tarfile.REGTYPE, 'license'),
            ('jre/legal/java.xml/LICENSE', tarfile.SYMTYPE, '../java.base/LICENSE'),
            ('jre/legal/java.sql/LICENSE', tarfile.LNKTYPE, 'jre/legal/java.base/LICENSE'),
            ('jre/legal/java.se/LICENSE', tarfile.SYMTYPE, '../java.xml/LICENSE'),
        ])
        agent.extract_java_archive(self.archive, self.staging)
        for module in ('java.xml', 'java.sql', 'java.se'):
            target = self.staging / 'jre/legal' / module / 'LICENSE'
            self.assertEqual(target.read_text(), 'license')
            self.assertFalse(target.is_symlink())

    def test_extraction_rejects_escaping_absolute_dangling_and_cyclic_links_before_writing(self):
        for target in ('/etc/passwd', '../../../escape', '../../other/license', 'C:/escape', '..\\escape', '../missing/LICENSE', 'LICENSE'):
            with self.subTest(target=target):
                self.tar([('jre/bin/java', tarfile.REGTYPE, 'safe'), ('jre/legal/LICENSE', tarfile.SYMTYPE, target)])
                with self.assertRaises(ValueError):
                    agent.extract_java_archive(self.archive, self.staging)
                self.assertEqual(list(self.staging.iterdir()), [])

    def test_link_cannot_be_directory_parent(self):
        self.tar([('jre/bin/java', tarfile.REGTYPE, 'safe'), ('jre/a', tarfile.SYMTYPE, 'bin/java'), ('jre/a/payload', tarfile.REGTYPE, 'bad')])
        with self.assertRaisesRegex(ValueError, 'non-directory parent'):
            agent.extract_java_archive(self.archive, self.staging)
        self.assertEqual(list(self.staging.iterdir()), [])

    def test_special_files_traversal_duplicates_and_multiple_roots_rejected(self):
        for name, kind, value in [('jre/device', tarfile.CHRTYPE, ''), ('../escape', tarfile.REGTYPE, 'bad'), ('jre/bin/java', tarfile.REGTYPE, 'duplicate'), ('other/file', tarfile.REGTYPE, 'other')]:
            with self.subTest(name=name, kind=kind):
                self.tar([('jre/bin/java', tarfile.REGTYPE, 'safe'), (name, kind, value)])
                with self.assertRaises(ValueError):
                    agent.extract_java_archive(self.archive, self.staging)
                self.assertEqual(list(self.staging.iterdir()), [])

    def test_permissions_strip_setuid_on_linux(self):
        if os.name == 'nt':
            self.skipTest('POSIX permission test')
        with tarfile.open(self.archive, 'w:gz') as bundle:
            entry = tarfile.TarInfo('jre/bin/java')
            entry.mode = 0o4755;entry.size = 4
            bundle.addfile(entry, io.BytesIO(b'java'))
        agent.extract_java_archive(self.archive, self.staging)
        self.assertEqual((self.staging / 'jre/bin/java').stat().st_mode & 0o7777, 0o755)

    def test_java_for_installs_safe_links_and_cleans_staging(self):
        executable = 'java.exe' if os.name == 'nt' else 'java'
        self.tar([('jre/bin/' + executable, tarfile.REGTYPE, 'java'), ('jre/legal/java.base/LICENSE', tarfile.REGTYPE, 'license'), ('jre/legal/java.xml/LICENSE', tarfile.SYMTYPE, '../java.base/LICENSE')])
        payload = self.archive.read_bytes()
        metadata = [{'binary': {'package': {'link': 'https://example.invalid/jre', 'checksum': hashlib.sha256(payload).hexdigest()}}}]
        with patch.object(agent.shutil, 'which', return_value=None), patch.object(agent, 'api', return_value=metadata), patch.object(agent, 'download', side_effect=lambda url, destination, *args: destination.write_bytes(payload)), patch('platform.machine', return_value='x86_64'):
            result = agent.java_for(self.root, 21)
        self.assertTrue(Path(result).is_file())
        self.assertEqual((self.root / '.cpt-java-21/legal/java.xml/LICENSE').read_text(), 'license')
        self.assertFalse(list(self.root.glob('.cpt-java-stage-*')))
        self.assertFalse((self.root / '.cpt-java-download').exists())

    def test_java_for_invalid_archive_never_publishes_runtime(self):
        self.tar([('jre/bin/java', tarfile.REGTYPE, 'java'), ('jre/legal/LICENSE', tarfile.SYMTYPE, '/etc/passwd')])
        payload = self.archive.read_bytes()
        with patch.object(agent.shutil, 'which', return_value=None), patch.object(agent, 'api', return_value=[{'binary': {'package': {'link': 'https://example.invalid/jre', 'checksum': 'a' * 64}}}]), patch.object(agent, 'download', side_effect=lambda url, destination, *args: destination.write_bytes(payload)), patch('platform.machine', return_value='x86_64'):
            with self.assertRaises(ValueError):
                agent.java_for(self.root, 21)
        self.assertFalse((self.root / '.cpt-java-21').exists())
        self.assertFalse(list(self.root.glob('.cpt-java-stage-*')))

    def test_windows_java_zip_and_unsafe_zip_link(self):
        with zipfile.ZipFile(self.archive, 'w') as bundle:
            bundle.writestr('jre/bin/java.exe', b'java')
            bundle.writestr('jre/legal/LICENSE', b'license')
        agent.extract_java_archive(self.archive, self.staging)
        self.assertEqual((self.staging / 'jre/bin/java.exe').read_bytes(), b'java')
        with zipfile.ZipFile(self.archive, 'w') as bundle:
            link = zipfile.ZipInfo('jre/link')
            link.external_attr = 0o120777 << 16
            bundle.writestr(link, '/etc/passwd')
        with tempfile.TemporaryDirectory() as empty:
            with self.assertRaises(ValueError):
                agent.extract_java_archive(self.archive, Path(empty))

    def test_downloaded_adoptium_fixture_if_present(self):
        archive = Path(__file__).parents[1] / 'output/java21-archive-inspect.tar.gz'
        if not archive.exists():
            self.skipTest('Real JRE fixture not downloaded')
        self.assertEqual(hashlib.sha256(archive.read_bytes()).hexdigest(), '2413149700df0f7d440500a84a8f764c535f21e5a5e87d38328b64eec2c5b500')
        agent.extract_java_archive(archive, self.staging)
        runtime = next(self.staging.iterdir())
        self.assertTrue((runtime / 'bin/java').is_file())
        self.assertEqual((runtime / 'legal/java.xml/LICENSE').read_bytes(), (runtime / 'legal/java.base/LICENSE').read_bytes())
        self.assertFalse(any(path.is_symlink() for path in self.staging.rglob('*')))


if __name__ == '__main__':
    unittest.main()
