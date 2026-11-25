import test from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { WebSocket } from 'ws';
import { createApplication } from '../server.mjs';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';

const origin = 'http://127.0.0.1:3210';
const adminPassword = 'local-admin-test-32-characters';
const password = 'TestPass1234';
test('CPT account passwords accept 6 and 12 characters and reject 5 and 13', async () => {
  const mails = [];
  const server = createApplication({ password: adminPassword, origin, database: ':memory:', sendMail: async message => mails.push(message) });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  const post = async (route, value) => {
    const response = await fetch(base + route, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(value) });
    return { status: response.status, body: await response.json() };
  };
  try {
    for (const length of [6, 12]) {
      const email = `length${length}@example.invalid`, candidate = 'x'.repeat(length);
      await post('/api/auth/code', { email, purpose: 'register' });
      const code = mails.at(-1).text.match(/\d{6}/)[0];
      for (const invalid of ['x'.repeat(5), 'x'.repeat(13)]) {
        assert.equal((await post('/api/auth/register', { email, nickname: 'Boundary', password: invalid, code })).status, 400);
        assert.equal((await post('/api/auth/login', { email, password: invalid })).status, 400);
      }
      assert.equal((await post('/api/auth/register', { email, nickname: 'Boundary', password: candidate, code })).status, 200);
      assert.equal((await post('/api/auth/login', { email, password: candidate })).status, 200);
      await post('/api/auth/code', { email, purpose: 'reset' });
      const reset = mails.at(-1).text.match(/\d{6}/)[0];
      for (const invalid of ['x'.repeat(5), 'x'.repeat(13)]) assert.equal((await post('/api/auth/reset', { email, password: invalid, code: reset })).status, 400);
      const updated = 'y'.repeat(length === 6 ? 12 : 6);
      assert.equal((await post('/api/auth/reset', { email, password: updated, code: reset })).status, 200);
      assert.equal((await post('/api/auth/login', { email, password: updated })).status, 200);
    }
  } finally { await new Promise(resolve => server.close(resolve)); }
});
test('verified registration, login, reset, SMTP admin, private rooms and relay', { timeout: 20000 }, async () => {
  const mail = [], sockets = [];
  const server = createApplication({ password: adminPassword, origin, database: ':memory:', sendMail: async message => mail.push(message) });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  let cookie = '', csrf = '';
  const api = async (route, value, token = '', method = value === undefined ? 'GET' : 'POST', extra = {}) => {
    const response = await fetch(base + route, { method, headers: { 'Content-Type': 'application/json', Origin: origin, Cookie: cookie, 'X-CSRF-Token': csrf, ...(token ? { Authorization: 'Bearer ' + token } : {}), ...extra }, body: value === undefined ? undefined : JSON.stringify(value) });
    const body = response.status === 204 ? null : await response.json(); return { status: response.status, body, response };
  };
  const socket = async ticket => {
    const client = new WebSocket(base.replace('http:', 'ws:') + '/api/rooms/socket', 'cpt.' + ticket);
    sockets.push(client);
    const first = once(client, 'message');
    await once(client, 'open');
    return { client, ready: JSON.parse((await first)[0].toString()) };
  };
  const register = async (email, nickname) => {
    assert.equal((await api('/api/auth/code', { email, purpose: 'register' })).status, 200);
    const code = mail.at(-1).text.match(/\d{6}/)[0];
    assert.equal((await api('/api/auth/register', { email, nickname, password, code: '000000' })).status, 400);
    const result = await api('/api/auth/register', { email, nickname, password, code });
    assert.equal(result.status, 200); return result.body;
  };
  try {
    assert.equal((await api('/api/rooms/create', { port: 25565 })).status, 401);
    const host = await register('host@example.invalid', 'Host');
    const guest = await register('guest@example.invalid', 'Guest');
    const outsider = await register('other@example.invalid', 'Other');
    assert.equal((await api('/api/auth/me', undefined, host.token)).body.user.nickname, 'Host');
    assert.equal((await api('/api/auth/login', { email: host.user.email, password: 'wrongpass' })).status, 401);
    assert.equal((await api('/api/auth/login', { email: host.user.email, password })).status, 200);
    const room = await api('/api/rooms/create', { port: 25565 }, host.token);
    assert.equal(room.status, 200); assert.match(room.body.code, /^[A-F0-9]{16}$/);
    const hc = await socket(room.body.ticket);
    assert.equal(hc.ready.type, 'ready');
    const replay = new WebSocket(base.replace('http:', 'ws:') + '/api/rooms/socket', 'cpt.' + room.body.ticket);
    const replayRejected = once(replay, 'error');
    await replayRejected;
    assert.ok([WebSocket.CLOSING, WebSocket.CLOSED].includes(replay.readyState));
    assert.equal((await api('/api/rooms/join', { code: 'FFFFFFFFFFFFFFFF' }, guest.token)).status, 404);
    assert.equal((await api('/api/rooms/list', undefined, guest.token)).status, 404);
    const join = await api('/api/rooms/join', { code: room.body.code }, guest.token);
    const gc = await socket(join.body.ticket);
    assert.equal(gc.ready.members, 2);
    assert.equal((await api('/api/rooms/stream', { code: room.body.code }, outsider.token)).status, 404);
    const incoming = new Promise(resolve => hc.client.on('message', bytes => { const value = JSON.parse(bytes); if (value.type === 'connection') resolve(value); }));
    const stream = await api('/api/rooms/stream', { code: room.body.code }, guest.token);
    const guestSocket = new WebSocket(base.replace('http:', 'ws:') + '/api/rooms/socket', 'cpt.' + stream.body.ticket); sockets.push(guestSocket);
    const guestReady = once(guestSocket, 'message'); await once(guestSocket, 'open');
    const hs = await socket((await incoming).ticket);
    assert.equal(hs.ready.type, 'ready'); assert.equal(JSON.parse((await guestReady)[0]).type, 'ready');
    const data = once(hs.client, 'message'); guestSocket.send(Buffer.from('minecraft-test'));
    assert.equal((await data)[0].toString(), 'minecraft-test');
    const back = once(guestSocket, 'message'); hs.client.send(Buffer.from('reply-test'));
    assert.equal((await back)[0].toString(), 'reply-test');
    const closed = once(gc.client, 'close');
    await api('/api/rooms/leave', {}, host.token); await closed;
    assert.equal((await api('/api/rooms/join', { code: room.body.code }, outsider.token)).status, 404);
    const login = await api('/api/admin/login', { password: adminPassword });
    cookie = login.response.headers.get('set-cookie').split(';')[0]; csrf = login.body.csrf;
    assert.equal((await api('/api/admin/mail', { enabled: true }, '', 'PUT', { 'X-CSRF-Token': 'bad' })).status, 403);
    assert.equal((await api('/api/admin/mail', { enabled: true, host: 'smtp.example.invalid', port: 465, secure: true, user: 'sender', password: 'secret-smtp', from: 'sender@example.invalid' }, '', 'PUT')).status, 200);
    const settings = (await api('/api/admin/mail')).body;
    assert.equal(settings.password, undefined); assert.equal(settings.passwordConfigured, true);
    assert.equal((await api('/api/admin/mail', { ...settings, host: 'smtp.qq.com', password: '' }, '', 'PUT')).status, 400);
    for (const host of ['smtp.qq.com', 'smtp.163.com', 'smtp.126.com', 'smtp.yeah.net']) {
      assert.equal((await api('/api/admin/mail', { ...settings, host, port: 465, secure: true, password: 'new-authorization-code' }, '', 'PUT')).status, 200);
      const saved = (await api('/api/admin/mail')).body;
      assert.equal(saved.host, host); assert.equal(saved.port, 465); assert.equal(saved.secure, true); assert.equal(saved.password, undefined);
      assert.equal((await api('/api/admin/mail', { ...saved, password: '' }, '', 'PUT')).status, 200);
    }
    assert.equal((await api('/api/admin/mail/test', { email: 'test@example.invalid' })).status, 200);
    const users = (await api('/api/admin/users')).body.users;
    assert.equal(users.length, 3);
    assert.equal((await api('/api/admin/users/' + outsider.user.id, { disabled: true, reason: 'Test disable' }, '', 'PATCH')).status, 200);
    assert.equal((await api('/api/auth/me', undefined, outsider.token)).status, 401);
    assert.equal((await api('/api/auth/code', { email: guest.user.email, purpose: 'reset' })).status, 200);
    const resetCode = mail.at(-1).text.match(/\d{6}/)[0];
    assert.equal((await api('/api/auth/reset', { email: guest.user.email, password: 'NewPass123', code: resetCode })).status, 200);
    assert.equal((await api('/api/auth/me', undefined, guest.token)).status, 401);
    assert.equal((await api('/api/auth/reset', { email: guest.user.email, password, code: resetCode })).status, 400);
    assert.equal((await api('/api/auth/login', { email: guest.user.email, password: 'NewPass123' })).status, 200);
    assert.equal((await api('/api/auth/me', undefined, host.token, 'GET', { Origin: 'https://evil.invalid' })).status, 403);
  } finally {
    for (const client of sockets) client.terminate();
    await new Promise(resolve => setTimeout(resolve, 30));
    await new Promise(resolve => server.close(resolve));
  }
});

