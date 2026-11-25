import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('cpt_network', Path(__file__).parents[1] / 'PCLCS/Server/Scripts/network.py')
network = importlib.util.module_from_spec(spec)
spec.loader.exec_module(network)


class NetworkTests(unittest.TestCase):
    def diagnostic(self, mode='resolved', ready=False, repairable=True):
        return {'ready': ready, 'canRepair': repairable, 'state': 'ok' if ready else 'dns',
                'message': 'test', 'method': mode, 'adapter': 'eth0'}

    def test_healthy_does_not_probe_public_ips_or_modify_network(self):
        with patch.object(network, 'adapter', return_value={'route': True, 'mode': 'resolved', 'name': 'eth0'}), patch.object(network, 'dns_probe', return_value=True), patch.object(network, 'https_probe', return_value='ok'), patch.object(network, 'internet_probe') as internet:
            report = network.check({'core': 'Paper'})
        self.assertTrue(report['ready'])
        self.assertFalse(report['canRepair'])
        self.assertEqual([item['host'] for item in report['checks']], ['piston-meta.mojang.com', 'api.adoptium.net', 'fill.papermc.io'])
        internet.assert_not_called()

    def test_dns_fault_only_repairable_with_route_and_public_connectivity(self):
        with patch.object(network, 'adapter', return_value={'route': True, 'mode': 'resolved', 'name': 'eth0'}), patch.object(network, 'dns_probe', return_value=False), patch.object(network, 'internet_probe', return_value=True):
            self.assertTrue(network.check({'core': 'Vanilla'})['canRepair'])
        for route, mode, public in [(False, 'resolved', True), (True, 'unsupported', True), (True, 'resolved', False)]:
            with patch.object(network, 'adapter', return_value={'route': route, 'mode': mode, 'name': 'eth0'}), patch.object(network, 'dns_probe', return_value=False), patch.object(network, 'internet_probe', return_value=public):
                self.assertFalse(network.check({'core': 'Vanilla'})['canRepair'])

    def test_tls_failure_not_offered_dns_repair(self):
        with patch.object(network, 'adapter', return_value={'route': True, 'mode': 'resolved', 'name': 'eth0'}), patch.object(network, 'dns_probe', return_value=True), patch.object(network, 'https_probe', return_value='tls'):
            report = network.check({'core': 'Vanilla'})
        self.assertFalse(report['ready'])
        self.assertFalse(report['canRepair'])
        self.assertEqual(report['state'], 'tls')

    def test_unknown_network_manager_does_not_block_healthy_downloads(self):
        with patch.object(network, 'adapter', side_effect=RuntimeError('missing tool')), patch.object(network, 'dns_probe', return_value=True), patch.object(network, 'https_probe', return_value='ok'):
            result = network.check({'core': 'Paper'})
        self.assertTrue(result['ready'])
        self.assertFalse(result['canRepair'])
        self.assertEqual(result['method'], 'unsupported')

    def test_command_keeps_utf8_adapter_names(self):
        from subprocess import CompletedProcess
        payload = '{"name":"以太网"}'
        with patch.object(network.subprocess, 'run', return_value=CompletedProcess(['test'], 0, stdout=payload.encode('utf-8'), stderr=b'')):
            self.assertEqual(network.command(['test']), payload)

    def test_confirmation_required_before_any_check_or_write(self):
        with patch.object(network, 'check') as check, patch.object(network, 'set_dns') as update:
            with self.assertRaisesRegex(ValueError, '明确确认'):
                network.repair({'core': 'Vanilla'})
            check.assert_not_called()
            update.assert_not_called()

    def test_unsupported_and_already_healthy_do_not_change_dns(self):
        for ready in (False, True):
            with patch.object(network, 'check', return_value=self.diagnostic(ready=ready, repairable=False)), patch.object(network, 'set_dns') as update, patch.object(network, 'backup_directory') as backup:
                result = network.repair({'core': 'Vanilla', 'confirmed': True})
                self.assertFalse(result['changed'])
                update.assert_not_called()
                backup.assert_not_called()

    def test_resolved_success_backups_and_retests(self):
        with tempfile.TemporaryDirectory() as folder:
            config = {'route': True, 'mode': 'resolved', 'name': 'eth0', 'dns': ['192.168.1.1']}
            with patch.object(network, 'check', side_effect=[self.diagnostic(), self.diagnostic(ready=True)]), patch.object(network, 'adapter', return_value=config), patch.object(network, 'backup_directory', return_value=Path(folder)), patch.object(network, 'set_dns') as update:
                result = network.repair({'core': 'Vanilla', 'confirmed': True})
            self.assertTrue(result['ready'])
            self.assertTrue(result['changed'])
            self.assertEqual(json.loads(Path(result['backup']).read_text())['dns'], ['192.168.1.1'])
            update.assert_called_once_with(config, network.PUBLIC_DNS)

    def test_failed_retest_restores_original_resolved_dns(self):
        with tempfile.TemporaryDirectory() as folder:
            config = {'mode': 'resolved', 'name': 'eth0', 'dns': ['192.168.1.1']}
            with patch.object(network, 'check', side_effect=[self.diagnostic(), self.diagnostic()]), patch.object(network, 'adapter', return_value=config), patch.object(network, 'backup_directory', return_value=Path(folder)), patch.object(network, 'set_dns') as update:
                result = network.repair({'confirmed': True})
            self.assertFalse(result['ready'])
            self.assertFalse(result['changed'])
            self.assertEqual(update.call_args_list[1].args[1], ['192.168.1.1'])
            self.assertIn('回退', result['message'])

    def test_windows_automatic_dns_restored_as_dhcp_after_failure(self):
        with tempfile.TemporaryDirectory() as folder:
            config = {'mode': 'windows', 'name': 'eth0', 'index': 4, 'dns': ['192.168.1.1'], 'automatic': True}
            with patch.object(network, 'check', side_effect=[self.diagnostic('windows'), self.diagnostic('windows')]), patch.object(network, 'adapter', return_value=config), patch.object(network, 'backup_directory', return_value=Path(folder)), patch.object(network, 'set_dns') as update:
                result = network.repair({'confirmed': True})
            self.assertFalse(result['ready'])
            self.assertTrue(update.call_args_list[1].kwargs['reset'])

    def test_windows_reset_does_not_require_static_addresses(self):
        with patch.object(network, 'powershell') as execute:
            network.set_dns({'mode': 'windows', 'index': 4}, [], reset=True)
        self.assertIn('-ResetServerAddresses', execute.call_args.args[0])
        self.assertNotIn('-ServerAddresses @(', execute.call_args.args[0])

    def test_resolved_empty_previous_dns_clears_override(self):
        with patch.object(network, 'command') as execute:
            network.set_dns({'mode': 'resolved', 'name': 'eth0'}, [''])
        self.assertEqual(execute.call_args_list[0].args[0], ['resolvectl', 'dns', 'eth0', ''])

    def test_plain_dns_file_requires_root_before_writing(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'resolv.conf'
            original = b'nameserver 192.168.1.1\n'
            path.write_bytes(original)
            with patch.object(network, 'RESOLV_CONF', path), patch.object(network.os, 'geteuid', return_value=1000, create=True), patch.object(network, 'check', return_value=self.diagnostic('resolv-file')), patch.object(network, 'adapter', return_value={'mode': 'resolv-file', 'name': 'eth0'}), patch.object(network, 'backup_directory', return_value=Path(folder)):
                with self.assertRaisesRegex(ValueError, 'root'):
                    network.repair({'confirmed': True})
            self.assertEqual(path.read_bytes(), original)

    def test_repair_permission_or_rollback_failure_is_reported(self):
        with tempfile.TemporaryDirectory() as folder:
            config = {'mode': 'resolved', 'name': 'eth0', 'dns': ['192.168.1.1']}
            with patch.object(network, 'check', return_value=self.diagnostic()), patch.object(network, 'adapter', return_value=config), patch.object(network, 'backup_directory', return_value=Path(folder)), patch.object(network, 'set_dns', side_effect=PermissionError()):
                result = network.repair({'confirmed': True})
            self.assertFalse(result['ready'])
            self.assertIn('回退也失败', result['message'])
            self.assertTrue(Path(result['backup']).exists())

    def test_plain_resolv_file_preserves_search_and_rolls_back(self):
        for healthy in (True, False):
            with tempfile.TemporaryDirectory() as folder:
                path = Path(folder) / 'resolv.conf'
                original = b'search internal.test\nnameserver 192.168.1.1\noptions timeout:2\n'
                path.write_bytes(original)
                config = {'mode': 'resolv-file', 'name': 'eth0'}
                with patch.object(network, 'RESOLV_CONF', path), patch.object(network.os, 'geteuid', return_value=0, create=True), patch.object(network, 'check', side_effect=[self.diagnostic('resolv-file'), self.diagnostic('resolv-file', ready=healthy)]), patch.object(network, 'adapter', return_value=config), patch.object(network, 'backup_directory', return_value=Path(folder)):
                    result = network.repair({'confirmed': True})
                self.assertEqual(result['ready'], healthy)
                if healthy:
                    self.assertIn(b'search internal.test', path.read_bytes())
                    self.assertIn(b'nameserver 223.5.5.5', path.read_bytes())
                    self.assertNotIn(b'nameserver 192.168.1.1', path.read_bytes())
                else:
                    self.assertEqual(path.read_bytes(), original)

    def test_adapter_change_stops_repair(self):
        with patch.object(network, 'check', return_value=self.diagnostic()), patch.object(network, 'adapter', return_value={'mode': 'resolved', 'name': 'different'}), patch.object(network, 'set_dns') as update:
            with self.assertRaisesRegex(ValueError, '配置已变化'):
                network.repair({'confirmed': True})
            update.assert_not_called()


if __name__ == '__main__':
    unittest.main()
