import net from 'node:net';
import { createRequire } from 'node:module';
import { BASE_DOMAIN } from './config.mjs';
const require = createRequire(import.meta.url);
// 房间域名 = <前缀>.<BASE_DOMAIN>，前缀只允许 3~32 位小写字母/数字/连字符。
const hostPattern = new RegExp('^([a-z0-9][a-z0-9-]{1,30}[a-z0-9])\\.' + BASE_DOMAIN.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '$');
// Parse only the bounded handshake, then transparently forward the encrypted game stream.
const { createDeserializer, createSerializer } = require('minecraft-protocol/src/transforms/serializer');
const parser = createDeserializer({ state: 'handshaking', isServer: true, version: '1.21.1', noErrorLogging: true });
const serializer = createSerializer({ state: 'handshaking', isServer: false, version: '1.21.1' });
function lengthPrefix(buffer) {
  let value = 0;
  for (let i = 0; i < Math.min(buffer.length, 3); i++) {
    const byte = buffer[i]; value |= (byte & 127) << (i * 7);
    if (byte < 128) { if (value < 1 || value > 4096) throw new Error('Invalid handshake size'); return { length: value, size: i + 1 }; }
  }
  if (buffer.length >= 3) throw new Error('Invalid handshake length');
  return null;
}
function frame(packet) {
  let length = packet.length; const prefix = [];
  do { const byte = length & 127; length >>>= 7; prefix.push(byte | (length ? 128 : 0)); } while (length);
  return Buffer.concat([Buffer.from(prefix), packet]);
}
export function createMinecraftGateway(rooms, options = {}) {
  const sockets = new Set(), quotas = new Map();
  const server = net.createServer(socket => {
    const peer = socket.remoteAddress, now = Date.now();
    const quota = quotas.get(peer) ?? { count: 0, until: now + 60000 };
    if (quota.until < now) { quota.count = 0; quota.until = now + 60000; }
    quota.count++; quotas.set(peer, quota);
    if (quota.count > 60 || sockets.size >= 256) { socket.destroy(); return; }
    sockets.add(socket); socket.on('error', () => {}); socket.once('close', () => sockets.delete(socket));
    socket.setTimeout(12000, () => socket.destroy());
    let data = Buffer.alloc(0), routed = false;
    function receive(chunk) {
      if (routed) return;
      data = Buffer.concat([data, chunk]);
      try {
        if (data.length > 65536) throw Error('Handshake buffer exceeded');
        const prefix = lengthPrefix(data);
        if (!prefix) return;
        if (data.length < prefix.size + prefix.length) { if (data.length > 4099) throw Error('Too large'); return; }
        const decoded = parser.parsePacketBuffer(data.subarray(prefix.size, prefix.size + prefix.length)).data;
        if (decoded.name !== 'set_protocol' || ![1, 2].includes(decoded.params.nextState)) throw Error('Not a Minecraft handshake');
        const hostname = decoded.params.serverHost.split('\0')[0].replace(/\.$/, '').toLowerCase();
        const match = hostPattern.exec(hostname);
        if (!match) throw Error('Unknown domain');
        routed = true; socket.pause(); socket.removeListener('data', receive);
        const suffix = decoded.params.serverHost.slice(decoded.params.serverHost.split('\0')[0].length);
        rooms.acceptMinecraft(match[1], socket, (targetHost, targetPort) => {
          const packet = serializer.createPacketBuffer({ name: 'set_protocol', params: { ...decoded.params, serverHost: targetHost + suffix, serverPort: targetPort } });
          return Buffer.concat([frame(packet), data.subarray(prefix.size + prefix.length)]);
        });
      } catch { socket.destroy(); }
    }
    socket.on('data', receive);
  });
  const timer = setInterval(() => { for (const [address, value] of quotas) if (value.until < Date.now()) quotas.delete(address); }, 60000).unref();
  return {
    server,
    listen(port = 25565, host = '0.0.0.0') { return new Promise((resolve, reject) => { server.once('error', reject); server.listen(port, host, () => { server.removeListener('error', reject); resolve(server.address()); }); }); },
    close() { clearInterval(timer); for (const socket of sockets) socket.destroy(); if (server.listening) server.close(); },
  };
}
