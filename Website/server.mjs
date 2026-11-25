import http from 'node:http';
import { DatabaseSync } from 'node:sqlite';
import { randomBytes, scryptSync, timingSafeEqual } from 'node:crypto';
import { readFileSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { isIP } from 'node:net';
import { createAccounts, ApiError } from './accounts.mjs';
import { createRooms } from './rooms.mjs';
import { createGameAddresses } from './game-addresses.mjs';
import { createMinecraftGateway } from './minecraft-gateway.mjs';

const directory = path.dirname(fileURLToPath(import.meta.url));
const now = () => Math.floor(Date.now() / 1000);
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const versionPattern = /^\d{1,4}\.\d{1,4}\.\d{1,4}(?:\.\d{1,4})?$/;
const channels = new Set(['release', 'snapshot']);

export function validateRelease(body) {
  let url;
  try { url = new URL(body.url); } catch { throw new Error('请填写迅雷网盘分享链接'); }
  if (url.protocol !== 'https:' || url.hostname !== 'pan.xunlei.com' || url.username || url.password) throw new Error('仅支持 HTTPS 迅雷网盘分享链接');
  if (!versionPattern.test(body.version ?? '') || !channels.has(body.channel)) throw new Error('版本号或发布通道无效');
  if (!Number.isSafeInteger(body.buildCode) || body.buildCode < 1 || body.buildCode > 2147483647) throw new Error('内部版本号无效');
  if (typeof body.notes !== 'string' || body.notes.length > 20000 || !/^[A-Za-z0-9]{0,16}$/.test(body.extractionCode ?? '')) throw new Error('更新说明或提取码无效');
  return { ...body, url: url.href, extractionCode: body.extractionCode ?? '' };
}

export function createApplication(options = {}) {
  const password = options.password ?? process.env.ADMIN_PASSWORD;
  if (typeof password !== 'string' || password.length < 16) throw new Error('ADMIN_PASSWORD must contain at least 16 characters.');
  const origin = options.origin ?? process.env.PUBLIC_ORIGIN ?? 'http://127.0.0.1:3210';
  const secure = new URL(origin).protocol === 'https:';
  // 明文 HTTP 兜底：宁波 E5 所在链路会对 TLS 注入伪造 RST（HTTPS 全端口被重置），
  // 故允许非 localhost 的 HTTP origin，绕开链路层 TLS 干扰。
  if (!secure && !['localhost', '127.0.0.1', '[::1]'].includes(new URL(origin).hostname)) {
    console.warn('[cpt] PUBLIC_ORIGIN uses plain HTTP (TLS RST workaround):', origin);
  }
  const data = options.database ?? path.join(directory, 'data', 'community.sqlite');
  if (data !== ':memory:') mkdirSync(path.dirname(data), { recursive: true });
  const db = new DatabaseSync(data);
  db.exec(`PRAGMA journal_mode=WAL;
    CREATE TABLE IF NOT EXISTS installations(id TEXT PRIMARY KEY, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL, version TEXT NOT NULL, channel TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS daily_activity(day TEXT NOT NULL, installation_id TEXT NOT NULL, PRIMARY KEY(day,installation_id));
    CREATE TABLE IF NOT EXISTS releases(id INTEGER PRIMARY KEY, version TEXT NOT NULL, build_code INTEGER NOT NULL, channel TEXT NOT NULL, notes TEXT NOT NULL, url TEXT NOT NULL, extraction_code TEXT NOT NULL, published_at INTEGER NOT NULL, active INTEGER NOT NULL DEFAULT 1, UNIQUE(channel,build_code));`);
  db.exec('CREATE TABLE IF NOT EXISTS admin_audit(id INTEGER PRIMARY KEY,created_at INTEGER NOT NULL,action TEXT NOT NULL,target TEXT NOT NULL,reason TEXT NOT NULL);');
  function audit(action, target, reason = '') { db.prepare('INSERT INTO admin_audit(created_at,action,target,reason) VALUES(?,?,?,?)').run(now(), action, String(target), reason); }
  function reason(input) {
    if (typeof input.reason !== 'string' || !input.reason.trim() || input.reason.length > 500) throw new ApiError(400, '请填写操作原因（不超过 500 字）');
    return input.reason.trim();
  }
  const salt = randomBytes(16);
  const accounts = createAccounts(db, options, data);
  const addresses = createGameAddresses(db);
  const passwordHash = scryptSync(password, salt, 32);
  const sessions = new Map();
  const limits = new Map();
  const files = new Map([
    ['/', ['index.html', 'text/html; charset=utf-8']],
    ['/room', ['room.html', 'text/html; charset=utf-8']],
    ['/room-link.js', ['room-link.js', 'text/javascript; charset=utf-8']],
    ['/admin', ['admin.html', 'text/html; charset=utf-8']],
    ['/app.js', ['app.js', 'text/javascript; charset=utf-8']],
    ['/management.js', ['management.js', 'text/javascript; charset=utf-8']],
    ['/style.css', ['style.css', 'text/css; charset=utf-8']],
  ]);
  const headers = {
    'Content-Security-Policy': "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'",
    'X-Content-Type-Options': 'nosniff', 'Referrer-Policy': 'no-referrer',
    'Cache-Control': 'no-store', ...(secure ? { 'Strict-Transport-Security': 'max-age=31536000' } : {}),
  };
  function send(res, status, body) {
    res.writeHead(status, { ...headers, 'Content-Type': 'application/json; charset=utf-8' });
    res.end(body === undefined ? undefined : JSON.stringify(body));
  }
  function rate(req, label, maximum) {
    const peer = req.socket.remoteAddress;
    const forwarded = req.headers['x-forwarded-for']?.split(',').at(-1)?.trim();
    const address = peer === process.env.TRUSTED_PROXY_IP && isIP(forwarded ?? '') ? forwarded : peer;
    const key = `${label}:${address}`;
    const time = now();
    const entry = limits.get(key) ?? { count: 0, expires: time + 60 };
    if (entry.expires < time) { entry.count = 0; entry.expires = time + 60; }
    entry.count++;
    limits.set(key, entry);
    return entry.count <= maximum;
  }
  async function body(req) {
    if (!req.headers['content-type']?.startsWith('application/json')) throw new Error('需要 JSON 请求');
    let size = 0;
    const chunks = [];
    for await (const chunk of req) {
      size += chunk.length;
      if (size > 32768) throw new Error('请求过大');
      chunks.push(chunk);
    }
    const value = JSON.parse(Buffer.concat(chunks).toString('utf8'));
    if (!value || typeof value !== 'object' || Array.isArray(value)) throw new ApiError(400, '请求必须为 JSON 对象');
    return value;
  }
  function session(req) {
    const token = /(?:^|;\s*)cpt_admin_session=([a-f0-9]{64})(?:;|$)/.exec(req.headers.cookie ?? '')?.[1];
    const entry = sessions.get(token);
    return entry && entry.expires > now() ? { token, ...entry } : null;
  }
  function publicRelease(row) {
    return row && { id: row.id, version: row.version, buildCode: row.build_code, channel: row.channel, notes: row.notes, url: row.url, extractionCode: row.extraction_code, publishedAt: row.published_at, active: Boolean(row.active) };
  }
  const server = http.createServer(async (req, res) => {
    try {
      const url = new URL(req.url, origin);
      const route = url.pathname;
      if (req.method === 'GET' && files.has(route)) {
        const [file, type] = files.get(route);
        res.writeHead(200, { ...headers, 'Content-Type': type });
        return res.end(readFileSync(path.join(directory, 'public', file)));
      }
      if (req.method === 'GET' && route === '/logo.png') {
        res.writeHead(200, { ...headers, 'Content-Type': 'image/png' });
        return res.end(readFileSync(path.join(directory, 'public/logo.png')));
      }
      if (req.method === 'GET' && route === '/api/releases/latest') {
        const channel = url.searchParams.get('channel') ?? 'release';
        if (!channels.has(channel)) return send(res, 400, { error: '无效通道' });
        const entry = db.prepare('SELECT * FROM releases WHERE channel=? AND active=1 ORDER BY build_code DESC LIMIT 1').get(channel);
        return send(res, entry ? 200 : 204, publicRelease(entry));
      }
      if (route.startsWith('/api/auth/') || route.startsWith('/api/rooms/') || route.startsWith('/api/game-addresses')) {
        if (req.headers.origin && req.headers.origin !== origin) throw new ApiError(403, '请求来源无效');
        const limit = ['/api/auth/guest', '/api/rooms/join'].includes(route) ? 60 : 120;
        if (!rate(req, route, limit)) throw new ApiError(429, '请求过于频繁，请稍后再试');
        const input = req.method === 'GET' ? {} : await body(req);
        if (route === '/api/rooms/invitation-status' && req.method === 'POST') return send(res, 200, rooms.invitationStatus(input));
        if (route.startsWith('/api/auth/')) {
          if (route === '/api/auth/logout') { const user = accounts.current(req); if (user) rooms.disableUser(user.id); }
          return send(res, 200, await accounts.publicRoute(req, route, input));
        }
        const user = accounts.current(req);
        if (!user) throw new ApiError(401, '请先登录 CPT');
        if (route === '/api/game-addresses' && req.method === 'GET') return send(res, 200, { addresses: rooms.addressList(user.id), limit: 2 });
        if (route === '/api/game-addresses' && req.method === 'POST') return send(res, 201, addresses.allocate(user.id, input.prefix ?? ''));
        if (/^\/api\/game-addresses\/[a-z0-9-]+$/.test(route) && req.method === 'DELETE') { const prefix = addresses.release(user.id, route.split('/').at(-1)); rooms.unpublish(prefix); return send(res, 200, { success: true }); }
        return send(res, 200, await rooms.route(req, route, user, input, url.searchParams));
      }
      if (req.method === 'POST' && route === '/api/telemetry/heartbeat') {
        if (!rate(req, 'heartbeat', 120)) return send(res, 429, { error: '请求过于频繁' });
        const value = await body(req);
        if (!uuid.test(value.installationId ?? '') || !versionPattern.test(value.version ?? '') || !channels.has(value.channel) || Object.keys(value).some(key => !['installationId', 'version', 'channel'].includes(key))) return send(res, 400, { error: '统计数据无效' });
        const time = now();
        db.prepare('INSERT INTO installations VALUES(?,?,?,?,?) ON CONFLICT(id) DO UPDATE SET last_seen=excluded.last_seen, version=excluded.version, channel=excluded.channel').run(value.installationId, time, time, value.version, value.channel);
        db.prepare('INSERT OR IGNORE INTO daily_activity VALUES(?,?)').run(new Date().toISOString().slice(0, 10), value.installationId);
        return send(res, 204);
      }
      if (req.method === 'POST' && route === '/api/admin/login') {
        if (req.headers.origin !== origin) return send(res, 403, { error: '请求来源无效' });
        if (!rate(req, 'login', 8)) return send(res, 429, { error: '登录尝试过多，请稍后再试' });
        const input = await body(req);
        const candidate = typeof input.password === 'string' && input.password.length <= 256 ? input.password : '';
        if (!timingSafeEqual(scryptSync(candidate, salt, 32), passwordHash)) return send(res, 401, { error: '密码错误' });
        const token = randomBytes(32).toString('hex');
        const csrf = randomBytes(24).toString('hex');
        sessions.set(token, { csrf, expires: now() + 28800 });
        res.setHeader('Set-Cookie', `cpt_admin_session=${token}; Path=/; HttpOnly; SameSite=Strict; Max-Age=28800${secure ? '; Secure' : ''}`);
        return send(res, 200, { csrf });
      }
      if (route.startsWith('/api/admin/')) {
        const current = session(req);
        if (!current) return send(res, 401, { error: '请先登录' });
        if (req.method !== 'GET' && (req.headers.origin !== origin || req.headers['x-csrf-token'] !== current.csrf)) return send(res, 403, { error: '请求验证失败，请重新登录' });
        if (req.method === 'GET' && route === '/api/admin/session') return send(res, 200, { csrf: current.csrf });
        if (['/api/admin/mail', '/api/admin/mail/test', '/api/admin/users', '/api/admin/users/batch'].includes(route) || /^\/api\/admin\/users\/\d+(?:\/sessions)?$/.test(route)) {
          if (route === '/api/admin/mail/test' && !rate(req, 'mail-test', 3)) throw new ApiError(429, '测试邮件发送过于频繁');
          const input = req.method === 'GET' ? {} : await body(req);
          const result = await accounts.adminRoute(req, route, input, url.searchParams);
          if (result === null) throw new ApiError(404, '未找到接口');
          if (result.revoke) for (const id of result.userIds ?? [result.userId]) rooms.disableUser(id);
          if (req.method === 'GET' && /^\/api\/admin\/users\/\d+$/.test(route)) result.rooms = rooms.byUser(result.user.id);
          if (req.method !== 'GET') audit(req.method + ' ' + route, result.userIds?.join(',') ?? result.userId ?? 'mail', input.reason ?? '');
          return send(res, 200, result);
        }
        if (route === '/api/admin/audit' && req.method === 'GET') {
          const page = Number(url.searchParams.get('page') ?? 1), q = (url.searchParams.get('q') ?? '').trim();
          if (!Number.isSafeInteger(page) || page < 1 || page > 1000000 || q.length > 100) throw new ApiError(400, '日志筛选参数无效');
          const where = ' WHERE instr(lower(action),lower(?))>0 OR instr(lower(target),lower(?))>0 OR instr(lower(reason),lower(?))>0';
          const total = db.prepare('SELECT COUNT(*) AS n FROM admin_audit' + where).get(q, q, q).n;
          const entries = db.prepare('SELECT id,created_at AS createdAt,action,target,reason FROM admin_audit' + where + ' ORDER BY id DESC LIMIT 25 OFFSET ?').all(q, q, q, (page - 1) * 25);
          return send(res, 200, { entries, total, page, pages: Math.max(1, Math.ceil(total / 25)) });
        }
        if (route === '/api/admin/rooms' && req.method === 'GET') return send(res, 200, rooms.stats(url.searchParams));
        const roomRoute = /^\/api\/admin\/rooms\/([A-F0-9]{16})(?:\/(members|blocked|connections)\/([a-f0-9]+))?$/.exec(route);
        if (roomRoute) {
          const [, code, kind, id] = roomRoute;
          if (!kind && req.method === 'GET') return send(res, 200, rooms.detail(code));
          const input = await body(req), note = reason(input);
          if (!kind && req.method === 'PATCH') { const room = rooms.configure(code, input); audit('room.configure', code, note); return send(res, 200, room); }
          if (!kind && req.method === 'DELETE') { rooms.close(code); audit('room.close', code, note); return send(res, 204); }
          if (req.method === 'DELETE' && kind === 'members' && /^\d+$/.test(id)) {
            if (!Number.isSafeInteger(Number(id)) || input.block !== undefined && typeof input.block !== 'boolean') throw new ApiError(400, '成员操作参数无效');
            rooms.kick(code, Number(id), input.block === true); audit(input.block ? 'room.kick-block' : 'room.kick', code + ':' + id, note); return send(res, 204);
          }
          if (req.method === 'DELETE' && kind === 'blocked' && /^\d+$/.test(id)) { rooms.unblock(code, Number(id)); audit('room.unblock', code + ':' + id, note); return send(res, 204); }
          if (req.method === 'DELETE' && kind === 'connections' && /^[a-f0-9]{32}$/.test(id)) { rooms.disconnect(code, id); audit('room.disconnect', code + ':' + id, note); return send(res, 204); }
          throw new ApiError(404, '未找到房间管理操作');
        }
        if (req.method === 'POST' && route === '/api/admin/logout') {
          sessions.delete(current.token);
          res.setHeader('Set-Cookie', `cpt_admin_session=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0${secure ? '; Secure' : ''}`);
          return send(res, 204);
        }
        if (req.method === 'GET' && route === '/api/admin/stats') {
          return send(res, 200, accounts.statistics());
        }
        if (req.method === 'GET' && route === '/api/admin/releases') return send(res, 200, db.prepare('SELECT * FROM releases ORDER BY published_at DESC,id DESC LIMIT 100').all().map(publicRelease));
        if (req.method === 'POST' && route === '/api/admin/releases') {
          const entry = validateRelease(await body(req));
          const maximum = db.prepare('SELECT MAX(build_code) AS code FROM releases WHERE channel=?').get(entry.channel).code ?? 0;
          if (entry.buildCode <= maximum) return send(res, 409, { error: '内部版本号必须大于该通道已发布版本' });
          const result = db.prepare('INSERT INTO releases(version,build_code,channel,notes,url,extraction_code,published_at) VALUES(?,?,?,?,?,?,?)').run(entry.version, entry.buildCode, entry.channel, entry.notes, entry.url, entry.extractionCode, now());
          return send(res, 201, { id: Number(result.lastInsertRowid) });
        }
        if (req.method === 'PATCH' && /^\/api\/admin\/releases\/\d+$/.test(route)) {
          const input = await body(req);
          if (typeof input.active !== 'boolean') return send(res, 400, { error: '发布状态无效' });
          const result = db.prepare('UPDATE releases SET active=? WHERE id=?').run(Number(input.active), Number(route.split('/').at(-1)));
          return send(res, result.changes ? 204 : 404);
        }
      }
      send(res, 404, { error: '未找到' });
    } catch (error) {
      send(res, error.status ?? 400, { error: error instanceof SyntaxError ? 'JSON 格式错误' : error.message });
    }
  });
  server.requestTimeout = 60000;
  const rooms = createRooms(server, accounts, origin, addresses);
  const gateway = createMinecraftGateway(rooms);
  server.gameGateway = gateway;
  const timer = setInterval(() => {
    for (const [key, entry] of sessions) if (entry.expires < now()) sessions.delete(key);
    for (const [key, entry] of limits) if (entry.expires < now()) limits.delete(key);
    accounts.clean();
    db.prepare('DELETE FROM user_daily_activity WHERE day<?').run(new Date(Date.now() - 90 * 86400000).toISOString().slice(0, 10));
    db.prepare('DELETE FROM admin_audit WHERE created_at<?').run(now() - 180 * 86400);
    db.prepare('DELETE FROM daily_activity WHERE day<?').run(new Date(Date.now() - 90 * 86400000).toISOString().slice(0, 10));
    db.prepare('DELETE FROM installations WHERE last_seen<?').run(now() - 365 * 86400);
  }, 60000).unref();
  server.on('close', () => { clearInterval(timer); gateway.close(); rooms.dispose(); db.close(); });
  return server;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const port = Number(process.env.PORT ?? 3210);
  const application = createApplication();
  if (process.env.MC_GATEWAY_ENABLED === 'true') await application.gameGateway.listen(Number(process.env.MC_GATEWAY_PORT ?? 25565));
  application.listen(port, process.env.HOST ?? '127.0.0.1', () => console.log(`CPT website listening on http://${process.env.HOST ?? '127.0.0.1'}:${port}`));
}
