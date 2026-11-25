import test from 'node:test';
import assert from 'node:assert/strict';
import net from 'node:net';
import { once } from 'node:events';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import { WebSocket } from 'ws';
import { createRequire } from 'node:module';
import { createApplication } from '../server.mjs';
import { createGameAddresses } from '../game-addresses.mjs';
const require = createRequire(import.meta.url);
const { createSerializer, createDeserializer } = require('minecraft-protocol/src/transforms/serializer');

test('prefixes are globally unique, case normalized, at most two per user, ownership protected', () => {
  const db = new DatabaseSync(':memory:'); const addresses = createGameAddresses(db);
  try {
    assert.equal(addresses.allocate(1, 'Survival').prefix, 'survival');
    assert.throws(() => addresses.allocate(2, 'SURVIVAL'), /占用/);
    const random = addresses.allocate(1);
    assert.match(random.prefix, /^mc-[0-9a-f]{12}$/);
    assert.throws(() => addresses.allocate(1, 'third-prefix'), /最多/);
    assert.throws(() => addresses.release(2, 'survival'), /其他/);
    for (const value of ['admin', 'two..dots', '-invalid', 'ab', '中文', 'too-long-' + 'x'.repeat(32)]) assert.throws(() => addresses.allocate(2, value));
    assert.equal(addresses.list(1).length, 2);
    assert.equal(createGameAddresses(db).list(1).length, 2);
    addresses.release(1, random.prefix);
    assert.equal(addresses.allocate(1, 'replacement').address, 'replacement.cpts.pw');
  } finally { db.close(); }
});

test('prefix ownership and quota survive closing and reopening the database', () => {
  const directory = mkdtempSync(path.join(tmpdir(), 'cpt-addresses-'));
  let db;
  try {
    const filename = path.join(directory, 'community.sqlite');
    db = new DatabaseSync(filename);
    const first = createGameAddresses(db);
    first.allocate(1, 'persistent'); first.allocate(1, 'second');
    db.close(); db = new DatabaseSync(filename);
    const restored = createGameAddresses(db);
    assert.equal(restored.owner('persistent'), 1);
    assert.equal(restored.list(1).length, 2);
    assert.throws(() => restored.allocate(2, 'persistent'), /占用/);
    assert.throws(() => restored.allocate(1, 'third'), /最多/);
    restored.release(1, 'persistent');
    assert.equal(restored.allocate(2, 'persistent').address, 'persistent.cpts.pw');
  } finally {
    db?.close();
    assert.equal(path.dirname(path.resolve(directory)), path.resolve(tmpdir()));
    assert.ok(path.basename(directory).startsWith('cpt-addresses-'));
    rmSync(directory, { recursive: true, force: true });
  }
});