test('account sessions and encrypted SMTP settings survive service restart', async () => {
  const folder = mkdtempSync(path.join(tmpdir(), 'cpt-account-persistence-'));
  const database = path.join(folder, 'community.sqlite');
  const mail = [];
  let server, endpoint, token;
  const launch = async () => {
    server = createApplication({ password: adminPassword, origin, database, sendMail: async value => mail.push(value) });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    endpoint = `http://127.0.0.1:${server.address().port}`;
  };
  const post = async (route, body, headers = {}, method = 'POST') => {
    const response = await fetch(endpoint + route, { method, headers: { 'Content-Type': 'application/json', Origin: origin, ...headers }, body: JSON.stringify(body) });
    return { response, data: await response.json() };
  };
  try {
    await launch();
    await post('/api/auth/code', { email: 'persist@example.invalid', purpose: 'register' });
    token = (await post('/api/auth/register', { email: 'persist@example.invalid', nickname: 'Persist', password, code: mail.at(-1).text.match(/\d{6}/)[0] })).data.token;
    const login = await post('/api/admin/login', { password: adminPassword });
    const headers = { Cookie: login.response.headers.get('set-cookie').split(';')[0], 'X-CSRF-Token': login.data.csrf };
    const userId = (await fetch(endpoint + '/api/auth/me', { headers: { Authorization: 'Bearer ' + token } }).then(response => response.json())).user.id;
    assert.equal((await post('/api/admin/users/' + userId, { note: 'Persistent management note', reason: 'review' }, headers, 'PATCH')).response.status, 200);
    const saved = await post('/api/admin/mail', { enabled: true, host: 'smtp.example.invalid', port: 587, secure: false, user: 'sender', password: 'smtp-persistence-secret', from: 'sender@example.invalid' }, headers, 'PUT');
    assert.equal(saved.response.status, 200);
    await new Promise(resolve => server.close(resolve)); server = null;
    assert.equal(readFileSync(database).includes(Buffer.from('smtp-persistence-secret')), false);
    assert.equal(readFileSync(database).includes(Buffer.from(password)), false);
    assert.equal(readFileSync(database).includes(Buffer.from(token)), false);
    await launch();
    const me = await fetch(endpoint + '/api/auth/me', { headers: { Authorization: 'Bearer ' + token } });
    assert.equal(me.status, 200);
    const admin = await post('/api/admin/login', { password: adminPassword });
    const cookie = admin.response.headers.get('set-cookie').split(';')[0];
    const stats = await fetch(endpoint + '/api/admin/stats', { headers: { Cookie: cookie } }).then(response => response.json());
    assert.equal(stats.total, 1); assert.equal(stats.active, 1); assert.equal(stats.daily.at(-1).active, 1);
    const config = await fetch(endpoint + '/api/admin/mail', { headers: { Cookie: cookie } }).then(response => response.json());
    assert.equal(config.passwordConfigured, true); assert.equal(config.password, undefined); assert.equal(config.port, 587);
    const detail = await fetch(endpoint + '/api/admin/users/' + userId, { headers: { Cookie: cookie } }).then(response => response.json());
    assert.equal(detail.user.note, 'Persistent management note');
    const log = await fetch(endpoint + '/api/admin/audit?q=review', { headers: { Cookie: cookie } }).then(response => response.json());
    assert.equal(log.total, 1); assert.equal(log.entries[0].reason, 'review');
    const result = await post('/api/admin/mail', { ...config, password: '' }, { Cookie: cookie, 'X-CSRF-Token': admin.data.csrf }, 'PUT');
    assert.equal(result.response.status, 200);
  } finally {
    if (server) await new Promise(resolve => server.close(resolve));
    rmSync(folder, { recursive: true, force: true });
  }
});

test('registration unavailable before configuring mail; verification is mandatory', async () => {
  const server = createApplication({ password: adminPassword, origin, database: ':memory:' });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  try {
    assert.equal((await fetch(base + '/api/auth/status').then(response => response.json())).emailRegistration, false);
    const response = await fetch(base + '/api/auth/code', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email: 'user@example.invalid', purpose: 'register' }) });
    assert.equal(response.status, 503);
    const register = await fetch(base + '/api/auth/register', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email: 'user@example.invalid', nickname: 'User', password, code: '123456' }) });
    assert.equal(register.status, 400);
  } finally { await new Promise(resolve => server.close(resolve)); }
});
