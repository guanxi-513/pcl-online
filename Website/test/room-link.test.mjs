import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { webcrypto } from 'node:crypto';

const script = readFileSync(new URL('../public/room-link.js', import.meta.url), 'utf8');
function page(hash, state = 'valid') {
  const elements = new Map();
  for (const id of ['invite-status', 'invite-code', 'open-cpt', 'copy-invite', 'download-cpt']) elements.set(id, { hidden: true, textContent: '', href: '', removeAttribute(name) { this[name] = ''; } });
  const copied = [];
  const location = { hash, origin: 'https://craftpilot.tolike.cn' }, listeners = {};
  const requests = [], context = vm.createContext({ location, crypto: webcrypto, TextEncoder, Uint8Array, AbortController, setTimeout, clearTimeout, setInterval: handler => { listeners.poll = handler; }, fetch: async (route, options) => { requests.push({ route, body: JSON.parse(options.body) }); const result = typeof state === 'function' ? state() : state; if (result === 'error') throw Error('offline'); return { ok: true, json: async () => ({ state: result }) }; }, window: { addEventListener: (name, handler) => { listeners[name] = handler; } }, document: { getElementById: id => elements.get(id) }, navigator: { clipboard: { writeText: async text => copied.push(text) } } });
  vm.runInContext(script, context);
  return { elements, copied, location, listeners, requests, render: () => vm.runInContext('renderInvitation()', context) };
}
test('web invitation offers explicit CPT activation and retains HTTPS copy fallback', async () => {
  const value = '0123456789ABCDEF.' + 'a'.repeat(64);
  const { elements, copied, render, requests } = page('#' + value);
  assert.equal(elements.get('open-cpt').hidden, true);
  await render();
  assert.equal(elements.get('open-cpt').hidden, false);
  assert.equal(elements.get('open-cpt').href, 'cpt://join#' + value);
  assert.equal(typeof elements.get('open-cpt').onclick, 'function');
  elements.get('open-cpt').onclick();
  assert.match(elements.get('invite-status').textContent, /CPT/);
  await elements.get('copy-invite').onclick();
  assert.deepEqual(copied, ['https://craftpilot.tolike.cn/room#' + value]);
  assert.equal(requests[0].route, '/api/rooms/invitation-status');
  assert.equal(requests[0].body.code, '0123456789ABCDEF');
  assert.equal(JSON.stringify(requests).includes('a'.repeat(64)), false);
});
test('a previously valid page hides actions when the room closes', async () => {
  let state = 'valid';
  const { elements, render } = page('#0123456789ABCDEF.' + 'a'.repeat(64), () => state);
  await render();
  assert.equal(elements.get('open-cpt').hidden, false);
  state = 'expired'; await render();
  assert.equal(elements.get('open-cpt').hidden, true);
  assert.equal(elements.get('copy-invite').hidden, true);
  assert.match(elements.get('invite-status').textContent, /失效/);
});
test('changing invitation fragment clears stale launch actions', async () => {
  const { elements, location, listeners, render } = page('#0123456789ABCDEF.' + 'a'.repeat(64));
  await render();
  location.hash = '#bad'; listeners.hashchange();
  assert.equal(elements.get('open-cpt').hidden, true);
  assert.equal(elements.get('open-cpt').href, '');
  assert.equal(elements.get('open-cpt').onclick, null);
  assert.equal(elements.get('invite-code').textContent, '');
  location.hash = '#1234567890ABCDEF.' + 'b'.repeat(64); await listeners.hashchange();
  assert.equal(elements.get('invite-code').textContent, '1234567890ABCDEF');
  assert.equal(elements.get('open-cpt').hidden, false);
});
test('expired/unavailable invitations and verification failures hide launch actions', async () => {
  for (const state of ['expired', 'unavailable', 'error']) {
    const { elements, render } = page('#0123456789ABCDEF.' + 'a'.repeat(64), state);
    await render();
    assert.equal(elements.get('open-cpt').hidden, true);
    assert.equal(elements.get('open-cpt').href, '');
    assert.equal(elements.get('copy-invite').hidden, true);
    assert.match(elements.get('invite-status').textContent, state === 'expired' ? /失效/ : state === 'unavailable' ? /已满/ : /无法验证/);
  }
});
test('invalid invitation never exposes an application launch action', () => {
  for (const hash of ['', '#bad', '#0123456789ABCDEF.' + 'a'.repeat(63), '#0123456789ABCDEF.' + 'a'.repeat(64) + '&url=other']) {
    const { elements } = page(hash);
    assert.equal(elements.get('open-cpt').hidden, true);
    assert.equal(elements.get('open-cpt').href, '');
    assert.equal(elements.get('copy-invite').hidden, true);
  }
});
