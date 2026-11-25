"""Bounded remote diagnostics and consent-gated DNS repair. No gateway/firewall edits."""
import base64
from datetime import datetime, timezone
import ipaddress
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import ssl
import subprocess
import sys
import urllib.error
import urllib.request

PUBLIC_DNS = ['223.5.5.5', '1.1.1.1']
BASE_HOST = 'piston-meta.mojang.com'
CORE_HOSTS = {'Paper': 'fill.papermc.io', 'Fabric': 'meta.fabricmc.net',
              'Forge': 'maven.minecraftforge.net', 'NeoForge': 'maven.neoforged.net'}
JAVA_HOST = 'api.adoptium.net'
RESOLV_CONF = Path('/etc/resolv.conf')


def command(argv, privileged=False):
    prefix = ['sudo', '-n'] if privileged and os.name != 'nt' and os.geteuid() != 0 else []
    result = subprocess.run(prefix + argv, capture_output=True, timeout=15)
    if result.returncode:
        raise RuntimeError('网络配置命令执行失败，请确认管理权限和网络服务状态。')
    return result.stdout.decode('utf-8', errors='replace').strip()


def powershell(code):
    code = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $ProgressPreference='SilentlyContinue'; " + code
    encoded = base64.b64encode(code.encode('utf-16-le')).decode()
    return command(['powershell.exe', '-NoProfile', '-NonInteractive', '-EncodedCommand', encoded])


def adapter():
    if os.name == 'nt':
        code = """$ErrorActionPreference='Stop';
        $route=Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Where-Object {$_.NextHop -ne '0.0.0.0'} |
          Sort-Object @{Expression={$_.RouteMetric + (Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4).InterfaceMetric}} | Select-Object -First 1;
        if (-not $route) { ConvertTo-Json @{route=$false} -Compress; exit };
        $nic=Get-NetAdapter | Where-Object {$_.ifIndex -eq $route.InterfaceIndex} | Select-Object -First 1;
        if (-not $nic) { throw 'Default-route adapter not found' };
        $v4=(Get-DnsClientServerAddress -InterfaceIndex $route.InterfaceIndex -AddressFamily IPv4).ServerAddresses;
        $v6=(Get-DnsClientServerAddress -InterfaceIndex $route.InterfaceIndex -AddressFamily IPv6).ServerAddresses;
        $guid=([guid]$nic.InterfaceGuid).ToString('B');
        $static4=(Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\'+$guid) -ErrorAction SilentlyContinue).NameServer;
        $static6=(Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\'+$guid) -ErrorAction SilentlyContinue).NameServer;
        ConvertTo-Json @{route=$true;mode='windows';index=[int]$route.InterfaceIndex;name=$nic.Name;dns=@($v4)+@($v6);automatic=(-not $static4 -and -not $static6)} -Compress"""
        return json.loads(powershell(code))
    try:
        routes = json.loads(command(['ip', '-j', 'route', 'get', '1.1.1.1']))
        interface = next((entry['dev'] for entry in routes if entry.get('dev') and entry.get('type', 'unicast') == 'unicast'), None)
    except (OSError, RuntimeError, ValueError, StopIteration):
        return {'route': False, 'mode': 'unsupported', 'name': ''}
    if not interface or not all(c.isalnum() or c in '_.:-' for c in interface):
        return {'route': False, 'mode': 'unsupported', 'name': ''}
    if shutil.which('resolvectl'):
        try:
            output = command(['resolvectl', 'dns', interface])
            addresses = output.split(': ', 1)[1].split() if ': ' in output else []
            for value in addresses:
                ipaddress.ip_address(value)
            return {'route': True, 'mode': 'resolved', 'name': interface, 'dns': addresses}
        except (OSError, RuntimeError, ValueError):
            pass
    # Managed symlinks and generated configs must use their network manager, not file replacement.
    try:
        contents = RESOLV_CONF.read_text()
        managed = RESOLV_CONF.is_symlink() or any(marker in contents.lower() for marker in ('networkmanager', 'systemd', 'resolvconf', 'generated', 'do not edit'))
        mode = 'unsupported' if managed else 'resolv-file'
    except OSError:
        mode = 'unsupported'
    return {'route': True, 'mode': mode, 'name': interface}


