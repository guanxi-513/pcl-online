import { randomBytes, createHash } from 'node:crypto';

const hash = value => createHash('sha256').update(value).digest('hex');
const time = () => Math.floor(Date.now() / 1000);

export class ApiError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}

// 昵称：2 至 24 位文字 / 数字 / 空格 / 下划线 / 连字符。没有邮箱、没有密码、没有验证码。
function nicknameOf(value) {
  if (typeof value !== 'string') throw new ApiError(400, '请输入昵称');
  const name = value.trim();
  if (!/^[\p{L}\p{N}_ -]{2,24}$/u.test(name)) throw new ApiError(400, '昵称需为 2 至 24 位文字、数字、空格、下划线或连字符');
  return name;
}

const SESSION_SECONDS = 30 * 86400;

// 轻量身份模块：本地私有部署，只做「谁是谁」，不做账号体系。
// 对外接口与旧的完整账号模块保持一致（current / publicRoute / statistics / adminRoute / clean / revokeUser），
// 因此 rooms.mjs 与 server.mjs 的调用点无需改动。
export function createAccounts(db) {
  db.exec(`CREATE TABLE IF NOT EXISTS users(id INTEGER PRIMARY KEY, nickname TEXT NOT NULL, created_at INTEGER NOT NULL, last_login INTEGER, last_activity INTEGER);
    CREATE TABLE IF NOT EXISTS user_sessions(token_hash TEXT PRIMARY KEY, user_id INTEGER NOT NULL, expires INTEGER NOT NULL, created_at INTEGER, last_seen INTEGER);
    CREATE INDEX IF NOT EXISTS sessions_user ON user_sessions(user_id,expires);
    CREATE TABLE IF NOT EXISTS user_daily_activity(day TEXT NOT NULL,user_id INTEGER NOT NULL,PRIMARY KEY(day,user_id));
    CREATE INDEX IF NOT EXISTS users_activity ON users(last_activity);`);
  const userColumns = new Set(db.prepare('PRAGMA table_info(users)').all().map(column => column.name));
  for (const [column, type] of Object.entries({ last_login: 'INTEGER', last_activity: 'INTEGER' })) if (!userColumns.has(column)) db.exec(`ALTER TABLE users ADD COLUMN ${column} ${type}`);
  const sessionColumns = new Set(db.prepare('PRAGMA table_info(user_sessions)').all().map(column => column.name));
  for (const [column, type] of Object.entries({ created_at: 'INTEGER', last_seen: 'INTEGER' })) if (!sessionColumns.has(column)) db.exec(`ALTER TABLE user_sessions ADD COLUMN ${column} ${type}`);

  function activity(id) {
    const timestamp = time(), day = new Date(timestamp * 1000).toISOString().slice(0, 10);
    db.prepare('UPDATE users SET last_activity=? WHERE id=? AND (last_activity IS NULL OR last_activity<?)').run(timestamp, id, timestamp - 60);
    db.prepare('INSERT OR IGNORE INTO user_daily_activity VALUES(?,?)').run(day, id);
  }
  function current(req) {
    const match = /^Bearer ([a-f0-9]{64})$/.exec(req.headers.authorization ?? '');
    if (!match) return null;
    const digest = hash(match[1]);
    const user = db.prepare('SELECT u.id,u.nickname FROM user_sessions s JOIN users u ON s.user_id=u.id WHERE s.token_hash=? AND s.expires>?').get(digest, time()) ?? null;
    if (user) {
      db.prepare('UPDATE user_sessions SET last_seen=? WHERE token_hash=? AND (last_seen IS NULL OR last_seen<?)').run(time(), digest, time() - 60);
      activity(user.id);
    }
    return user;
  }
  function issue(row) {
    db.prepare('DELETE FROM user_sessions WHERE user_id=? AND token_hash NOT IN (SELECT token_hash FROM user_sessions WHERE user_id=? ORDER BY expires DESC LIMIT 4)').run(row.id, row.id);
    const token = randomBytes(32).toString('hex');
    db.prepare('INSERT INTO user_sessions(token_hash,user_id,expires,created_at,last_seen) VALUES(?,?,?,?,?)').run(hash(token), row.id, time() + SESSION_SECONDS, time(), time());
    db.prepare('UPDATE users SET last_login=? WHERE id=?').run(time(), row.id);
    activity(row.id);
    return { token, expiresIn: SESSION_SECONDS, user: { id: row.id, nickname: row.nickname } };
  }
  function create(value) {
    const name = nicknameOf(value);
    const result = db.prepare('INSERT INTO users(nickname,created_at,last_login,last_activity) VALUES(?,?,?,?)').run(name, time(), time(), time());
    return issue({ id: Number(result.lastInsertRowid), nickname: name });
  }
  return {
    current,
    statistics() {
      const total = db.prepare('SELECT COUNT(*) AS n FROM users').get().n;
      const active = db.prepare('SELECT COUNT(*) AS n FROM users WHERE last_activity>=?').get(time() - 86400).n;
      const monthly = db.prepare('SELECT COUNT(*) AS n FROM users WHERE last_activity>=?').get(time() - 30 * 86400).n;
      const start = new Date(Date.now() - 29 * 86400000).toISOString().slice(0, 10);
      const activeDays = new Map(db.prepare('SELECT day,COUNT(*) AS n FROM user_daily_activity WHERE day>=? GROUP BY day').all(start).map(row => [row.day, row.n]));
      const registeredDays = new Map(db.prepare("SELECT date(created_at,'unixepoch') AS day,COUNT(*) AS n FROM users WHERE date(created_at,'unixepoch')>=? GROUP BY day").all(start).map(row => [row.day, row.n]));
      const daily = Array.from({ length: 30 }, (_, index) => { const day = new Date(Date.now() - (29 - index) * 86400000).toISOString().slice(0, 10); return { day, active: activeDays.get(day) ?? 0, registered: registeredDays.get(day) ?? 0 }; });
      return { total, active, monthly, statuses: [{ status: '正常', count: total }], daily, basis: 'nickname-guests' };
    },
    revokeUser(id) { db.prepare('DELETE FROM user_sessions WHERE user_id=?').run(id); },
    clean() { db.prepare('DELETE FROM user_sessions WHERE expires<=?').run(time()); },
    async publicRoute(req, route, input) {
      if (route === '/api/auth/status' && req.method === 'GET') return { emailRegistration: false, nicknameOnly: true };
      if (route === '/api/auth/me' && req.method === 'GET') {
        const user = current(req); if (!user) throw new ApiError(401, '请先登录 CPT'); return { user };
      }
      if (req.method !== 'POST') throw new ApiError(404, '未找到接口');
      if (route === '/api/auth/logout') {
        const token = req.headers.authorization?.slice(7) ?? '';
        if (/^[a-f0-9]{64}$/.test(token)) db.prepare('DELETE FROM user_sessions WHERE token_hash=?').run(hash(token));
        return { success: true };
      }
      // 昵称即用：直接换一个会话 token，不校验任何凭据。
      if (route === '/api/auth/guest') return create(input.nickname);
      if (route === '/api/auth/rename') {
        const user = current(req); if (!user) throw new ApiError(401, '请先登录 CPT');
        const name = nicknameOf(input.nickname);
        db.prepare('UPDATE users SET nickname=? WHERE id=?').run(name, user.id);
        return { user: { id: user.id, nickname: name } };
      }
      throw new ApiError(404, '未找到接口');
    },
    // 管理端：保留「来宾」列表与只读的邮件占位，其余账号管理接口一律返回 null（404）。
    async adminRoute(req, route, input, query = new URLSearchParams()) {
      if (route === '/api/admin/mail' && req.method === 'GET') return { enabled: false, host: '', port: 465, secure: true, user: '', from: '', passwordConfigured: false };
      if (route === '/api/admin/mail' && req.method !== 'GET') throw new ApiError(400, '本部署已移除账号系统，不提供邮件服务');
      if (route === '/api/admin/users' && req.method === 'GET') {
        const search = (query.get('q') ?? '').trim();
        if (search.length > 100) throw new ApiError(400, '搜索参数无效');
        const where = search ? ' WHERE instr(lower(nickname),lower(?))>0 OR CAST(id AS TEXT)=?' : '';
        const parameters = search ? [search, search] : [];
        const total = db.prepare('SELECT COUNT(*) AS n FROM users' + where).get(...parameters).n;
        const users = db.prepare(`SELECT id,nickname,created_at AS createdAt,last_login AS lastLogin,(SELECT COUNT(*) FROM user_sessions s WHERE s.user_id=users.id AND s.expires>?) AS sessions,(SELECT MAX(last_seen) FROM user_sessions s WHERE s.user_id=users.id AND s.expires>?) AS lastSeen FROM users${where} ORDER BY id DESC LIMIT 100`).all(time(), time(), ...parameters);
        return { users, total, page: 1, pageSize: 100, pages: 1, summary: { total, disabled: 0, enabled: total, recent: users.filter(user => user.lastSeen && user.lastSeen >= time() - 300).length } };
      }
      if (/^\/api\/admin\/users\/\d+$/.test(route) && req.method === 'GET') {
        const user = db.prepare('SELECT id,nickname,created_at AS createdAt,last_login AS lastLogin FROM users WHERE id=?').get(Number(route.split('/').at(-1)));
        if (!user) throw new ApiError(404, '用户不存在');
        const sessions = db.prepare('SELECT created_at AS createdAt,last_seen AS lastSeen,expires FROM user_sessions WHERE user_id=? AND expires>? ORDER BY created_at DESC').all(user.id, time());
        return { user, sessions };
      }
      return null;
    },
  };
}
