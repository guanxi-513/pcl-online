import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { once } from 'node:events';
import path from 'node:path';
import { WebSocket } from 'ws';
import { createApplication } from './server.mjs';
const mails = [];
const server = createApplication({ password: 'local-web-invite-admin-password', origin: 'http://127.0.0.1:3210', database: ':memory:', sendMail: async mail => mails.push(mail) });
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const endpoint = `http://127.0.0.1:${server.address().port}`;
let host;
try {
  async function post(route, body, token = '') {
    const response = await fetch(endpoint + route, { method: 'POST', headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: 'Bearer ' + token } : {}) }, body: JSON.stringify(body) });
    assert.ok(response.ok); return response.json();
  }
  const users = {};
  for (const name of ['host', 'guest']) {
    const email = name + '@example.invalid';
    await post('/api/auth/code', { email, purpose: 'register' });
    users[name] = await post('/api/auth/register', { email, nickname: name === 'host' ? 'Host' : 'Guest', password: 'LocalPass123', code: mails.at(-1).text.match(/\d{6}/)[0] });
  }
  const room = await post('/api/rooms/create', { port: 25565, title: 'Web invitation test', password: 'Room123' }, users.host.token);
  host = new WebSocket(endpoint.replace('http:', 'ws:') + '/api/rooms/socket', 'cpt.' + room.ticket);
  const ready = once(host, 'message'); await once(host, 'open'); await ready;
  const invite = room.shareLink.replace('http://127.0.0.1:3210', endpoint);
  const executable = path.resolve(process.argv[2] ?? '../output/CPT-Native-Refresh/CPT.exe');
  const { stdout, stderr } = await promisify(execFile)('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.resolve('../tests/web-invite-client.ps1'), '-Root', path.resolve('..'), '-Endpoint', endpoint, '-Invite', invite, '-Executable', executable], { timeout: 40000, windowsHide: true });
  console.log(stdout.trim()); if (stderr) console.error(stderr);
} finally { host?.terminate(); await new Promise(resolve => setTimeout(resolve, 30)); await new Promise(resolve => server.close(resolve)); }
