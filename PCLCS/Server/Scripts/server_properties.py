"""Chinese field metadata and lossless patches for Java server.properties files."""
import hashlib
import ipaddress
import re

PROTECTED = {'enable-rcon', 'rcon.port', 'rcon.password', 'broadcast-rcon-to-ops',
             'management-server-secret', 'management-server-enabled'}
SECRET = {'rcon.password', 'management-server-secret'}


def field(key, label, group, kind='text', default='', **extra):
    return {'key': key, 'label': label, 'group': group, 'type': kind, 'default': default, **extra}


FIELDS = [
    field('motd', '服务器简介', '连接与验证', default='A Minecraft Server'),
    field('server-port', '游戏端口', '连接与验证', 'integer', '25565', minimum=1, maximum=65535),
    field('server-ip', '绑定地址', '连接与验证'),
    field('online-mode', '正版在线验证', '连接与验证', 'boolean', 'true'),
    field('enforce-secure-profile', '要求安全玩家档案', '连接与验证', 'boolean', 'true'),
    field('prevent-proxy-connections', '限制代理连接', '连接与验证', 'boolean', 'false'),
    field('enable-status', '允许查询服务器状态', '连接与验证', 'boolean', 'true'),
    field('hide-online-players', '隐藏在线玩家名单', '连接与验证', 'boolean', 'false'),
    field('rate-limit', '连接速率限制', '连接与验证', 'integer', '0', minimum=0, maximum=2147483647),
    field('player-idle-timeout', '玩家挂机超时（分钟）', '连接与验证', 'integer', '0', minimum=0, maximum=2147483647),
    field('max-players', '人数上限', '游戏规则', 'integer', '20', minimum=1, maximum=10000),
    field('difficulty', '难度', '游戏规则', 'enum', 'easy', options={'peaceful': '和平', 'easy': '简单', 'normal': '普通', 'hard': '困难'}),
    field('gamemode', '默认游戏模式', '游戏规则', 'enum', 'survival', options={'survival': '生存', 'creative': '创造', 'adventure': '冒险', 'spectator': '旁观'}),
    field('force-gamemode', '强制默认游戏模式', '游戏规则', 'boolean', 'false'),
    field('white-list', '启用白名单', '游戏规则', 'boolean', 'false'),
    field('enforce-whitelist', '强制执行白名单', '游戏规则', 'boolean', 'false'),
    field('pvp', '允许玩家对战', '游戏规则', 'boolean', 'true'),
    field('hardcore', '极限模式', '游戏规则', 'boolean', 'false'),
    field('allow-flight', '允许飞行', '游戏规则', 'boolean', 'false'),
    field('spawn-protection', '出生点保护半径', '游戏规则', 'integer', '16', minimum=0, maximum=2147483647),
    field('enable-command-block', '启用命令方块', '游戏规则', 'boolean', 'false'),
    field('op-permission-level', '管理员权限等级', '游戏规则', 'integer', '4', minimum=1, maximum=4),
    field('function-permission-level', '函数权限等级', '游戏规则', 'integer', '2', minimum=1, maximum=4),
    field('view-distance', '视距（区块）', '性能', 'integer', '10', minimum=2, maximum=32),
    field('simulation-distance', '模拟距离（区块）', '性能', 'integer', '10', minimum=2, maximum=32),
    field('max-tick-time', '单刻最长时间（毫秒）', '性能', 'integer', '60000', minimum=-1, maximum=9223372036854775807),
    field('network-compression-threshold', '网络压缩阈值（字节）', '性能', 'integer', '256', minimum=-1, maximum=2147483647),
    field('entity-broadcast-range-percentage', '实体广播范围（百分比）', '性能', 'integer', '100', minimum=10, maximum=1000),
    field('max-chained-neighbor-updates', '连锁方块更新上限', '性能', 'integer', '1000000', minimum=-1, maximum=2147483647),
    field('sync-chunk-writes', '同步写入区块', '性能', 'boolean', 'true'),
    field('use-native-transport', '使用原生网络传输', '性能', 'boolean', 'true'),
    field('level-name', '世界文件夹名称', '世界', default='world'),
    field('level-seed', '世界种子', '世界'),
    field('level-type', '世界类型', '世界', 'enum', 'minecraft:normal', options={'minecraft:normal': '默认', 'minecraft:flat': '超平坦', 'minecraft:large_biomes': '巨型生物群系', 'minecraft:amplified': '放大化', 'minecraft:single_biome_surface': '单一生物群系'}),
    field('generate-structures', '生成结构', '世界', 'boolean', 'true'),
    field('max-world-size', '世界最大半径', '世界', 'integer', '29999984', minimum=1, maximum=29999984),
    field('spawn-animals', '生成动物', '世界', 'boolean', 'true'),
    field('spawn-npcs', '生成村民', '世界', 'boolean', 'true'),
    field('spawn-monsters', '生成怪物', '世界', 'boolean', 'true'),
    field('allow-nether', '允许进入下界', '世界', 'boolean', 'true'),
    field('generator-settings', '世界生成器参数', '世界', default='{}'),
    field('initial-enabled-packs', '初始启用数据包', '世界', default='vanilla'),
    field('initial-disabled-packs', '初始禁用数据包', '世界'),
    field('resource-pack', '资源包下载地址', '资源包'),
    field('resource-pack-sha1', '资源包 SHA-1', '资源包'),
    field('resource-pack-id', '资源包 ID', '资源包'),
    field('resource-pack-prompt', '资源包提示消息', '资源包'),
    field('require-resource-pack', '强制使用资源包', '资源包', 'boolean', 'false'),
    field('enable-query', '启用游戏查询协议', '高级网络', 'boolean', 'false'),
    field('query.port', '查询端口（UDP）', '高级网络', 'integer', '25565', minimum=1, maximum=65535),
    field('enable-jmx-monitoring', '启用 JMX 监控', '高级网络', 'boolean', 'false'),
    field('broadcast-console-to-ops', '向管理员广播控制台消息', '高级网络', 'boolean', 'true'),
    field('log-ips', '日志记录玩家 IP', '高级网络', 'boolean', 'true'),
    field('accepts-transfers', '允许服务器转移连接', '高级网络', 'boolean', 'false'),
    field('region-file-compression', '区域文件压缩方式', '性能', 'enum', 'deflate', options={'deflate': '标准压缩', 'lz4': '快速压缩', 'none': '不压缩'}),
    field('pause-when-empty-seconds', '无人在线后暂停（秒）', '性能', 'integer', '60', minimum=-1, maximum=2147483647),
    field('max-build-height', '最大建筑高度', '世界', 'integer', '256', minimum=1, maximum=4096),
    field('bug-report-link', '问题反馈链接', '高级网络'),
    field('text-filtering-config', '聊天过滤配置', '高级网络'),
    field('text-filtering-version', '聊天过滤版本', '高级网络', 'integer', '0', minimum=0, maximum=2147483647),
    field('enable-code-of-conduct', '启用行为准则', '游戏规则', 'boolean', 'false'),
    field('enable-rcon', '远程管理开关', 'CPT 管理', 'boolean', 'true'),
    field('rcon.port', '远程管理端口', 'CPT 管理', 'integer', '25575', minimum=1, maximum=65535),
    field('rcon.password', '远程管理密码', 'CPT 管理'),
    field('broadcast-rcon-to-ops', '广播远程管理消息', 'CPT 管理', 'boolean', 'false'),
    field('management-server-enabled', '管理协议开关', 'CPT 管理', 'boolean', 'false'),
    field('management-server-secret', '管理协议密钥', 'CPT 管理'),
]
SCHEMA = {entry['key']: entry for entry in FIELDS}