test('Minecraft TCP domain routes handshake, preserves bytes and refuses unpublished prefixes', { timeout: 15000 }, async () => {
  const mail = [], sockets = [];
  const server = createApplication({ password: 'admin-game-address-test-32', origin: 'http://127.0.0.1:3210', database: ':memory:', sendMail: async value => mail.push(value) });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const gateway = await server.gameGateway.listen(0, '127.0.0.1');
  const endpoint = `http://127.0.0.1:${server.address().port}`;
  const request = async (route, value, token = '', method = value ? 'POST' : 'GET') => {
    const response = await fetch(endpoint + route, { method, headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: 'Bearer ' + token } : {}) }, body: value ? JSON.stringify(value) : undefined });
    return { status: response.status, body: await response.json() };
  };
  const socket = async ticket => {
    const ws = new WebSocket(endpoint.replace('http:', 'ws:') + '/api/rooms/socket', 'cpt.' + ticket); sockets.push(ws);
    const ready = once(ws, 'message'); await once(ws, 'open'); await ready; return ws;
  };
  try {
    await request('/api/auth/code', { email: 'owner@example.invalid', purpose: 'register' });
    const owner = (await request('/api/auth/register', { email: 'owner@example.invalid', nickname: 'Owner', password: 'Pass1234', code: mail.at(-1).text.match(/\d{6}/)[0] })).body;
    assert.equal((await request('/api/game-addresses')).status, 401);
    assert.equal((await request('/api/game-addresses', { prefix: 'world' }, owner.token)).status, 201);
    assert.equal((await request('/api/game-addresses', { prefix: 'unpublished' }, owner.token)).status, 201);
    assert.equal((await request('/api/game-addresses', { prefix: 'third' }, owner.token)).status, 409);
    assert.equal((await request('/api/game-addresses', undefined, owner.token)).body.addresses[0].published, false);
    const room = (await request('/api/rooms/create', { port: 25565 }, owner.token)).body;
    const host = await socket(room.ticket);
    assert.equal((await request('/api/rooms/publish-address', { code: room.code, prefix: 'world', targetHost: 'target.example.invalid', targetPort: 25566 }, owner.token)).status, 403);
    assert.equal((await request('/api/rooms/publish-address', { code: room.code, prefix: 'world', targetHost: 'target.example.invalid', targetPort: 25566, confirmed: true }, owner.token)).body.address, 'world.cpts.pw');
    const serializer = createSerializer({ version: '1.21.1', state: 'handshaking', isServer: false });
    const parser = createDeserializer({ version: '1.21.1', state: 'handshaking', isServer: true, noErrorLogging: true });
    function handshake(prefix) {
      const bytes = serializer.createPacketBuffer({ name: 'set_protocol', params: { protocolVersion: 767, serverHost: prefix + '.cpts.pw', serverPort: 25565, nextState: 1 } });
      assert.ok(bytes.length < 128);
      return Buffer.concat([Buffer.from([bytes.length]), bytes, Buffer.from([1, 0])]);
    }
    async function rejected(prefix) {
      const client = net.connect(gateway.port, '127.0.0.1'); client.on('error', () => {});
      await once(client, 'connect'); const closed = once(client, 'close'); client.write(handshake(prefix)); await closed;
    }
    await rejected('unpublished');
    const incoming = new Promise(resolve => host.on('message', data => { const value = JSON.parse(data); if (value.type === 'connection') resolve(value); }));
    const tcp = net.connect(gateway.port, '127.0.0.1'); tcp.on('error', () => {}); await once(tcp, 'connect');
    const bytes = handshake('world'); tcp.write(bytes.subarray(0, 1));
    await new Promise(resolve => setTimeout(resolve, 10)); tcp.write(bytes.subarray(1));
    const connection = await incoming;
    const remote = new WebSocket(endpoint.replace('http:', 'ws:') + '/api/rooms/socket', 'cpt.' + connection.ticket); sockets.push(remote);
    const received = [];
    remote.on('message', (bytes, binary) => { if (binary) received.push(bytes); });
    await once(remote, 'open');
    for (let index = 0; index < 100 && !received.length; index++) await new Promise(resolve => setTimeout(resolve, 10));
    assert.equal(received.length, 1);
    const payload = received[0];
    let length = payload[0], offset = 1;
    if (length & 128) { length = (length & 127) | (payload[1] << 7); offset = 2; }
    const decoded = parser.parsePacketBuffer(payload.subarray(offset, offset + length)).data;
    assert.equal(decoded.params.serverHost, 'target.example.invalid'); assert.equal(decoded.params.serverPort, 25566);
    assert.deepEqual(payload.subarray(offset + length), Buffer.from([1, 0]));
    const response = once(tcp, 'data'); remote.send(Buffer.from('raw-encrypted-game-bytes'), { binary: true });
    assert.equal((await response)[0].toString(), 'raw-encrypted-game-bytes');
    const toHost = once(remote, 'message'); tcp.write(Buffer.from('opaque-client-bytes'));
    assert.equal((await toHost)[0].toString(), 'opaque-client-bytes');
    const gone = once(tcp, 'close'); await request('/api/rooms/unpublish-address', { prefix: 'world' }, owner.token); await gone;
    await rejected('world');
    const unknown = net.connect(gateway.port, '127.0.0.1'); unknown.on('error', () => {}); await once(unknown, 'connect'); const refused = once(unknown, 'close'); unknown.write(handshake('unknown')); await refused;
    assert.equal((await request('/api/game-addresses', undefined, owner.token)).body.addresses[0].published, false);
    await request('/api/game-addresses/world', {}, owner.token, 'DELETE');
    assert.equal((await request('/api/game-addresses', undefined, owner.token)).body.addresses.length, 1);
    await request('/api/rooms/publish-address', { code: room.code, prefix: 'unpublished', targetHost: 'target.example.invalid', targetPort: 25566, confirmed: true }, owner.token);
    host.terminate(); await once(host, 'close');
    await rejected('unpublished');
    assert.equal((await request('/api/game-addresses', undefined, owner.token)).body.addresses[0].published, false);
  } finally { for (const ws of sockets) ws.terminate(); server.gameGateway.close(); await new Promise(resolve => setTimeout(resolve, 30)); await new Promise(resolve => server.close(resolve)); }
});
