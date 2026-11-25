"""SSH deployment console: credentials are read from the terminal, never stored."""
import base64
import getpass
import hashlib
import json
from pathlib import Path
import sys
import paramiko

HOST = '103.116.247.92'
EXPECTED = 'SHA256:kmW3mKrISAdLHGHKTtLAJLWg1KvV0w1j+I2s56fvR1U'


class PinnedHost(paramiko.MissingHostKeyPolicy):
    def missing_host_key(self, client, hostname, key):
        digest = 'SHA256:' + base64.b64encode(hashlib.sha256(key.asbytes()).digest()).decode().rstrip('=')
        if digest != EXPECTED:
            raise RuntimeError('SSH host fingerprint changed: ' + digest)
        client.get_host_keys().add(hostname, key.get_name(), key)


client = paramiko.SSHClient()
client.set_missing_host_key_policy(PinnedHost())
try:
    password = getpass.getpass('SSH password: ')
    client.connect(HOST, username='root', password=password, port=22,
                   look_for_keys=False, allow_agent=False, timeout=15, auth_timeout=20,
                   disabled_algorithms={'keys': ['rsa-sha2-256', 'rsa-sha2-512', 'ssh-rsa', 'ecdsa-sha2-nistp256']})
    password = None
    print('CONNECTED', flush=True)
    for line in sys.stdin:
        try:
            request = json.loads(line)
            if request['action'] == 'close':
                break
            if request['action'] == 'exec':
                _, stdout, stderr = client.exec_command(request['command'], timeout=request.get('timeout', 900))
                output = stdout.read().decode('utf-8', 'replace')
                errors = stderr.read().decode('utf-8', 'replace')
                status = stdout.channel.recv_exit_status()
                print(json.dumps({'status': status, 'stdout': output, 'stderr': errors}), flush=True)
            elif request['action'] == 'upload':
                with client.open_sftp() as sftp:
                    sftp.put(str(Path(request['local']).resolve()), request['remote'])
                    sftp.chmod(request['remote'], request.get('mode', 0o644))
                print(json.dumps({'uploaded': request['remote']}), flush=True)
            elif request['action'] == 'download':
                with client.open_sftp() as sftp:
                    sftp.get(request['remote'], str(Path(request['local']).resolve()))
                print(json.dumps({'downloaded': request['local']}), flush=True)
        except Exception as error:
            print(json.dumps({'error': str(error)}), flush=True)
finally:
    client.close()