def decode(value):
    def unescape(match):
        character = match[2]
        if character == 'u':
            raise ValueError('配置中有无效 Unicode 转义。')
        return {'t': '\t', 'n': '\n', 'r': '\r', 'f': '\f'}.get(character, character)
    # Decode escapes in one pass so an escaped backslash cannot become a Unicode escape.
    result = re.sub(r'\\u([0-9a-fA-F]{4})|\\(.)', lambda match: chr(int(match[1], 16)) if match[1] is not None else unescape(match), value)
    return result.encode('utf-16-le', errors='surrogatepass').decode('utf-16-le', errors='surrogatepass')


def records(text):
    lines = re.findall(r'[^\r\n]*(?:\r\n|\r|\n|$)', text)
    if lines and lines[-1] == '':
        lines.pop()
    result = []
    index = 0
    while index < len(lines):
        start = index
        logical = lines[index].rstrip('\r\n')
        index += 1
        stripped = logical.lstrip(' \t\f')
        if not stripped or stripped.startswith(('#', '!')):
            result.append((None, None, ''.join(lines[start:index])))
            continue
        while (len(logical) - len(logical.rstrip('\\'))) & 1:
            logical = logical[:-1]
            if index >= len(lines):
                break
            logical += lines[index].rstrip('\r\n').lstrip(' \t\f')
            index += 1
        logical = logical.lstrip(' \t\f')
        boundary = len(logical)
        escaped = False
        for position, character in enumerate(logical):
            if escaped:
                escaped = False
            elif character == '\\':
                escaped = True
            elif character in '=:\t\f ':
                boundary = position
                break
        value = logical[boundary:].lstrip(' \t\f')
        if value.startswith(('=', ':')):
            value = value[1:].lstrip(' \t\f')
        result.append((decode(logical[:boundary]), decode(value), ''.join(lines[start:index])))
    return result


def values(text):
    return {key: value for key, value, _ in records(text) if key is not None}


