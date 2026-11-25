"""Local SSH/SFTP fixture for live event delivery; never executes remote commands."""
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import tempfile
import threading
import time
import unittest

import paramiko


class DeploymentStreamTest(unittest.TestCase):
    def test_events_arrive_during_command(self):
        self.scenario('stream')

    def test_resume_installed_running_server_without_duplicate_start(self):
        self.scenario('resume')

    def scenario(self, scenario):
        errors = []
        actions = []
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            (directory / 'home/fixture').mkdir(parents=True)
            def resolve(path):
                target = (directory / path.lstrip('/')).resolve()
                if directory not in target.parents:
                    raise ValueError('Fixture path escape')
                return target

            class SFTP(paramiko.SFTPServerInterface):
                def canonicalize(self, path):
                    return '/home/fixture' if path == '.' else path

                def stat(self, path):
                    try:
                        return paramiko.SFTPAttributes.from_stat(resolve(path).stat())
                    except FileNotFoundError:
                        return paramiko.SFTP_NO_SUCH_FILE

                lstat = stat

                def open(self, path, flags, attr):
                    target = resolve(path)
                    mode = 'w+b' if flags & os.O_TRUNC else 'r+b' if flags & os.O_RDWR or flags & os.O_WRONLY else 'rb'
                    if flags & os.O_CREAT and not target.exists():
                        target.touch()
                    stream = target.open(mode)
                    handle = paramiko.SFTPHandle(flags)
                    handle.readfile = stream
                    handle.writefile = stream
                    return handle

                def chattr(self, path, attr):
                    return paramiko.SFTP_OK

                def rename(self, old, new):
                    resolve(old).rename(resolve(new))
                    return paramiko.SFTP_OK

                def remove(self, path):
                    resolve(path).unlink()
                    return paramiko.SFTP_OK

            class Server(paramiko.ServerInterface):
                def check_auth_password(self, username, password):
                    return paramiko.AUTH_SUCCESSFUL

                def check_channel_request(self, kind, channel_id):
                    return paramiko.OPEN_SUCCEEDED

                def check_channel_exec_request(self, channel, command):
                    def reply():
                        try:
                            text = command.decode()
                            if text.startswith('mkdir '):
                                (directory / 'home/fixture/.cpt-server').mkdir(exist_ok=True)
                                output = ''
                            elif text == 'id -u':
                                output = '0'
                            elif text == 'uname -s':
                                output = 'Linux'
                            elif text.startswith('bash '):
                                output = ''
                            elif 'python3 ' in text:
                                request = resolve('/home/fixture/.cpt-server/' + re.search(r"request-[0-9a-f]{32}\.json", text)[0])
                                args = json.loads(request.read_text())
                                actions.append(args['action'])
                                if scenario == 'resume':
                                    if args['action'] == 'deploy-resume':
                                        value = {'java': '/fixture/java', 'core': 'NeoForge', 'version': '1.21.1', 'memory': 2048, 'port': 25565, 'loaderVersion': '21.1.255', 'resumedInstalled': True}
                                    elif args['action'] == 'status':
                                        value = {'state': 'running', 'online': True}
                                    elif args['action'] == 'client-pack':
                                        value = {'package': 'fixture'}
                                    else:
                                        raise ValueError('Installed resume unexpectedly executed ' + args['action'])
                                    output = json.dumps({'ok': True, 'result': value})
                                    channel.sendall(output.encode());channel.send_exit_status(0);channel.close();return
                                events = request.parent / args['progressFile']
                                with events.open('a') as sink:
                                    for event in [{'kind': 'detail', 'message': 'fixture installing'}, {'kind': 'download', 'message': 'server.jar', 'bytes': 50, 'total': 100}, {'kind': 'terminal', 'message': 'installer output'}]:
                                        sink.write(json.dumps(event) + '\n'); sink.flush(); time.sleep(0.4)
                                        if event['kind'] == 'download':
                                            marker = request.parent / (args['controlFile'] + '.pause')
                                            until = time.monotonic() + 5
                                            while not marker.exists() and time.monotonic() < until:
                                                time.sleep(0.05)
                                            if not marker.exists():
                                                raise ValueError('Pause marker not delivered')
                                            sink.write(json.dumps({'kind': 'paused', 'message': 'paused'}) + '\n');sink.flush()
                                            while marker.exists() and time.monotonic() < until:
                                                time.sleep(0.05)
                                            if marker.exists():
                                                raise ValueError('Resume marker not removed')
                                            sink.write(json.dumps({'kind': 'resumed', 'message': 'resumed'}) + '\n');sink.flush()
                                time.sleep(0.6)
                                output = json.dumps({'ok': True, 'result': {'message': 'fixture complete'}})
                            else:
                                raise ValueError('Unexpected fixture command')
                            channel.sendall(output.encode())
                            channel.send_exit_status(0); channel.close()
                        except Exception as error:
                            errors.append(error)
                            channel.send_exit_status(1); channel.close()
                    threading.Thread(target=reply, daemon=True).start()
                    return True

            key = paramiko.RSAKey.generate(2048)
            fingerprint = 'SHA256:' + base64.b64encode(hashlib.sha256(key.asbytes()).digest()).decode().rstrip('=')
            listener = socket.socket(); listener.bind(('127.0.0.1', 0)); listener.listen(); listener.settimeout(0.2)
            port = listener.getsockname()[1]
            stop = threading.Event(); transports = []
            def serve():
                while not stop.is_set():
                    try:
                        peer, _ = listener.accept()
                    except socket.timeout:
                        continue
                    except OSError:
                        return
                    try:
                        transport = paramiko.Transport(peer); transports.append(transport)
                        transport.add_server_key(key)
                        transport.set_subsystem_handler('sftp', paramiko.SFTPServer, SFTP)
                        transport.start_server(server=Server())
                    except Exception as error:
                        errors.append(error)
            thread = threading.Thread(target=serve, daemon=True); thread.start()
            try:
                root = Path(__file__).resolve().parents[1]
                result = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(root / 'tests/deployment-stream-client.ps1'), '-Root', str(root), '-Port', str(port), '-Fingerprint', fingerprint, '-Scenario', scenario], capture_output=True, timeout=30)
                self.assertEqual(result.returncode, 0, result.stdout.decode(errors='replace') + result.stderr.decode(errors='replace'))
                self.assertEqual(errors, [])
                self.assertFalse(list((directory / 'home/fixture/.cpt-server').glob('events-*')))
                self.assertFalse(list((directory / 'home/fixture/.cpt-server').glob('control-*')))
                if scenario == 'resume':
                    self.assertNotIn('start', actions)
                    self.assertEqual(actions[0], 'deploy-resume')
            finally:
                stop.set(); listener.close()
                for transport in transports:
                    transport.close()
                thread.join(2)


if __name__ == '__main__':
    unittest.main()
