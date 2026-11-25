"""Private SSH-only server manager. No HTTP listener; JSON in, JSON out."""
import base64
import errno
from contextlib import contextmanager
import hashlib
import io
import ipaddress
import math
import json
import os
from pathlib import Path, PurePosixPath
import re
import secrets
import shutil
import socket
import ssl
import struct
import subprocess
import sys
import tarfile
import time
import threading
from concurrent.futures import ThreadPoolExecutor
import urllib.parse
import urllib.error
import urllib.request
import zipfile
import xml.etree.ElementTree as ET

USER_AGENT = 'CPT-ServerManager/1.0 (SSH deployment)'
NETWORK_ATTEMPTS = 3
PROGRESS_FILE = None
PROGRESS_LOCK = threading.Lock()
CONTROL_FILE = None
MIRROR_BASE = 'https://bmclapi2.bangbang93.com'
DOWNLOAD_CACHE_ENABLED = False


class SourceDownloadError(ValueError):
    pass


class DownloadIntegrityError(ValueError):
    pass


def download_candidates(url, digest):
    parsed = urllib.parse.urlparse(url)
    if not digest or parsed.scheme != 'https' or parsed.username or parsed.password or parsed.query or parsed.fragment:
        return [url]
    mirrors = []
    if parsed.hostname in ('piston-data.mojang.com', 'launcher.mojang.com') and re.fullmatch(r'/v1/objects/[0-9a-f]{40}/(?:server\.jar|server\.txt)', parsed.path):
        mirrors.append(MIRROR_BASE + parsed.path)
    elif parsed.hostname == 'maven.minecraftforge.net' and parsed.path.startswith('/'):
        mirrors.append(MIRROR_BASE + '/maven' + parsed.path)
    elif parsed.hostname == 'maven.neoforged.net' and parsed.path.startswith('/releases/'):
        mirrors.append(MIRROR_BASE + '/maven' + parsed.path[len('/releases'):])
    elif parsed.hostname == 'libraries.minecraft.net':
        mirrors.append(MIRROR_BASE + '/maven' + parsed.path)
    elif parsed.hostname == 'github.com' and parsed.path.startswith('/adoptium/'):
        match = re.fullmatch(r'/adoptium/temurin(8|11|16|17|21|25)-binaries/releases/download/[^/]+/(OpenJDK[^/]+)', parsed.path)
        if match:
            filename = urllib.parse.unquote(match[2])
            package = re.fullmatch(r'OpenJDK\d+U-jre_(x64|aarch64)_(linux|windows)_hotspot_[A-Za-z0-9_.+-]+\.(tar\.gz|zip)', filename)
            if package:
                mirrors.append(f'https://mirrors.tuna.tsinghua.edu.cn/Adoptium/{match[1]}/jre/{package[1]}/{package[2]}/' + urllib.parse.quote(filename, safe='._-'))
    return [*mirrors, url]


PROBE_SCRIPT = r'''
import json,sys,time,urllib.request,urllib.parse
url=sys.argv[1]
start=time.monotonic()
request=urllib.request.Request(url,headers={'User-Agent':'CPT-ServerManager/1.0','Range':'bytes=0-131071'})
with urllib.request.urlopen(request,timeout=3) as response:
    if urllib.parse.urlparse(response.url).scheme!='https': raise ValueError('Insecure redirect')
    if 'text/html' in response.headers.get('Content-Type','').lower(): raise ValueError('HTML response')
    data=response.read(131072)
    if not data: raise ValueError('Empty response')
elapsed=max(0.001,time.monotonic()-start)
print(json.dumps({'speed':len(data)/elapsed,'elapsed':elapsed}))
'''


def probe_download_source(url):
    # A subprocess bounds DNS lookup as well as socket/read time on every OS.
    try:
        result = subprocess.run([sys.executable, '-c', PROBE_SCRIPT, url], capture_output=True, text=True, timeout=5)
        if result.returncode:
            return None
        score = json.loads(result.stdout)
        speed = float(score['speed'])
        return speed if speed > 0 and math.isfinite(speed) else None
    except (OSError, subprocess.SubprocessError, ValueError, KeyError, TypeError):
        return None


def rank_download_sources(candidates):
    if len(candidates) < 2:
        return candidates
    deployment_checkpoint()
    progress_event('detail', '远端并行测速：国内镜像与官方源（最多 5 秒）')
    with ThreadPoolExecutor(max_workers=min(3, len(candidates))) as pool:
        scores = list(pool.map(probe_download_source, candidates))
    ranked = sorted(zip(candidates, scores), key=lambda item: -(item[1] or 0))
    for url, score in ranked:
        progress_event('terminal', '线路测速 ' + urllib.parse.urlparse(url).hostname + ('：' + f'{score / 1048576:.2f} MiB/s' if score else '：不可用，保留回退'))
    # If every probe fails, retain official as the first fallback.
    return [url for url, _ in ranked] if any(scores) else [candidates[-1], *candidates[:-1]]


def deployment_checkpoint():
    if CONTROL_FILE is None:
        return
    def state():
        if CONTROL_FILE.is_symlink() or not CONTROL_FILE.is_file():
            raise ValueError('Deployment control channel lost; refusing to continue.')
        marker = CONTROL_FILE.with_name(CONTROL_FILE.name + '.pause')
        if marker.is_symlink():
            raise ValueError('Unsafe pause marker.')
        return 'pause' if marker.exists() else 'run'
    value = state()
    if value not in ('run', 'pause'):
        raise ValueError('Invalid deployment control state.')
    if value == 'pause':
        progress_event('paused', '部署已暂停，等待继续')
        until = time.monotonic() + 24 * 3600
        while value == 'pause':
            if time.monotonic() > until:
                raise ValueError('Deployment pause exceeded 24 hours.')
            time.sleep(0.2)
            value = state()
        if value != 'run':
            raise ValueError('Invalid deployment resume state.')
        progress_event('resumed', '继续部署')


def progress_event(kind, message='', **values):
    if PROGRESS_FILE is None:
        return
    message = re.sub(r'(?i)((?:rcon\.password|password|access_token|refresh_token)\s*[=:]\s*)\S+', r'\1[redacted]', message)
    event = json.dumps({'kind': kind, 'message': message[:4000], **values}, ensure_ascii=True) + '\n'
    try:
        with PROGRESS_LOCK:
            if PROGRESS_FILE.stat().st_size < 4 * 1024 * 1024:
                with PROGRESS_FILE.open('a', encoding='utf-8') as output:
                    output.write(event)
    except OSError:
        pass


def installer_output(path, stop):
    offset = 0
    while not stop.is_set():
        offset = relay_log(path, offset)
        stop.wait(0.2)
    relay_log(path, offset)


def relay_log(path, offset):
    if not path.exists():
        return offset
    with path.open('rb') as source:
        source.seek(offset)
        data = source.read(32768)
        # Keep incomplete lines for the next poll.
        end = data.rfind(b'\n') + 1
        if not end and len(data) == 32768:
            end = len(data)
        if end:
            for line in data[:end].decode('utf-8', errors='replace').splitlines():
                progress_event('terminal', line)
        return offset + end


def transient_network_error(error):
    reason = error.reason if isinstance(error, urllib.error.URLError) else error
    if isinstance(error, urllib.error.HTTPError) or isinstance(reason, ssl.SSLError):
        return False
    if isinstance(reason, socket.gaierror):
        return reason.errno == socket.EAI_AGAIN
    return isinstance(reason, (TimeoutError, ConnectionResetError, ConnectionAbortedError)) or (
        isinstance(reason, OSError) and reason.errno in (errno.ETIMEDOUT, errno.ENETUNREACH, errno.EHOSTUNREACH))


def network_failure(url, error, attempts):
    host = urllib.parse.urlparse(url).hostname or 'unknown'
    reason = error.reason if isinstance(error, urllib.error.URLError) else error
    if isinstance(reason, socket.gaierror):
        return SourceDownloadError(f'远端服务器 DNS 解析失败：{host}（尝试 {attempts} 次）。'
                          'SSH 已连通，但这不代表服务器能访问互联网。请在远端服务器检查 DNS、默认网关及外网连接；'
                          'Linux 可执行 getent hosts ' + host + ' 并检查 /etc/resolv.conf；'
                          'Windows 可执行 nslookup ' + host + '。域名可能包含下载重定向，请同时检查下载站点。'
                          'CPT 未修改系统 DNS，请修复服务器网络后重试。')
    if isinstance(reason, ssl.SSLError):
        return SourceDownloadError(f'远端服务器 HTTPS 校验失败：{host}。请检查服务器时间和系统 CA 证书，不要关闭证书校验。')
    if isinstance(error, urllib.error.HTTPError):
        return SourceDownloadError(f'远端下载站点 {host} 返回 HTTP {error.code}，请稍后重试或检查站点访问限制。')
    return SourceDownloadError(f'远端服务器无法访问下载站点 {host}（尝试 {attempts} 次，{type(reason).__name__}）。'
                      '请检查远端服务器的外网、代理和防火墙设置，然后重试。')


def fetch(url, limit=8 * 1024 * 1024):
    deployment_checkpoint()
    parsed = urllib.parse.urlparse(url)
    if parsed.scheme != 'https' or not parsed.hostname or parsed.username or parsed.password:
        raise ValueError('An HTTPS URL without credentials is required.')
    request = urllib.request.Request(url, headers={'User-Agent': USER_AGENT})
    progress_event('detail', '读取元数据：' + parsed.hostname)
    for attempt in range(1, NETWORK_ATTEMPTS + 1):
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                if urllib.parse.urlparse(response.url).scheme != 'https':
                    raise ValueError('Insecure redirect rejected.')
                data = response.read(limit + 1)
            break
        except (urllib.error.URLError, OSError) as error:
            if attempt == NETWORK_ATTEMPTS or not transient_network_error(error):
                raise network_failure(url, error, attempt) from error
            time.sleep(attempt)
    if len(data) > limit:
        raise ValueError('Response exceeds size limit.')
    return data


def api(url):
    return json.loads(fetch(url))


def download(url, destination, digest=None, algorithm='sha256'):
    address = urllib.parse.urlparse(url)
    if address.scheme != 'https' or not address.hostname or address.username or address.password:
        raise ValueError('An HTTPS URL without credentials is required.')
    cache = None
    if DOWNLOAD_CACHE_ENABLED and digest and re.fullmatch(r'[0-9a-fA-F]{' + str(hashlib.new(algorithm).digest_size * 2) + r'}', digest):
        directory = Path.home() / '.cpt-server' / 'artifact-cache'
        if any(path.is_symlink() for path in [directory, *directory.parents]):
            raise ValueError('Unsafe deployment cache directory.')
        directory.mkdir(parents=True, exist_ok=True, mode=0o700)
        cache = directory / (algorithm + '-' + digest.lower())
        if cache.is_symlink():
            raise ValueError('Unsafe deployment cache file.')
        if cache.is_file():
            hasher = hashlib.new(algorithm)
            valid_size = 0 < cache.stat().st_size <= 512 * 1024 * 1024
            if valid_size:
                with cache.open('rb') as source:
                    while block := source.read(1024 * 1024):
                        deployment_checkpoint()
                        hasher.update(block)
            if valid_size and hasher.hexdigest().lower() == digest.lower():
                temporary = destination.with_name(destination.name + '.part')
                if temporary.is_symlink():
                    raise ValueError('Unsafe download staging file.')
                try:
                    shutil.copyfile(cache, temporary)
                    os.replace(temporary, destination)
                finally:
                    temporary.unlink(missing_ok=True)
                progress_event('detail', '复用已校验下载缓存：' + destination.name)
                return
            cache.unlink()
    download_from_sources(url, destination, digest, algorithm)
    if cache is not None:
        temporary = cache.with_name(cache.name + '-' + secrets.token_hex(6) + '.tmp')
        try:
            shutil.copyfile(destination, temporary)
            if os.name != 'nt':
                temporary.chmod(0o600)
            os.replace(temporary, cache)
        except OSError:
            progress_event('detail', '下载已完成，但无法保存复用缓存；下次可能需要重新下载')
        finally:
            temporary.unlink(missing_ok=True)