def dns_probe(host):
    try:
        subprocess.run([sys.executable, '-c', 'import socket,sys;socket.getaddrinfo(sys.argv[1],443,type=socket.SOCK_STREAM)', host],
                       capture_output=True, timeout=7, check=True)
        return True
    except (OSError, subprocess.SubprocessError):
        return False


def internet_probe():
    for address in ('1.1.1.1', '223.5.5.5'):
        try:
            with socket.create_connection((address, 443), timeout=3):
                return True
        except OSError:
            pass
    return False


def https_probe(host):
    urls = {BASE_HOST: 'https://piston-meta.mojang.com/mc/game/version_manifest_v2.json',
            JAVA_HOST: 'https://api.adoptium.net/v3/info/available_releases',
            'fill.papermc.io': 'https://fill.papermc.io/v3/projects/paper',
            'meta.fabricmc.net': 'https://meta.fabricmc.net/v2/versions/installer',
            'maven.minecraftforge.net': 'https://maven.minecraftforge.net/',
            'maven.neoforged.net': 'https://maven.neoforged.net/'}
    try:
        request = urllib.request.Request(urls[host], headers={'User-Agent': 'CPT-ServerManager/1.0'})
        with urllib.request.urlopen(request, timeout=8) as response:
            if not response.url.startswith('https://'):
                return 'tls'
            response.read(1024)
        return 'ok'
    except urllib.error.HTTPError as error:
        # A root without directory listing can return 403/404 while TLS and routing work.
        return 'ok' if error.code in (403, 404) and host.startswith('maven.') else 'http'
    except (urllib.error.URLError, OSError) as error:
        detail = error.reason if isinstance(error, urllib.error.URLError) else error
        return 'tls' if isinstance(detail, ssl.SSLError) else 'https'


def check(args):
    core = args.get('core', 'Vanilla')
    if core not in ('Vanilla', *CORE_HOSTS):
        raise ValueError('无效的服务端类型。')
    try:
        config = adapter()
    except (OSError, RuntimeError, ValueError, subprocess.SubprocessError):
        config = {'route': False, 'mode': 'unsupported', 'name': ''}
    hosts = [BASE_HOST, JAVA_HOST] + ([CORE_HOSTS[core]] if core in CORE_HOSTS else [])
    probes = [{'host': host, 'dns': dns_probe(host)} for host in hosts]
    broken = [entry['host'] for entry in probes if not entry['dns']]
    direct = internet_probe() if broken else None
    if broken:
        state = 'dns' if direct else 'offline'
        message = 'DNS 解析失败：' + '、'.join(broken) if direct else '域名解析和公网 TCP 探测均失败，可能是网关、虚拟机网络或外网限制；自动 DNS 修复已停止。'
    else:
        for entry in probes:
            entry['https'] = https_probe(entry['host'])
        state = next((entry['https'] for entry in probes if entry['https'] != 'ok'), 'ok')
        message = {'ok': '远端服务器域名解析和 HTTPS 下载连接检测通过。',
                   'tls': '远端 HTTPS 证书校验失败，请检查系统时间和 CA 证书；不会关闭证书校验。',
                   'http': '下载站点返回异常 HTTP 状态，请检查访问限制。',
                   'https': 'DNS 已解析，但 HTTPS 下载连接失败，请检查代理、线路或防火墙。'}[state]
    repairable = state == 'dns' and config.get('route') is True and config['mode'] in ('resolved', 'resolv-file', 'windows')
    if state == 'dns' and not repairable:
        message += ' 当前网络配置由未知管理器维护或未找到路由，不能自动覆盖。'
    return {'ready': state == 'ok', 'state': state, 'canRepair': repairable, 'message': message,
            'adapter': config.get('name', ''), 'method': config['mode'], 'checks': probes, 'resolvers': PUBLIC_DNS}


def backup_directory():
    path = Path.home() / '.cpt-server' / 'network-backups'
    if any(parent.is_symlink() for parent in [path, *path.parents]):
        raise ValueError('网络备份目录不能为符号链接。')
    path.mkdir(parents=True, exist_ok=True, mode=0o700)
    return path


