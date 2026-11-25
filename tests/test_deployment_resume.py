import hashlib
import importlib.util
import json
import subprocess
import sys
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('resume_agent', Path(__file__).parents[1] / 'PCLCS/Server/Scripts/agent.py')
agent = importlib.util.module_from_spec(spec)
spec.loader.exec_module(agent)


class DeploymentResumeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.home = Path(self.temporary.name).resolve()
        self.root = self.home / 'servers/test'
        self.home_patch = patch.object(Path, 'home', return_value=self.home)
        self.home_patch.start()
        self.args = {'action': 'deploy', 'acceptEula': True, 'core': 'Vanilla', 'version': '1.21.1', 'memory': 2048, 'port': 25565, 'loaderVersion': '', 'root': str(self.root), 'password': 'never-store', 'progressFile': 'events-test'}

    def tearDown(self):
        self.home_patch.stop()
        self.temporary.cleanup()

    def test_failed_install_keeps_parameters_and_reuses_verified_cache_on_resume(self):
        payload = b'verified-runtime'
        digest = hashlib.sha256(payload).hexdigest()
        attempts = []
        def prepare(staging, args):
            staging.mkdir()
            agent.download('https://example.invalid/server.jar', staging / 'server.jar', digest)
            attempts.append(args)
            if len(attempts) == 1:
                raise ValueError('fixture installer failed')
            settings = {'java': str(staging / 'java'), 'core': args['core'], 'version': args['version'], 'memory': args['memory'], 'port': args['port'], 'launchArgs': ['-jar', 'server.jar', 'nogui'], 'loaderVersion': ''}
            (staging / 'java').write_bytes(b'java')
            (staging / '.cpt-server.json').write_text(json.dumps(settings))
            return settings
        def direct(url, target, *args):
            target.write_bytes(payload)
        with patch.object(agent, 'manage', side_effect=prepare), patch.object(agent, 'download_from_sources', side_effect=direct) as fetch:
            with self.assertRaisesRegex(ValueError, 'installer failed'):
                agent.deploy_transaction(self.root, self.args)
            self.assertFalse(self.root.exists())
            history = json.loads(agent.deployment_journal(self.root).read_text())
            self.assertEqual(history['state'], 'failed')
            self.assertNotIn('never-store', json.dumps(history))
            self.assertNotIn('progressFile', history['request'])
            self.assertFalse(list(self.root.parent.glob('*.cpt-stage-*')))
            result = agent.deploy_transaction(self.root, {'action': 'deploy-resume', 'core': 'Paper', 'root': str(self.root)})
            self.assertEqual(fetch.call_count, 1)
            self.assertEqual(result['core'], 'Vanilla')
            self.assertEqual(result['java'], str(self.root / 'java'))
            self.assertEqual((self.root / 'server.jar').read_bytes(), payload)
            self.assertEqual(json.loads(agent.deployment_journal(self.root).read_text())['state'], 'installed')

    def installed(self):
        self.root.mkdir(parents=True)
        java = self.root / 'java'
        java.write_bytes(b'java')
        (self.root / 'server.jar').write_bytes(b'game')
        config = {'java': str(java), 'core': 'Vanilla', 'version': '1.21.1', 'memory': 2048, 'port': 25565, 'launchArgs': ['-jar', 'server.jar', 'nogui']}
        (self.root / '.cpt-server.json').write_text(json.dumps(config))
        (self.root / 'server.properties').write_text('motd=preserve\nrcon.password=original\n')
        (self.root / 'world').mkdir()
        (self.root / 'world/level.dat').write_bytes(b'world')
        return config

    def test_old_installed_server_resume_needs_no_history_and_preserves_world(self):
        config = self.installed()
        with patch.object(agent, 'download') as fetch, patch.object(agent, 'manage') as manage:
            result = agent.deploy_transaction(self.root, {'action': 'deploy-resume'})
            self.assertTrue(result['resumedInstalled'])
            self.assertEqual(result['java'], config['java'])
            fetch.assert_not_called();manage.assert_not_called()
        self.assertEqual((self.root / 'world/level.dat').read_bytes(), b'world')
        self.assertIn('rcon.password=original', (self.root / 'server.properties').read_text())

    def test_missing_installed_java_or_launch_file_refuses_reinstall(self):
        self.installed()
        (self.root / 'java').unlink()
        with self.assertRaisesRegex(ValueError, 'Java executable is missing'):
            agent.deploy_transaction(self.root, {'action': 'deploy-resume'})
        self.assertEqual((self.root / 'world/level.dat').read_bytes(), b'world')

    def test_unknown_nonempty_directory_never_overwritten(self):
        self.root.mkdir(parents=True)
        (self.root / 'world.dat').write_bytes(b'untouched')
        with self.assertRaisesRegex(ValueError, 'unrecognized'):
            agent.deploy_transaction(self.root, {'action': 'deploy-resume'})
        self.assertEqual((self.root / 'world.dat').read_bytes(), b'untouched')

    def test_missing_or_mismatched_history_refused(self):
        with self.assertRaisesRegex(ValueError, '没有可延续'):
            agent.deploy_transaction(self.root, {'action': 'deploy-resume'})
        agent.deployment_journal(self.root).write_text(json.dumps({'root': str(self.home / 'other'), 'request': self.args}))
        with self.assertRaisesRegex(ValueError, 'does not match'):
            agent.deploy_transaction(self.root, {'action': 'deploy-resume'})

    def test_imported_pack_incomplete_requires_reselection(self):
        history = agent.deployment_journal(self.root)
        history.write_text(json.dumps({'root': str(self.root), 'request': {'acceptEula': True, 'pack': {'core': 'Fabric'}}}))
        with self.assertRaisesRegex(ValueError, '重新选择原开服包'):
            agent.deploy_transaction(self.root, {'action': 'deploy-resume'})

    def test_corrupt_cached_file_is_redownloaded_and_verified(self):
        payload = b'good'
        digest = hashlib.sha256(payload).hexdigest()
        directory = self.home / '.cpt-server/artifact-cache'
        directory.mkdir(parents=True)
        (directory / ('sha256-' + digest)).write_bytes(b'bad')
        destination = self.home / 'download.jar'
        with patch.object(agent, 'DOWNLOAD_CACHE_ENABLED', True), patch.object(agent, 'download_from_sources', side_effect=lambda url, target, *args: target.write_bytes(payload)) as fetch:
            agent.download('https://example.invalid/file', destination, digest)
        self.assertEqual(fetch.call_count, 1)
        self.assertEqual(destination.read_bytes(), payload)

    def test_unhashed_dynamic_artifacts_do_not_use_cache(self):
        destination = self.home / 'fabric.jar'
        with patch.object(agent, 'DOWNLOAD_CACHE_ENABLED', True), patch.object(agent, 'download_from_sources', side_effect=lambda url, target, *args: target.write_bytes(b'dynamic')) as fetch:
            for _ in range(2):
                agent.download('https://example.invalid/dynamic', destination)
        self.assertEqual(fetch.call_count, 2)

    def test_cached_hash_does_not_bypass_https_validation(self):
        with patch.object(agent, 'DOWNLOAD_CACHE_ENABLED', True), patch.object(agent, 'download_from_sources') as fetch:
            with self.assertRaisesRegex(ValueError, 'HTTPS'):
                agent.download('file:///etc/passwd', self.home / 'unsafe.jar', 'a' * 64)
            fetch.assert_not_called()

    def test_saved_history_without_eula_refused(self):
        agent.deployment_journal(self.root).write_text(json.dumps({'root': str(self.root), 'request': {'core': 'Vanilla'}}))
        with self.assertRaisesRegex(ValueError, 'EULA'):
            agent.deploy_transaction(self.root, {'action': 'deploy-resume'})

    def test_running_install_resume_does_not_call_start_or_change_files(self):
        self.installed()
        with patch.object(agent, 'start') as start, patch.object(agent, 'protect_ports') as firewall:
            result = agent.deploy_transaction(self.root, {'action': 'deploy-resume'})
        self.assertTrue(result['resumedInstalled'])
        start.assert_not_called();firewall.assert_not_called()

    def test_actual_agent_cli_resumes_installed_server_without_reinstall(self):
        self.installed()
        arguments = {'action': 'deploy-resume', 'root': str(self.root)}
        encoded = __import__('base64').b64encode(json.dumps(arguments).encode()).decode()
        result = subprocess.run([sys.executable, str(Path(agent.__file__).resolve()), encoded], capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
        response = json.loads(result.stdout)
        self.assertTrue(response['ok'])
        self.assertTrue(response['result']['resumedInstalled'])
        self.assertEqual((self.root / 'world/level.dat').read_bytes(), b'world')


if __name__ == '__main__':
    unittest.main()
