import assert from 'node:assert/strict';
import net from 'node:net';
import { once } from 'node:events';
import { spawn } from 'node:child_process';
import path from 'node:path';
import { randomBytes } from 'node:crypto';
import mc from 'minecraft-protocol';
import { createApplication } from './server.mjs';

const mails = [];
const application = createApplication({ password: 'local-game-client-admin-password', origin: 'http://127.0.0.1:3210', database: ':memory:', sendMail: async mail => mails.push(mail) });
const target = mc.createServer({ host: '127.0.0.1', port: 0, version: '1.16.5', 'online-mode': false, motd: 'CPT protocol verification' });
target.on('error', error => console.error(error));
let host, player;
try {
  await once(target, 'listening');
  await new Promise(resolve => application.listen(0, '127.0.0.1', resolve));
  const gateway = await application.gameGateway.listen(0, '127.0.0.1');
  const endpoint = `http://127.0.0.1:${application.address().port}`;
  async function post(route, body) {
    const response = await fetch(endpoint + route, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    assert.ok(response.ok, await response.clone().text());
    return response.json();
  }
  await post('/api/auth/code', { email: 'host@example.invalid', purpose: 'register' });
  await post('/api/auth/register', { email: 'host@example.invalid', nickname: 'Host', password: 'LocalPass123', code: mails.at(-1).text.match(/\d{6}/)[0] });
  host = spawn('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.resolve('../tests/game-address-client.ps1'), '-Root', path.resolve('..'), '-Endpoint', endpoint, '-TargetPort', String(target.socketServer.address().port)], { windowsHide: true });
  const hostExit = once(host, 'exit');
  let output = '';
  host.stderr.on('data', bytes => console.error(bytes.toString()));
  await Promise.race([
    new Promise(resolve => host.stdout.on('data', bytes => { output += bytes; if (output.includes('READY')) resolve(); })),
    hostExit.then(([code]) => { throw Error(`Host exited before ready: ${code} ${output}`); }),
    new Promise((_, reject) => setTimeout(() => reject(Error('Host startup timeout')), 20000).unref()),
  ]);
  const status = await mc.ping({ host: 'client-world.cpts.pw', port: gateway.port, version: '1.16.5', closeTimeout: 10000, connect(client) { client.setSocket(net.connect(gateway.port, '127.0.0.1')); } });
  assert.equal(status.description.text, 'CPT protocol verification');
  const joined = once(target, 'playerJoin');
  player = mc.createClient({ host: 'client-world.cpts.pw', port: gateway.port, username: 'CptTestPlayer', version: '1.16.5', auth: 'offline', connect(client) { client.setSocket(net.connect(gateway.port, '127.0.0.1')); } });
  const [[remote]] = await Promise.all([
    Promise.race([joined, once(player, 'error').then(([error]) => { throw error; }), new Promise((_, reject) => setTimeout(() => reject(Error('Minecraft login timeout')), 10000).unref())]),
    new Promise(resolve => player.on('state', state => { if (state === 'play') resolve(); })),
  ]);
  // Switch both protocol endpoints to encryption; the gateway must not inspect it.
  const secret = randomBytes(16), payload = randomBytes(48000);
  remote.setEncryption(secret); player.setEncryption(secret);
  const incoming = once(remote, 'custom_payload');
  player.write('custom_payload', { channel: 'cpt:verification', data: payload });
  assert.deepEqual((await incoming)[0].data, payload);
  const outgoing = once(player, 'custom_payload');
  remote.write('custom_payload', { channel: 'cpt:verification', data: payload });
  assert.deepEqual((await outgoing)[0].data, payload);
  const ended = once(player, 'end'); player.end(); await ended;
  host.stdin.end('\n');
  const [code] = await hostExit;
  assert.equal(code, 0, output);
  console.log('Actual .NET RoomClient: prefix allocation/publication, Minecraft status, Java 1.16.5 compressed login, encrypted bidirectional 48 KB payloads, unpublication and release passed.');
} finally {
  if (player && !player.ended) player.end();
  if (host && host.exitCode === null) { host.kill(); await once(host, 'exit'); }
  for (const client of Object.values(target.clients)) client.end();
  target.close();
  application.gameGateway.close();
  await new Promise(resolve => application.close(resolve));
}
