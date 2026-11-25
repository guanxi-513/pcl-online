import { DatabaseSync } from 'node:sqlite';
import { randomBytes, scryptSync } from 'node:crypto';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { once } from 'node:events';
import { WebSocket } from 'ws';
import { createApplication } from './server.mjs';

const folder = mkdtempSync(path.join(tmpdir(), 'cpt-management-preview-'));
const database = path.join(folder, 'community.sqlite'), password = randomBytes(18).toString('hex');
const seed = new DatabaseSync(database);
seed.exec('CREATE TABLE users(id INTEGER PRIMARY KEY,email TEXT UNIQUE NOT NULL,nickname TEXT NOT NULL,password_hash TEXT NOT NULL,created_at INTEGER NOT NULL,disabled INTEGER NOT NULL DEFAULT 0);');
const salt = randomBytes(16).toString('hex'), accountPassword = 'Demo1234', record = salt + ':' + scryptSync(accountPassword, salt, 32).toString('hex');
for (let id = 1; id <= 32; id++) seed.prepare('INSERT INTO users VALUES(?,?,?,?,?,?)').run(id, `demo${id}@example.invalid`, '测试用户' + id, record, Math.floor(Date.now() / 1000) - id * 3600, Number(id % 7 === 0));
seed.close();
const port = Number(process.env.PORT ?? 3213), origin = `http://127.0.0.1:${port}`;
const server = createApplication({ password, origin, database });
await new Promise(resolve => server.listen(port, '127.0.0.1', resolve));
const sockets = [];
const request = async (route, body, token = '') => {
  const response = await fetch(origin + route, { method: 'POST', headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: 'Bearer ' + token } : {}) }, body: JSON.stringify(body) });
  if (!response.ok) throw new Error(await response.text());
  return response.json();
};
const connect = async ticket => {
  const socket = new WebSocket(origin.replace('http:', 'ws:') + '/api/rooms/socket', 'cpt.' + ticket); sockets.push(socket);
  const ready = once(socket, 'message'); await once(socket, 'open'); await ready; return socket;
};
const host = await request('/api/auth/login', { email: 'demo1@example.invalid', password: accountPassword });
const guest = await request('/api/auth/login', { email: 'demo2@example.invalid', password: accountPassword });
const room = await request('/api/rooms/create', { port: 25565 }, host.token);
const owner = await connect(room.ticket);
owner.on('message', async bytes => {
  const event = JSON.parse(bytes); if (event.type !== 'connection') return;
  const socket = await connect(event.ticket);
  socket.on('message', (data, binary) => { if (binary) socket.send(data); });
});
const join = await request('/api/rooms/join', { code: room.code }, guest.token); await connect(join.ticket);
const stream = await request('/api/rooms/stream', { code: room.code }, guest.token);
const game = await connect(stream.ticket); game.send(Buffer.alloc(4096, 1));
console.log(`Management preview: ${origin}/admin`);
console.log(`Temporary administrator password: ${password}`);
console.log('All users and rooms are isolated local fixtures; no email is sent.');