def download_from_sources(url, destination, digest=None, algorithm='sha256'):
    candidates = download_candidates(url, digest)
    if len(candidates) == 1:
        return download_direct(url, destination, digest, algorithm)
    if not re.fullmatch(r'[0-9a-fA-F]{' + str(hashlib.new(algorithm).digest_size * 2) + r'}', digest):
        raise ValueError('Invalid official artifact checksum.')
    sources = rank_download_sources(candidates)
    for index, source in enumerate(sources):
        deployment_checkpoint()
        progress_event('detail', '选择下载线路：' + urllib.parse.urlparse(source).hostname)
        try:
            return download_direct(source, destination, digest, algorithm, attempts=1, timeout=15)
        except (SourceDownloadError, DownloadIntegrityError):
            if index == len(sources) - 1:
                raise
            progress_event('detail', '线路失败或校验不匹配，自动切换下一线路')


def download_direct(url, destination, digest=None, algorithm='sha256', attempts=NETWORK_ATTEMPTS, timeout=60):
    # Download to a sibling staging file; never truncate the installed artifact.
    temporary = destination.with_name(destination.name + '.part')
    try:
        if temporary.is_symlink():
            raise ValueError('Unsafe download staging file.')
        parsed = urllib.parse.urlparse(url)
        if parsed.scheme != 'https' or not parsed.hostname or parsed.username or parsed.password:
            raise ValueError('An HTTPS URL without credentials is required.')
        request = urllib.request.Request(url, headers={'User-Agent': USER_AGENT})
        for attempt in range(1, attempts + 1):
            hasher = hashlib.new(algorithm)
            total = 0
            started = time.monotonic()
            last_event = 0
            progress_event('detail', '远端直链下载：' + destination.name + ' · 来源 ' + parsed.hostname)
            try:
                with urllib.request.urlopen(request, timeout=timeout) as response, temporary.open('wb') as output:
                    length = int(response.headers.get('Content-Length', '0')) if hasattr(response, 'headers') else 0
                    if urllib.parse.urlparse(response.url).scheme != 'https':
                        raise DownloadIntegrityError('Insecure redirect rejected.')
                    if hasattr(response, 'headers') and 'text/html' in response.headers.get('Content-Type', '').lower():
                        raise DownloadIntegrityError('Artifact source returned HTML instead of a file.')
                    while True:
                        deployment_checkpoint()
                        block = response.read(64 * 1024)
                        if not block:
                            break
                        total += len(block)
                        if total > 512 * 1024 * 1024:
                            raise ValueError('Artifact exceeds 512 MiB.')
                        hasher.update(block)
                        output.write(block)
                        now = time.monotonic()
                        if now - last_event >= 0.2:
                            progress_event('download', destination.name, bytes=total, total=length, speed=total / max(0.001, now - started))
                            last_event = now
                break
            except (urllib.error.URLError, OSError) as error:
                # Only network failures are retried; disk and permission errors must surface unchanged.
                if not isinstance(error, urllib.error.URLError) and not isinstance(error, (TimeoutError, ConnectionError, socket.gaierror)):
                    raise
                if attempt == attempts or not transient_network_error(error):
                    raise network_failure(url, error, attempt) from error
                time.sleep(attempt)
        if digest and hasher.hexdigest().lower() != digest.lower():
            raise DownloadIntegrityError('Artifact checksum mismatch.')
        if length > 0 and total != length:
            raise DownloadIntegrityError('Artifact size does not match Content-Length.')
        os.replace(temporary, destination)
        progress_event('download', destination.name, bytes=total, total=total, speed=total / max(0.001, time.monotonic() - started), complete=True)
        progress_event('detail', '下载与校验完成：' + destination.name)
    finally:
        temporary.unlink(missing_ok=True)


def root_path(value):
    root = Path(value).expanduser()
    if not root.is_absolute() or root == Path(root.anchor) or root.resolve() == Path.home().resolve():
        raise ValueError('Use an absolute dedicated server directory, not a drive root or home directory.')
    if any(p.is_symlink() for p in [root, *root.parents]):
        raise ValueError('Symbolic-link server directories are not supported.')
    return root.resolve()


def child(root, name):
    target = root / name
    if target.is_symlink() or root not in target.resolve().parents:
        raise ValueError('Path escapes the server directory.')
    for parent in target.parents:
        if parent == root:
            break
        if parent.is_symlink():
            raise ValueError('Symbolic links are not supported.')
    return target


def write_atomic(path, data):
    temporary = path.with_name(path.name + '.tmp')
    if temporary.is_symlink():
        raise ValueError('Unsafe staging file.')
    temporary.write_bytes(data)
    os.replace(temporary, path)


@contextmanager
def operation_lock(root):
    locks = Path.home() / '.cpt-server' / 'locks'
    locks.mkdir(parents=True, exist_ok=True)
    path = locks / (hashlib.sha256(str(root).encode()).hexdigest() + '.lock')
    with path.open('a+b') as handle:
        handle.seek(0)
        if os.name == 'nt':
            import msvcrt
            handle.write(b'0')
            handle.flush()
            handle.seek(0)
            try:
                msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
            except OSError as error:
                raise ValueError('Another operation is in progress.') from error
        else:
            import fcntl
            try:
                fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError as error:
                raise ValueError('Another operation is in progress.') from error
        try:
            yield
        finally:
            if os.name == 'nt':
                handle.seek(0)
                msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(handle, fcntl.LOCK_UN)


def read_properties(root):
    path = child(root, 'server.properties')
    if not path.exists():
        return ''
    with path.open('r', encoding='utf-8', newline='') as stream:
        return stream.read()


def parse_properties(text):
    result = {}
    for line in text.splitlines():
        if line.strip() and not line.lstrip().startswith(('#', '!')) and '=' in line:
            key, value = line.split('=', 1)
            result[key.strip()] = value.strip()
    return result


def set_properties(text, values):
    lines = text.splitlines()
    found = set()
    for index, line in enumerate(lines):
        key = line.split('=', 1)[0].strip()
        if '=' in line and key in values and not line.lstrip().startswith(('#', '!')):
            lines[index] = key + '=' + str(values[key])
            found.add(key)
    lines.extend(key + '=' + str(value) for key, value in values.items() if key not in found)
    return '\n'.join(lines) + '\n'


def exact(stream, length):
    data = bytearray()
    while len(data) < length:
        part = stream.recv(length - len(data))
        if not part:
            raise ConnectionError('RCON connection closed.')
        data.extend(part)
    return bytes(data)


def rcon(root, command):
    props = parse_properties(read_properties(root))
    password = props.get('rcon.password', '')
    if props.get('enable-rcon') != 'true' or not password:
        raise ValueError('RCON is not enabled. Deploy or configure RCON first.')
    with socket.create_connection(('127.0.0.1', int(props.get('rcon.port', 25575))), timeout=8) as stream:
        def send(request_id, kind, body):
            packet = struct.pack('<ii', request_id, kind) + body.encode('utf-8') + b'\0\0'
            stream.sendall(struct.pack('<i', len(packet)) + packet)

        def receive():
            length, = struct.unpack('<i', exact(stream, 4))
            if not 10 <= length <= 4 * 1024 * 1024:
                raise ValueError('Invalid RCON packet.')
            packet = exact(stream, length)
            return (*struct.unpack('<ii', packet[:8]), packet[8:-2].decode('utf-8', 'replace'))

        send(1, 3, password)
        for _ in range(3):
            request_id, kind, _ = receive()
            if request_id == -1:
                raise PermissionError('RCON authentication failed.')
            if kind == 2 and request_id == 1:
                break
        else:
            raise PermissionError('RCON authentication failed.')
        send(2, 2, command)
        return receive()[2]


def java_entry_name(value):
    if not isinstance(value, str) or not value or '\\' in value or ':' in value or '\0' in value:
        raise ValueError('Unsafe Java archive entry path.')
    path = PurePosixPath(value)
    if path.is_absolute() or '..' in path.parts or not path.parts:
        raise ValueError('Unsafe Java archive entry path.')
    return path.as_posix()


def extract_java_archive(archive, staging):
    # Validate the whole archive before writing; materialize safe internal file links.
    if zipfile.is_zipfile(archive):
        with zipfile.ZipFile(archive) as bundle:
            entries = [(java_entry_name(entry.filename), entry) for entry in bundle.infolist()]
            if len(entries) > 50000 or sum(entry.file_size for _, entry in entries) > 2 * 1024 ** 3:
                raise ValueError('Java archive exceeds extraction limits.')
            names = {}
            for name, entry in entries:
                mode = entry.external_attr >> 16
                if mode & 0o170000 not in (0, 0o100000, 0o040000):
                    raise ValueError('Unsafe Java ZIP archive entry type.')
                if name.casefold() in names:
                    raise ValueError('Duplicate Java archive entry.')
                names[name.casefold()] = entry
                child(staging, name)
            if len({PurePosixPath(name).parts[0] for name, _ in entries}) != 1:
                raise ValueError('Unexpected Java archive layout.')
            for name, entry in entries:
                for parent in PurePosixPath(name).parents:
                    existing = names.get(parent.as_posix().casefold())
                    if existing is not None and not existing.is_dir():
                        raise ValueError('Java archive entry has a non-directory parent.')
            for name, entry in entries:
                deployment_checkpoint()
                target = child(staging, name)
                if entry.is_dir():
                    target.mkdir(parents=True, exist_ok=True)
                else:
                    target.parent.mkdir(parents=True, exist_ok=True)
                    with bundle.open(entry) as source, target.open('xb') as output:
                        shutil.copyfileobj(source, output)
        return
    with tarfile.open(archive) as bundle:
        entries = {}
        folded = set()
        total = 0
        for entry in bundle.getmembers():
            name = java_entry_name(entry.name)
            if name.casefold() in folded:
                raise ValueError('Duplicate Java archive entry.')
            if not (entry.isfile() or entry.isdir() or entry.issym() or entry.islnk()):
                raise ValueError('Unsafe Java archive entry type.')
            total += entry.size
            if len(entries) >= 50000 or total > 2 * 1024 ** 3:
                raise ValueError('Java archive exceeds extraction limits.')
            child(staging, name)
            entries[name] = entry
            folded.add(name.casefold())
        tops = {PurePosixPath(name).parts[0] for name in entries}
        if len(tops) != 1:
            raise ValueError('Unexpected Java archive layout.')
        top = next(iter(tops))
        for name in entries:
            for parent in PurePosixPath(name).parents:
                existing = entries.get(parent.as_posix())
                if existing is not None and not existing.isdir():
                    raise ValueError('Java archive entry has a non-directory parent.')

        def linked_file(name, visiting):
            if name in visiting or len(visiting) > 64:
                raise ValueError('Cyclic Java archive link.')
            entry = entries.get(name)
            if entry is None or entry.isdir():
                raise ValueError('Java archive link does not target a regular file.')
            if entry.isfile():
                return name
            target = entry.linkname
            if not target or '\\' in target or ':' in target or '\0' in target or PurePosixPath(target).is_absolute():
                raise ValueError('Unsafe Java archive link target.')
            parts = list(PurePosixPath(name).parent.parts) if entry.issym() else []
            for part in PurePosixPath(target).parts:
                if part == '..':
                    if not parts:
                        raise ValueError('Java archive link escapes runtime directory.')
                    parts.pop()
                elif part != '.':
                    parts.append(part)
            if not parts or parts[0] != top:
                raise ValueError('Java archive link escapes runtime directory.')
            return linked_file('/'.join(parts), visiting | {name})

        links = {name: linked_file(name, set()) for name, entry in entries.items() if entry.issym() or entry.islnk()}
        if total + sum(entries[source].size for source in links.values()) > 2 * 1024 ** 3:
            raise ValueError('Java archive links exceed extraction limits.')
        for name, entry in entries.items():
            deployment_checkpoint()
            target = child(staging, name)
            if entry.isdir():
                target.mkdir(parents=True, exist_ok=True)
            elif entry.isfile():
                target.parent.mkdir(parents=True, exist_ok=True)
                with bundle.extractfile(entry) as source, target.open('xb') as output:
                    shutil.copyfileobj(source, output)
                if os.name != 'nt':
                    target.chmod(entry.mode & 0o777)
        for name, source_name in links.items():
            deployment_checkpoint()
            target = child(staging, name)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(child(staging, source_name), target)
            if os.name != 'nt':
                target.chmod(entries[source_name].mode & 0o777)
        progress_event('detail', 'Java 解压完成，安全展开内部文件链接：' + str(len(links)))