def set_dns(config, addresses, reset=False):
    if config['mode'] == 'resolved':
        command(['resolvectl', 'dns', config['name'], *addresses], privileged=True)
        command(['resolvectl', 'flush-caches'], privileged=True)
    elif config['mode'] == 'windows':
        index = int(config['index'])
        if not reset:
            for value in addresses:
                ipaddress.ip_address(value)
        setting = '-ResetServerAddresses' if reset else '-ServerAddresses @(' + ','.join("'" + value + "'" for value in addresses) + ')'
        powershell(f"$ErrorActionPreference='Stop'; Set-DnsClientServerAddress -InterfaceIndex {index} {setting}; Clear-DnsClientCache")
    else:
        raise ValueError('不支持的 DNS 修复方式。')


def repair(args):
    if args.get('confirmed') is not True:
        raise ValueError('自动修复 DNS 需要用户明确确认。')
    before = check(args)
    if before['ready']:
        return {**before, 'changed': False, 'message': '网络已恢复，无需更改 DNS。'}
    if not before['canRepair']:
        return {**before, 'changed': False, 'message': before['message']}
    config = adapter()
    if config['mode'] != before['method'] or config.get('name') != before['adapter']:
        raise ValueError('服务器网络配置已变化，请重新检测。')
    folder = backup_directory()
    token = datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S') + '-' + secrets.token_hex(4)
    backup = folder / (token + '.json')
    original = None
    if config['mode'] == 'resolv-file':
        if os.geteuid() != 0 or RESOLV_CONF.is_symlink():
            raise ValueError('此服务器 DNS 文件修复需要 root 权限；未修改设置。')
        original = RESOLV_CONF.read_bytes()
        config['original'] = base64.b64encode(original).decode()
        config['fileHash'] = __import__('hashlib').sha256(original).hexdigest()
    backup.write_text(json.dumps(config), encoding='utf-8')
    if os.name != 'nt':
        backup.chmod(0o600)
    applied = False
    replacement = None
    try:
        if config['mode'] == 'resolv-file':
            if RESOLV_CONF.read_bytes() != original:
                raise ValueError('DNS 文件已被其他程序修改，请重新检测。')
            text = original.decode('utf-8')
            lines = [line for line in text.splitlines() if not line.lstrip().startswith('nameserver ')]
            replacement = ('\n'.join(lines) + '\n' + ''.join('nameserver ' + value + '\n' for value in PUBLIC_DNS)).encode()
            applied = True
            with RESOLV_CONF.open('wb') as output:
                output.write(replacement)
        else:
            applied = True
            set_dns(config, PUBLIC_DNS)
        after = check(args)
        if not after['ready']:
            raise RuntimeError(after['message'])
        return {**after, 'changed': True, 'backup': str(backup),
                'message': 'DNS 修复并复测成功。原配置已备份：' + str(backup) +
                           ('。systemd-resolved 设置为运行时更改，网络重连后可能恢复。' if config['mode'] == 'resolved' else '')}
    except Exception as error:
        if applied:
            try:
                if config['mode'] == 'resolv-file':
                    if RESOLV_CONF.is_symlink():
                        raise ValueError('DNS 文件类型已变化，拒绝覆盖。')
                    current = RESOLV_CONF.read_bytes()
                    if current not in (replacement, original) and current != replacement[:len(current)]:
                        raise ValueError('DNS 文件被其他程序修改，拒绝覆盖。')
                    RESOLV_CONF.write_bytes(original)
                else:
                    set_dns(config, config.get('dns', []) or [''], reset=config.get('automatic', False))
            except Exception:
                return {**before, 'ready': False, 'changed': True, 'backup': str(backup), 'message': '修复或复测失败，自动回退也失败。请使用备份恢复 DNS：' + str(backup)}
        message = '自动修复未通过，已回退原 DNS。' if applied else '未修改 DNS。'
        return {**before, 'ready': False, 'changed': False, 'backup': str(backup), 'message': message + str(error)}
