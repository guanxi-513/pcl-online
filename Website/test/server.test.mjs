import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { createApplication, validateRelease } from '../server.mjs';

const origin = 'http://127.0.0.1:3210';
const password = 'test-password-32-characters-long';
const release = { version: '2.13.1.2', buildCode: 411, channel: 'release', notes: '<script>alert(1)</script>', url: 'https://pan.xunlei.com/s/example', extractionCode: 'abcd' };

test('release validation rejects non-Xunlei URLs and invalid versions', () => {
  assert.equal(validateRelease(release).buildCode, 411);
  for (const url of ['http://pan.xunlei.com/a', 'https://pan.xunlei.com.evil.test/a', 'https://x:y@pan.xunlei.com/a', 'javascript:alert(1)']) assert.throws(() => validateRelease({ ...release, url }));
  assert.throws(() => validateRelease({ ...release, version: 'bad' }));
});

test('authentication, CSRF, unique statistics, release publishing and withdrawal', async () => {
  const server = createApplication({ password, origin, database: ':memory:' });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  let cookie = '', csrf = '';
  const request = (route, method = 'GET', value, extra = {}) => fetch(base + route, {
    method, headers: { 'Content-Type': 'application/json', Origin: origin, Cookie: cookie, 'X-CSRF-Token': csrf, ...extra },
    body: value === undefined ? undefined : JSON.stringify(value),
  });
  try {
    assert.equal((await request('/api/admin/stats')).status, 401);
    assert.equal((await request('/api/admin/login', 'POST', { password: 'bad' })).status, 401);
    assert.equal((await request('/api/admin/login', 'POST', { password }, { Origin: 'https://evil.test' })).status, 403);
    const login = await request('/api/admin/login', 'POST', { password });
    assert.equal(login.status, 200);
    cookie = login.headers.get('set-cookie').split(';')[0];
    assert.match(login.headers.get('set-cookie'), /HttpOnly; SameSite=Strict/);
    csrf = (await login.json()).csrf;
    const installationId = randomUUID();
    const payload = { installationId, version: '2.13.1.1', channel: 'release' };
    assert.equal((await request('/api/telemetry/heartbeat', 'POST', payload)).status, 204);
    assert.equal((await request('/api/telemetry/heartbeat', 'POST', payload)).status, 204);
    assert.equal((await request('/api/telemetry/heartbeat', 'POST', { ...payload, installationId: randomUUID(), channel: 'snapshot' })).status, 204);
    assert.equal((await request('/api/telemetry/heartbeat', 'POST', { ...payload, account: 'not allowed' })).status, 400);
    const stats = await (await request('/api/admin/stats')).json();
    assert.equal(stats.total, 0); assert.equal(stats.active, 0); assert.equal(stats.basis, 'registered-users'); assert.equal(stats.daily.length, 30);
    assert.equal((await request('/api/releases/latest')).status, 204);
    assert.equal((await request('/api/admin/releases', 'POST', release, { 'X-CSRF-Token': 'wrong' })).status, 403);
    const published = await request('/api/admin/releases', 'POST', release);
    assert.equal(published.status, 201);
    const { id } = await published.json();
    assert.equal((await request('/api/admin/releases', 'POST', release)).status, 409);
    const latest = await (await request('/api/releases/latest')).json();
    assert.equal(latest.url, release.url); assert.equal(latest.extractionCode, 'abcd');
    assert.equal((await request('/api/admin/releases/' + id, 'PATCH', { active: false })).status, 204);
    assert.equal((await request('/api/releases/latest')).status, 204);
    assert.equal((await request('/api/admin/releases/' + id, 'PATCH', { active: true })).status, 204);
    assert.equal((await request('/api/releases/latest')).status, 200);
    assert.equal((await request('/api/admin/logout', 'POST')).status, 204);
    assert.equal((await request('/api/admin/stats')).status, 401);
    const page = await request('/admin');
    assert.equal(page.status, 200); assert.match(page.headers.get('content-security-policy'), /frame-ancestors 'none'/);
    assert.equal((await request('/logo.png')).status, 200);
    const homepage = await (await request('/')).text();
    assert.doesNotMatch(homepage, /href=["']\/admin["']/);
    assert.doesNotMatch(homepage, /管理后台/);
    assert.match(homepage, /href="https:\/\/afdian\.com\/a\/HUASQlaodaHH"/);
    assert.match(homepage, /src="\/logo\.png\?v=cpt-1" alt="CPT"/);
    assert.match(homepage, /<title>CPT 方舟领航<\/title>/);
    assert.match(homepage, /CPT 增补内容 © 2026 HUASQ/);
    assert.match(homepage, /PCL 原始代码 © 成都瓜皮龙科技有限公司/);
    assert.match(homepage, /href="https:\/\/meloong\.com\/afd\/a\/LTCat"/);
    const admin = await page.text();
    assert.match(admin, /href="\/logo\.png\?v=cpt-1"/);
    assert.match(admin, /<title>管理后台 \| CPT 方舟领航<\/title>/);
  } finally { await new Promise(resolve => server.close(resolve)); }
});