def java_for(root, major):
    deployment_checkpoint()
    progress_event('detail', '检查 Java ' + str(major))
    runtime = child(root, '.cpt-java-' + str(major))
    executable = runtime / 'bin' / ('java.exe' if os.name == 'nt' else 'java')
    if executable.exists():
        progress_event('detail', '复用已有独立 Java ' + str(major))
        return str(executable)
    system_java = shutil.which('java')
    if system_java:
        result = subprocess.run([system_java, '-version'], capture_output=True, text=True, timeout=15)
        match = re.search(r'version "(?:1\.)?(\d+)', result.stderr + result.stdout)
        if match and int(match[1]) == major:
            progress_event('detail', '复用系统 Java ' + str(major))
            return system_java
    architecture = {'amd64': 'x64', 'x86_64': 'x64', 'aarch64': 'aarch64', 'arm64': 'aarch64'}.get(__import__('platform').machine().lower())
    if not architecture:
        raise ValueError('Unsupported Java architecture.')
    platform = 'windows' if os.name == 'nt' else 'linux'
    entries = api(f'https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture={architecture}&image_type=jre&os={platform}&vendor=eclipse')
    package = entries[0]['binary']['package']
    archive = child(root, '.cpt-java-download')
    download(package['link'], archive, package['checksum'])
    staging = child(root, '.cpt-java-stage-' + secrets.token_hex(4))
    staging.mkdir()
    progress_event('detail', '解压 Java 运行环境')
    try:
        extract_java_archive(archive, staging)
        folders = [p for p in staging.iterdir() if p.is_dir()]
        if len(folders) != 1:
            raise ValueError('Unexpected Java archive layout.')
        staged_executable = child(staging, folders[0].name + '/bin/' + ('java.exe' if os.name == 'nt' else 'java'))
        if not staged_executable.is_file():
            raise ValueError('Java archive does not contain the expected executable.')
        os.replace(folders[0], runtime)
    finally:
        archive.unlink(missing_ok=True)
        shutil.rmtree(staging)
    if not executable.exists():
        raise ValueError('Java installation failed.')
    if os.name != 'nt':
        executable.chmod(0o755)
    return str(executable)


def maven_artifact_path(coordinate):
    match = re.fullmatch(r'([A-Za-z0-9_.-]+):([A-Za-z0-9_.-]+):([A-Za-z0-9_.+-]+)(?::([A-Za-z0-9_.+-]+))?(?:@([A-Za-z0-9]+))?', coordinate)
    if not match or any('..' in part for part in match.groups() if part):
        raise ValueError('Invalid installer Maven coordinate.')
    group, artifact, version, classifier, extension = match.groups()
    return group.replace('.', '/') + '/' + artifact + '/' + version + '/' + artifact + '-' + version + (('-' + classifier) if classifier else '') + '.' + (extension or 'jar')


def installer_dependencies(installer, minecraft_info, minecraft_version):
    with zipfile.ZipFile(installer) as bundle:
        def document(name):
            entry = bundle.getinfo(name)
            if entry.file_size > 8 * 1024 * 1024:
                raise ValueError('Installer metadata too large.')
            return json.loads(bundle.read(entry))
        profile = document('install_profile.json')
        version = document(profile.get('json', '/version.json').lstrip('/'))
        if profile.get('minecraft') != minecraft_version:
            raise ValueError('Installer targets a different Minecraft version.')
        artifacts = {}
        embedded = {}
        for library in profile.get('libraries', []) + version.get('libraries', []):
            artifact = library.get('downloads', {}).get('artifact')
            if not artifact:
                continue
            path = artifact.get('path', '')
            if path != maven_artifact_path(library['name']):
                raise ValueError('Installer library path differs from its Maven coordinate.')
            digest, size = artifact.get('sha1', ''), artifact.get('size', 0)
            if not re.fullmatch(r'[0-9a-fA-F]{40}', digest) or not isinstance(size, int) or not 0 < size <= 512 * 1024 * 1024:
                raise ValueError('Installer dependency lacks valid checksum/size.')
            url = artifact.get('url', '')
            if not url:
                entry = bundle.getinfo('maven/' + path)
                if entry.file_size != size:
                    raise ValueError('Embedded installer library size mismatch.')
                embedded[path] = entry.filename
            else:
                address = urllib.parse.urlparse(url)
                if address.scheme != 'https' or address.username or address.password or address.query or address.fragment:
                    raise ValueError('Unsafe installer dependency URL.')
            record = {'url': url, 'sha1': digest, 'size': size}
            if path in artifacts and artifacts[path] != record:
                raise ValueError('Conflicting installer dependency metadata.')
            artifacts[path] = record
        server_path = profile.get('serverJarPath', 'minecraft_server.' + minecraft_version + '.jar')
        server_path = server_path.replace('{LIBRARY_DIR}', 'libraries').replace('{MINECRAFT_VERSION}', minecraft_version)
        if server_path.startswith('{ROOT}/'):
            server_path = server_path[len('{ROOT}/'):]
        if '{' in server_path or not server_path.endswith('.jar'):
            raise ValueError('Unsupported installer server JAR path.')
        java_entry_name(server_path)
        mappings = profile.get('data', {}).get('MOJMAPS', {}).get('server')
        mapping_path = None
        if mappings:
            if not isinstance(mappings, str) or not mappings.startswith('[') or not mappings.endswith(']'):
                raise ValueError('Unsupported installer server mapping path.')
            mapping_path = 'libraries/' + maven_artifact_path(mappings[1:-1])
            if not minecraft_info.get('downloads', {}).get('server_mappings'):
                raise ValueError('Minecraft server mappings missing from official metadata.')
        if len(artifacts) > 1000:
            raise ValueError('Installer dependency count exceeds limit.')
        return artifacts, embedded, server_path, mapping_path


def prepare_installer_dependencies(installer, staging, info, minecraft_version):
    artifacts, embedded, server_path, mapping_path = installer_dependencies(installer, info, minecraft_version)
    # Complete all validation before starting downloads or extracting embedded libraries.
    for path in artifacts:
        child(staging, 'libraries/' + path)
    child(staging, server_path)
    if mapping_path:
        child(staging, mapping_path)
    for key in ['server'] + (['server_mappings'] if mapping_path else []):
        record = info.get('downloads', {}).get(key, {})
        address = urllib.parse.urlparse(record.get('url', ''))
        if not re.fullmatch(r'[0-9a-fA-F]{40}', record.get('sha1', '')) or address.scheme != 'https' or address.username or address.password or address.query or address.fragment:
            raise ValueError('Official Minecraft server metadata lacks safe URL/checksum.')
    with zipfile.ZipFile(installer) as bundle:
        for index, (path, artifact) in enumerate(artifacts.items(), 1):
            deployment_checkpoint()
            target = child(staging, 'libraries/' + path)
            target.parent.mkdir(parents=True, exist_ok=True)
            progress_event('detail', f'预下载安装器支持库 {index}/{len(artifacts)}：{target.name}')
            if path in embedded:
                data = bundle.read(embedded[path])
                if hashlib.sha1(data).hexdigest().lower() != artifact['sha1'].lower():
                    raise ValueError('Embedded installer dependency checksum mismatch.')
                write_atomic(target, data)
            else:
                download(artifact['url'], target, artifact['sha1'], 'sha1')
            if target.stat().st_size != artifact['size']:
                target.unlink()
                raise ValueError('Installer dependency size mismatch: ' + target.name)
    for path, key in [(server_path, 'server'), (mapping_path, 'server_mappings')]:
        if path is None:
            continue
        artifact = info['downloads'][key]
        target = child(staging, path)
        target.parent.mkdir(parents=True, exist_ok=True)
        progress_event('detail', '预下载原版服务端文件：' + target.name)
        download(artifact['url'], target, artifact['sha1'], 'sha1')
        if artifact.get('size') and target.stat().st_size != artifact['size']:
            target.unlink()
            raise ValueError('Minecraft server artifact size mismatch.')
    write_atomic(child(staging, minecraft_version + '.json'), json.dumps(info).encode('utf-8'))
    progress_event('detail', '安装器支持库预下载和校验完成')


def installer_failure(core, code, log):
    lines = log.splitlines()
    causes = [line.strip() for line in lines if re.search(r'(?i)(exception|failed to|could not|unable to|sslhandshake|timed? ?out|unknownhost|download.*fail)', line) and not re.fullmatch(r'[A-Za-z0-9_.-]+:[A-Za-z0-9_.+-]+:[A-Za-z0-9_.+@:-]+', line.strip())]
    cause = causes[-1][:200] if causes else '请查看远端终端中的安装器输出'
    cause = re.sub(r'https?://\S+', '[下载地址]', cause)
    return f'{core} 安装器失败（退出码 {code}）：{cause}。完整日志已保留在部署日志中。'


