let generation = 0, checking = false, controller;
function hideActions() {
  const open = document.getElementById('open-cpt'), copy = document.getElementById('copy-invite');
  open.hidden = true; open.removeAttribute('href'); open.onclick = null;
  copy.hidden = true; copy.onclick = null; copy.textContent = '复制邀请链接';
  document.getElementById('download-cpt').hidden = true;
}
async function renderInvitation() {
  const current = ++generation;
  controller?.abort(); controller = new AbortController();
  const requestController = controller;
  checking = true;
  hideActions();
  document.getElementById('invite-code').textContent = '';
  const status = document.getElementById('invite-status');
  const value = location.hash.slice(1), match = /^([A-F0-9]{16})\.([a-f0-9]{64})$/.exec(value);
  if (!match) { status.textContent = '邀请链接格式无效。'; checking = false; return; }
  status.textContent = '正在检查房间邀请……';
  const timeout = setTimeout(() => requestController.abort(), 8000);
  try {
    // Send only a proof in a POST body; the invite credential stays in the fragment.
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(value));
    const proof = Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('');
    const response = await fetch('/api/rooms/invitation-status', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code: match[1], proof }), cache: 'no-store', signal: requestController.signal });
    if (!response.ok) throw Error('Invitation check failed');
    const result = await response.json();
    if (current !== generation) return;
    if (result.state === 'expired') { status.textContent = '邀请已失效，房间已关闭或邀请已重新生成。'; return; }
    if (result.state === 'unavailable') { status.textContent = '房间已满或暂不接受加入。'; return; }
    if (result.state !== 'valid') throw Error('Invalid invitation state');
    status.textContent = '房间邀请已就绪。';
    document.getElementById('invite-code').textContent = match[1];
    const open = document.getElementById('open-cpt'); open.hidden = false;
    open.href = 'cpt://join#' + value;
    open.onclick = () => { status.textContent = '已请求打开 CPT。'; };
    document.getElementById('download-cpt').hidden = false;
    const copy = document.getElementById('copy-invite'); copy.hidden = false;
    copy.onclick = async () => {
      try { await navigator.clipboard.writeText(location.origin + '/room#' + value); if (current === generation) copy.textContent = '已复制'; }
      catch { if (current === generation) status.textContent = '无法访问剪贴板，请使用地址栏中的完整邀请链接。'; }
    };
  } catch {
    if (current === generation) status.textContent = '暂时无法验证邀请，请稍后重试。';
  } finally {
    clearTimeout(timeout);
    if (current === generation) checking = false;
  }
}
renderInvitation();
window.addEventListener('hashchange', renderInvitation);
window.addEventListener('focus', () => { if (!checking) renderInvitation(); });
setInterval(() => { if (!checking && !document.hidden) renderInvitation(); }, 15000);
