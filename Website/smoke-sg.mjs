import net from 'node:net';
import { createApplication } from './server.mjs';

const PORT = 39999, GW = 25599;
const origin = `http://127.0.0.1:${PORT}`;
const app = createApplication({ password: 'x'.repeat(16), origin, database: ':memory:' });
await new Promise((resolve, reject) => { app.once('error', reject); app.listen(PORT, '127.0.0.1', resolve); });
await app.gameGateway.listen(GW, '127.0.0.1');

async function call(method, route, body, token) {
  const res = await fetch(origin + route, {
    method,
    headers: { 'content-type': 'application/json', ...(token ? { authorization: 'Bearer ' + token } : {}) },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
  let parsed = null; try { parsed = await res.json(); } catch {}
  return { status: res.status, body: parsed };
}
const out = [];
const wait = ms => new Promise(r => setTimeout(r, ms));

// 主机：昵称 -> 建房 -> WebSocket 控制通道
const host = (await call('POST', '/api/auth/guest', { nickname: '房主' })).body;
const room = (await call('POST', '/api/rooms/create', { port: 25565, public: true, targetKind: 'lan', title: '端到端房间' }, host.token)).body;
out.push(['建房', room.code, room.shareLink.startsWith(origin + '/room#')]);

const hostMessages = [];
const hostWs = new WebSocket(`ws://127.0.0.1:${PORT}/api/rooms/socket`, 'cpt.' + room.ticket);
hostWs.addEventListener('message', event => { if (typeof event.data === 'string') hostMessages.push(JSON.parse(event.data)); });
await new Promise((resolve, reject) => { hostWs.addEventListener('open', resolve); hostWs.addEventListener('error', reject); });
await wait(200);
out.push(['主机 WS ready', hostMessages[0]?.type, hostMessages[0]?.title]);

// 申请前缀 -> 发布游戏地址
const allocated = await call('POST', '/api/game-addresses', { prefix: 'world' }, host.token);
out.push(['申请前缀', allocated.status, allocated.body?.address]);
const published = await call('POST', '/api/rooms/publish-address', { code: room.code, prefix: 'world', confirmed: true, targetHost: '127.0.0.1', targetPort: 25565 }, host.token);
out.push(['发布地址', published.status, published.body?.address]);

// 大厅应能看到公开房间
const lobby = await call('GET', '/api/rooms/lobby', undefined, host.token);
out.push(['大厅可见', lobby.body?.rooms?.length, lobby.body?.rooms?.[0]?.gameAddresses ?? lobby.body?.rooms?.[0]?.title]);

// 网关：用玩家身份发一个原始握手，主机应收到 connection 票据
function varint(value) { const out = []; do { let b = value & 127; value >>>= 7; if (value) b |= 128; out.push(b); } while (value); return Buffer.from(out); }
function mcString(value) { const b = Buffer.from(value, 'utf8'); return Buffer.concat([varint(b.length), b]); }
const hostname = 'world.' + (process.env.MC_BASE_DOMAIN ?? '186.244.238.232.nip.io');
const payload = Buffer.concat([Buffer.from([0x00]), varint(769), mcString(hostname), Buffer.from([0x63, 0xdd]), varint(1)]);
const player = net.connect(GW, '127.0.0.1');
await new Promise(r => player.once('connect', r));
player.write(Buffer.concat([varint(payload.length), payload]));
await wait(400);
out.push(['网关命中房间', hostMessages.some(m => m.type === 'connection')]);
player.destroy();

// 访客：昵称 -> 用房间号加入
const guest = (await call('POST', '/api/auth/guest', { nickname: '访客' })).body;
const joined = await call('POST', '/api/rooms/join', { code: room.code }, guest.token);
out.push(['访客加入', joined.status, joined.body?.code === room.code]);
const guestWs = new WebSocket(`ws://127.0.0.1:${PORT}/api/rooms/socket`, 'cpt.' + joined.body.ticket);
const guestMessages = [];
guestWs.addEventListener('message', event => { if (typeof event.data === 'string') guestMessages.push(JSON.parse(event.data)); });
await new Promise((resolve, reject) => { guestWs.addEventListener('open', resolve); guestWs.addEventListener('error', reject); });
await wait(300);
out.push(['访客 WS ready', guestMessages[0]?.type]);
out.push(['人数广播', hostMessages.filter(m => m.type === 'members').at(-1)?.members]);

// 错误路径
out.push(['旧端点 404', (await call('POST', '/api/auth/login', { email: 'a@b.c', password: 'secret1' })).status]);
out.push(['空昵称 400', (await call('POST', '/api/auth/guest', { nickname: '' })).status]);
out.push(['无 token 建房 401', (await call('POST', '/api/rooms/create', { port: 25565 })).status]);

for (const row of out) console.log(JSON.stringify(row));
hostWs.close(); guestWs.close(); app.close();
