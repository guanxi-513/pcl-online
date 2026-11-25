import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('installer_agent', Path(__file__).parents[1] / 'PCLCS/Server/Scripts/agent.py')
agent = importlib.util.module_from_spec(spec)
spec.loader.exec_module(agent)


class InstallerDependencyTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name).resolve()
        self.staging = self.root / 'runtime'
        self.staging.mkdir()
        self.installer = self.root / 'installer.jar'
        self.library_data = b'library-verified'
        self.library = {'name': 'org.example:library:1.0', 'downloads': {'artifact': {'path': 'org/example/library/1.0/library-1.0.jar', 'url': 'https://maven.neoforged.net/releases/org/example/library/1.0/library-1.0.jar', 'sha1': hashlib.sha1(self.library_data).hexdigest(), 'size': len(self.library_data)}}}
        self.profile = {'minecraft': '1.21.1', 'json': '/version.json', 'libraries': [self.library], 'serverJarPath': '{LIBRARY_DIR}/net/minecraft/server/{MINECRAFT_VERSION}/server-{MINECRAFT_VERSION}.jar', 'data': {'MOJMAPS': {'server': '[net.minecraft:server:1.21.1:mappings@txt]'}}}
        self.info = {'downloads': {key: {'url': 'https://example.invalid/' + key, 'sha1': hashlib.sha1(key.encode()).hexdigest(), 'size': len(key)} for key in ('server', 'server_mappings')}}

    def tearDown(self):
        self.temporary.cleanup()

    def write_installer(self, extra=None, version=None):
        with zipfile.ZipFile(self.installer, 'w') as bundle:
            bundle.writestr('install_profile.json', json.dumps(self.profile))
            bundle.writestr('version.json', json.dumps(version or {'libraries': [self.library]}))
            for path, data in (extra or {}).items():
                bundle.writestr(path, data)

    def test_dependencies_deduplicated_and_expected_server_mapping_paths(self):
        self.write_installer()
        plan, embedded, server, mapping = agent.installer_dependencies(self.installer, self.info, '1.21.1')
        self.assertEqual(len(plan), 1)
        self.assertEqual(embedded, {})
        self.assertEqual(server, 'libraries/net/minecraft/server/1.21.1/server-1.21.1.jar')
        self.assertEqual(mapping, 'libraries/net/minecraft/server/1.21.1/server-1.21.1-mappings.txt')

    def test_prefetch_downloads_verified_libraries_and_official_server_artifacts(self):
        self.write_installer()
        def download(url, destination, digest, algorithm):
            data = self.library_data if 'library-1.0.jar' in url else url.rsplit('/', 1)[1].encode()
            self.assertEqual(hashlib.sha1(data).hexdigest(), digest)
            self.assertEqual(algorithm, 'sha1')
            destination.write_bytes(data)
        with patch.object(agent, 'download', side_effect=download) as fetch:
            agent.prepare_installer_dependencies(self.installer, self.staging, self.info, '1.21.1')
        self.assertEqual(fetch.call_count, 3)
        self.assertEqual((self.staging / 'libraries/org/example/library/1.0/library-1.0.jar').read_bytes(), self.library_data)
        self.assertEqual(json.loads((self.staging / '1.21.1.json').read_text()), self.info)

    def test_embedded_library_keeps_checksum_and_does_not_need_network(self):
        self.library['downloads']['artifact']['url'] = ''
        self.write_installer({'maven/org/example/library/1.0/library-1.0.jar': self.library_data})
        with patch.object(agent, 'download', side_effect=lambda url, destination, *args: destination.write_bytes(url.rsplit('/', 1)[1].encode())) as download:
            agent.prepare_installer_dependencies(self.installer, self.staging, self.info, '1.21.1')
        self.assertEqual(download.call_count, 2)
        self.assertEqual((self.staging / 'libraries/org/example/library/1.0/library-1.0.jar').read_bytes(), self.library_data)

    def test_traversal_or_coordinate_mismatch_rejected_before_downloading(self):
        for path in ('../escape.jar', '/absolute.jar', 'org/example/wrong.jar'):
            self.library['downloads']['artifact']['path'] = path
            self.write_installer()
            with patch.object(agent, 'download') as download:
                with self.assertRaises(ValueError):
                    agent.prepare_installer_dependencies(self.installer, self.staging, self.info, '1.21.1')
                download.assert_not_called()
            self.assertEqual(list(self.staging.iterdir()), [])

    def test_conflicting_digest_unsafe_url_missing_digest_and_wrong_version_rejected(self):
        for mutation in ('digest', 'url', 'version'):
            library = json.loads(json.dumps(self.library))
            if mutation == 'digest':
                library['downloads']['artifact']['sha1'] = 'bad'
            elif mutation == 'url':
                library['downloads']['artifact']['url'] = 'http://example.invalid/library.jar'
            else:
                self.profile['minecraft'] = '1.20.1'
            self.profile['libraries'] = [library]
            self.write_installer()
            with self.assertRaises(ValueError):
                agent.installer_dependencies(self.installer, self.info, '1.21.1')

    def test_duplicate_artifact_with_conflicting_digest_rejected(self):
        library = json.loads(json.dumps(self.library))
        library['downloads']['artifact']['sha1'] = 'a' * 40
        self.write_installer(version={'libraries': [library]})
        with self.assertRaisesRegex(ValueError, 'Conflicting'):
            agent.installer_dependencies(self.installer, self.info, '1.21.1')

    def test_failure_summary_not_a_dependency_wall(self):
        log = '\n'.join(['org.example:noexception:1.0'] * 200) + '\nThere was an error during installation'
        summary = agent.installer_failure('NeoForge', 1, log)
        self.assertNotIn('org.example', summary)
        self.assertLess(len(summary), 300)
        self.assertIn('退出码 1', summary)
        summary = agent.installer_failure('NeoForge', 1, 'java.net.UnknownHostException: maven.neoforged.net\nThere was an error during installation')
        self.assertIn('UnknownHostException', summary)

    def test_real_neoforge_profile_if_present(self):
        installer = Path(__file__).parents[1] / 'output/neoforge-21.1.255-installer-inspect.jar'
        if not installer.exists():
            self.skipTest('Real installer fixture absent')
        plan, embedded, server, mapping = agent.installer_dependencies(installer, self.info, '1.21.1')
        self.assertEqual(len(plan), 70)
        self.assertTrue(any(path.endswith('neoforge-21.1.255-universal.jar') for path in plan))
        self.assertEqual(server, 'libraries/net/minecraft/server/1.21.1/server-1.21.1.jar')
        self.assertIn('mappings.txt', mapping)
        self.assertTrue(all(len(artifact['sha1']) == 40 for artifact in plan.values()))


if __name__ == '__main__':
    unittest.main()