def prepare_runtime(root, args, staging):
    deployment_checkpoint()
    progress_event('detail', '准备服务端与 Java 版本')
    core, version = args['core'], args['version']
    if core not in ('Vanilla', 'Paper', 'Fabric', 'Forge', 'NeoForge') or not re.fullmatch(r'[A-Za-z0-9_.+-]{1,50}', version):
        raise ValueError('Invalid server core or version.')
    memory, port = int(args['memory']), int(args['port'])
    if not 512 <= memory <= 262144 or not 1 <= port <= 65535 or port == 25575:
        raise ValueError('Invalid memory or game port (25575 is reserved for private RCON).')
    manifest = api('https://piston-meta.mojang.com/mc/game/version_manifest_v2.json')
    entry = next((e for e in manifest['versions'] if e['id'] == version), None)
    if not entry:
        raise ValueError('Minecraft version not found.')
    info = api(entry['url'])
    required_major = info.get('javaVersion', {}).get('majorVersion', 8)
    major = int(args.get('javaMajor') or required_major)
    if major < required_major or major not in (8, 11, 16, 17, 21, 25):
        raise ValueError(f'This Minecraft version requires Java {required_major} or newer; select a supported Java major.')
    java = args.get('javaPath', '').strip()
    if java:
        if not Path(java).is_absolute() or not Path(java).is_file():
            raise ValueError('Java path must be an existing absolute executable path on the server.')
        result = subprocess.run([java, '-version'], capture_output=True, text=True, timeout=15)
        match = re.search(r'version "(?:1\.)?(\d+)', result.stderr + result.stdout)
        if result.returncode != 0 or not match or int(match[1]) != major:
            raise ValueError(f'The selected executable must be Java {major}.')
    else:
        java = java_for(root, major)
    staging.mkdir(parents=True, exist_ok=True)
    jar = child(staging, 'server.jar')
    loader_version = args.get('loaderVersion', '').strip()
    if loader_version and not re.fullmatch(r'[A-Za-z0-9_.+-]{1,60}', loader_version):
        raise ValueError('Invalid loader version.')
    launch_args = ['-jar', 'server.jar', 'nogui']
    if core == 'Vanilla':
        artifact = info['downloads']['server']
        download(artifact['url'], jar, artifact['sha1'], 'sha1')
    elif core == 'Paper':
        builds = api(f'https://fill.papermc.io/v3/projects/paper/versions/{version}/builds')
        stable = [b for b in builds if b['channel'] == 'STABLE']
        if not stable:
            raise ValueError('No stable Paper build for this version.')
        build = next((build for build in stable if str(build['id']) == loader_version), None) if loader_version else max(stable, key=lambda b: b['id'])
        if build is None:
            raise ValueError('Selected Paper build is not available for this Minecraft version.')
        loader_version = str(build['id'])
        artifact = build['downloads']['server:default']
        download(artifact['url'], jar, artifact['checksums']['sha256'])
    elif core == 'Fabric':
        progress_event('detail', '并行查询 Fabric Loader 与 Installer')
        with ThreadPoolExecutor(max_workers=2) as pool:
            loader_request = pool.submit(api, f'https://meta.fabricmc.net/v2/versions/loader/{version}')
            installer_request = pool.submit(api, 'https://meta.fabricmc.net/v2/versions/installer')
            loaders, installers = loader_request.result(), installer_request.result()
        loader = loader_version or next(e['loader']['version'] for e in loaders if e['loader']['stable'])
        if not any(e['loader']['version'] == loader for e in loaders):
            raise ValueError('Fabric loader version is not available for the selected Minecraft version.')
        installer = next(e['version'] for e in installers if e['stable'])
        download(f'https://meta.fabricmc.net/v2/versions/loader/{version}/{loader}/{installer}/server/jar', jar)
        loader_version = loader
    else:
        if not re.fullmatch(r'1\.\d+\.\d+|1\.\d+', version):
            raise ValueError('Forge/NeoForge deployment currently supports numbered 1.x releases.')
        parts = [int(part) for part in version.split('.')]
        if core == 'Forge':
            if parts[1] < 17:
                raise ValueError('Forge installer deployment requires Minecraft 1.17 or newer.')
            promotions = api('https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json')['promos']
            loader_version = loader_version or promotions.get(version + '-recommended') or promotions.get(version + '-latest')
            if not loader_version:
                raise ValueError('No Forge build found for this Minecraft version.')
            coordinate = version + '-' + loader_version
            base = f'https://maven.minecraftforge.net/net/minecraftforge/forge/{coordinate}/forge-{coordinate}-installer.jar'
            argument_folder = f'libraries/net/minecraftforge/forge/{coordinate}'
        else:
            if parts[1] < 20 or parts[1] == 20 and (len(parts) < 3 or parts[2] < 2):
                raise ValueError('NeoForge requires Minecraft 1.20.2 or newer.')
            prefix = f'{parts[1]}.{parts[2] if len(parts) > 2 else 0}.'
            if not loader_version:
                try:
                    metadata = ET.fromstring(fetch('https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml'))
                    candidates = [entry.text for entry in metadata.findall('./versioning/versions/version') if entry.text]
                except (OSError, ET.ParseError):
                    candidates = api('https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge?filter=' + prefix)['versions']
            else:
                candidates = [loader_version]
            available = [value for value in candidates if value.startswith(prefix) and re.fullmatch(r'\d+\.\d+\.\d+', value)]
            loader_version = loader_version or (max(available, key=lambda value: tuple(map(int, value.split('.')))) if available else '')
            if not loader_version or not loader_version.startswith(prefix):
                raise ValueError('No compatible stable NeoForge build found.')
            coordinate = loader_version
            base = f'https://maven.neoforged.net/releases/net/neoforged/neoforge/{coordinate}/neoforge-{coordinate}-installer.jar'
            argument_folder = f'libraries/net/neoforged/neoforge/{coordinate}'
        installer_path = child(staging, 'cpt-installer.jar')
        checksum = fetch(base + '.sha1').decode().strip().split()[0]
        if not re.fullmatch(r'[0-9a-fA-F]{40}', checksum):
            raise ValueError('Installer checksum metadata is invalid.')
        download(base, installer_path, checksum, 'sha1')
        prepare_installer_dependencies(installer_path, staging, info, version)
        with child(staging, 'cpt-installer.log').open('wb') as log:
            deployment_checkpoint()
            progress_event('detail', '执行 ' + core + ' 安装器')
            stop = threading.Event()
            monitor = threading.Thread(target=installer_output, args=(child(staging, 'cpt-installer.log'), stop), daemon=True)
            monitor.start()
            try:
                result = subprocess.run([java, '-jar', str(installer_path), '--installServer'], cwd=staging, stdout=log, stderr=log, timeout=900)
            finally:
                stop.set()
                monitor.join(2)
        if result.returncode != 0:
            log_text = child(staging, 'cpt-installer.log').read_text('utf-8', errors='replace')
            for line in log_text.splitlines()[-100:]:
                progress_event('terminal', line)
            raise ValueError(installer_failure(core, result.returncode, log_text))
        argument_file = argument_folder + ('/win_args.txt' if os.name == 'nt' else '/unix_args.txt')
        if not child(staging, argument_file).exists():
            raise ValueError('Installer did not produce the expected server argument file.')
        launch_args = ['@' + argument_file, 'nogui']
        installer_path.unlink()
    reserved = {'mods', 'plugins', 'world', 'world_nether', 'world_the_end', 'server.properties', 'eula.txt', 'ops.json', 'whitelist.json', 'banned-players.json', 'banned-ips.json'}
    if any(item.name in reserved or item.name.startswith(('.cpt-', '.pcl-')) for item in staging.iterdir()):
        raise ValueError('Installer produced reserved server data; refusing to replace active data.')
    return {'java': java, 'javaMajor': major, 'javaPath': args.get('javaPath', ''), 'memory': memory,
            'core': core, 'version': version, 'port': port, 'loaderVersion': loader_version,
            'launchArgs': launch_args, 'artifacts': [p.name for p in staging.iterdir()]}


def deploy(root, args):
    deployment_checkpoint()
    progress_event('detail', '校验部署目录和 EULA')
    if args.get('acceptEula') is not True:
        raise ValueError('Minecraft EULA must be explicitly accepted.')
    if root.exists() and any(root.iterdir()):
        raise ValueError('Server already exists. Deployment will not overwrite an existing server.')
    root.mkdir(parents=True, exist_ok=True)
    if args.get('pack'):
        return deploy_pack(root, args)
    if args.get('autoConfigure'):
        if os.name == 'nt':
            output = subprocess.check_output(['powershell.exe', '-NoProfile', '-NonInteractive', '-Command', '(Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory'], text=True, timeout=20)
            available = int(output.strip()) // 1024
        else:
            memory = Path('/proc/meminfo').read_text()
            available = int(re.search(r'MemAvailable:\s+(\d+)', memory)[1]) // 1024
        if available < 1536:
            raise ValueError('服务器可用内存不足 1.5 GiB，请释放内存或升级服务器配置。')
        args['memory'] = min(4096, max(1024, int(available * 0.6)))
    if args.get('configureFirewall'):
        progress_event('detail', '保护 RCON 端口并配置游戏端口')
        protect_ports(int(args['port']))
    staging = child(root, '.cpt-initial-runtime')
    settings = prepare_runtime(root, args, staging)
    settings['firewallManaged'] = bool(args.get('configureFirewall'))
    deployment_checkpoint()
    for name in settings['artifacts']:
        child(staging, name).rename(child(root, name))
    staging.rmdir()
    write_atomic(child(root, 'eula.txt'), b'eula=true\n')
    progress_event('detail', '生成服务端配置与私有管理凭据')
    props = set_properties('', {'server-port': settings['port'], 'online-mode': 'true', 'enable-rcon': 'true',
                              'rcon.port': 25575, 'rcon.password': secrets.token_hex(24),
                              'broadcast-rcon-to-ops': 'false'})
    write_atomic(child(root, 'server.properties'), props.encode('utf-8'))
    if os.name != 'nt':
        child(root, 'server.properties').chmod(0o600)
    write_atomic(child(root, '.cpt-server.json'), json.dumps(settings).encode())
    child(root, 'mods').mkdir(exist_ok=True)
    child(root, 'plugins').mkdir(exist_ok=True)
    return {'message': 'Deployed. Keep TCP 25575 private; open only the game port in your firewall.', **settings}


def pack_path(name):
    if not isinstance(name, str) or not name or '\\' in name or name.startswith('/'):
        raise ValueError('Unsafe server pack path.')
    for part in name.split('/'):
        if part in ('', '.', '..') or any(ord(c) < 32 or c in ':*?"<>|' for c in part) or part.endswith(('.', ' ')):
            raise ValueError('Unsafe server pack path.')
        if part.lower().startswith(('.cpt-', '.pcl-')) or re.fullmatch(r'(?:con|prn|aux|nul|com\d|lpt\d)(?:\..*)?', part, re.I):
            raise ValueError('Reserved server pack path.')
    return name


def validate_pack_args(root, config):
    launch = config.get('launchArgs')
    if not isinstance(launch, list) or not all(isinstance(value, str) for value in launch):
        raise ValueError('Invalid server pack launch arguments.')
    if len(launch) == 3 and launch[0] == '-jar' and launch[2] == 'nogui':
        entry = pack_path(launch[1])
        if not entry.lower().endswith('.jar') or not zipfile.is_zipfile(child(root, entry)):
            raise ValueError('Server pack entry is not a valid JAR.')
    elif len(launch) == 2 and launch[0].startswith('@libraries/') and launch[1] == 'nogui':
        entry = pack_path(launch[0][1:])
        # The selected argument file must match the remote operating system.
        expected = 'win_args.txt' if os.name == 'nt' else 'unix_args.txt'
        entry = str(Path(entry).with_name(expected)).replace('\\', '/')
        if not child(root, entry).is_file():
            raise ValueError('Server pack does not contain arguments for this operating system.')
        launch[0] = '@' + entry
        text = child(root, entry).read_text('utf-8')
        if len(text) > 1024 * 1024 or any(value in text.lower() for value in ('-javaagent', '-agentpath', '-agentlib', '../', '..\\')):
            raise ValueError('Unsafe server pack argument file.')
    else:
        raise ValueError('Only server JAR and Forge/NeoForge argument-file launchers are supported.')
    jvm = config.get('jvmArgs', [])
    if not isinstance(jvm, list) or len(jvm) > 128 or not all(isinstance(value, str) and re.fullmatch(r'-(?:XX:[A-Za-z0-9+_=:.-]+|D[A-Za-z0-9_.-]+=[A-Za-z0-9_.,:/+-]+)', value) for value in jvm):
        raise ValueError('Unsupported server pack JVM arguments.')


