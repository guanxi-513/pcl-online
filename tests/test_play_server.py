import json
from pathlib import Path
import socket
import struct
import subprocess
import threading
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]


def exact(stream, size):
    value = b''
    while len(value) < size:
        chunk = stream.recv(size - len(value))
        if not chunk:
            raise EOFError()
        value += chunk
    return value


def varint(stream):
    value = 0
    for index in range(5):
        byte = exact(stream, 1)[0]
        value |= (byte & 127) << (7 * index)
        if byte < 128:
            return value
    raise ValueError('Invalid VarInt')


def encode(value):
    result = bytearray()
    while value >= 128:
        result.append((value & 127) | 128)
        value >>= 7
    result.append(value)
    return bytes(result)


class PlayServerTests(unittest.TestCase):
    @unittest.skipUnless((ROOT / 'output/CPT-Status-Users/CPT.exe').exists(), 'Build multiplayer launcher first')
    def test_offline_status(self):
        listener = socket.socket()
        listener.bind(('127.0.0.1', 0))
        port = listener.getsockname()[1]
        listener.close()
        command = f"& ([ScriptBlock]::Create((Get-Content -LiteralPath '{ROOT.as_posix()}/tests/play-server-query.ps1' -Encoding UTF8 -Raw))) -Root '{ROOT.as_posix()}' -Port {port}"
        result = subprocess.run(['C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe', '-NoProfile', '-STA', '-Command', command], capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=12)
        self.assertEqual(result.returncode, 0, result.stderr)
        data = json.loads(next(line for line in reversed(result.stdout.splitlines()) if line.startswith('{')))
        self.assertFalse(data['online'])

    @unittest.skipUnless((ROOT / 'output/CPT-Status-Users/CPT.exe').exists(), 'Build multiplayer launcher first')
    def test_status_protocol(self):
        listener = socket.socket()
        listener.bind(('127.0.0.1', 0))
        listener.listen()
        listener.settimeout(15)
        port = listener.getsockname()[1]
        errors = []

        def peer():
            try:
                connection, _ = listener.accept()
                with connection:
                    connection.settimeout(5)
                    handshake = exact(connection, varint(connection))
                    self.assertNotIn(b'\xff\xff\xff\xff\x0f', handshake)
                    exact(connection, varint(connection))
                    data = json.dumps({'version': {'name': '1.21.1', 'protocol': 767},
                                       'players': {'max': 20, 'online': 3, 'sample': []},
                                       'description': {'text': 'Local protocol test'}}).encode()
                    packet = b'\0' + encode(len(data)) + data
                    response = encode(len(packet)) + packet
                    for offset in range(0, len(response), 7):
                        connection.sendall(response[offset:offset + 7])
                        time.sleep(0.002)
                    try:
                        ping = exact(connection, varint(connection))
                        connection.sendall(encode(len(ping)) + ping)
                    except (EOFError, socket.timeout):
                        pass
            except BaseException as error:
                errors.append(error)
            finally:
                listener.close()

        worker = threading.Thread(target=peer, daemon=True)
        worker.start()
        command = f"& ([ScriptBlock]::Create((Get-Content -LiteralPath '{ROOT.as_posix()}/tests/play-server-query.ps1' -Encoding UTF8 -Raw))) -Root '{ROOT.as_posix()}' -Port {port}"
        result = subprocess.run(['C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe', '-NoProfile', '-STA', '-Command', command], capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=20)
        worker.join(3)
        self.assertEqual(result.returncode, 0, result.stderr)
        raw = next(line for line in reversed(result.stdout.splitlines()) if line.startswith('{'))
        data = json.loads(raw)
        self.assertTrue(data['online'], data)
        self.assertEqual(data['version'], '1.21.1')
        self.assertEqual(data['players'], 3)
        self.assertEqual(data['maximum'], 20)
        self.assertGreaterEqual(data['latency'], 0)
        if errors:
            raise errors[0]

    def query(self, port, address='', dns_port=0):
        command = f"& ([ScriptBlock]::Create((Get-Content -LiteralPath '{ROOT.as_posix()}/tests/play-server-query.ps1' -Encoding UTF8 -Raw))) -Root '{ROOT.as_posix()}' -Port {port}"
        if address:
            command += f" -Address '{address}'"
        if dns_port:
            command += f' -DnsPort {dns_port}'
        result = subprocess.run(['C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe', '-NoProfile', '-STA', '-Command', command], capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=25)
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(next(line for line in reversed(result.stdout.splitlines()) if line.startswith('{')))

    @unittest.skipUnless((ROOT / 'output/CPT-Status-Users/CPT.exe').exists(), 'Build status launcher first')
    def test_reachable_status_disabled_is_not_marked_offline(self):
        listener = socket.socket()
        listener.bind(('127.0.0.1', 0))
        listener.listen()
        listener.settimeout(10)
        port = listener.getsockname()[1]
        def peer():
            try:
                for _ in range(2):
                    client, _ = listener.accept()
                    client.close()
            finally:
                listener.close()
        worker = threading.Thread(target=peer, daemon=True)
        worker.start()
        result = self.query(port)
        self.assertFalse(result['online'])
        self.assertTrue(result['reachable'])
        self.assertEqual(result['status'], 'status-unavailable')
        worker.join(2)

    @unittest.skipUnless((ROOT / 'output/CPT-Status-Users/CPT.exe').exists(), 'Build status launcher first')
    def test_srv_target_keeps_original_host_in_handshake_and_hidden_players(self):
        listener = socket.socket()
        listener.bind(('127.0.0.1', 0)); listener.listen(); listener.settimeout(12)
        port = listener.getsockname()[1]
        dns = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        dns.bind(('127.0.0.1', 0)); dns.settimeout(12)
        dns_port = dns.getsockname()[1]
        errors = []
        def dns_peer():
            try:
                data, sender = dns.recvfrom(2048)
                end = 12
                while data[end]:
                    end += data[end] + 1
                end += 5
                rdata = struct.pack('!HHH', 0, 10, port) + b'\x09127.0.0.1\0'
                response = data[:2] + struct.pack('!HHHHH', 0x8180, 1, 1, 0, 0) + data[12:end] + b'\xc0\x0c' + struct.pack('!HHIH', 33, 1, 60, len(rdata)) + rdata
                dns.sendto(response, sender)
            except BaseException as error:
                errors.append(error)
            finally:
                dns.close()
        def game_peer():
            try:
                connection, _ = listener.accept()
                with connection:
                    handshake = exact(connection, varint(connection))
                    self.assertIn(b'original.example.invalid', handshake)
                    exact(connection, varint(connection))
                    payload = json.dumps({'version': {'name': 'hidden-player server'}, 'description': 'Hello'}).encode()
                    packet = b'\0' + encode(len(payload)) + payload
                    connection.sendall(encode(len(packet)) + packet)
            except BaseException as error:
                errors.append(error)
            finally:
                listener.close()
        dns_worker = threading.Thread(target=dns_peer, daemon=True)
        game_worker = threading.Thread(target=game_peer, daemon=True)
        dns_worker.start(); game_worker.start()
        result = self.query(port, 'original.example.invalid', dns_port)
        dns_worker.join(2); game_worker.join(2)
        if errors:
            raise errors[0]
        self.assertTrue(result['online'], result)
        self.assertIsNone(result['players'])
        self.assertIsNone(result['maximum'])
