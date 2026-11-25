"""Exercise the SSH.NET root/PTY exchange against a local, non-executing server."""
import base64
import hashlib
from pathlib import Path
import re
import socket
import subprocess
import threading
import unittest

import paramiko


class SuSSHTests(unittest.TestCase):
    def scenario(self, mode):
        host_key = paramiko.RSAKey.generate(2048)
        fingerprint = 'SHA256:' + base64.b64encode(hashlib.sha256(host_key.asbytes()).digest()).decode().rstrip('=')
        listener = socket.socket()
        listener.bind(('127.0.0.1', 0))
        listener.listen()
        listener.settimeout(20)
        port = listener.getsockname()[1]
        errors, transcripts, terminals = [], [], []
        stop = threading.Event()

        class Server(paramiko.ServerInterface):
            def check_auth_password(self, username, password):
                return paramiko.AUTH_SUCCESSFUL if password == 'fixture-ssh-not-real' else paramiko.AUTH_FAILED

            def get_allowed_auths(self, username):
                return 'password'

            def check_channel_request(self, kind, channel_id):
                return paramiko.OPEN_SUCCEEDED

            def check_channel_pty_request(self, channel, term, width, height, px_width, px_height, modes):
                terminals.append(modes)
                return True

            def check_channel_exec_request(self, channel, command):
                def reply():
                    text = command.decode()
                    if text == 'uname -s':
                        channel.sendall(b'Linux\n')
                    elif text == 'id -u':
                        channel.sendall(b'0\n' if mode == 'direct' else b'1000\n')
                    elif text == 'sudo -n -u root -- id -u':
                        if mode in ('sudo-n', 'auto-n'):
                            channel.sendall(b'0\n')
                        else:
                            channel.send_exit_status(1);channel.close();return
                    else:
                        errors.append('Unexpected command: ' + text)
                    channel.send_exit_status(0)
                    channel.close()
                threading.Thread(target=reply, daemon=True).start()
                return True

            def check_channel_shell_request(self, channel):
                def dialogue():
                    try:
                        channel.settimeout(10)
                        def line():
                            data = b''
                            while b'\n' not in data and b'\r' not in data:
                                chunk = channel.recv(4096)
                                if not chunk:
                                    raise RuntimeError('Unexpected shell close')
                                data += chunk
                            return data.decode().strip()
                        command = line()
                        transcripts.append(command)
                        marker = re.search(r'CPT_[0-9a-f]{32}', command)[0]
                        if mode.startswith('sudo'):
                            self_outer.assertIn('sudo -S -k -p', command)
                            self_outer.assertIn('-u root -- /bin/sh -c', command)
                        else:
                            self_outer.assertIn('su - root -s /bin/sh -c', command)
                        self_outer.assertNotIn('fixture-root-not-real', command)
                        channel.sendall((marker + '_PASSWORD:').encode() if mode.startswith('sudo') else b'Password:')
                        credential = line()
                        self_outer.assertEqual(credential, 'fixture-root-not-real')
                        if mode in ('reject', 'sudo-reject'):
                            channel.sendall(('su: Authentication failure\r\n' + marker + '_END:AUTH\r\n').encode())
                        else:
                            channel.sendall(('su: login warning\r\n\x1b[32mroot banner\x1b[0m\r\n' + marker + '_BEGIN\r\n').encode())
                            value = '0' if 'fixture-command' not in command else '{"ok":true}'
                            encoded = base64.b64encode(value.encode()).decode()
                            if 'fixture-command' in command and mode == 'agent-error':
                                encoded = base64.b64encode(b'{"ok":false,"error":"fixture deployment blocked"}').decode()
                            if 'fixture-command' in command and mode == 'corrupt':
                                encoded = '/invalid raw terminal output'
                            status = 1 if 'fixture-command' in command and mode == 'agent-error' else 0
                            channel.sendall((encoded + '\r\n' + marker + '_END:' + str(status) + '\r\n').encode())
                        while channel.recv(1024):
                            pass
                    except Exception as error:
                        errors.append(error)
                threading.Thread(target=dialogue, daemon=True).start()
                return True

        self_outer = self
        transports = []
        def serve():
            try:
                while not stop.is_set():
                    try:
                        connection, _ = listener.accept()
                    except socket.timeout:
                        continue
                    transport = paramiko.Transport(connection)
                    transports.append(transport)
                    transport.add_server_key(host_key)
                    transport.start_server(server=Server())
            except OSError:
                if not stop.is_set():
                    errors.append('Listener failed')
            except Exception as error:
                errors.append(error)
        worker = threading.Thread(target=serve, daemon=True)
        worker.start()
        try:
            root = Path(__file__).resolve().parents[1]
            result = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(root / 'tests/su-shell-client.ps1'), '-Root', str(root), '-Port', str(port), '-Fingerprint', fingerprint, '-Scenario', mode], capture_output=True, timeout=35)
            self.assertEqual(result.returncode, 0, result.stdout.decode(errors='replace') + result.stderr.decode(errors='replace'))
            self.assertEqual(errors, [])
            if mode in ('direct', 'missing', 'sudo-n', 'auto-n', 'sudo-missing'):
                self.assertEqual(transcripts, [])
            else:
                self.assertTrue(terminals)
                # RFC 4254: ECHO opcode 53, value 0.
                self.assertTrue(all(b'\x35\x00\x00\x00\x00' in modes for modes in terminals))
        finally:
            stop.set()
            listener.close()
            for transport in transports:
                transport.close()
            worker.join(2)

    def test_root_direct(self):
        self.scenario('direct')

    def test_su_success(self):
        self.scenario('success')

    def test_su_rejected(self):
        self.scenario('reject')

    def test_nonroot_requires_separate_root_password(self):
        self.scenario('missing')

    def test_structured_deployment_error_is_preserved(self):
        self.scenario('agent-error')

    def test_corrupted_encoding_is_rejected(self):
        self.scenario('corrupt')

    def test_sudo_password_authentication(self):
        self.scenario('sudo-success')

    def test_sudo_password_rejected(self):
        self.scenario('sudo-reject')

    def test_sudo_without_password(self):
        self.scenario('sudo-n')

    def test_default_sudo_requests_user_password(self):
        self.scenario('sudo-missing')

    def test_auto_prefers_passwordless_sudo(self):
        self.scenario('auto-n')


if __name__ == '__main__':
    unittest.main()