def deploy_pack(root, args):
    deployment_checkpoint()
    progress_event('detail', '校验并导入开服包')
    config = args['pack']
    token = args.get('uploadToken', '')
    if not re.fullmatch(r'import-[0-9a-f]{32}\.(?:zip|jar)', token):
        raise ValueError('Invalid server pack upload token.')
    upload = Path.home() / '.cpt-server' / token
    if upload.is_symlink() or not upload.is_file() or not 0 < upload.stat().st_size <= 2 * 1024 * 1024 * 1024:
        raise ValueError('Invalid server pack upload.')
    digest = hashlib.sha256()
    with upload.open('rb') as stream:
        while block := stream.read(1024 * 1024):
            digest.update(block)
    if digest.hexdigest() != args.get('uploadHash') or digest.hexdigest() != config.get('sha256'):
        raise ValueError('Server pack checksum mismatch.')
    if token.endswith('.jar'):
        if not zipfile.is_zipfile(upload):
            raise ValueError('Invalid server JAR.')
        shutil.copyfile(upload, child(root, 'server.jar'))
    else:
        prefix = config.get('prefix', '')
        if prefix:
            pack_path(prefix.rstrip('/'))
            if not prefix.endswith('/'):
                raise ValueError('Invalid server pack prefix.')
        with zipfile.ZipFile(upload) as pack:
            if not 0 < len(pack.infolist()) <= 50000:
                raise ValueError('Server pack contains too many files.')
            total = 0
            names = set()
            checked = []
            for info in pack.infolist():
                pack_path(info.orig_filename.rstrip('/'))
                pack_path(info.filename.rstrip('/'))
                if not info.filename.startswith(prefix):
                    if info.is_dir() and prefix.startswith(info.filename):
                        continue
                    raise ValueError('Server pack prefix does not cover all files.')
                name = info.filename[len(prefix):].rstrip('/')
                if not name:
                    continue
                pack_path(name)
                if name.lower() in names or (info.external_attr >> 16) & 0o170000 == 0o120000 or info.flag_bits & 1:
                    raise ValueError('Server pack has duplicate files, symbolic links or encryption.')
                names.add(name.lower())
                total += info.file_size
                if info.file_size > 2 * 1024 * 1024 * 1024 or total > 4 * 1024 * 1024 * 1024:
                    raise ValueError('Server pack exceeds extraction limits.')
                checked.append((info, name))
            for info, name in checked:
                target = child(root, name)
                if info.is_dir():
                    target.mkdir(parents=True, exist_ok=True)
                else:
                    target.parent.mkdir(parents=True, exist_ok=True)
                    with pack.open(info) as source, target.open('xb') as output:
                        shutil.copyfileobj(source, output, 1024 * 1024)
    validate_pack_args(root, config)
    core, version = config['core'], config['version']
    if core not in ('Vanilla', 'Paper', 'Fabric', 'Forge', 'NeoForge') or not re.fullmatch(r'[A-Za-z0-9_.+-]{1,50}', version):
        raise ValueError('Invalid imported server core or Minecraft version.')
    loader = config.get('loaderVersion', '')
    if core in ('Fabric', 'Forge', 'NeoForge') and not re.fullmatch(r'[A-Za-z0-9_.+-]{1,60}', loader):
        raise ValueError('Imported loader version is missing or invalid.')
    memory, port = int(config['memory']), int(config['port'])
    minimum = int(config.get('minMemory', 512))
    if not 512 <= memory <= 262144 or not 128 <= minimum <= memory or not 1 <= port <= 65535 or port == 25575:
        raise ValueError('Invalid imported server memory or port.')
    manifest = api('https://piston-meta.mojang.com/mc/game/version_manifest_v2.json')
    entry = next((item for item in manifest['versions'] if item['id'] == version), None)
    if entry is None:
        raise ValueError('Minecraft version not found.')
    required = api(entry['url']).get('javaVersion', {}).get('majorVersion', 8)
    major = int(config.get('javaMajor') or required)
    if major < required or major not in (8, 11, 16, 17, 21, 25):
        raise ValueError(f'This server pack requires Java {required} or newer.')
    java = args.get('javaPath', '').strip()
    if java:
        if not Path(java).is_absolute() or not Path(java).is_file():
            raise ValueError('Invalid remote Java executable.')
        probe = subprocess.run([java, '-version'], capture_output=True, text=True, timeout=15)
        match = re.search(r'version "(?:1\.)?(\d+)', probe.stderr + probe.stdout)
        if probe.returncode != 0 or not match or int(match[1]) != major:
            raise ValueError('Remote Java does not match the server pack.')
    else:
        java = java_for(root, major)
    props = set_properties(read_properties(root), {'server-port': port, 'enable-rcon': 'true', 'rcon.port': 25575,
                           'rcon.password': secrets.token_hex(24), 'broadcast-rcon-to-ops': 'false'})
    if args.get('configureFirewall'):
        protect_ports(port)
    write_atomic(child(root, 'server.properties'), props.encode('utf-8'))
    if os.name != 'nt':
        child(root, 'server.properties').chmod(0o600)
    write_atomic(child(root, 'eula.txt'), b'eula=true\n')
    settings = {key: config[key] for key in ('core', 'version', 'memory', 'port', 'loaderVersion', 'launchArgs', 'jvmArgs')}
    settings.update(java=java, javaMajor=major, javaPath=args.get('javaPath', ''), minMemory=minimum, imported=True,
                    firewallManaged=bool(args.get('configureFirewall')), artifacts=[])
    child(root, 'mods').mkdir(exist_ok=True)
    child(root, 'plugins').mkdir(exist_ok=True)
    write_atomic(child(root, '.cpt-server.json'), json.dumps(settings).encode())
    return {'message': 'Imported server pack deployed.', **settings}


def firewall_command(command, allow_missing=False, timeout=30):
    deployment_checkpoint()
    progress_event('terminal', '$ ' + ' '.join(command))
    try:
        result = subprocess.run(command, capture_output=True, text=True, timeout=timeout)
    except subprocess.TimeoutExpired as error:
        raise ValueError('防火墙配置超时：请检查规则锁或系统包管理器，部署未继续。') from error
    except OSError as error:
        raise ValueError('无法执行防火墙工具：请检查工具是否安装及 SSH 用户权限，部署未继续。') from error
    detail = (result.stderr or '').strip()
    if result.returncode == 0:
        for line in (result.stdout or '').splitlines()[-20:]:
            progress_event('terminal', line)
    missing = not detail or 'does a matching rule exist in that chain' in detail.lower()
    if result.returncode == 0 or allow_missing and result.returncode == 1 and missing:
        return result.returncode
    lower = detail.lower()
    if 'sudo' in command and ('password' in lower or 'not allowed' in lower or 'sudoers' in lower):
        advice = '当前 SSH 用户无法免交互提权。请使用 root，或由管理员为该用户配置必要的 sudo 权限后重试。'
    elif any(value in lower for value in ('permission denied', 'operation not permitted', 'must be root', 'you must be root')):
        advice = '当前进程没有修改防火墙的权限。请使用有权限的 SSH 用户；容器或受限 VPS 还需宿主机提供 NET_ADMIN 能力。'
    elif any(value in lower for value in ('xtables lock', 'resource temporarily unavailable', 'another app is currently holding')):
        advice = '防火墙规则锁被其他程序占用，等待后重试；不要删除锁文件或清空已有规则。'
    elif any(value in lower for value in ('table does not exist', 'protocol not supported', 'could not fetch rule set', 'invalid argument')):
        advice = '系统防火墙后端或内核能力不兼容。请由管理员检查 iptables/nftables 后端及内核支持，不要直接关闭防火墙。'
    else:
        advice = '请由管理员检查 sudo 权限、iptables/ip6tables 和系统内核防火墙支持。'
    raise ValueError(f'防火墙配置失败（退出码 {result.returncode}）。{advice}'
                     'RCON 端口 25575 未确认受保护，部署未继续。'
                     + ('\n系统原因：' + detail[-1000:] if detail else ''))


def protect_ports(game_port):
    if game_port == 25575 or not 1 <= game_port <= 65535:
        raise ValueError('Invalid game port.')
    if os.name == 'nt':
        code = "$ErrorActionPreference='Stop'; "
        code += "if (-not (Get-NetFirewallRule -DisplayName 'CraftPilot private RCON' -ErrorAction SilentlyContinue)) { New-NetFirewallRule -DisplayName 'CraftPilot private RCON' -Direction Inbound -Action Block -Protocol TCP -LocalPort 25575 | Out-Null }; "
        code += f"if (-not (Get-NetFirewallRule -DisplayName 'CraftPilot game {game_port}' -ErrorAction SilentlyContinue)) {{ New-NetFirewallRule -DisplayName 'CraftPilot game {game_port}' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {game_port} | Out-Null }}"
        result = subprocess.run(['powershell.exe', '-NoProfile', '-NonInteractive', '-Command', code], capture_output=True, text=True, timeout=30)
        if result.returncode:
            raise ValueError('Firewall setup failed. ' + result.stderr[-500:])
    else:
        prefix = [] if os.geteuid() == 0 else ['sudo', '-n']
        if prefix:
            if not shutil.which('sudo'):
                raise ValueError('防火墙配置需要 root 或免交互 sudo；当前 SSH 用户没有 sudo 工具，部署未继续。')
        if not shutil.which('iptables'):
            if shutil.which('apt-get'):
                firewall_command([*prefix, 'apt-get', 'update'], timeout=300)
                firewall_command([*prefix, 'apt-get', 'install', '-y', 'iptables'], timeout=300)
            elif shutil.which('dnf'):
                firewall_command([*prefix, 'dnf', 'install', '-y', 'iptables'], timeout=300)
            else:
                raise ValueError('Firewall setup failed: no supported firewall tool.')
        for binary in ('iptables', 'ip6tables'):
            if not shutil.which(binary):
                raise ValueError('Firewall setup failed: IPv4 and IPv6 protection are both required.')
            firewall_command([*prefix, binary, '-w', '10', '-S', 'INPUT'])
        for rule in (['!', '-i', 'lo', '-p', 'tcp', '--dport', '25575', '-j', 'DROP'],
                     ['-p', 'tcp', '--dport', str(game_port), '-j', 'ACCEPT']):
            for binary in ('iptables', 'ip6tables'):
                if firewall_command([*prefix, binary, '-w', '10', '-C', 'INPUT', *rule], allow_missing=True):
                    firewall_command([*prefix, binary, '-w', '10', '-I', 'INPUT', '1', *rule])
                    firewall_command([*prefix, binary, '-w', '10', '-C', 'INPUT', *rule])


def redact_startup_text(text):
    text = re.sub(r'\x1b\[[0-?]*[ -/]*[@-~]', '', text)
    return re.sub(r'(?i)((?:rcon\.password|password|access_token|refresh_token)\s*[=:]\s*)\S+', r'\1[redacted]', text)


def startup_diagnostics(root):
    attempt_path = child(root, '.cpt-start-attempt.json')
    attempt = json.loads(attempt_path.read_text('utf-8')) if attempt_path.exists() else {}
    logfile = child(root, 'cpt-console.log')
    text = ''
    if logfile.exists():
        length = logfile.stat().st_size
        offset = attempt.get('offset', 0)
        if not isinstance(offset, int) or offset < 0:
            raise ValueError('Invalid startup log offset.')
        with logfile.open('rb') as stream:
            stream.seek(max(min(offset, length), length - 65536))
            text = stream.read(65536).decode('utf-8', errors='replace')
    crash = ''
    crash_name = ''
    reports = child(root, 'crash-reports')
    if reports.exists():
        candidates = [path for path in reports.iterdir() if path.is_file() and not path.is_symlink() and path.name.endswith('.txt') and path.stat().st_mtime >= attempt.get('started', float('inf'))]
        if candidates:
            report = max(candidates, key=lambda path: path.stat().st_mtime)
            with report.open('rb') as stream:
                crash = stream.read(32768).decode('utf-8', errors='replace')
            crash_name = report.name
    text, crash = redact_startup_text(text)[:65536], redact_startup_text(crash)[:32768]
    lines = (text + '\n' + crash).splitlines()
    indicators = r'(?i)(outofmemory|could not reserve|unable to access jarfile|could not find or load|unsupportedclassversion|bindexception|failed to bind|address already in use|caused by:|exception|fatal|error occurred|failed to start|requires.*java|eula)'
    causes = [line.strip() for line in lines if re.search(indicators, line)]
    summary = causes[0][:240] if causes else '未取得明确启动原因，请查看本次启动控制台和崩溃报告。'
    if any('FAILED TO BIND TO PORT' in line.upper() for line in lines):
        props = parse_properties(read_properties(root))
        address = props.get('server-ip', '').strip() or '所有本机网卡'
        port = props.get('server-port', '25565')
        reason = '绑定地址无效或不是本机地址' if any('cannot assign requested address' in line.lower() for line in lines) else '端口可能已被其他进程占用或绑定地址不正确'
        summary = f'游戏入口 {address}:{port} 绑定失败：{reason}。请检查 server-port 与 server-ip；不自动停止其他进程。'
    return {'summary': summary, 'text': text, 'crash': crash, 'crashFile': crash_name}


