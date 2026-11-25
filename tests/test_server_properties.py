import importlib.util
import base64
import json
from pathlib import Path
import sys
import subprocess
import tempfile
import unittest
from unittest.mock import Mock, patch

scripts = Path(__file__).parents[1] / 'PCLCS/Server/Scripts'
spec = importlib.util.spec_from_file_location('server_properties', scripts / 'server_properties.py')
properties = importlib.util.module_from_spec(spec)
spec.loader.exec_module(properties)
spec = importlib.util.spec_from_file_location('properties_agent', scripts / 'agent.py')
agent = importlib.util.module_from_spec(spec)
spec.loader.exec_module(agent)


class ServerPropertiesTests(unittest.TestCase):
    def setUp(self):
        self.text = ('# keep comments\r\n! Another comment\r\n'
                     'motd=\\u4f60\\u597d\r\nserver-port=25565\r\nserver-ip=\r\n'
                     'online-mode=true\r\nmax-players=20\r\ndifficulty=easy\r\n'
                     'rcon.password=private-password\r\nrcon.port=25575\r\nenable-rcon=true\r\n'
                     'management-server-secret=private-secret\r\ncustom.mod.setting = untouched\\:value\r\n')

    def test_chinese_labels_keep_english_keys_and_hide_management_secrets(self):
        panel = properties.panel(self.text)
        fields = {field['key']: field for field in panel['fields']}
        self.assertEqual(fields['online-mode']['label'], '正版在线验证')
        self.assertEqual(fields['online-mode']['value'], 'true')
        self.assertEqual(fields['server-port']['type'], 'integer')
        self.assertEqual(fields['motd']['value'], '你好')
        self.assertEqual(fields['custom.mod.setting']['value'], 'untouched:value')
        self.assertFalse(fields['rcon.password']['editable'])
        self.assertFalse(fields['enable-rcon']['editable'])
        self.assertNotIn('private-password', json.dumps(panel))
        self.assertNotIn('private-secret', json.dumps(panel))

    def test_patch_preserves_untouched_lines_comments_and_crlf(self):
        result = properties.patch(self.text, {'server-port': '25566', 'motd': '中文世界'}, properties.revision(self.text))
        self.assertIn('server-port=25566\r\n', result)
        self.assertIn('motd=\\u4e2d\\u6587\\u4e16\\u754c\r\n', result)
        self.assertIn('# keep comments\r\n! Another comment\r\n', result)
        self.assertIn('custom.mod.setting = untouched\\:value\r\n', result)
        self.assertIn('rcon.password=private-password\r\n', result)
        self.assertEqual(properties.values(result)['motd'], '中文世界')
        self.assertNotIn('游戏端口', result)

    def test_offline_mode_needs_explicit_confirmation_and_does_not_change_other_rules(self):
        with self.assertRaisesRegex(ValueError, '单独确认'):
            properties.patch(self.text, {'online-mode': 'false'}, properties.revision(self.text))
        result = properties.patch(self.text, {'online-mode': 'false'}, properties.revision(self.text), True)
        self.assertEqual(properties.values(result)['online-mode'], 'false')
        self.assertEqual(result.replace('online-mode=false', 'online-mode=true'), self.text)
        result = properties.patch(result, {'online-mode': 'true'}, properties.revision(result))
        self.assertEqual(result, self.text)

    def test_stale_revision_does_not_overwrite(self):
        with self.assertRaisesRegex(ValueError, '其他操作修改'):
            properties.patch(self.text + 'pvp=false\n', {'online-mode': 'false'}, properties.revision(self.text), True)

    def test_protected_unknown_injection_invalid_ranges_and_types_rejected(self):
        for changes in ({'rcon.password': 'new'}, {'enable-rcon': 'false'}, {'management-server-secret': 'new'},
                        {'new-key': 'new'}, {'server-port\nonline-mode': '25566'}, {'motd': 'x\nonline-mode=false'},
                        {'max-players': '0'}, {'server-port': '25575'}, {'server-port': '65536'}, {'online-mode': '0'},
                        {'difficulty': 'impossible'}, {'server-ip': 'mc.example.com'}, {'view-distance': '0'},
                        {'level-name': '../world'}, {'resource-pack-sha1': 'bad'}, {'motd': True}, {'generator-settings': '[]'}):
            with self.subTest(changes=changes):
                with self.assertRaises(ValueError):
                    properties.patch(self.text, changes, properties.revision(self.text), True)

    def test_standard_java_properties_delimiters_escapes_continuations_and_duplicate_keys(self):
        text = ('  # comment\nkey\\:name : value\\=ok\nspaced\\ key value\n'
                'continued=part1\\\n   part2\nlast=first\nlast:second\nflag\n'
                'motd=literal\\\\u4f60\nemoji=\\ud83d\\ude00\n')
        result = properties.values(text)
        self.assertEqual(result['key:name'], 'value=ok')
        self.assertEqual(result['spaced key'], 'value')
        self.assertEqual(result['continued'], 'part1part2')
        self.assertEqual(result['last'], 'second')
        self.assertEqual(result['flag'], '')
        self.assertEqual(result['motd'], r'literal\u4f60')
        self.assertEqual(result['emoji'], '\U0001f600')
        changed = properties.patch(text, {'last': 'third'}, properties.revision(text))
        self.assertIn('last=first\nlast=third\n', changed)
        self.assertEqual(properties.values(changed)['last'], 'third')

    def test_unicode_and_literal_backslash_values_roundtrip(self):
        for value in ('中文服务器', 'hello = world : text', r'\u4f60', ' leading spaces', '\U0001f600 world'):
            result = properties.patch(self.text, {'motd': value}, properties.revision(self.text))
            self.assertEqual(properties.values(result)['motd'], value)
        ipv6 = properties.patch(self.text, {'server-ip': '::1'}, properties.revision(self.text))
        self.assertIn('server-ip=::1', ipv6)

    def test_unknown_fields_boolean_and_secret_handling(self):
        fields = {field['key']: field for field in properties.panel('unknown-secret=private\nmod.custom=true\n')['fields']}
        self.assertFalse(fields['unknown-secret']['editable'])
        self.assertNotIn('private', str(fields))
        self.assertEqual(fields['mod.custom']['type'], 'boolean')
        changed = properties.patch('mod.custom=true\n', {'mod.custom': 'false'}, properties.revision('mod.custom=true\n'))
        self.assertEqual(changed, 'mod.custom=false\n')


class ConfigPanelAgentTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.original = '# untouched\r\nserver-port=25565\r\nonline-mode=true\r\nmotd=\\u4f60\\u597d\r\nrcon.password=private\r\nrcon.port=25575\r\nenable-rcon=true\r\n'
        (self.root / 'server.properties').write_bytes(self.original.encode('utf-8'))
        (self.root / '.cpt-server.json').write_text(json.dumps({'port': 25565, 'core': 'NeoForge'}))
        self.modules = patch.dict(sys.modules, {'server_properties': properties})
        self.modules.start()

    def tearDown(self):
        self.modules.stop()
        self.temporary.cleanup()

    def test_agent_patch_save_backups_original_and_updates_port_without_renaming_keys(self):
        before = agent.manage(self.root, {'action': 'config-panel-read'})
        with patch.object(agent, 'require_stopped') as stopped:
            result = agent.manage(self.root, {'action': 'config-panel-save', 'revision': before['revision'], 'changes': {'online-mode': 'false', 'server-port': '25566'}, 'confirmOffline': True})
        stopped.assert_called_once()
        self.assertEqual(result['port'], 25566)
        self.assertEqual((self.root / 'server.properties.bak').read_bytes(), self.original.encode('utf-8'))
        saved = (self.root / 'server.properties').read_bytes().decode('utf-8')
        self.assertIn('online-mode=false\r\n', saved)
        self.assertIn('rcon.password=private\r\n', saved)
        self.assertNotIn('private', json.dumps(result))
        self.assertEqual(json.loads((self.root / '.cpt-server.json').read_text())['port'], 25566)

    def test_running_server_and_stale_file_prevent_writes(self):
        before = agent.manage(self.root, {'action': 'config-panel-read'})
        request = {'action': 'config-panel-save', 'revision': before['revision'], 'changes': {'server-port': '25566'}}
        with patch.object(agent, 'require_stopped', side_effect=ValueError('running')):
            with self.assertRaisesRegex(ValueError, 'running'):
                agent.manage(self.root, request)
        self.assertFalse((self.root / 'server.properties.bak').exists())

        changed = self.original + 'pvp=false\r\n'
        (self.root / 'server.properties').write_bytes(changed.encode('utf-8'))
        with patch.object(agent, 'require_stopped'):
            with self.assertRaisesRegex(ValueError, '其他操作修改'):
                agent.manage(self.root, request)
        self.assertEqual((self.root / 'server.properties').read_bytes(), changed.encode('utf-8'))
        self.assertFalse((self.root / 'server.properties.bak').exists())

    def prepare_runtime(self, port=25565):
        settings_path = self.root / '.cpt-server.json'
        settings = json.loads(settings_path.read_text())
        settings.update(core='Vanilla', version='1.21.1', java='java', memory=4096, artifacts=[])
        settings_path.write_text(json.dumps(settings))
        (self.root / 'mods').mkdir(exist_ok=True)
        (self.root / 'plugins').mkdir(exist_ok=True)
        directory = '.cpt-runtime-' + 'a' * 12
        (self.root / directory).mkdir(exist_ok=True)
        queue = {'resources': [], 'runtime': {'directory': directory, 'settings': {**settings, 'port': port, 'memory': 8192}}}
        (self.root / '.cpt-pending.json').write_text(json.dumps(queue))

    def restart(self):
        process = Mock(pid=12345)
        process.poll.return_value = None
        with patch.object(agent, 'stop_wait'), patch.object(agent, 'require_stopped'), patch.object(agent, 'build_client_pack'), patch.object(agent, 'check_game_binding', side_effect=lambda root: int(properties.values(agent.read_properties(root))['server-port'])), patch.object(agent.subprocess, 'Popen', return_value=process), patch.object(agent.time, 'sleep'):
            result = agent.manage(self.root, {'action': 'restart'})
        self.assertEqual(result['state'], 'starting')
        self.assertEqual(result['settings']['port'], int(properties.values(agent.read_properties(self.root))['server-port']))

    def test_saved_port_and_offline_mode_survive_runtime_activation_and_restart(self):
        self.prepare_runtime()
        before = agent.manage(self.root, {'action': 'config-panel-read'})
        with patch.object(agent, 'require_stopped'):
            saved = agent.manage(self.root, {'action': 'config-panel-save', 'revision': before['revision'], 'changes': {'server-port': '25566', 'online-mode': 'false'}, 'confirmOffline': True})
        contents = (self.root / 'server.properties').read_bytes()
        self.assertEqual(agent.pending(self.root)['runtime']['settings']['port'], 25566)
        self.restart()
        self.assertEqual((self.root / 'server.properties').read_bytes(), contents)
        self.assertEqual(json.loads((self.root / '.cpt-server.json').read_text())['port'], 25566)
        self.assertEqual(json.loads((self.root / '.cpt-server.json').read_text())['memory'], 8192)
        reread = agent.manage(self.root, {'action': 'config-panel-read'})
        self.assertEqual(reread['revision'], saved['revision'])
        self.assertEqual({field['key']: field['value'] for field in reread['fields']}['online-mode'], 'false')
        self.assertEqual(agent.pending(self.root), {'resources': []})
        self.restart()
        self.assertEqual((self.root / 'server.properties').read_bytes(), contents)

    def test_saved_values_are_loaded_by_a_new_agent_process(self):
        before = agent.manage(self.root, {'action': 'config-panel-read'})
        with patch.object(agent, 'require_stopped'):
            saved = agent.manage(self.root, {'action': 'config-panel-save', 'revision': before['revision'], 'changes': {'server-port': '25566', 'online-mode': 'false', 'motd': '重启保留'}, 'confirmOffline': True})
        request = base64.b64encode(json.dumps({'root': str(self.root), 'action': 'config-panel-read'}).encode()).decode()
        for _ in range(2):
            process = subprocess.run([sys.executable, '-B', str(scripts / 'agent.py'), request], capture_output=True, text=True, timeout=20)
            self.assertEqual(process.returncode, 0, process.stdout + process.stderr)
            result = json.loads(process.stdout)
            self.assertTrue(result['ok'])
            self.assertEqual(result['result']['revision'], saved['revision'])
            fields = {field['key']: field['value'] for field in result['result']['fields']}
            self.assertEqual(fields['server-port'], '25566')
            self.assertEqual(fields['online-mode'], 'false')
            self.assertEqual(fields['motd'], '重启保留')

    def test_unconfirmed_offline_save_does_not_write_files_or_backup(self):
        before = agent.manage(self.root, {'action': 'config-panel-read'})
        originals = {name: (self.root / name).read_bytes() for name in ('server.properties', '.cpt-server.json')}
        with patch.object(agent, 'require_stopped'):
            with self.assertRaisesRegex(ValueError, '单独确认'):
                agent.manage(self.root, {'action': 'config-panel-save', 'revision': before['revision'], 'changes': {'online-mode': 'false'}})
        for name, contents in originals.items():
            self.assertEqual((self.root / name).read_bytes(), contents)
        self.assertFalse((self.root / 'server.properties.bak').exists())

    def restart_request(self):
        return {'action': 'config-panel-save-restart', 'changes': {'online-mode': 'false'},
                'revision': properties.revision(self.original), 'confirmOffline': True}

    def test_restart_saves_and_verifies_offline_setting_before_launch(self):
        calls = []

        def launch(root, apply_queue):
            self.assertFalse(apply_queue)
            text = agent.read_properties(root)
            self.assertEqual(properties.values(text)['online-mode'], 'false')
            self.assertEqual(text.replace('online-mode=false', 'online-mode=true'), self.original)
            self.assertEqual((root / 'server.properties.bak').read_bytes(), self.original.encode())
            calls.append('start')
            return {'state': 'starting', 'settings': {'port': 25565}}

        with patch.object(agent, 'stop_wait', side_effect=lambda root: calls.append('stop')), patch.object(agent, 'require_stopped'), patch.object(agent, 'apply_pending', side_effect=lambda root: calls.append('apply')), patch.object(agent, 'start', side_effect=launch):
            result = agent.manage(self.root, self.restart_request())
        self.assertEqual(calls, ['stop', 'apply', 'start'])
        self.assertEqual({field['key']: field['value'] for field in result['fields']}['online-mode'], 'false')

    def test_restart_validation_stop_and_save_failures_do_not_launch(self):
        request = self.restart_request()
        for invalid in ({'confirmOffline': False}, {'revision': 'stale'}, {'changes': {'rcon.password': 'new'}}):
            with patch.object(agent, 'stop_wait') as stop, patch.object(agent, 'start') as launch:
                with self.assertRaises(ValueError):
                    agent.manage(self.root, {**request, **invalid})
                stop.assert_not_called()
                launch.assert_not_called()
        for method in ('stop_wait', 'save_config'):
            with patch.object(agent, 'stop_wait'), patch.object(agent, 'require_stopped'), patch.object(agent, method, side_effect=OSError('failure')), patch.object(agent, 'apply_pending') as apply, patch.object(agent, 'start') as launch:
                with self.assertRaisesRegex(OSError, 'failure'):
                    agent.manage(self.root, request)
                apply.assert_not_called()
                launch.assert_not_called()
        self.assertEqual(agent.read_properties(self.root), self.original)

    def test_shutdown_reformat_preserves_other_settings_but_conflicting_edit_blocks_start(self):
        request = self.restart_request()
        changed = '# shutdown comment\n' + self.original.replace('\r\n', '\n') + 'pvp=false\n'

        def stop(root):
            (root / 'server.properties').write_bytes(changed.encode())

        with patch.object(agent, 'stop_wait', side_effect=stop), patch.object(agent, 'require_stopped'), patch.object(agent, 'apply_pending'), patch.object(agent, 'start', return_value={'settings': {'port': 25565}, 'state': 'starting'}):
            agent.manage(self.root, request)
        self.assertEqual(agent.read_properties(self.root), changed.replace('online-mode=true', 'online-mode=false'))
        (self.root / 'server.properties').write_bytes(self.original.encode())
        changed = self.original.replace('online-mode=true', 'online-mode=false')
        with patch.object(agent, 'stop_wait', side_effect=stop), patch.object(agent, 'start') as launch:
            with self.assertRaisesRegex(ValueError, '发生变化'):
                agent.manage(self.root, request)
            launch.assert_not_called()

    def test_save_readback_failure_restores_original_and_does_not_report_success(self):
        replace = agent.os.replace

        def drop_properties(source, target):
            if target.name == 'server.properties':
                source.unlink()
            else:
                replace(source, target)

        with patch.object(agent, 'require_stopped'), patch.object(agent.os, 'replace', side_effect=drop_properties):
            with self.assertRaisesRegex(ValueError, '校验失败'):
                agent.manage(self.root, {**self.restart_request(), 'action': 'config-panel-save'})
        self.assertEqual(agent.read_properties(self.root), self.original)
        self.assertFalse((self.root / 'server.properties.bak').exists())

    def test_unrelated_save_keeps_intentionally_prepared_port_and_preserves_other_lines(self):
        self.prepare_runtime(port=25567)
        before = agent.manage(self.root, {'action': 'config-panel-read'})
        with patch.object(agent, 'require_stopped'):
            agent.manage(self.root, {'action': 'config-panel-save', 'revision': before['revision'], 'changes': {'online-mode': 'false'}, 'confirmOffline': True})
        contents = (self.root / 'server.properties').read_bytes()
        self.assertEqual(agent.pending(self.root)['runtime']['settings']['port'], 25567)
        self.restart()
        self.assertEqual((self.root / 'server.properties').read_bytes(), contents.replace(b'server-port=25565', b'server-port=25567'))

    def test_raw_port_save_also_updates_prepared_runtime(self):
        self.prepare_runtime()
        with patch.object(agent, 'require_stopped'):
            agent.manage(self.root, {'action': 'config-save', 'text': self.original.replace('server-port=25565', 'server-port=25568')})
        contents = (self.root / 'server.properties').read_bytes()
        self.restart()
        self.assertEqual((self.root / 'server.properties').read_bytes(), contents)
        self.assertEqual(json.loads((self.root / '.cpt-server.json').read_text())['port'], 25568)

    def test_write_failure_rolls_back_configuration_metadata_queue_and_backup(self):
        for failing in ('.cpt-server.json', '.cpt-pending.json'):
            for existing_backup in (False, True):
                with self.subTest(failing=failing, existing_backup=existing_backup):
                    self.prepare_runtime()
                    backup = self.root / 'server.properties.bak'
                    if existing_backup:
                        backup.write_bytes(b'previous backup')
                    else:
                        backup.unlink(missing_ok=True)
                    names = ['server.properties', '.cpt-server.json', '.cpt-pending.json']
                    originals = {name: (self.root / name).read_bytes() for name in names}
                    write_atomic = agent.write_atomic

                    def fail_write(path, data):
                        if path.name == failing:
                            raise OSError('disk failure')
                        write_atomic(path, data)

                    before = agent.manage(self.root, {'action': 'config-panel-read'})
                    with patch.object(agent, 'require_stopped'), patch.object(agent, 'write_atomic', side_effect=fail_write):
                        with self.assertRaisesRegex(OSError, 'disk failure'):
                            agent.manage(self.root, {'action': 'config-panel-save', 'revision': before['revision'], 'changes': {'server-port': '25566'}})
                    for name, data in originals.items():
                        self.assertEqual((self.root / name).read_bytes(), data)
                    if existing_backup:
                        self.assertEqual(backup.read_bytes(), b'previous backup')
                    else:
                        self.assertFalse(backup.exists())


if __name__ == '__main__':
    if len(sys.argv) > 1 and sys.argv[1] == '--fixture':
        print(json.dumps(properties.panel('# fixture\r\nmotd=\\u4e2d\\u6587\\u670d\\u52a1\\u5668\r\nserver-port=25566\r\nserver-ip=\r\nonline-mode=true\r\nmax-players=20\r\ndifficulty=easy\r\ngamemode=survival\r\nrcon.port=25575\r\nrcon.password=fixture-secret\r\nenable-rcon=true\r\ncustom.mod.setting=preserve\r\n'), ensure_ascii=True))
    else:
        unittest.main()