def encode(value, key=False):
    result = []
    for index, character in enumerate(value):
        code = ord(character)
        if character in '\\\t\n\r\f':
            result.append({'\\': '\\\\', '\t': '\\t', '\n': '\\n', '\r': '\\r', '\f': '\\f'}[character])
        elif code < 32 or code > 126:
            for offset in range(0, len(character.encode('utf-16-be', errors='surrogatepass')), 2):
                unit = character.encode('utf-16-be', errors='surrogatepass')[offset:offset + 2]
                result.append('\\u' + unit.hex())
        elif (key and character in '=:# !') or (not key and index == 0 and character == ' '):
            result.append('\\' + character)
        else:
            result.append(character)
    return ''.join(result)


def revision(text):
    return hashlib.sha256(text.encode('utf-8')).hexdigest()


def panel(text):
    current = values(text)
    entries = []
    for definition in FIELDS:
        key = definition['key']
        if key in PROTECTED and key not in current:
            continue
        entry = {**definition, 'value': '[由 CPT 管理]' if key in SECRET else current.get(key, definition['default']), 'present': key in current, 'editable': key not in PROTECTED}
        entries.append(entry)
    for key, value in current.items():
        if key not in SCHEMA:
            secret = bool(re.search(r'(?i)(password|secret|token)', key))
            entries.append(field(key, '其他配置项', '其他配置', 'boolean' if value.lower() in ('true', 'false') else 'text', value='[已隐藏]' if secret else value, present=True, editable=not secret and bool(re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]{0,99}', key))))
    return {'revision': revision(text), 'fields': entries}


def patch(text, changes, expected, confirm_offline=False):
    if expected != revision(text):
        raise ValueError('配置已被其他操作修改，请重新读取后再保存。')
    if not isinstance(changes, dict) or not changes or len(changes) > 200:
        raise ValueError('请选择需要修改的配置项。')
    current = values(text)
    for key, value in changes.items():
        if not isinstance(key, str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]{0,99}', key) or key in PROTECTED or re.search(r'(?i)(password|secret|token)', key):
            raise ValueError('不能修改受保护的管理配置。')
        if key not in SCHEMA and key not in current:
            raise ValueError('未知配置项，请先重新读取配置。')
        if not isinstance(value, str) or len(value) > 8192 or '\0' in value or any(character in value for character in '\r\n'):
            raise ValueError('配置值必须为单行文本。')
        definition = SCHEMA.get(key, {})
        kind = definition.get('type', 'boolean' if current.get(key, '').lower() in ('true', 'false') else 'text')
        if kind == 'boolean' and value not in ('true', 'false'):
            raise ValueError('开关配置值无效。')
        if kind == 'integer':
            if not re.fullmatch(r'-?\d+', value) or not definition['minimum'] <= int(value) <= definition['maximum']:
                raise ValueError(definition['label'] + '超出允许范围。')
        if kind == 'enum' and value not in definition['options']:
            raise ValueError(definition['label'] + '选项无效。')
        if key == 'server-ip' and value:
            try:
                ipaddress.ip_address(value)
            except ValueError as error:
                raise ValueError('绑定地址应留空或填写本机 IP，不能填写域名或端口。') from error
        if key == 'level-name' and (not value or value in ('.', '..') or any(character in value for character in '/\\:*?"<>|')):
            raise ValueError('世界文件夹名称无效。')
        if key == 'resource-pack-sha1' and value and not re.fullmatch(r'[0-9a-fA-F]{40}', value):
            raise ValueError('资源包 SHA-1 应为 40 位十六进制值。')
        if key == 'generator-settings':
            import json
            try:
                generator = json.loads(value)
            except ValueError as error:
                raise ValueError('世界生成器参数不是有效 JSON 对象。') from error
            if not isinstance(generator, dict):
                raise ValueError('世界生成器参数必须为 JSON 对象。')
    if changes.get('online-mode') == 'false' and current.get('online-mode', 'true').lower() != 'false' and confirm_offline is not True:
        raise ValueError('关闭在线验证需要单独确认账号冒用风险。')
    if int(changes.get('server-port', current.get('server-port', '25565'))) == int(current.get('rcon.port', '25575')):
        raise ValueError('游戏端口不能与 RCON 管理端口相同。')
    return replace_values(text, changes)


def replace_values(text, changes):
    """Replace only the final definition of each key, retaining all other lines."""
    parsed = records(text)
    last = {key: index for index, (key, _, _) in enumerate(parsed) if key is not None}
    newline = '\r\n' if '\r\n' in text else '\n'
    output = []
    for index, (key, _, raw) in enumerate(parsed):
        if key in changes and last[key] == index:
            suffix = '\r\n' if raw.endswith('\r\n') else '\n' if raw.endswith('\n') else '\r' if raw.endswith('\r') else ''
            output.append(encode(key, True) + '=' + encode(changes[key]) + suffix)
        else:
            output.append(raw)
    result = ''.join(output)
    for key, value in changes.items():
        if key not in last:
            if result and not result.endswith(('\r', '\n')):
                result += newline
            result += encode(key, True) + '=' + encode(value) + newline
    return result