def check_game_binding(root):
    props = parse_properties(read_properties(root))
    try:
        port = int(props.get('server-port', '25565'))
        if not 1 <= port <= 65535 or port == int(props.get('rcon.port', '25575')):
            raise ValueError()
    except ValueError as error:
        raise ValueError('游戏端口无效或与 RCON 管理端口冲突，请检查 server-port。') from error
    address = props.get('server-ip', '').strip()
    targets = [(socket.AF_INET, '0.0.0.0')]
    if address:
        try:
            parsed = ipaddress.ip_address(address)
        except ValueError as error:
            raise ValueError('server-ip 必须留空或填写本机网卡的 IP 地址，不能填写公网域名、带端口地址或 URL。') from error
        targets = [(socket.AF_INET6 if parsed.version == 6 else socket.AF_INET, str(parsed))]
    elif socket.has_ipv6:
        targets.append((socket.AF_INET6, '::'))
    for family, host in targets:
        try:
            with socket.socket(family, socket.SOCK_STREAM) as probe:
                if os.name == 'nt' and hasattr(socket, 'SO_EXCLUSIVEADDRUSE'):
                    probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
                if family == socket.AF_INET6:
                    probe.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 1)
                probe.bind((host, port))
        except OSError as error:
            if not address and family == socket.AF_INET6 and error.errno in (errno.EAFNOSUPPORT, errno.EPROTONOSUPPORT, errno.EADDRNOTAVAIL):
                continue
            if error.errno == errno.EADDRINUSE or getattr(error, 'winerror', None) == 10048:
                raise ValueError(f'游戏端口 {port} 已被占用，未启动 Java。请停止你确认可以停止的旧服务，或将 server-port 改为空闲端口；CPT 不会结束其他进程。') from error
            if error.errno == errno.EADDRNOTAVAIL or getattr(error, 'winerror', None) == 10049:
                raise ValueError(f'server-ip={host} 不属于本机可绑定地址。通常应将 server-ip 留空，再启动；公网 NAT 地址不要填写在这里。') from error
            raise ValueError(f'无法绑定游戏入口 {host}:{port}（系统错误 {error.errno}），请检查地址、权限和端口配置。') from error
    return port


def start(root, apply_queue=True):
    deployment_checkpoint()
    progress_event('detail', '检查运行状态与待应用变更')
    settings = json.loads(child(root, '.cpt-server.json').read_text())
    require_stopped(root)
    if apply_queue:
        apply_pending(root)
        settings = json.loads(child(root, '.cpt-server.json').read_text())
    progress_event('detail', '启动前检查实际游戏端口与本机绑定地址')
    settings['port'] = check_game_binding(root)
    write_atomic(child(root, '.cpt-server.json'), json.dumps(settings).encode('utf-8'))
    if settings.get('firewallManaged'):
        protect_ports(int(settings['port']))
    deployment_checkpoint()
    log_path = child(root, 'cpt-console.log')
    write_atomic(child(root, '.cpt-start-attempt.json'), json.dumps({'offset': log_path.stat().st_size if log_path.exists() else 0, 'started': time.time()}).encode('utf-8'))
    logfile = log_path.open('ab')
    try:
        options = {'creationflags': subprocess.CREATE_NEW_PROCESS_GROUP | subprocess.DETACHED_PROCESS} if os.name == 'nt' else {'start_new_session': True}
        process = subprocess.Popen([settings['java'], f"-Xms{settings.get('minMemory', 512)}M", f"-Xmx{settings['memory']}M", *settings.get('jvmArgs', []), *settings.get('launchArgs', ['-jar', 'server.jar', 'nogui'])],
                                   cwd=root, stdin=subprocess.DEVNULL, stdout=logfile, stderr=logfile, **options)
        write_atomic(child(root, '.cpt-pid'), str(process.pid).encode())
        progress_event('terminal', 'Java 已启动，PID ' + str(process.pid))
        time.sleep(2)
        if process.poll() is not None:
            diagnosis = startup_diagnostics(root)
            for line in diagnosis['text'].splitlines()[-100:]:
                progress_event('terminal', line)
            raise ValueError('Server exited. Java 退出码 ' + str(process.returncode) + '。' + diagnosis['summary'])
        return {'message': 'Starting', 'state': 'starting', 'pid': process.pid, 'settings': settings}
    finally:
        logfile.close()


def process_running(root):
    pid_file = child(root, '.cpt-pid')
    if not pid_file.exists():
        return False
    pid = int(pid_file.read_text())
    if pid <= 0:
        raise ValueError('Invalid server process identifier.')
    if os.name == 'nt':
        result = subprocess.run(['tasklist', '/FI', f'PID eq {pid}', '/FO', 'CSV', '/NH'], capture_output=True, text=True)
        if result.returncode != 0:
            raise ValueError('Unable to inspect server process.')
        return f'"{pid}"' in result.stdout
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False


def require_stopped(root):
    try:
        rcon(root, 'list')
    except PermissionError:
        raise ValueError('RCON authentication failed; refusing to assume that the server is stopped.')
    except (OSError, ConnectionError):
        if process_running(root):
            raise ValueError('Server process is still running or starting.')
        return
    raise ValueError('Stop the server before changing files or configuration.')


def stop_wait(root):
    try:
        require_stopped(root)
        return
    except ValueError:
        pass
    try:
        rcon(root, 'stop')
    except ConnectionError:
        pass  # A graceful stop may close the RCON socket before acknowledging.
    deadline = time.monotonic() + 120
    while time.monotonic() < deadline:
        try:
            require_stopped(root)
            return
        except ValueError:
            time.sleep(1)
    raise ValueError('Server did not stop within 120 seconds. Pending changes were not applied.')


def pending(root):
    path = child(root, '.cpt-pending.json')
    return json.loads(path.read_text('utf-8')) if path.exists() else {'resources': []}


def save_config(root, original, text, port, port_changed):
    settings_path = child(root, '.cpt-server.json')
    settings = json.loads(settings_path.read_text('utf-8'))
    settings['port'] = port
    writes = {'server.properties.bak': original.encode('utf-8'),
              'server.properties': text.encode('utf-8'),
              '.cpt-server.json': json.dumps(settings).encode('utf-8')}
    # A later explicit port save takes precedence over an earlier prepared runtime.
    if port_changed:
        queue = pending(root)
        if 'runtime' in queue:
            queue['runtime']['settings']['port'] = port
            writes['.cpt-pending.json'] = json.dumps(queue).encode('utf-8')
    originals = {name: child(root, name).read_bytes() if child(root, name).exists() else None for name in writes}
    committed = []
    try:
        for name, data in writes.items():
            write_atomic(child(root, name), data)
            committed.append(name)
            if os.name != 'nt' and name.startswith('server.properties'):
                child(root, name).chmod(0o600)
        if child(root, 'server.properties').read_bytes() != text.encode('utf-8'):
            raise ValueError('配置写入后校验失败，未确认保存成功。')
    except Exception:
        for name in reversed(committed):
            if originals[name] is None:
                child(root, name).unlink()
            else:
                write_atomic(child(root, name), originals[name])
                if os.name != 'nt' and name.startswith('server.properties'):
                    child(root, name).chmod(0o600)
        raise


def compatible(category, core):
    return core in ('Fabric', 'Forge', 'NeoForge') if category == 'mods' else core == 'Paper'


def snapshot(root, backup):
    backup.mkdir(parents=True)
    for item in root.iterdir():
        if item.name.startswith(('.cpt-', 'cpt-console', '.pcl-', 'pcl-console')):
            continue
        target = child(root, item.name)
        if target.is_dir():
            for parent, dirs, files in os.walk(target, followlinks=False):
                for name in [*dirs, *files]:
                    if (Path(parent) / name).is_symlink():
                        raise ValueError('Cannot back up a server containing symbolic links.')
            shutil.copytree(target, backup / item.name)
        else:
            shutil.copy2(target, backup / item.name)


def apply_pending(root):
    require_stopped(root)
    queue = pending(root)
    old = json.loads(child(root, '.cpt-server.json').read_text())
    new = queue.get('runtime', {}).get('settings', old)
    resources = queue['resources']
    if not resources and 'runtime' not in queue:
        return
    moves = []
    if 'runtime' in queue:
        staging = child(root, queue['runtime']['directory'])
        for name in new['artifacts']:
            source = child(staging, name)
            if not source.exists():
                raise ValueError('Prepared runtime is incomplete; queue retained.')
            moves.append((source, child(root, name)))
        if old['core'] != new['core'] or old['version'] != new['version']:
            # Existing JARs cannot safely be inferred compatible across versions/loaders.
            for category in ('mods', 'plugins'):
                if any(p.name.endswith('.jar') for p in child(root, category).iterdir()):
                    raise ValueError('Disable installed resources before changing Minecraft version or loader.')
    for entry in resources:
        if entry['core'] != new['core'] or entry['version'] != new['version'] or not compatible(entry['category'], new['core']):
            raise ValueError('Queued resource does not match the prepared server version/loader; queue retained.')
        source = child(root, entry['staged'])
        if not source.is_file() or hashlib.sha256(source.read_bytes()).hexdigest() != entry['digest']:
            raise ValueError('Queued resource checksum mismatch; queue retained.')
        moves.append((source, child(root, entry['category'] + '/' + entry['name'])))
    backup = child(root, '.cpt-backups/' + time.strftime('%Y%m%d-%H%M%S') + '-' + secrets.token_hex(4))
    backup.mkdir(parents=True)
    if 'runtime' in queue and (old['version'] != new['version'] or old['core'] != new['core']):
        snapshot(root, backup / 'server-data')
    changes = []
    originals = {name: child(root, name).read_bytes() for name in ('.cpt-server.json', 'server.properties')}
    for name, data in originals.items():
        write_atomic(backup / name, data)
    if os.name != 'nt':
        backup.chmod(0o700)
    try:
        for index, (source, target) in enumerate(moves):
            saved = backup / ('artifact-' + str(index))
            had_target = target.exists()
            if had_target:
                target.rename(saved)
            changes.append((source, target, saved, had_target))
            source.rename(target)
        if 'runtime' in queue:
            import server_properties
            write_atomic(child(root, '.cpt-server.json'), json.dumps(new).encode())
            text = read_properties(root)
            if server_properties.values(text).get('server-port', '25565') != str(new['port']):
                text = server_properties.replace_values(text, {'server-port': str(new['port'])})
                write_atomic(child(root, 'server.properties'), text.encode('utf-8'))
            if os.name != 'nt':
                child(root, 'server.properties').chmod(0o600)
        write_atomic(child(root, '.cpt-pending.json'), b'{"resources":[]}')
    except Exception:
        for source, target, saved, had_target in reversed(changes):
            if target.exists():
                target.rename(source)
            if had_target:
                saved.rename(target)
        for name, data in originals.items():
            write_atomic(child(root, name), data)
        raise
    if 'runtime' in queue:
        staging.rmdir()
    try:
        build_client_pack(root)
    except Exception as error:
        write_atomic(child(root, '.cpt-client-error.txt'), str(error).encode('utf-8'))


