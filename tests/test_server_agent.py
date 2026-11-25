import importlib.util
import json
import hashlib
import io
from pathlib import Path
import tempfile
import socket
import struct
import subprocess
import sys
import threading
import zipfile
import unittest
import urllib.error
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('agent', Path(__file__).parents[1] / 'PCLCS/Server/Scripts/agent.py')
agent = importlib.util.module_from_spec(spec)
spec.loader.exec_module(agent)
properties_spec = importlib.util.spec_from_file_location('server_properties', Path(__file__).parents[1] / 'PCLCS/Server/Scripts/server_properties.py')
properties = importlib.util.module_from_spec(properties_spec)
properties_spec.loader.exec_module(properties)


class AgentTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name).resolve() / 'server'
        self.root.mkdir()
        (self.root / '.cpt-server.json').write_text(json.dumps({'core': 'Fabric', 'version': '1.21.1', 'java': 'java', 'memory': 4096, 'port': 25565}))
        (self.root / 'mods').mkdir()
        (self.root / 'plugins').mkdir()
        (self.root / 'server.properties').write_text('online-mode=true\nenable-rcon=true\nrcon.port=25575\nrcon.password=secret\nserver-port=25565\n')

    def tearDown(self):
        self.temporary.cleanup()

    def response(self, data, url='https://example.com/download'):
        response = io.BytesIO(data)
        response.url = url
        return response

    def test_metadata_retries_temporary_dns_failure(self):
        error = urllib.error.URLError(socket.gaierror(socket.EAI_AGAIN, 'Temporary failure in name resolution'))
        with patch.object(agent.urllib.request, 'urlopen', side_effect=[error, error, self.response(b'{"ok":true}')]) as request, patch.object(agent.time, 'sleep') as sleep:
            self.assertEqual(agent.api('https://example.com/metadata'), {'ok': True})
            self.assertEqual(request.call_count, 3)
            self.assertEqual([entry.args[0] for entry in sleep.call_args_list], [1, 2])

    def test_persistent_dns_error_reports_remote_host(self):
        error = urllib.error.URLError(socket.gaierror(socket.EAI_AGAIN, 'Temporary failure in name resolution'))
        with patch.object(agent.urllib.request, 'urlopen', side_effect=error) as request, patch.object(agent.time, 'sleep'):
            with self.assertRaisesRegex(ValueError, r'远端服务器 DNS 解析失败：piston-meta.mojang.com.*尝试 3 次') as failure:
                agent.fetch('https://piston-meta.mojang.com/version?secret=not-for-log')
            self.assertNotIn('not-for-log', str(failure.exception))
            self.assertIn('getent hosts', str(failure.exception))
            self.assertEqual(request.call_count, 3)

    def test_permanent_dns_and_tls_errors_not_retried(self):
        for reason in (socket.gaierror(socket.EAI_NONAME, 'not found'), agent.ssl.SSLCertVerificationError('certificate verify failed')):
            with patch.object(agent.urllib.request, 'urlopen', side_effect=urllib.error.URLError(reason)) as request, patch.object(agent.time, 'sleep') as sleep:
                with self.assertRaises(ValueError):
                    agent.fetch('https://example.com/metadata')
                self.assertEqual(request.call_count, 1)
                sleep.assert_not_called()

    def test_download_retry_restarts_partial_file_and_hash(self):
        class Interrupted(io.BytesIO):
            url = 'https://example.com/download'

            def read(self, size=-1):
                if self.tell() > 0:
                    raise ConnectionResetError('connection reset')
                return super().read(size)

        target = self.root / 'download.jar'
        target.write_bytes(b'installed')
        payload = b'new-complete-artifact'
        with patch.object(agent.urllib.request, 'urlopen', side_effect=[Interrupted(b'partial'), self.response(payload)]) as request, patch.object(agent.time, 'sleep'):
            agent.download('https://example.com/download', target, hashlib.sha256(payload).hexdigest())
        self.assertEqual(request.call_count, 2)
        self.assertEqual(target.read_bytes(), payload)
        self.assertFalse(target.with_name(target.name + '.part').exists())

    def test_download_dns_failure_preserves_installed_artifact(self):
        target = self.root / 'download.jar'
        target.write_bytes(b'installed')
        error = urllib.error.URLError(socket.gaierror(socket.EAI_AGAIN, 'Temporary failure in name resolution'))
        with patch.object(agent.urllib.request, 'urlopen', side_effect=error), patch.object(agent.time, 'sleep'):
            with self.assertRaisesRegex(ValueError, '远端服务器 DNS'):
                agent.download('https://example.com/download', target)
        self.assertEqual(target.read_bytes(), b'installed')
        self.assertFalse(target.with_name(target.name + '.part').exists())

    def test_http_checksum_and_insecure_redirect_fail_without_retry(self):
        error = urllib.error.HTTPError('https://example.com/download', 403, 'Forbidden', {}, None)
        with patch.object(agent.urllib.request, 'urlopen', side_effect=error) as request, patch.object(agent.time, 'sleep') as sleep:
            with self.assertRaisesRegex(ValueError, 'HTTP 403'):
                agent.fetch('https://example.com/download')
            self.assertEqual(request.call_count, 1)
            sleep.assert_not_called()
        target = self.root / 'checksum.jar'
        target.write_bytes(b'installed')
        with patch.object(agent.urllib.request, 'urlopen', return_value=self.response(b'bad')) as request:
            with self.assertRaisesRegex(ValueError, 'checksum mismatch'):
                agent.download('https://example.com/download', target, 'a' * 64)
            self.assertEqual(request.call_count, 1)
        self.assertEqual(target.read_bytes(), b'installed')
        with patch.object(agent.urllib.request, 'urlopen', return_value=self.response(b'bad', 'http://example.com/download')):
            with self.assertRaisesRegex(ValueError, 'Insecure redirect'):
                agent.download('https://example.com/download', target)

    def test_download_disk_error_does_not_retry_or_change_destination(self):
        target = self.root / 'permission.jar'
        target.write_bytes(b'installed')
        with patch.object(agent.urllib.request, 'urlopen', return_value=self.response(b'new')) as request, patch.object(Path, 'open', side_effect=PermissionError('disk permission')), patch.object(agent.time, 'sleep') as sleep:
            with self.assertRaises(PermissionError):
                agent.download('https://example.com/download', target)
            self.assertEqual(request.call_count, 1)
            sleep.assert_not_called()
        self.assertEqual(target.read_bytes(), b'installed')

    def test_download_emits_actual_bytes_and_completion(self):
        payload = b'game-data' * 20000
        response = self.response(payload)
        response.headers = {'Content-Length': str(len(payload))}
        with patch.object(agent.urllib.request, 'urlopen', return_value=response), patch.object(agent, 'progress_event') as events:
            agent.download('https://example.com/download', self.root / 'server.jar', hashlib.sha256(payload).hexdigest())
        downloads = [call.kwargs for call in events.call_args_list if call.args[0] == 'download']
        self.assertGreaterEqual(len(downloads), 2)
        self.assertEqual(downloads[0]['bytes'], 65536)
        self.assertEqual(downloads[0]['total'], len(payload))
        self.assertEqual(downloads[-1]['bytes'], len(payload))
        self.assertTrue(downloads[-1]['complete'])

    def test_known_mirror_mappings_require_digest_and_preserve_path(self):
        vanilla = 'https://piston-data.mojang.com/v1/objects/' + 'a' * 40 + '/server.jar'
        self.assertEqual(agent.download_candidates(vanilla, 'a' * 40), [agent.MIRROR_BASE + '/v1/objects/' + 'a' * 40 + '/server.jar', vanilla])
        forge = 'https://maven.minecraftforge.net/net/minecraftforge/forge/1.21.1-52.1.0/forge-1.21.1-52.1.0-installer.jar'
        self.assertEqual(agent.download_candidates(forge, 'a' * 40)[0], agent.MIRROR_BASE + '/maven/net/minecraftforge/forge/1.21.1-52.1.0/forge-1.21.1-52.1.0-installer.jar')
        java = 'https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.12.1%2B1/OpenJDK21U-jre_x64_linux_hotspot_21.0.12.1_1.tar.gz'
        self.assertEqual(agent.download_candidates(java, 'a' * 64)[0], 'https://mirrors.tuna.tsinghua.edu.cn/Adoptium/21/jre/x64/linux/OpenJDK21U-jre_x64_linux_hotspot_21.0.12.1_1.tar.gz')
        for url in (vanilla + '?secret=private', vanilla.replace('mojang.com/', 'mojang.com.evil/'), 'https://meta.fabricmc.net/v2/versions/loader/1.21.1/0.16.10/1.0.3/server/jar'):
            self.assertEqual(agent.download_candidates(url, 'a' * 64), [url])
        self.assertEqual(agent.download_candidates(vanilla, None), [vanilla])

    def test_source_ranking_uses_measured_speed_and_official_when_all_fail(self):
        sources = ['https://mirror.invalid/file', 'https://official.invalid/file']
        with patch.object(agent, 'probe_download_source', side_effect=lambda url: 100 if url == sources[0] else 500):
            self.assertEqual(agent.rank_download_sources(sources), list(reversed(sources)))

    def test_source_probes_run_concurrently(self):
        barrier = threading.Barrier(2)
        sources = ['https://mirror.invalid/file', 'https://official.invalid/file']
        def probe(url):
            barrier.wait(timeout=2)
            return 100
        with patch.object(agent, 'probe_download_source', side_effect=probe):
            self.assertEqual(set(agent.rank_download_sources(sources)), set(sources))
        with patch.object(agent, 'probe_download_source', return_value=None):
            self.assertEqual(agent.rank_download_sources(sources), list(reversed(sources)))

    def test_source_probe_is_bounded_and_rejects_invalid_result(self):
        with patch.object(agent.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, '{"speed":100,"elapsed":1}', '')) as command:
            self.assertEqual(agent.probe_download_source('https://example.com/file'), 100)
            self.assertEqual(command.call_args.kwargs['timeout'], 5)
            self.assertIn('Range', command.call_args.args[0][2])
        for output in ('bad', '{"speed":-1}', '{"speed":"NaN"}'):
            with patch.object(agent.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, output, '')):
                self.assertIsNone(agent.probe_download_source('https://example.com/file'))
        with patch.object(agent.subprocess, 'run', side_effect=subprocess.TimeoutExpired('probe', 5)):
            self.assertIsNone(agent.probe_download_source('https://example.com/file'))

    def test_mirror_checksum_failure_falls_back_to_verified_official(self):
        payload = b'official-artifact'
        original = 'https://piston-data.mojang.com/v1/objects/' + 'a' * 40 + '/server.jar'
        sources = agent.download_candidates(original, 'a' * 40)
        destination = self.root / 'mirror.jar'
        destination.write_bytes(b'existing')
        with patch.object(agent, 'rank_download_sources', return_value=sources), patch.object(agent.urllib.request, 'urlopen', side_effect=[self.response(b'bad-mirror'), self.response(payload)]) as request:
            agent.download(original, destination, hashlib.sha1(payload).hexdigest(), 'sha1')
        self.assertEqual(destination.read_bytes(), payload)
        self.assertEqual(request.call_count, 2)
        self.assertEqual(request.call_args_list[1].args[0].full_url, original)
        self.assertEqual(request.call_args_list[0].kwargs['timeout'], 15)
        self.assertFalse(destination.with_name(destination.name + '.part').exists())

    def test_mirror_http_failure_switches_without_waiting_for_three_retries(self):
        payload = b'artifact'
        original = 'https://piston-data.mojang.com/v1/objects/' + 'a' * 40 + '/server.jar'
        sources = agent.download_candidates(original, 'a' * 40)
        with patch.object(agent, 'rank_download_sources', return_value=sources), patch.object(agent.urllib.request, 'urlopen', side_effect=[urllib.error.HTTPError(sources[0], 404, 'Not found', {}, None), self.response(payload)]) as request:
            agent.download(original, self.root / 'fallback.jar', hashlib.sha1(payload).hexdigest(), 'sha1')
        self.assertEqual(request.call_count, 2)

    def test_all_mirror_checksum_failures_preserve_existing_and_disk_errors_do_not_switch(self):
        original = 'https://piston-data.mojang.com/v1/objects/' + 'a' * 40 + '/server.jar'
        sources = agent.download_candidates(original, 'a' * 40)
        destination = self.root / 'preserve.jar'
        destination.write_bytes(b'existing')
        with patch.object(agent, 'rank_download_sources', return_value=sources), patch.object(agent.urllib.request, 'urlopen', side_effect=[self.response(b'bad'), self.response(b'bad')]):
            with self.assertRaisesRegex(ValueError, 'checksum mismatch'):
                agent.download(original, destination, 'b' * 40, 'sha1')
        self.assertEqual(destination.read_bytes(), b'existing')
        with patch.object(agent, 'rank_download_sources', return_value=sources), patch.object(agent, 'download_direct', side_effect=PermissionError('disk')) as direct:
            with self.assertRaises(PermissionError):
                agent.download(original, destination, 'b' * 40, 'sha1')
            self.assertEqual(direct.call_count, 1)

    def test_html_download_rejected_before_replacing_file(self):
        response = self.response(b'<html>mirror error</html>')
        response.headers = {'Content-Type': 'text/html'}
        destination = self.root / 'html.jar'
        destination.write_bytes(b'existing')
        with patch.object(agent.urllib.request, 'urlopen', return_value=response):
            with self.assertRaisesRegex(ValueError, 'HTML'):
                agent.download('https://example.com/file', destination)
        self.assertEqual(destination.read_bytes(), b'existing')

    def test_truncated_content_length_never_replaces_artifact(self):
        response = self.response(b'short')
        response.headers = {'Content-Length': '100'}
        destination = self.root / 'truncated.jar'
        destination.write_bytes(b'existing')
        with patch.object(agent.urllib.request, 'urlopen', return_value=response):
            with self.assertRaisesRegex(ValueError, 'Content-Length'):
                agent.download('https://example.com/file', destination)
        self.assertEqual(destination.read_bytes(), b'existing')

    def test_progress_output_separate_redacted_and_bounded(self):
        output = self.root / 'events.jsonl'
        output.touch()
        with patch.object(agent, 'PROGRESS_FILE', output):
            agent.progress_event('terminal', 'password=secret access_token=private rcon.password=hidden')
        event = json.loads(output.read_text())
        self.assertNotIn('secret', event['message'])
        self.assertNotIn('private', event['message'])
        self.assertNotIn('hidden', event['message'])
        output.write_bytes(b'x' * (4 * 1024 * 1024))
        with patch.object(agent, 'PROGRESS_FILE', output):
            agent.progress_event('terminal', 'ignored')
        self.assertEqual(output.stat().st_size, 4 * 1024 * 1024)

    def test_startup_log_cursor_preserves_partial_lines_and_redacts(self):
        log = self.root / 'cpt-console.log'
        log.write_bytes(b'booting\npassword=private\npartial')
        first = agent.manage(self.root, {'action': 'deployment-logs', 'offset': 0})
        self.assertNotIn('private', first['text'])
        self.assertNotIn('partial', first['text'])
        with log.open('ab') as stream:
            stream.write(b' complete\n')
        second = agent.manage(self.root, {'action': 'deployment-logs', 'offset': first['offset']})
        self.assertEqual(second['text'], 'partial complete\n')

    def test_startup_diagnostics_only_current_attempt_and_redacts(self):
        log = self.root / 'cpt-console.log'
        old = b'old failure: OutOfMemoryError\n'
        log.write_bytes(old + b'new boot\njava.net.BindException: Address already in use\npassword=private\n')
        (self.root / '.cpt-start-attempt.json').write_text(json.dumps({'offset': len(old), 'started': agent.time.time()}))
        result = agent.manage(self.root, {'action': 'startup-diagnostics'})
        self.assertNotIn('old failure', result['text'])
        self.assertIn('BindException', result['summary'])
        self.assertNotIn('private', result['text'])

    def test_startup_diagnostics_latest_crash_and_limits(self):
        reports = self.root / 'crash-reports'
        reports.mkdir()
        old = reports / 'old.txt'
        old.write_text('old crash')
        start = agent.time.time() - 1
        agent.os.utime(old, (start - 20, start - 20))
        latest = reports / 'latest.txt'
        latest.write_text('java.lang.OutOfMemoryError: Java heap space\naccess_token=private\n' + 'x' * 40000)
        (self.root / '.cpt-start-attempt.json').write_text(json.dumps({'offset': 0, 'started': start}))
        result = agent.startup_diagnostics(self.root)
        self.assertEqual(result['crashFile'], 'latest.txt')
        self.assertIn('OutOfMemoryError', result['summary'])
        self.assertNotIn('private', result['crash'])
        self.assertLessEqual(len(result['crash']), 32768)

    def test_immediate_java_exit_reports_current_log_and_exit_code(self):
        settings = json.loads((self.root / '.cpt-server.json').read_text())
        settings['launchArgs'] = ['@libraries/neoforge/unix_args.txt', 'nogui']
        (self.root / '.cpt-server.json').write_text(json.dumps(settings))
        process = __import__('unittest.mock', fromlist=['Mock']).Mock()
        process.pid = 12345; process.returncode = 1; process.poll.return_value = 1
        def spawn(command, **kwargs):
            self.assertEqual(command[-2:], settings['launchArgs'])
            self.assertEqual(kwargs['cwd'], self.root)
            self.assertTrue(kwargs.get('start_new_session') if agent.os.name != 'nt' else kwargs.get('creationflags'))
            kwargs['stdout'].write(b'Error occurred during initialization of VM\nCould not reserve enough space for heap\n')
            kwargs['stdout'].flush()
            return process
        with patch.object(agent, 'require_stopped'), patch.object(agent, 'apply_pending'), patch.object(agent, 'check_game_binding', return_value=25565), patch.object(agent.time, 'sleep'), patch.object(agent.subprocess, 'Popen', side_effect=spawn), patch.object(agent, 'progress_event') as events:
            with self.assertRaisesRegex(ValueError, 'Java 退出码 1.*initialization'):
                agent.start(self.root)
        self.assertTrue(any('Could not reserve' in call.args[1] for call in events.call_args_list if call.args[0] == 'terminal'))

    def test_occupied_game_port_rejected_before_java_and_firewall(self):
        listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        listener.bind(('127.0.0.1', 0));listener.listen()
        port = listener.getsockname()[1]
        (self.root / 'server.properties').write_text(f'server-port={port}\nserver-ip=127.0.0.1\nrcon.port=25575\n')
        try:
            with patch.object(agent, 'require_stopped'), patch.object(agent, 'apply_pending'), patch.object(agent.subprocess, 'Popen') as launch, patch.object(agent, 'protect_ports') as firewall:
                with self.assertRaisesRegex(ValueError, '已被占用'):
                    agent.start(self.root)
                launch.assert_not_called();firewall.assert_not_called()
            self.assertEqual(listener.getsockname()[1], port)
        finally:
            listener.close()

    def test_game_port_probe_free_port_and_invalid_bind_address(self):
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
            probe.bind(('127.0.0.1', 0));port = probe.getsockname()[1]
        (self.root / 'server.properties').write_text(f'server-port={port}\nserver-ip=127.0.0.1\nrcon.port=25575\n')
        self.assertEqual(agent.check_game_binding(self.root), port)
        (self.root / 'server.properties').write_text(f'server-port={port}\nserver-ip=mc.example.com\n')
        with self.assertRaisesRegex(ValueError, 'server-ip'):
            agent.check_game_binding(self.root)

    def test_nonlocal_bind_address_is_actionable(self):
        (self.root / 'server.properties').write_text('server-port=25565\nserver-ip=192.0.2.123\n')
        error = OSError(agent.errno.EADDRNOTAVAIL, 'Cannot assign requested address')
        with patch.object(agent.socket, 'socket') as factory:
            factory.return_value.__enter__.return_value.bind.side_effect = error
            with self.assertRaisesRegex(ValueError, 'server-ip.*不属于本机'):
                agent.check_game_binding(self.root)

    def test_bind_crash_summary_identifies_game_port_and_host(self):
        (self.root / 'cpt-console.log').write_text('**** FAILED TO BIND TO PORT!\njava.net.BindException: Address already in use\n')
        result = agent.startup_diagnostics(self.root)
        self.assertIn('25565', result['summary'])
        self.assertIn('server-port', result['summary'])
        self.assertIn('不自动停止', result['summary'])

    def test_game_port_change_updates_management_metadata_preserving_rcon(self):
        with patch.object(agent, 'require_stopped'):
            result = agent.manage(self.root, {'action': 'config-save', 'text': 'server-port=25566\nserver-ip=\nonline-mode=true\n'})
        self.assertEqual(result['port'], 25566)
        self.assertEqual(json.loads((self.root / '.cpt-server.json').read_text())['port'], 25566)
        self.assertIn('rcon.password=secret', (self.root / 'server.properties').read_text())
        self.assertIn('server-port=25565', (self.root / 'server.properties.bak').read_text())

    def test_startup_diagnostics_missing_log_is_explicit(self):
        result = agent.startup_diagnostics(self.root)
        self.assertEqual(result['text'], '')
        self.assertEqual(result['crash'], '')
        self.assertIn('未取得明确', result['summary'])

    def test_startup_diagnostics_does_not_follow_console_or_crash_symlinks(self):
        if agent.os.name == 'nt':
            self.skipTest('POSIX symlink safety test')
        outside = self.root.parent / 'outside.txt'
        outside.write_text('private')
        (self.root / 'cpt-console.log').symlink_to(outside)
        with self.assertRaises(ValueError):
            agent.startup_diagnostics(self.root)

    def test_progress_log_unavailable_does_not_break_deployment(self):
        with patch.object(agent, 'PROGRESS_FILE', self.root / 'missing-events'):
            agent.progress_event('detail', 'continue')

    def test_deployment_pause_checkpoint_acknowledges_and_resumes(self):
        control = self.root / 'control-test.txt'
        control.write_text('run')
        marker = control.with_name(control.name + '.pause')
        marker.touch()
        paused = threading.Event()
        completed = threading.Event()
        errors = []
        def event(kind, message='', **values):
            if kind == 'paused':
                paused.set()
        def worker():
            try:
                agent.deployment_checkpoint()
                completed.set()
            except Exception as error:
                errors.append(error)
        with patch.object(agent, 'CONTROL_FILE', control), patch.object(agent, 'progress_event', side_effect=event) as events:
            thread = threading.Thread(target=worker)
            thread.start()
            try:
                self.assertTrue(paused.wait(2))
                self.assertFalse(completed.wait(0.1))
                marker.unlink()
                thread.join(2)
                self.assertTrue(completed.is_set())
                self.assertEqual(errors, [])
                self.assertEqual([call.args[0] for call in events.call_args_list], ['paused', 'resumed'])
            finally:
                marker.unlink(missing_ok=True)
                thread.join(2)

    def test_pause_channel_loss_refuses_to_continue(self):
        with patch.object(agent, 'CONTROL_FILE', self.root / 'missing-control'):
            with self.assertRaisesRegex(ValueError, 'control channel lost'):
                agent.deployment_checkpoint()

    def test_path_escape(self):
        for name in ('../outside', '/outside', '../../world'):
            with self.assertRaises(ValueError):
                agent.child(self.root, name)
        with self.assertRaises(ValueError):
            agent.root_path(Path.home())

    def test_eula_and_existing_deploy(self):
        with self.assertRaisesRegex(ValueError, 'explicitly'):
            agent.deploy(self.root, {'acceptEula': False})
        with self.assertRaisesRegex(ValueError, 'already exists'):
            agent.deploy(self.root, {'acceptEula': True, 'core': 'Vanilla', 'version': '1.21.1', 'memory': 4096, 'port': 25565})

    def test_config_redaction_and_backup(self):
        result = agent.manage(self.root, {'action': 'config-read'})
        self.assertNotIn('secret', result['text'])
        with patch.object(agent, 'require_stopped'):
            agent.manage(self.root, {'action': 'config-save', 'text': 'online-mode=true\nserver-port=25566\nmotd=Hello\n'})
        self.assertIn('rcon.password=secret', (self.root / 'server.properties').read_text())
        self.assertTrue((self.root / 'server.properties.bak').exists())

    def test_invalid_port_rejected(self):
        with patch.object(agent, 'require_stopped'):
            with self.assertRaises(ValueError):
                agent.manage(self.root, {'action': 'config-save', 'text': 'server-port=25575'})

    def test_toggle_and_category(self):
        (self.root / 'mods' / 'test.jar').write_bytes(b'jar')
        with patch.object(agent, 'require_stopped'):
            agent.manage(self.root, {'action': 'toggle', 'category': 'mods', 'name': 'test.jar'})
            self.assertTrue((self.root / 'mods' / 'test.jar.disabled').exists())
            agent.manage(self.root, {'action': 'toggle', 'category': 'mods', 'name': 'test.jar.disabled'})
            with self.assertRaises(ValueError):
                agent.manage(self.root, {'action': 'install', 'category': 'plugins', 'name': 'test.jar', 'url': 'https://example.com/a'})

    def test_player_input_and_commands(self):
        with self.assertRaises(ValueError):
            agent.manage(self.root, {'action': 'player', 'player': 'abc\nstop', 'verb': 'ban'})
        with self.assertRaises(ValueError):
            agent.manage(self.root, {'action': 'player', 'player': 'Steve', 'verb': 'execute'})
        with patch.object(agent, 'rcon', return_value='Banned') as mock:
            self.assertEqual(agent.manage(self.root, {'action': 'player', 'player': 'Steve', 'verb': 'ban'})['message'], 'Banned')
            mock.assert_called_once_with(self.root, 'ban Steve')

    def test_running_server_blocks_mutation(self):
        with patch.object(agent, 'rcon', return_value='Online'):
            with self.assertRaises(ValueError):
                agent.manage(self.root, {'action': 'toggle', 'category': 'mods', 'name': 'test.jar'})

    def test_https_download_validation(self):
        for url in ('file:///etc/passwd', 'http://example.com/a', 'https://user:pass@example.com/a'):
            with self.assertRaises(ValueError):
                agent.fetch(url)

    def test_fragmented_rcon_protocol(self):
        listener = socket.socket()
        listener.bind(('127.0.0.1', 0))
        listener.listen()
        port = listener.getsockname()[1]
        (self.root / 'server.properties').write_text(f'enable-rcon=true\nrcon.password=secret\nrcon.port={port}\n')
        errors = []

        def peer():
            try:
                connection, _ = listener.accept()
                with connection:
                    length = struct.unpack('<i', agent.exact(connection, 4))[0]
                    packet = agent.exact(connection, length)
                    self.assertEqual(packet[8:-2], b'secret')
                    auth = struct.pack('<iii', 10, 1, 2) + b'\0\0'
                    for byte in auth:
                        connection.sendall(bytes([byte]))
                    length = struct.unpack('<i', agent.exact(connection, 4))[0]
                    packet = agent.exact(connection, length)
                    self.assertEqual(packet[8:-2], b'ban Steve')
                    payload = struct.pack('<ii', 2, 0) + b'Banned Steve\0\0'
                    connection.sendall(struct.pack('<i', len(payload)) + payload)
            except BaseException as error:
                errors.append(error)
            finally:
                listener.close()

        worker = threading.Thread(target=peer)
        worker.start()
        self.assertEqual(agent.rcon(self.root, 'ban Steve'), 'Banned Steve')
        worker.join(10)
        self.assertFalse(worker.is_alive())
        if errors:
            raise errors[0]

    def test_start_blocks_existing_process(self):
        with patch.object(agent, 'require_stopped', side_effect=ValueError('running')):
            with self.assertRaisesRegex(ValueError, 'running'):
                agent.start(self.root)

    def queue(self, name='test.jar'):
        def download(url, target, *args):
            with zipfile.ZipFile(target, 'w') as jar:
                jar.writestr('fabric.mod.json', '{"id":"test"}')
        with patch.object(agent, 'download', side_effect=download):
            return agent.manage(self.root, {'action': 'install', 'category': 'mods', 'name': name, 'url': 'https://example.com/test.jar'})

    def test_preinstallation_does_not_touch_running_files(self):
        active = self.root / 'mods/test.jar'
        active.write_bytes(b'old')
        with patch.object(agent, 'rcon', return_value='Online'):
            result = self.queue()
        self.assertEqual(active.read_bytes(), b'old')
        self.assertEqual(result['pendingResources'], 1)
        self.assertTrue(agent.pending(self.root)['resources'][0]['replaces'])

    def test_apply_all_with_backup(self):
        (self.root / 'mods/test.jar').write_bytes(b'old')
        self.queue()
        self.queue('other.jar')
        with patch.object(agent, 'require_stopped'):
            agent.apply_pending(self.root)
        self.assertTrue(zipfile.is_zipfile(self.root / 'mods/test.jar'))
        self.assertTrue(zipfile.is_zipfile(self.root / 'mods/other.jar'))
        self.assertEqual(agent.pending(self.root)['resources'], [])
        backups = list((self.root / '.cpt-backups').rglob('artifact-*'))
        self.assertTrue(any(file.read_bytes() == b'old' for file in backups))

    def test_cancel_and_duplicate_queue(self):
        self.queue()
        with self.assertRaisesRegex(ValueError, 'already queued'):
            self.queue()
        entry = agent.pending(self.root)['resources'][0]
        agent.manage(self.root, {'action': 'pending-cancel', 'id': entry['id']})
        self.assertEqual(agent.pending(self.root)['resources'], [])
        self.assertFalse((self.root / entry['staged']).exists())

    def test_corrupt_queue_blocks_all_changes(self):
        self.queue()
        self.queue('other.jar')
        entry = agent.pending(self.root)['resources'][1]
        (self.root / entry['staged']).write_bytes(b'corrupt')
        with patch.object(agent, 'require_stopped'):
            with self.assertRaisesRegex(ValueError, 'checksum mismatch'):
                agent.apply_pending(self.root)
        self.assertFalse((self.root / 'mods/test.jar').exists())
        self.assertEqual(len(agent.pending(self.root)['resources']), 2)

    def test_restart_orders_stop_apply_start(self):
        calls = []
        with patch.object(agent, 'stop_wait', side_effect=lambda root: calls.append('stop')), patch.object(agent, 'apply_pending', side_effect=lambda root: calls.append('apply')), patch.object(agent, 'start', side_effect=lambda root, apply_queue: calls.append('start') or {'state': 'starting'}):
            agent.manage(self.root, {'action': 'restart'})
        self.assertEqual(calls, ['stop', 'apply', 'start'])

    def test_restart_does_not_apply_after_stop_failure(self):
        with patch.object(agent, 'stop_wait', side_effect=ValueError('timeout')), patch.object(agent, 'apply_pending') as apply:
            with self.assertRaisesRegex(ValueError, 'timeout'):
                agent.manage(self.root, {'action': 'restart'})
            apply.assert_not_called()

    def test_runtime_preparation_and_activation(self):
        def prepare(root, args, staging):
            staging.mkdir()
            (staging / 'server.jar').write_bytes(b'new-server')
            return {'core': 'Paper', 'version': '1.21.2', 'java': 'java21', 'javaMajor': 21,
                    'memory': 8192, 'port': 25566, 'artifacts': ['server.jar'], 'launchArgs': ['-jar', 'server.jar', 'nogui']}
        (self.root / 'server.jar').write_bytes(b'old-server')
        (self.root / 'world').mkdir()
        (self.root / 'world/level.dat').write_bytes(b'world-data')
        with patch.object(agent, 'prepare_runtime', side_effect=prepare):
            agent.manage(self.root, {'action': 'runtime-prepare', 'confirmBackup': True, 'core': 'Paper', 'version': '1.21.2'})
        self.assertEqual((self.root / 'server.jar').read_bytes(), b'old-server')
        with patch.object(agent, 'require_stopped'), patch.dict(sys.modules, {'server_properties': properties}):
            agent.apply_pending(self.root)
        self.assertEqual((self.root / 'server.jar').read_bytes(), b'new-server')
        self.assertEqual(json.loads((self.root / '.cpt-server.json').read_text())['core'], 'Paper')
        self.assertTrue(list((self.root / '.cpt-backups').rglob('level.dat')))
        self.assertEqual((self.root / 'world/level.dat').read_bytes(), b'world-data')
        self.assertIn('server-port=25566', (self.root / 'server.properties').read_text())

    def test_status_includes_queue_and_process_state(self):
        self.queue()
        with patch.object(agent, 'rcon', side_effect=ConnectionRefusedError()), patch.object(agent, 'process_running', return_value=True):
            result = agent.manage(self.root, {'action': 'status'})
        self.assertEqual(result['state'], 'starting-or-unresponsive')
        self.assertEqual(result['pendingResources'], 1)

    def test_rcon_wrong_password_not_assumed_stopped(self):
        with patch.object(agent, 'rcon', side_effect=PermissionError('wrong password')):
            with self.assertRaisesRegex(ValueError, 'authentication'):
                agent.require_stopped(self.root)

    def test_queue_version_race_rejected(self):
        with self.assertRaisesRegex(ValueError, 'target changed'):
            agent.manage(self.root, {'action': 'install', 'category': 'mods', 'name': 'test.jar',
                                     'expectedCore': 'Forge', 'expectedVersion': '1.21.1', 'url': 'https://example.com/a'})

    def test_batch_move_failure_restores_active_and_queue(self):
        (self.root / 'mods/test.jar').write_bytes(b'old')
        self.queue()
        self.queue('other.jar')
        original_rename = Path.rename

        def fail_second(path, target):
            if str(path).endswith('.jar') and path.parent.name == '.cpt-pending' and Path(target).name == 'other.jar':
                raise OSError('disk failure')
            return original_rename(path, target)

        with patch.object(agent, 'require_stopped'), patch.object(Path, 'rename', fail_second):
            with self.assertRaisesRegex(OSError, 'disk failure'):
                agent.apply_pending(self.root)
        self.assertEqual((self.root / 'mods/test.jar').read_bytes(), b'old')
        self.assertEqual(len(agent.pending(self.root)['resources']), 2)
        self.assertTrue(all((self.root / entry['staged']).exists() for entry in agent.pending(self.root)['resources']))

    def test_forge_and_neoforge_installer_launch(self):
        def metadata(url):
            if 'version_manifest' in url:
                return {'versions': [{'id': '1.21.1', 'url': 'https://example.com/version'}]}
            if 'promotions_slim' in url:
                return {'promos': {'1.21.1-recommended': '52.1.0'}}
            return {'javaVersion': {'majorVersion': 21}}

        def install(command, cwd, **kwargs):
            folder = cwd / ('libraries/net/minecraftforge/forge/1.21.1-52.1.0' if cwd.name.startswith('Forge-') else 'libraries/net/neoforged/neoforge/21.1.200')
            folder.mkdir(parents=True)
            (folder / ('win_args.txt' if agent.os.name == 'nt' else 'unix_args.txt')).write_text('-cp libraries/example.jar')
            return __import__('subprocess').CompletedProcess(command, 0)

        for core in ('Forge', 'NeoForge'):
            args = {'core': core, 'version': '1.21.1', 'memory': 4096, 'port': 25565, 'javaMajor': 21,
                    'loaderVersion': '52.1.0' if core == 'Forge' else '21.1.200'}
            staging = self.root / (core + '-runtime')
            with patch.object(agent, 'api', side_effect=metadata), patch.object(agent, 'java_for', return_value='java21'), patch.object(agent, 'fetch', return_value=b'a' * 40), patch.object(agent, 'download', side_effect=lambda url, destination, *extra: destination.write_bytes(b'installer')), patch.object(agent, 'prepare_installer_dependencies') as dependencies, patch.object(agent.subprocess, 'run', side_effect=install):
                result = agent.prepare_runtime(self.root, args, staging)
            dependencies.assert_called_once()
            self.assertTrue(result['launchArgs'][0].startswith('@libraries/'))
            self.assertTrue((staging / result['launchArgs'][0][1:]).exists())
            self.assertEqual(result['core'], core)

    def test_java_incompatible_version_rejected(self):
        args = {'core': 'Fabric', 'version': '1.21.1', 'memory': 4096, 'port': 25565, 'javaMajor': 17}
        with patch.object(agent, 'api', side_effect=[{'versions': [{'id': '1.21.1', 'url': 'https://example.com/a'}]}, {'javaVersion': {'majorVersion': 21}}]):
            with self.assertRaisesRegex(ValueError, 'requires Java 21'):
                agent.prepare_runtime(self.root, args, self.root / 'staging')

    def test_installed_mods_block_version_change(self):
        (self.root / 'mods/test.jar').write_bytes(b'old')
        directory = '.cpt-runtime-' + 'a' * 12
        stage = self.root / directory
        stage.mkdir()
        (stage / 'server.jar').write_bytes(b'new')
        queue = {'resources': [], 'runtime': {'directory': directory, 'settings': {'core': 'Forge', 'version': '1.21.2', 'artifacts': ['server.jar']}}}
        (self.root / '.cpt-pending.json').write_text(json.dumps(queue))
        with patch.object(agent, 'require_stopped'):
            with self.assertRaisesRegex(ValueError, 'Disable installed resources'):
                agent.apply_pending(self.root)
        self.assertEqual((self.root / 'mods/test.jar').read_bytes(), b'old')
        self.assertTrue((stage / 'server.jar').exists())

    def test_one_click_auto_memory_and_firewall(self):
        root = self.root / 'new-server'
        captured = {}

        def prepare(root, args, staging):
            captured.update(args)
            staging.mkdir()
            (staging / 'server.jar').write_bytes(b'jar')
            return {'core': 'Vanilla', 'version': '1.21.1', 'java': 'java', 'memory': args['memory'],
                    'port': args['port'], 'artifacts': ['server.jar']}

        with patch.object(agent, 'prepare_runtime', side_effect=prepare), patch.object(agent, 'protect_ports') as firewall, patch.object(agent.subprocess, 'check_output', return_value=str(4096 * 1024)), patch.object(Path, 'read_text', return_value='MemAvailable: 4194304 kB'):
            settings = agent.deploy(root, {'acceptEula': True, 'autoConfigure': True, 'configureFirewall': True, 'port': 25565})
        self.assertEqual(settings['memory'], 2457)
        self.assertTrue(settings['firewallManaged'])
        firewall.assert_called_once_with(25565)
        self.assertEqual(captured['memory'], 2457)
        self.assertTrue((root / 'server.jar').exists())

    def test_firewall_failure_prevents_installation(self):
        root = self.root / 'new-server'
        with patch.object(agent, 'protect_ports', side_effect=ValueError('Firewall setup failed')), patch.object(agent, 'prepare_runtime') as install:
            with self.assertRaisesRegex(ValueError, 'Firewall'):
                agent.deploy(root, {'acceptEula': True, 'configureFirewall': True, 'port': 25565})
            install.assert_not_called()

    def test_linux_firewall_waits_and_verifies_both_families_before_game_port(self):
        rules = set()

        def run(command, **kwargs):
            self.assertNotIn('sudo', command)
            self.assertEqual(command[1:3], ['-w', '10'])
            if '-C' in command:
                key = (command[0], tuple(command[command.index('INPUT') + 1:]))
                return subprocess.CompletedProcess(command, 0 if key in rules else 1, '', '' if key in rules else 'Bad rule (does a matching rule exist in that chain?).')
            if '-I' in command:
                rules.add((command[0], tuple(command[command.index('INPUT') + 2:])))
            return subprocess.CompletedProcess(command, 0, '', '')

        with patch.object(agent.os, 'name', 'posix'), patch.object(agent.os, 'geteuid', return_value=0, create=True), patch.object(agent.shutil, 'which', side_effect=lambda name: '/sbin/' + name), patch.object(agent.subprocess, 'run', side_effect=run) as commands:
            agent.protect_ports(25565)
            inserted = [call.args[0] for call in commands.call_args_list if '-I' in call.args[0]]
            self.assertEqual([command[-1] for command in inserted], ['DROP', 'DROP', 'ACCEPT', 'ACCEPT'])
            self.assertEqual(len(rules), 4)
            self.assertEqual(sum('-C' in call.args[0] for call in commands.call_args_list), 8)
            commands.reset_mock()
            agent.protect_ports(25565)
            self.assertFalse(any('-I' in call.args[0] for call in commands.call_args_list))

    def test_linux_firewall_sudo_failure_is_actionable(self):
        failure = subprocess.CompletedProcess([], 1, '', 'sudo: a password is required')
        with patch.object(agent.os, 'name', 'posix'), patch.object(agent.os, 'geteuid', return_value=1000, create=True), patch.object(agent.shutil, 'which', return_value='/usr/bin/sudo'), patch.object(agent.subprocess, 'run', return_value=failure) as commands:
            with self.assertRaisesRegex(ValueError, '免交互提权'):
                agent.protect_ports(25565)
            self.assertEqual(commands.call_args.args[0], ['sudo', '-n', 'iptables', '-w', '10', '-S', 'INPUT'])
            self.assertEqual(commands.call_count, 1)

    def test_linux_firewall_preflight_does_not_change_rules_on_permission_failure(self):
        def run(command, **kwargs):
            return subprocess.CompletedProcess(command, 1 if command[0] == 'ip6tables' else 0, '', 'Permission denied (you must be root)' if command[0] == 'ip6tables' else '')
        with patch.object(agent.os, 'name', 'posix'), patch.object(agent.os, 'geteuid', return_value=0, create=True), patch.object(agent.shutil, 'which', return_value='/sbin/iptables'), patch.object(agent.subprocess, 'run', side_effect=run) as commands:
            with self.assertRaisesRegex(ValueError, 'NET_ADMIN'):
                agent.protect_ports(25565)
            self.assertFalse(any('-I' in call.args[0] for call in commands.call_args_list))

    def test_firewall_permission_errors_not_treated_as_missing_rule(self):
        for detail, hint in [('Permission denied', '权限'), ('Another app is currently holding the xtables lock', '规则锁'), ('Table does not exist', '内核')]:
            with patch.object(agent.subprocess, 'run', return_value=subprocess.CompletedProcess([], 1, '', detail)):
                with self.assertRaisesRegex(ValueError, hint):
                    agent.firewall_command(['iptables', '-C', 'INPUT'], allow_missing=True)

    def test_firewall_timeout_is_actionable(self):
        with patch.object(agent.subprocess, 'run', side_effect=subprocess.TimeoutExpired('iptables', 30)):
            with self.assertRaisesRegex(ValueError, '防火墙配置超时'):
                agent.firewall_command(['iptables', '-w', '10', '-S', 'INPUT'])

    def test_firewall_failed_insert_keeps_game_port_closed(self):
        def run(command, **kwargs):
            if '-C' in command:
                return subprocess.CompletedProcess(command, 1, '', '')
            if '-I' in command:
                return subprocess.CompletedProcess(command, 1, '', 'iptables: Permission denied')
            return subprocess.CompletedProcess(command, 0, '', '')
        with patch.object(agent.os, 'name', 'posix'), patch.object(agent.os, 'geteuid', return_value=0, create=True), patch.object(agent.shutil, 'which', return_value='/sbin/iptables'), patch.object(agent.subprocess, 'run', side_effect=run) as commands:
            with self.assertRaisesRegex(ValueError, '25575.*未确认受保护'):
                agent.protect_ports(25565)
            self.assertFalse(any('ACCEPT' in call.args[0] for call in commands.call_args_list))

    def test_paper_selected_build_not_latest(self):
        args = {'core': 'Paper', 'version': '1.21.1', 'memory': 4096, 'port': 25565, 'loaderVersion': '100'}
        metadata = [{'versions': [{'id': '1.21.1', 'url': 'https://example.com/version'}]},
                    {'javaVersion': {'majorVersion': 21}},
                    [{'id': 101, 'channel': 'STABLE', 'downloads': {'server:default': {'url': 'https://example.com/101', 'checksums': {'sha256': 'a'}}}},
                     {'id': 100, 'channel': 'STABLE', 'downloads': {'server:default': {'url': 'https://example.com/100', 'checksums': {'sha256': 'b'}}}}]]
        with patch.object(agent, 'api', side_effect=metadata), patch.object(agent, 'java_for', return_value='java'), patch.object(agent, 'download', side_effect=lambda url, path, *extra: path.write_bytes(b'jar')) as download:
            result = agent.prepare_runtime(self.root, args, self.root / 'paper-runtime')
        self.assertEqual(result['loaderVersion'], '100')
        self.assertEqual(download.call_args.args[0], 'https://example.com/100')

    def test_vanilla_client_pack_excludes_server_secrets(self):
        (self.root / '.cpt-server.json').write_text(json.dumps({'core': 'Paper', 'version': '1.21.1'}))
        (self.root / 'ops.json').write_text('[{"name":"private"}]')
        (self.root / 'plugins/secret.jar').write_bytes(b'plugin')
        result = agent.build_client_pack(self.root)
        with zipfile.ZipFile(self.root / result['package']) as pack:
            self.assertEqual(set(pack.namelist()), {'modrinth.index.json', 'craftpilot.client.json'})
            index = json.loads(pack.read('modrinth.index.json'))
        self.assertEqual(index['dependencies'], {'minecraft': '1.21.1'})
        self.assertEqual(index['files'], [])
        self.assertNotIn('secret', json.dumps(index))
        self.assertEqual(len(result['revision']), 64)

    def test_required_client_mod_and_dependencies(self):
        (self.root / '.cpt-server.json').write_text(json.dumps({'core': 'Fabric', 'version': '1.21.1', 'loaderVersion': '0.16.10'}))
        data = b'installed-mod'
        (self.root / 'mods/example.jar').write_bytes(data)
        artifact = {'filename': 'example.jar', 'size': len(data), 'url': 'https://cdn.modrinth.com/example.jar',
                    'hashes': {'sha1': hashlib.sha1(data).hexdigest(), 'sha512': hashlib.sha512(data).hexdigest()}, 'primary': True}
        dep = {'filename': 'dependency.jar', 'size': 10, 'url': 'https://cdn.modrinth.com/dependency.jar',
               'hashes': {'sha1': 'a' * 40, 'sha512': 'b' * 128}, 'primary': True}
        version = {'project_id': 'example', 'game_versions': ['1.21.1'], 'loaders': ['fabric'], 'files': [artifact],
                   'dependencies': [{'dependency_type': 'required', 'version_id': 'depversion'}]}
        dependency = {'project_id': 'dependency', 'game_versions': ['1.21.1'], 'loaders': ['fabric'], 'files': [dep], 'dependencies': []}
        def metadata(url):
            if '/version_file/' in url:
                return version
            if '/project/example' in url:
                return {'client_side': 'required'}
            if '/version/depversion' in url:
                return dependency
            return {'client_side': 'optional'}
        with patch.object(agent, 'api', side_effect=metadata):
            result = agent.build_client_pack(self.root)
        self.assertEqual(result['requiredMods'], 2)
        self.assertEqual(result['unresolved'], [])
        with zipfile.ZipFile(self.root / result['package']) as pack:
            index = json.loads(pack.read('modrinth.index.json'))
        self.assertEqual(index['dependencies']['fabric-loader'], '0.16.10')
        self.assertEqual({file['path'] for file in index['files']}, {'mods/example.jar', 'mods/dependency.jar'})

    def test_unknown_mod_blocks_client_sync(self):
        (self.root / '.cpt-server.json').write_text(json.dumps({'core': 'Forge', 'version': '1.21.1', 'loaderVersion': '52.1.0'}))
        (self.root / 'mods/unknown.jar').write_bytes(b'unknown')
        with patch.object(agent, 'api', side_effect=ValueError('Not found')):
            result = agent.build_client_pack(self.root)
        self.assertEqual(result['requiredMods'], 0)
        self.assertEqual(result['unresolved'][0]['name'], 'unknown.jar')

    def test_server_only_mod_not_sent_to_client(self):
        (self.root / '.cpt-server.json').write_text(json.dumps({'core': 'NeoForge', 'version': '1.21.1', 'loaderVersion': '21.1.200'}))
        (self.root / 'mods/server-only.jar').write_bytes(b'server-only')
        with patch.object(agent, 'api', side_effect=[{'project_id': 'server'}, {'client_side': 'unsupported'}]):
            result = agent.build_client_pack(self.root)
        self.assertEqual(result['requiredMods'], 0)
        self.assertEqual(result['unresolved'], [])

    def test_basic_settings_preserve_private_config(self):
        with patch.object(agent, 'require_stopped'):
            agent.manage(self.root, {'action': 'basic-config-save', 'maxPlayers': 35, 'difficulty': 'hard', 'gamemode': 'creative', 'whitelist': True})
        result = agent.manage(self.root, {'action': 'basic-config-read'})
        self.assertEqual(result, {'maxPlayers': 35, 'difficulty': 'hard', 'gamemode': 'creative', 'whitelist': True})
        self.assertIn('rcon.password=secret', (self.root / 'server.properties').read_text())
        self.assertTrue((self.root / 'server.properties.bak').exists())
        self.assertNotIn('secret', json.dumps(result))

    def test_basic_settings_reject_invalid_values(self):
        for values in ({'maxPlayers': 0}, {'difficulty': 'invalid'}, {'gamemode': 'stop\n'}, {'whitelist': 'true'}):
            args = {'action': 'basic-config-save', 'maxPlayers': 20, 'difficulty': 'easy', 'gamemode': 'survival', 'whitelist': False, **values}
            with self.assertRaises(ValueError):
                agent.manage(self.root, args)

    def test_local_jar_import_is_queued(self):
        with tempfile.TemporaryDirectory() as home:
            directory = Path(home) / '.cpt-server'
            directory.mkdir()
            token = 'import-' + 'a' * 32 + '.jar'
            uploaded = directory / token
            with zipfile.ZipFile(uploaded, 'w') as jar:
                jar.writestr('fabric.mod.json', '{}')
            digest = hashlib.sha256(uploaded.read_bytes()).hexdigest()
            with patch.object(Path, 'home', return_value=Path(home)):
                result = agent.manage(self.root, {'action': 'install', 'category': 'mods', 'name': 'custom.jar', 'uploadToken': token, 'uploadHash': digest})
            self.assertEqual(result['pendingResources'], 1)
            self.assertFalse((self.root / 'mods/custom.jar').exists())
            self.assertTrue((self.root / agent.pending(self.root)['resources'][0]['staged']).exists())

    def test_local_jar_import_rejects_checksum_mismatch(self):
        with tempfile.TemporaryDirectory() as home:
            directory = Path(home) / '.cpt-server'
            directory.mkdir()
            token = 'import-' + 'b' * 32 + '.jar'
            (directory / token).write_bytes(b'bad')
            with patch.object(Path, 'home', return_value=Path(home)):
                with self.assertRaisesRegex(ValueError, 'checksum mismatch'):
                    agent.manage(self.root, {'action': 'install', 'category': 'mods', 'name': 'custom.jar', 'uploadToken': token, 'uploadHash': 'a' * 64})
            self.assertEqual(agent.pending(self.root)['resources'], [])

    def imported_pack(self, directory, entries=None, extension='zip'):
        token = 'import-' + 'c' * 32 + '.' + extension
        upload = directory / '.cpt-server' / token
        upload.parent.mkdir(exist_ok=True)
        jar = __import__('io').BytesIO()
        with zipfile.ZipFile(jar, 'w') as archive:
            archive.writestr('version.json', '{"id":"1.21.1"}')
        with zipfile.ZipFile(upload, 'w') as archive:
            if extension == 'jar':
                archive.writestr('version.json', '{"id":"1.21.1"}')
            else:
                for name, data in (entries or {'server.jar': jar.getvalue(), 'server.properties': 'server-port=25566\nonline-mode=false\nmotd=Private pack\n', 'world/level.dat': b'world', 'mods/example.jar': jar.getvalue(), 'config/example.cfg': 'keep=true'}).items():
                    info = zipfile.ZipInfo(name)
                    info.filename = name
                    archive.writestr(info, data)
        digest = hashlib.sha256(upload.read_bytes()).hexdigest()
        return {'acceptEula': True, 'configureFirewall': True, 'uploadToken': token, 'uploadHash': digest,
                'pack': {'sha256': digest, 'prefix': '', 'core': 'Vanilla', 'version': '1.21.1',
                         'memory': 6144, 'minMemory': 2048, 'port': 25566, 'javaMajor': 21, 'loaderVersion': '',
                         'launchArgs': ['-jar', 'server.jar', 'nogui'], 'jvmArgs': ['-XX:+UseG1GC']}}

    def test_imported_pack_preserves_world_config_and_memory(self):
        with tempfile.TemporaryDirectory() as home:
            home = Path(home)
            args = self.imported_pack(home)
            root = self.root / 'imported'
            with patch.object(Path, 'home', return_value=home), patch.object(agent, 'java_for', return_value='java21'), patch.object(agent, 'api', side_effect=[{'versions': [{'id': '1.21.1', 'url': 'https://example.com/version'}]}, {'javaVersion': {'majorVersion': 21}}]), patch.object(agent, 'protect_ports') as firewall:
                result = agent.deploy(root, args)
            self.assertEqual(result['memory'], 6144)
            self.assertEqual(result['minMemory'], 2048)
            self.assertEqual(result['jvmArgs'], ['-XX:+UseG1GC'])
            self.assertEqual((root / 'world/level.dat').read_bytes(), b'world')
            self.assertEqual((root / 'config/example.cfg').read_text(), 'keep=true')
            props = agent.parse_properties((root / 'server.properties').read_text())
            self.assertEqual(props['online-mode'], 'false')
            self.assertEqual(props['motd'], 'Private pack')
            self.assertEqual(props['server-port'], '25566')
            self.assertEqual(props['enable-rcon'], 'true')
            firewall.assert_called_once_with(25566)

    def test_imported_pack_rejects_unsafe_entries_before_extracting(self):
        for name in ('../escape', 'C:/escape', '.cpt-server.json', 'world/../../escape', 'CON.txt', 'world\\escape'):
            with tempfile.TemporaryDirectory() as home:
                home = Path(home)
                args = self.imported_pack(home, {'safe.txt': b'safe', name: b'bad'})
                root = self.root / ('unsafe-' + hashlib.md5(name.encode()).hexdigest())
                with patch.object(Path, 'home', return_value=home):
                    with self.assertRaises(ValueError):
                        agent.deploy(root, args)
                self.assertFalse((root / 'safe.txt').exists(), name)

    def test_imported_pack_checksum_and_java_flags(self):
        with tempfile.TemporaryDirectory() as home:
            home = Path(home)
            args = self.imported_pack(home)
            args['uploadHash'] = 'a' * 64
            with patch.object(Path, 'home', return_value=home):
                with self.assertRaisesRegex(ValueError, 'checksum'):
                    agent.deploy(self.root / 'checksum', args)
            args['uploadHash'] = args['pack']['sha256']
            args['pack']['jvmArgs'] = ['-javaagent:unknown.jar']
            with patch.object(Path, 'home', return_value=home):
                with self.assertRaisesRegex(ValueError, 'JVM'):
                    agent.deploy(self.root / 'javaagent', args)

    def test_imported_pack_prefix_and_remote_argument_platform(self):
        with tempfile.TemporaryDirectory() as home:
            home = Path(home)
            folder = 'libraries/net/minecraftforge/forge/1.21.1-52.1.0/'
            args = self.imported_pack(home, {'Pack/' + folder + 'unix_args.txt': '-cp libraries/example.jar', 'Pack/' + folder + 'win_args.txt': '-cp libraries/example.jar'})
            args['pack'].update(prefix='Pack/', core='Forge', loaderVersion='52.1.0', launchArgs=['@' + folder + 'unix_args.txt', 'nogui'])
            root = self.root / 'forge-imported'
            with patch.object(Path, 'home', return_value=home), patch.object(agent, 'java_for', return_value='java21'), patch.object(agent, 'api', side_effect=[{'versions': [{'id': '1.21.1', 'url': 'https://example.com/version'}]}, {'javaVersion': {'majorVersion': 21}}]), patch.object(agent, 'protect_ports'):
                result = agent.deploy(root, args)
            self.assertEqual(result['launchArgs'][0], '@' + folder + ('win_args.txt' if agent.os.name == 'nt' else 'unix_args.txt'))
            self.assertFalse((root / 'Pack').exists())

    def test_imported_single_jar(self):
        with tempfile.TemporaryDirectory() as home:
            home = Path(home)
            args = self.imported_pack(home, extension='jar')
            with patch.object(Path, 'home', return_value=home), patch.object(agent, 'java_for', return_value='java21'), patch.object(agent, 'api', side_effect=[{'versions': [{'id': '1.21.1', 'url': 'https://example.com/version'}]}, {'javaVersion': {'majorVersion': 21}}]), patch.object(agent, 'protect_ports'):
                result = agent.deploy(self.root / 'jar-imported', args)
            self.assertTrue(result['imported'])

    def test_imported_pack_symlink_and_case_collision_rejected(self):
        for variant in ('symlink', 'duplicate'):
            with tempfile.TemporaryDirectory() as home:
                home = Path(home)
                args = self.imported_pack(home)
                upload = home / '.cpt-server' / args['uploadToken']
                with zipfile.ZipFile(upload, 'a') as archive:
                    if variant == 'symlink':
                        info = zipfile.ZipInfo('world/link')
                        info.external_attr = 0o120777 << 16
                        archive.writestr(info, '../escape')
                    else:
                        archive.writestr('SERVER.JAR', b'collision')
                args['uploadHash'] = args['pack']['sha256'] = hashlib.sha256(upload.read_bytes()).hexdigest()
                with patch.object(Path, 'home', return_value=home):
                    with self.assertRaisesRegex(ValueError, 'duplicate files'):
                        agent.deploy(self.root / variant, args)

    def test_legacy_metadata_migrates_without_touching_originals(self):
        root = self.root / 'legacy'
        root.mkdir()
        source = {'core': 'Vanilla', 'version': '1.21.1', 'java': str(root / '.pcl-java-21/bin/java'), 'memory': 4096, 'port': 25565}
        original = json.dumps(source).encode()
        (root / '.pcl-server.json').write_bytes(original)
        (root / '.pcl-java-21/bin').mkdir(parents=True)
        (root / '.pcl-java-21/bin/java').write_bytes(b'java')
        (root / '.pcl-pending').mkdir()
        (root / '.pcl-pending/test.jar').write_bytes(b'jar')
        (root / '.pcl-pending.json').write_text(json.dumps({'resources': [{'staged': '.pcl-pending/test.jar'}]}))
        (root / 'server.properties').write_text('motd=keep\n')
        agent.migrate_cpt_metadata(root)
        result = json.loads((root / '.cpt-server.json').read_text())
        self.assertEqual(result['java'], str(root / '.cpt-java-21/bin/java'))
        self.assertEqual((root / '.pcl-server.json').read_bytes(), original)
        self.assertEqual((root / '.pcl-java-21/bin/java').read_bytes(), b'java')
        self.assertEqual((root / '.cpt-java-21/bin/java').read_bytes(), b'java')
        self.assertEqual(agent.pending(root)['resources'][0]['staged'], '.cpt-pending/test.jar')
        self.assertEqual((root / 'server.properties').read_text(), 'motd=keep\n')
        result['memory'] = 8192
        (root / '.cpt-server.json').write_text(json.dumps(result))
        agent.migrate_cpt_metadata(root)
        self.assertEqual(json.loads((root / '.cpt-server.json').read_text())['memory'], 8192)

    def test_legacy_metadata_does_not_overwrite_cpt(self):
        (self.root / '.pcl-server.json').write_text('{"memory":1024}')
        original = (self.root / '.cpt-server.json').read_bytes()
        agent.migrate_cpt_metadata(self.root)
        self.assertEqual((self.root / '.cpt-server.json').read_bytes(), original)


if __name__ == '__main__':
    unittest.main()
