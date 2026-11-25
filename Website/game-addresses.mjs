import { randomBytes } from 'node:crypto';
import { ApiError } from './accounts.mjs';
import { BASE_DOMAIN } from './config.mjs';

export function createGameAddresses(db) {
  db.exec(`CREATE TABLE IF NOT EXISTS game_addresses(prefix TEXT PRIMARY KEY COLLATE NOCASE,user_id INTEGER NOT NULL,created_at INTEGER NOT NULL);
    CREATE INDEX IF NOT EXISTS addresses_user ON game_addresses(user_id);`);
  const reserved = new Set(['www', 'api', 'admin', 'mail', 'smtp', 'ftp', 'ssh', 'root', 'status', 'localhost']);
  function validate(value) {
    if (typeof value !== 'string') throw new ApiError(400, '域名前缀无效');
    const prefix = value.trim().toLowerCase();
    if (!/^[a-z0-9][a-z0-9-]{1,30}[a-z0-9]$/.test(prefix) || reserved.has(prefix)) throw new ApiError(400, '前缀使用 3 至 32 位小写字母、数字或连字符，首尾不能是连字符');
    return prefix;
  }
  return {
    domain: BASE_DOMAIN, validate,
    owner(prefix) { return db.prepare('SELECT user_id FROM game_addresses WHERE prefix=?').get(prefix)?.user_id; },
    list(id) { return db.prepare('SELECT prefix,created_at AS createdAt FROM game_addresses WHERE user_id=? ORDER BY created_at,prefix').all(id).map(row => ({ ...row, address: row.prefix + '.' + BASE_DOMAIN })); },
    allocate(id, requested = '') {
      let prefix = requested === '' ? 'mc-' + randomBytes(6).toString('hex') : validate(requested);
      db.exec('BEGIN IMMEDIATE');
      try {
        if (db.prepare('SELECT COUNT(*) AS n FROM game_addresses WHERE user_id=?').get(id).n >= 2) throw new ApiError(409, '每个用户最多保留两个域名前缀');
        if (requested === '') while (db.prepare('SELECT 1 FROM game_addresses WHERE prefix=?').get(prefix)) prefix = 'mc-' + randomBytes(6).toString('hex');
        if (db.prepare('SELECT 1 FROM game_addresses WHERE prefix=?').get(prefix)) throw new ApiError(409, '此前缀已被占用，请更换');
        db.prepare('INSERT INTO game_addresses VALUES(?,?,?)').run(prefix, id, Math.floor(Date.now() / 1000));
        db.exec('COMMIT'); return { prefix, address: prefix + '.' + BASE_DOMAIN };
      } catch (error) { db.exec('ROLLBACK'); throw error; }
    },
    release(id, requested) { const prefix = validate(requested); if (this.owner(prefix) !== id) throw new ApiError(403, '不能删除其他用户的前缀'); db.prepare('DELETE FROM game_addresses WHERE prefix=? AND user_id=?').run(prefix, id); return prefix; },
  };
}
