import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { createApplication } from '../server.mjs';
import { createAccounts } from '../accounts.mjs';
import { DatabaseSync } from 'node:sqlite';

test('legacy migration preserves only evidenced user activity dates and runs once', () => {
  const db = new DatabaseSync(':memory:');
  db.exec('CREATE TABLE users(id INTEGER PRIMARY KEY,email TEXT UNIQUE NOT NULL,nickname TEXT NOT NULL,password_hash TEXT NOT NULL,created_at INTEGER NOT NULL,disabled INTEGER NOT NULL DEFAULT 0,last_login INTEGER); CREATE TABLE user_sessions(token_hash TEXT PRIMARY KEY,user_id INTEGER NOT NULL,expires INTEGER NOT NULL,last_seen INTEGER);');
  const now = Math.floor(Date.now() / 1000), old = now - 2 * 86400, recent = now - 60;
  db.prepare('INSERT INTO users VALUES(?,?,?,?,?,?,?)').run(1, 'legacy@example.invalid', 'Legacy', 'dummy', old, 0, old);
  db.prepare('INSERT INTO users VALUES(?,?,?,?,?,?,?)').run(2, 'unused@example.invalid', 'Unused', 'dummy', old, 0, null);
  db.prepare('INSERT INTO user_sessions VALUES(?,?,?,?)').run('hash', 1, now + 1000, recent);
  try {
    const accounts = createAccounts(db, {}, ':memory:');
    const stats = accounts.statistics();
    assert.equal(stats.total, 2); assert.equal(stats.active, 1); assert.equal(stats.monthly, 1);
    assert.equal(db.prepare('SELECT COUNT(*) AS n FROM user_daily_activity').get().n, 1);
    assert.equal(db.prepare('SELECT last_activity FROM users WHERE id=1').get().last_activity, recent);
    db.prepare('DELETE FROM user_daily_activity').run();
    const second = createAccounts(db, {}, ':memory:');
    assert.equal(second.statistics().active, 1);
    assert.equal(db.prepare('SELECT COUNT(*) AS n FROM user_daily_activity').get().n, 0);
  } finally { db.close(); }
});

test('dashboard counts unique registered users, not installations or sessions', async () => {
  const mails = [], origin = 'http://127.0.0.1:3210', password = 'Test1234';
  const server = createApplication({ password: 'admin-statistics-password-32', origin, database: ':memory:', sendMail: async value => mails.push(value) });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  let cookie = '', csrf = '';
  const api = async (route, body, token = '', method = body ? 'POST' : 'GET') => {
    const response = await fetch(base + route, { method, headers: { 'Content-Type': 'application/json', Origin: origin, Cookie: cookie, 'X-CSRF-Token': csrf, ...(token ? { Authorization: 'Bearer ' + token } : {}) }, body: body ? JSON.stringify(body) : undefined });
    return { response, value: response.status === 204 ? null : await response.json() };
  };
  try {
    const admin = await api('/api/admin/login', { password: 'admin-statistics-password-32' });
    cookie = admin.response.headers.get('set-cookie').split(';')[0]; csrf = admin.value.csrf;
    for (let index = 0; index < 3; index++) await api('/api/telemetry/heartbeat', { installationId: randomUUID(), version: '2.13.1.1', channel: 'release' });
    assert.equal((await api('/api/admin/stats')).value.total, 0);
    for (const name of ['First', 'Second']) {
      const email = name.toLowerCase() + '@example.invalid';
      await api('/api/auth/code', { email, purpose: 'register' });
      const user = await api('/api/auth/register', { email, nickname: name, password, code: mails.at(-1).text.match(/\d{6}/)[0] });
      await api('/api/auth/login', { email, password });
      await api('/api/auth/me', undefined, user.value.token);
      await api('/api/auth/logout', {}, user.value.token);
    }
    const stats = (await api('/api/admin/stats')).value;
    assert.equal(stats.basis, 'registered-users'); assert.equal(stats.total, 2); assert.equal(stats.active, 2); assert.equal(stats.monthly, 2);
    assert.equal(stats.daily.at(-1).registered, 2); assert.equal(stats.daily.at(-1).active, 2);
    assert.equal(stats.statuses[0].count, 2); assert.equal(stats.versions, undefined);
    assert.equal(stats.daily.slice(0, -1).every(day => day.active === 0 && day.registered === 0), true);
    const users = (await api('/api/admin/users')).value.users;
    await api('/api/admin/users/' + users[0].id, { disabled: true, reason: 'statistics test' }, '', 'PATCH');
    const after = (await api('/api/admin/stats')).value;
    assert.equal(after.total, 2); assert.equal(after.active, 2); assert.equal(after.statuses[0].count, 1); assert.equal(after.statuses[1].count, 1);
    assert.equal(JSON.stringify(after).includes('@'), false);
  } finally { await new Promise(resolve => server.close(resolve)); }
});
