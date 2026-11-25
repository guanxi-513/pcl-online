"""Read-only administration smoke test with a caller-supplied trusted CA."""
import getpass
import http.cookiejar
import json
import ssl
import sys
import urllib.error
import urllib.request

origin = 'https://craftpilot.tolike.cn'
context = ssl.create_default_context(cafile=sys.argv[1] if len(sys.argv) > 1 else None)
cookies = http.cookiejar.CookieJar()
client = urllib.request.build_opener(urllib.request.HTTPSHandler(context=context), urllib.request.HTTPCookieProcessor(cookies))
password = getpass.getpass('Website administrator password: ')


def request(path, body=None, headers=None):
    data = None if body is None else json.dumps(body).encode()
    values = {'Content-Type': 'application/json', 'Origin': origin, **(headers or {})}
    with client.open(urllib.request.Request(origin + path, data=data, headers=values), timeout=20) as response:
        raw = response.read()
        return response.status, json.loads(raw) if raw else None, response.headers


status, session, headers = request('/api/admin/login', {'password': password})
password = None
assert status == 200
assert 'HttpOnly' in headers['Set-Cookie'] and 'Secure' in headers['Set-Cookie']
status, stats, _ = request('/api/admin/stats')
assert status == 200
print('Authenticated statistics:', json.dumps(stats, ensure_ascii=False))
status, releases, _ = request('/api/admin/releases')
assert status == 200
print('Published release records:', len(releases))
try:
    request('/api/admin/releases', {})
except urllib.error.HTTPError as error:
    assert error.code == 403
    print('CSRF protection verified; no release was created.')
else:
    raise RuntimeError('CSRF protection missing.')
request('/api/admin/logout', {}, {'X-CSRF-Token': session['csrf']})
try:
    request('/api/admin/stats')
except urllib.error.HTTPError as error:
    assert error.code == 401
    print('Logout invalidates the session.')
else:
    raise RuntimeError('Session remained active after logout.')
print('Public HTTPS, login, read-only administration and logout checks passed.')