def build_client_pack(root):
    settings = json.loads(child(root, '.cpt-server.json').read_text())
    dependencies = {'minecraft': settings['version']}
    if settings['core'] in ('Fabric', 'Forge', 'NeoForge'):
        key = {'Fabric': 'fabric-loader', 'Forge': 'forge', 'NeoForge': 'neoforge'}[settings['core']]
        if not settings.get('loaderVersion'):
            raise ValueError('Server loader version is missing; cannot generate matching client.')
        dependencies[key] = settings['loaderVersion']
    files, unresolved = [], []
    seen = set()
    def add_version(version, artifact, filename):
        if artifact['hashes']['sha1'] in seen:
            return
        if len(seen) >= 200:
            raise ValueError('Client dependency graph is too large.')
        if settings['version'] not in version['game_versions'] or settings['core'].lower() not in version['loaders']:
            raise ValueError('Client mod does not match the server version/loader.')
        url = urllib.parse.urlparse(artifact['url'])
        if url.scheme != 'https' or url.username or url.password or not url.hostname:
            raise ValueError('Invalid client download URL.')
        if not re.fullmatch(r'[A-Za-z0-9_.+ -]{1,180}\.jar', filename) or filename.startswith('.'):
            raise ValueError('Invalid client artifact filename.')
        if any(entry['path'].lower() == ('mods/' + filename).lower() for entry in files):
            raise ValueError('Conflicting client dependency filenames.')
        seen.add(artifact['hashes']['sha1'])
        files.append({'path': 'mods/' + filename, 'fileSize': artifact['size'],
                      'hashes': artifact['hashes'], 'downloads': [artifact['url']],
                      'env': {'client': 'required', 'server': 'required'}})
        for dependency in version.get('dependencies', []):
            if dependency['dependency_type'] != 'required':
                continue
            if dependency.get('version_id'):
                needed = api('https://api.modrinth.com/v2/version/' + dependency['version_id'])
            elif dependency.get('project_id'):
                query = urllib.parse.urlencode({'loaders': json.dumps([settings['core'].lower()]), 'game_versions': json.dumps([settings['version']])})
                choices = api('https://api.modrinth.com/v2/project/' + dependency['project_id'] + '/version?' + query)
                if not choices:
                    raise ValueError('Required client dependency has no compatible version.')
                needed = next((entry for entry in choices if entry['version_type'] == 'release'), choices[0])
            else:
                raise ValueError('Required client dependency has no project/version identifier.')
            project = api('https://api.modrinth.com/v2/project/' + needed['project_id'])
            if project.get('client_side') == 'unsupported':
                continue
            if project.get('client_side') not in ('optional', 'required'):
                raise ValueError('Required dependency client support is unknown.')
            target = next((file for file in needed['files'] if file.get('primary')), needed['files'][0])
            add_version(needed, target, target['filename'])
    if settings['core'] in ('Fabric', 'Forge', 'NeoForge'):
        for path in sorted(child(root, 'mods').iterdir()):
            if not path.name.endswith('.jar'):
                continue
            path = child(root, 'mods/' + path.name)
            data = path.read_bytes()
            sha1 = hashlib.sha1(data).hexdigest()
            try:
                version = api('https://api.modrinth.com/v2/version_file/' + sha1 + '?algorithm=sha1')
                project = api('https://api.modrinth.com/v2/project/' + version['project_id'])
                side = project.get('client_side')
                if side in ('unsupported', 'optional'):
                    continue
                if side != 'required':
                    raise ValueError('Client-side requirement is unknown.')
                artifact = next(file for file in version['files'] if file['hashes'].get('sha1') == sha1)
                if artifact['hashes'].get('sha512') != hashlib.sha512(data).hexdigest():
                    raise ValueError('Modrinth metadata checksum does not match installed mod.')
                add_version(version, artifact, path.name)
            except Exception as error:
                unresolved.append({'name': path.name, 'reason': str(error)})
    manifest = {'formatVersion': 1, 'game': 'minecraft', 'versionId': '1',
                'name': 'CPT Client ' + settings['version'], 'summary': 'Generated from the active server configuration.',
                'dependencies': dependencies, 'files': files}
    canonical = json.dumps(manifest, sort_keys=True, separators=(',', ':')).encode()
    revision = hashlib.sha256(canonical).hexdigest()
    manifest['versionId'] = revision[:16]
    temporary = child(root, '.cpt-client.mrpack.tmp')
    if temporary.is_symlink():
        raise ValueError('Unsafe client package staging file.')
    # Never copy server.properties, authentication files, worlds or server-only configuration.
    with zipfile.ZipFile(temporary, 'w', zipfile.ZIP_DEFLATED) as bundle:
        bundle.writestr('modrinth.index.json', json.dumps(manifest, ensure_ascii=False))
        bundle.writestr('craftpilot.client.json', json.dumps({'revision': revision, 'unresolved': unresolved}))
    os.replace(temporary, child(root, '.cpt-client.mrpack'))
    result = {'revision': revision, 'minecraft': settings['version'], 'core': settings['core'],
              'loaderVersion': settings.get('loaderVersion', ''), 'requiredMods': len(files), 'unresolved': unresolved,
              'package': '.cpt-client.mrpack', 'sha256': hashlib.sha256(child(root, '.cpt-client.mrpack').read_bytes()).hexdigest(),
              'size': child(root, '.cpt-client.mrpack').stat().st_size}
    write_atomic(child(root, '.cpt-client.json'), json.dumps(result).encode())
    return result


def runtime_configure(root, args):
    if args.get('confirmBackup') is not True:
        raise ValueError('Confirm server backup and compatibility risks before changing runtime.')
    queue = pending(root)
    if 'runtime' in queue:
        raise ValueError('A runtime is already prepared. Cancel it before preparing another one.')
    for entry in queue['resources']:
        if entry['core'] != args['core'] or entry['version'] != args['version']:
            raise ValueError('Cancel incompatible queued resources before preparing this runtime.')
    directory = '.cpt-runtime-' + secrets.token_hex(6)
    staging = child(root, directory)
    try:
        settings = prepare_runtime(root, args, staging)
        settings['firewallManaged'] = json.loads(child(root, '.cpt-server.json').read_text()).get('firewallManaged', False)
        queue['runtime'] = {'directory': directory, 'settings': settings}
        write_atomic(child(root, '.cpt-pending.json'), json.dumps(queue).encode())
    except Exception:
        if staging.exists():
            shutil.rmtree(staging)
        raise
    return {'message': 'Runtime prepared; apply on next restart.', 'settings': settings}


def migrate_cpt_metadata(root):
    marker = child(root, '.cpt-migration-v1')
    legacy = child(root, '.pcl-server.json')
    if marker.exists() or not legacy.is_file():
        return
    # Earlier CPT server-manager builds used .pcl-server.json; stock PCL does not.
    items = [item for item in root.iterdir() if item.name.startswith(('.pcl-', 'pcl-console'))]
    for item in items:
        child(root, item.name)
        if item.is_dir():
            for parent, dirs, files in os.walk(item, followlinks=False):
                for name in [*dirs, *files]:
                    if (Path(parent) / name).is_symlink():
                        raise ValueError('Cannot migrate linked server metadata.')
    for item in items:
        name = item.name.replace('.pcl-', '.cpt-', 1).replace('pcl-console', 'cpt-console', 1)
        target = child(root, name)
        if target.exists():
            continue
        if item.is_dir():
            shutil.copytree(item, target)
        elif item.suffix == '.json':
            value = json.loads(item.read_text('utf-8'))

            def convert(data):
                if isinstance(data, dict):
                    return {key: convert(value) for key, value in data.items()}
                if isinstance(data, list):
                    return [convert(value) for value in data]
                if isinstance(data, str):
                    return data.replace('.pcl-', '.cpt-').replace('pcl-console', 'cpt-console')
                return data

            write_atomic(target, json.dumps(convert(value)).encode('utf-8'))
        else:
            shutil.copy2(item, target)
    write_atomic(marker, b'CPT server metadata isolation v1\n')


def manage(root, args):
    action = args['action']
    if action == 'deploy':
        return deploy(root, args)
    migrate_cpt_metadata(root)
    if not child(root, '.cpt-server.json').exists():
        raise ValueError('Deploy this directory first.')
    if action == 'start':
        return start(root)
    if action == 'restart':
        stop_wait(root)
        apply_pending(root)
        return start(root, apply_queue=False)
    if action == 'config-panel-save-restart':
        import server_properties
        original = read_properties(root)
        changes = args.get('changes')
        server_properties.patch(original, changes, args.get('revision'), args.get('confirmOffline', False))
        stop_wait(root)
        stopped = read_properties(root)
        # Shutdown may rewrite comments/ordering; reject changed edited values, not formatting.
        before_values, stopped_values = server_properties.values(original), server_properties.values(stopped)
        if any(before_values.get(key) != stopped_values.get(key) for key in changes):
            raise ValueError('停服期间待保存的配置值发生变化，服务器保持停止，请重新读取后保存。')
        manage(root, {**args, 'action': 'config-panel-save', 'revision': server_properties.revision(stopped)})
        apply_pending(root)
        result = start(root, apply_queue=False)
        return {**result, **server_properties.panel(read_properties(root)), 'port': result['settings']['port'],
                'message': '配置已保存并校验，服务器正在启动。'}
    if action == 'status':
        settings = json.loads(child(root, '.cpt-server.json').read_text())
        queue = pending(root)
        try:
            result = {'online': True, 'state': 'running', 'players': rcon(root, 'list')}
        except PermissionError:
            result = {'online': False, 'state': 'starting-or-unresponsive', 'players': 'RCON authentication failed.'}
        except OSError:
            result = {'online': False, 'state': 'starting-or-unresponsive' if process_running(root) else 'stopped', 'players': ''}
        return {**result, 'settings': settings, 'pendingResources': len(queue['resources']), 'pendingRuntime': 'runtime' in queue}
    if action == 'stop':
        stop_wait(root)
        return {'message': 'Stopped; pending changes have not been applied.', 'state': 'stopped'}
    if action == 'runtime-read':
        return {'settings': json.loads(child(root, '.cpt-server.json').read_text()), 'pending': pending(root).get('runtime', {}).get('settings')}
    if action == 'client-pack':
        return build_client_pack(root)
    if action == 'runtime-prepare':
        return runtime_configure(root, args)
    if action == 'runtime-cancel':
        queue = pending(root)
        if 'runtime' in queue:
            directory = queue['runtime']['directory']
            if not re.fullmatch(r'\.cpt-runtime-[0-9a-f]{12}', directory):
                raise ValueError('Invalid prepared runtime directory.')
            path = child(root, directory)
            if path.exists():
                shutil.rmtree(path)
            del queue['runtime']
            write_atomic(child(root, '.cpt-pending.json'), json.dumps(queue).encode())
        return {'message': 'Prepared runtime canceled; active server unchanged.'}
    if action == 'pending':
        return {'resources': pending(root)['resources']}
    if action == 'pending-cancel':
        queue = pending(root)
        entry = next((entry for entry in queue['resources'] if entry['id'] == args['id']), None)
        if entry is None:
            raise ValueError('Queued resource not found.')
        if not entry['staged'].startswith('.cpt-pending/'):
            raise ValueError('Invalid queue path.')
        child(root, entry['staged']).unlink(missing_ok=True)
        queue['resources'].remove(entry)
        write_atomic(child(root, '.cpt-pending.json'), json.dumps(queue).encode())
        return {'message': 'Preinstallation canceled; active files unchanged.'}
    if action == 'startup-diagnostics':
        return startup_diagnostics(root)
    if action == 'deployment-logs':
        path = child(root, 'cpt-console.log')
        offset = args.get('offset', 0)
        if not isinstance(offset, int) or offset < 0:
            raise ValueError('Invalid log offset.')
        if not path.exists():
            return {'offset': 0, 'text': ''}
        with path.open('rb') as stream:
            stream.seek(min(offset, path.stat().st_size))
            data = stream.read(32768)
            end = data.rfind(b'\n') + 1
            if not end and len(data) == 32768:
                end = len(data)
            text = data[:end].decode('utf-8', errors='replace')
            text = re.sub(r'(?i)((?:rcon\.password|password|access_token|refresh_token)\s*[=:]\s*)\S+', r'\1[redacted]', text)
            return {'offset': stream.tell() - len(data) + end, 'text': text}
    if action == 'logs':
        path = child(root, 'cpt-console.log')
        if not path.exists():
            return {'text': ''}
        with path.open('rb') as stream:
            stream.seek(max(0, path.stat().st_size - 65536))
            return {'text': stream.read().decode('utf-8', 'replace')}
    if action == 'players':
        return {'online': rcon(root, 'list'), **{name: json.loads(child(root, name + '.json').read_text('utf-8')) if child(root, name + '.json').exists() else [] for name in ('whitelist', 'ops', 'banned-players')}}
    if action == 'player':
        player, verb = args['player'], args['verb']
        if not re.fullmatch(r'[A-Za-z0-9_]{1,16}', player):
            raise ValueError('Invalid player name.')
        if verb not in ('ban', 'pardon', 'kick', 'op', 'deop', 'whitelist add', 'whitelist remove'):
            raise ValueError('Unsupported player command.')
        return {'message': rcon(root, verb + ' ' + player)}
    if action == 'config-read':
        return {'text': set_properties(read_properties(root), {'rcon.password': '<managed>'})}
    if action == 'config-panel-read':
        import server_properties
        text = read_properties(root)
        if len(text.encode('utf-8')) > 256 * 1024:
            raise ValueError('配置文件过大，未读取中文面板。')
        return server_properties.panel(text)
    if action == 'config-panel-save':
        import server_properties
        require_stopped(root)
        original = read_properties(root)
        text = server_properties.patch(original, args.get('changes'), args.get('revision'), args.get('confirmOffline', False))
        if len(text.encode('utf-8')) > 256 * 1024:
            raise ValueError('配置文件过大，未保存。')
        port = int(server_properties.values(text).get('server-port', '25565'))
        save_config(root, original, text, port, 'server-port' in args['changes'])
        return {'message': '设置已保存并备份，下次启动时生效。', 'port': port, **server_properties.panel(text)}
    if action == 'basic-config-read':
        props = parse_properties(read_properties(root))
        return {'maxPlayers': int(props.get('max-players', 20)), 'difficulty': props.get('difficulty', 'easy'),
                'gamemode': props.get('gamemode', 'survival'), 'whitelist': props.get('white-list', 'false') == 'true'}
    if action == 'basic-config-save':
        maximum = int(args['maxPlayers'])
        difficulty, mode = args['difficulty'], args['gamemode']
        if not 1 <= maximum <= 10000 or difficulty not in ('peaceful', 'easy', 'normal', 'hard') or mode not in ('survival', 'creative', 'adventure', 'spectator') or not isinstance(args['whitelist'], bool):
            raise ValueError('Invalid basic server settings.')
        text = set_properties(read_properties(root), {'max-players': maximum, 'difficulty': difficulty,
                              'gamemode': mode, 'white-list': str(args['whitelist']).lower()})
        return manage(root, {'action': 'config-save', 'text': text})
    if action == 'config-save':
        require_stopped(root)
        original = read_properties(root)
        text = args['text']
        if len(text) > 256 * 1024 or '\0' in text:
            raise ValueError('Invalid configuration.')
        old = parse_properties(original)
        text = set_properties(text, {key: old[key] for key in ('enable-rcon', 'rcon.port', 'rcon.password')})
        new = parse_properties(text)
        if not 1 <= int(new.get('server-port', '25565')) <= 65535 or new.get('server-port') == old['rcon.port']:
            raise ValueError('Invalid game port.')
        port = int(new.get('server-port', '25565'))
        save_config(root, original, text, port, int(old.get('server-port', '25565')) != port)
        return {'message': 'Saved with backup.', 'port': port}
    if action in ('files', 'toggle', 'install'):
        category = args['category']
        if category not in ('mods', 'plugins'):
            raise ValueError('Invalid resource directory.')
        directory = child(root, category)
        if action == 'files':
            return {'files': [{'name': p.name, 'size': p.stat().st_size, 'enabled': p.suffix == '.jar'} for p in directory.iterdir() if p.is_file() and not p.is_symlink() and p.name.endswith(('.jar', '.jar.disabled'))]}
        settings = json.loads(child(root, '.cpt-server.json').read_text())
        desired = pending(root).get('runtime', {}).get('settings', settings)
        if args.get('expectedCore', desired['core']) != desired['core'] or args.get('expectedVersion', desired['version']) != desired['version']:
            raise ValueError('Server target changed during resource selection. Search again for the current version/loader.')
        if not compatible(category, desired['core']):
            raise ValueError('Mods require Fabric/Forge/NeoForge; plugins require Paper.')
        name = args['name']
        if not re.fullmatch(r'[A-Za-z0-9_.+ -]{1,180}\.jar(?:\.disabled)?', name) or name.startswith('.'):
            raise ValueError('Invalid JAR filename.')
        target = child(root, category + '/' + name)
        if action == 'toggle':
            require_stopped(root)
            other = child(root, category + '/' + (name[:-9] if name.endswith('.disabled') else name + '.disabled'))
            if other.exists():
                raise ValueError('Target already exists.')
            target.rename(other)
        else:
            queue = pending(root)
            if any(entry['name'].lower() == name.lower() and entry['category'] == category for entry in queue['resources']):
                raise ValueError('This resource is already queued. Cancel it before replacing.')
            if not name.endswith('.jar'):
                raise ValueError('Preinstalled resources must use a .jar filename.')
            identifier = secrets.token_hex(12)
            staging_name = '.cpt-pending/' + identifier + '.jar'
            staging = child(root, staging_name)
            staging.parent.mkdir(exist_ok=True)
            try:
                if args.get('uploadToken'):
                    token = args['uploadToken']
                    if not re.fullmatch(r'import-[0-9a-f]{32}\.jar', token):
                        raise ValueError('Invalid local import token.')
                    uploaded = Path.home() / '.cpt-server' / token
                    if uploaded.is_symlink() or not uploaded.is_file() or uploaded.stat().st_size > 512 * 1024 * 1024:
                        raise ValueError('Invalid uploaded JAR.')
                    if hashlib.sha256(uploaded.read_bytes()).hexdigest() != args['uploadHash']:
                        raise ValueError('Uploaded JAR checksum mismatch.')
                    shutil.move(str(uploaded), staging)
                else:
                    download(args['url'], staging, args.get('sha512') or args.get('sha256'), 'sha512' if args.get('sha512') else 'sha256')
                if not zipfile.is_zipfile(staging):
                    raise ValueError('Downloaded resource is not a valid JAR archive.')
                queue['resources'].append({'id': identifier, 'category': category, 'name': name, 'staged': staging_name,
                                           'core': desired['core'], 'version': desired['version'],
                                           'digest': hashlib.sha256(staging.read_bytes()).hexdigest(),
                                           'size': staging.stat().st_size, 'replaces': target.exists()})
                write_atomic(child(root, '.cpt-pending.json'), json.dumps(queue).encode())
            except Exception:
                staging.unlink(missing_ok=True)
                raise
            return {'message': 'Preinstalled; active files unchanged. Applies on next restart/start.', 'pendingResources': len(queue['resources'])}
        return {'message': 'Done. Restart the server to apply.'}
    raise ValueError('Unknown action.')


