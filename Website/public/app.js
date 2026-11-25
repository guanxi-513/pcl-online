let csrf = '';
const $ = id => document.getElementById(id);
function message(text) { $('message').textContent = text; $('message').hidden = !text; }
async function api(url, method = 'GET', value) {
  const response = await fetch(url, { method, headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf }, body: value ? JSON.stringify(value) : undefined });
  if (response.status === 204) return null;
  const data = await response.json();
  if (!response.ok) throw new Error(data.error ?? '请求失败');
  return data;
}
function rows(target, values, fields) {
  target.replaceChildren();
  if (!values.length) { const row = target.insertRow(); const cell = row.insertCell(); cell.colSpan = fields.length; cell.textContent = '暂无记录'; return; }
  for (const value of values) { const row = target.insertRow(); for (const field of fields) row.insertCell().textContent = value[field]; }
}
const channelName = value => value === 'release' ? '正式版' : '快照版';
const mailPresets = {
  qq: { host: 'smtp.qq.com', port: 465, secure: true },
  '163': { host: 'smtp.163.com', port: 465, secure: true },
  '126': { host: 'smtp.126.com', port: 465, secure: true },
  yeah: { host: 'smtp.yeah.net', port: 465, secure: true },
};
function identifyMailProvider() {
  const form = $('mail-settings');
  const entry = Object.entries(mailPresets).find(([, preset]) => preset.host === form.elements.host.value.trim().toLowerCase() && preset.port === Number(form.elements.port.value) && preset.secure === form.elements.secure.checked);
  $('mail-provider').value = entry?.[0] ?? 'custom';
}
async function loadDashboard() {
  const [stats, releases] = await Promise.all([api('/api/admin/stats'), api('/api/admin/releases')]);
  for (const key of ['total', 'active', 'monthly']) $(key).textContent = stats[key].toLocaleString();
  rows($('user-statuses'), stats.statuses, ['status', 'count']);
  rows($('daily'), stats.daily, ['day', 'registered', 'active']);
  rows($('releases'), releases.map(v => ({ ...v, channel: channelName(v.channel), status: v.active ? '已发布' : '已撤回' })), ['version', 'buildCode', 'channel', 'status']);
  for (let i = 0; i < releases.length; i++) {
    const release = releases[i];
    const button = document.createElement('button'); button.textContent = release.active ? '撤回' : '恢复';
    button.addEventListener('click', () => execute(button, async () => {
      await api('/api/admin/releases/' + release.id, 'PATCH', { active: !release.active }); await loadDashboard();
    }));
    $('releases').rows[i].insertCell().append(button);
  }
  $('login-panel').hidden = true; $('dashboard').hidden = false; $('logout').hidden = false;
}
async function loadUsers() {
  await management.loadUsers();
}
async function loadMail() {
  const settings = await api('/api/admin/mail');
  const form = $('mail-settings');
  for (const name of ['host', 'port', 'user', 'from']) form.elements[name].value = settings[name];
  form.elements.enabled.checked = settings.enabled; form.elements.secure.checked = settings.secure; form.elements.password.value = '';
  form.elements.password.placeholder = settings.passwordConfigured ? '留空保留当前邮箱的授权码' : 'QQ / 网易填写 SMTP 授权码';
  identifyMailProvider();
  $('mail-status').textContent = settings.enabled ? '邮件服务已启用' : '邮件服务未启用';
}
async function execute(button, action) {
  button.disabled = true; message('');
  try { await action(); } catch (error) { message(error.message); } finally { button.disabled = false; }
}
async function loadPublic() {
  const release = await api('/api/releases/latest?channel=' + $('download-channel').value);
  $('download-version').textContent = release ? release.version : '暂无已发布版本';
  $('download-notes').textContent = release?.notes ?? '';
  $('download-code').textContent = release?.extractionCode ? '提取码：' + release.extractionCode : '';
  $('download-link').hidden = !release;
  if (release) $('download-link').href = release.url;
}
if (document.body.dataset.page === 'admin') {
  const mailForm = $('mail-settings');
  $('mail-provider').addEventListener('change', event => {
    const preset = mailPresets[event.target.value];
    if (!preset) return;
    if (mailForm.elements.host.value.trim().toLowerCase() !== preset.host) {
      mailForm.elements.password.value = '';
      mailForm.elements.password.placeholder = '请输入此邮箱的 SMTP 授权码';
    }
    mailForm.elements.host.value = preset.host;
    mailForm.elements.port.value = String(preset.port);
    mailForm.elements.secure.checked = preset.secure;
  });
  mailForm.elements.host.addEventListener('input', identifyMailProvider);
  mailForm.elements.secure.addEventListener('change', identifyMailProvider);
  mailForm.elements.port.addEventListener('change', event => { mailForm.elements.secure.checked = event.target.value === '465'; identifyMailProvider(); });
  document.querySelectorAll('[data-view]').forEach(button => button.addEventListener('click', () => execute(button, async () => {
    document.querySelectorAll('.admin-view').forEach(view => { view.hidden = view.id !== button.dataset.view; });
    document.querySelectorAll('[data-view]').forEach(tab => tab.setAttribute('aria-pressed', String(tab === button)));
    if (button.dataset.view === 'mail-view') await loadMail();
    if (button.dataset.view === 'users-view') await loadUsers();
    if (button.dataset.view === 'rooms-view') await management.loadRooms();
    if (button.dataset.view === 'audit-view') await management.loadAudit();
  })));
  $('refresh-users').onclick = event => execute(event.currentTarget, loadUsers);
  $('mail-settings').onsubmit = event => { event.preventDefault(); execute(event.submitter, async () => {
    const form = event.target, value = Object.fromEntries(new FormData(form));
    value.enabled = form.elements.enabled.checked; value.secure = form.elements.secure.checked; value.port = Number(value.port);
    await api('/api/admin/mail', 'PUT', value); form.elements.password.value = ''; await loadMail(); message('邮箱配置已保存');
  }); };
  $('mail-test').onsubmit = event => { event.preventDefault(); execute(event.submitter, async () => { await api('/api/admin/mail/test', 'POST', Object.fromEntries(new FormData(event.target))); message('测试邮件已发送'); }); };
  $('login').addEventListener('submit', event => { event.preventDefault(); execute(event.submitter, async () => {
    csrf = (await api('/api/admin/login', 'POST', { password: new FormData(event.target).get('password') })).csrf;
    event.target.reset(); await loadDashboard();
  }); });
  $('publish').addEventListener('submit', event => { event.preventDefault(); execute(event.submitter, async () => {
    const value = Object.fromEntries(new FormData(event.target)); value.buildCode = Number(value.buildCode);
    await api('/api/admin/releases', 'POST', value); event.target.reset(); await loadDashboard(); message('更新已发布');
  }); });
  $('refresh').addEventListener('click', event => execute(event.currentTarget, loadDashboard));
  $('logout').addEventListener('click', event => execute(event.currentTarget, async () => {
    await api('/api/admin/logout', 'POST'); csrf = ''; $('dashboard').hidden = true; $('logout').hidden = true; $('login-panel').hidden = false;
  }));
  api('/api/admin/session').then(value => { csrf = value.csrf; return loadDashboard(); }).catch(() => {});
} else {
  $('download-channel').addEventListener('change', () => loadPublic().catch(error => message(error.message)));
  loadPublic().catch(error => message(error.message));
}