def deployment_journal(root):
    directory = Path.home() / '.cpt-server' / 'deployment-history'
    if any(path.is_symlink() for path in [directory, *directory.parents]):
        raise ValueError('Unsafe deployment history directory.')
    directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    return child(directory, hashlib.sha256(str(root).encode()).hexdigest() + '.json')


def deploy_transaction(root, args):
    global DOWNLOAD_CACHE_ENABLED
    continuing = args['action'] == 'deploy-resume'
    config = child(root, '.cpt-server.json')
    if continuing and config.is_file():
        settings = json.loads(config.read_text('utf-8'))
        if not isinstance(settings.get('java'), str) or not Path(settings['java']).is_absolute():
            raise ValueError('Saved Java path is invalid; check server management before resuming.')
        if not Path(settings['java']).is_file():
            raise ValueError('Installed Java executable is missing; no files were replaced.')
        launch = settings.get('launchArgs', ['-jar', 'server.jar', 'nogui'])
        if not isinstance(launch, list) or not launch or not all(isinstance(item, str) for item in launch):
            raise ValueError('Installed server launch configuration is invalid.')
        entry = launch[0][1:] if launch[0].startswith('@') else launch[1] if len(launch) >= 2 and launch[0] == '-jar' else None
        if not entry or not child(root, entry).is_file():
            raise ValueError('Installed launch artifact is missing; no files were replaced.')
        progress_event('detail', '检测到已安装服务器，跳过下载与安装，从运行状态检查继续')
        return {**settings, 'message': 'Installed server reused.', 'resumedInstalled': True}
    if root.exists() and any(root.iterdir()):
        raise ValueError('Server already exists. Refusing to resume an unrecognized nonempty directory.')
    history = deployment_journal(root)
    if continuing:
        if not history.is_file():
            raise ValueError('没有可延续的部署记录。旧版安装失败时未保留断点；请新建部署。已安装服务器可通过管理页启动。')
        saved = json.loads(history.read_text('utf-8'))
        if saved.get('root') != str(root) or not isinstance(saved.get('request'), dict):
            raise ValueError('Deployment history does not match the target directory.')
        request = saved['request']
        if request.get('acceptEula') is not True:
            raise ValueError('Saved deployment did not accept the Minecraft EULA.')
        if request.get('pack'):
            raise ValueError('本地开服包安装未完成，请重新选择原开服包；不会覆盖目标目录。')
        args = {**request, **{key: value for key, value in args.items() if key in ('progressFile', 'controlFile')}, 'action': 'deploy', 'root': str(root)}
        progress_event('detail', '读取上次部署参数，复用摘要校验通过的缓存重新完成安装')
    else:
        keys = ('acceptEula', 'core', 'version', 'memory', 'port', 'javaMajor', 'javaPath', 'loaderVersion', 'autoConfigure', 'configureFirewall', 'pack')
        request = {key: args[key] for key in keys if key in args}
    write_atomic(history, json.dumps({'root': str(root), 'request': request, 'state': 'installing'}).encode('utf-8'))
    if os.name != 'nt':
        history.chmod(0o600)
    root.parent.mkdir(parents=True, exist_ok=True)
    staging = root.with_name(root.name + '.cpt-stage-' + secrets.token_hex(6))
    old_cache = DOWNLOAD_CACHE_ENABLED
    DOWNLOAD_CACHE_ENABLED = True
    try:
        result = manage(staging, args)
        java = result['java']
        if java.startswith(str(staging) + os.sep):
            result['java'] = str(root) + java[len(str(staging)):]
            config = json.loads(child(staging, '.cpt-server.json').read_text())
            config['java'] = result['java']
            write_atomic(child(staging, '.cpt-server.json'), json.dumps(config).encode())
        deployment_checkpoint()
        if root.exists():
            root.rmdir()
        staging.rename(root)
        write_atomic(history, json.dumps({'root': str(root), 'request': request, 'state': 'installed'}).encode('utf-8'))
        return result
    except Exception:
        write_atomic(history, json.dumps({'root': str(root), 'request': request, 'state': 'failed'}).encode('utf-8'))
        raise
    finally:
        DOWNLOAD_CACHE_ENABLED = old_cache
        if staging.exists():
            shutil.rmtree(staging)


def main():
    global PROGRESS_FILE, CONTROL_FILE
    try:
        if sys.argv[1] == '--request':
            if not re.fullmatch(r'request-[0-9a-f]{32}\.json', sys.argv[2]):
                raise ValueError('Invalid request file.')
            path = Path.home() / '.cpt-server' / sys.argv[2]
            if path.is_symlink() or path.stat().st_size > 2 * 1024 * 1024:
                raise ValueError('Invalid request file.')
            try:
                args = json.loads(path.read_text('utf-8'))
            finally:
                path.unlink()
        else:
            args = json.loads(base64.b64decode(sys.argv[1], validate=True))
        token = args.get('progressFile')
        if token is not None:
            if not re.fullmatch(r'events-[0-9a-f]{32}\.jsonl', token):
                raise ValueError('Invalid progress file.')
            path = Path.home() / '.cpt-server' / token
            if path.is_symlink() or not path.is_file():
                raise ValueError('Unsafe progress file.')
            PROGRESS_FILE = path
            progress_event('detail', '远端代理开始执行：' + str(args.get('action', '')))
        control = args.get('controlFile')
        if control is not None:
            if not re.fullmatch(r'control-[0-9a-f]{32}\.txt', control):
                raise ValueError('Invalid deployment control file.')
            CONTROL_FILE = Path.home() / '.cpt-server' / control
            deployment_checkpoint()
        if args.get('action') in ('network-check', 'network-repair'):
            import network
            with operation_lock(Path.home() / '.cpt-server' / 'network-settings'):
                result = network.check(args) if args['action'] == 'network-check' else network.repair(args)
            print(json.dumps({'ok': True, 'result': result}, ensure_ascii=True))
            return
        root = root_path(args['root'])
        with operation_lock(root):
            if args['action'] in ('deploy', 'deploy-resume'):
                result = deploy_transaction(root, args)
            else:
                result = manage(root, args)
        print(json.dumps({'ok': True, 'result': result}, ensure_ascii=True))
    except Exception as error:
        print(json.dumps({'ok': False, 'error': str(error)}, ensure_ascii=True))
        sys.exit(1)


if __name__ == '__main__':
    main()
