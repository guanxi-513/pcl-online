const management = (() => {
  const pages = { users: 1, rooms: 1, audit: 1 }, selected = new Set();
  let userId = null, roomCode = null, userGeneration = 0, roomGeneration = 0, action = null, roomLoading = false;
  const date = value => value ? new Date(value * 1000).toLocaleString('zh-CN', { hour12: false }) : '暂无';
  const bytes = value => value >= 1048576 ? (value / 1048576).toFixed(1) + ' MiB' : value >= 1024 ? (value / 1024).toFixed(1) + ' KiB' : value + ' B';
  const duration = value => `${Math.floor(value / 3600)} 小时 ${Math.floor(value % 3600 / 60)} 分`;
  function textCell(row, value, sub) { const cell = row.insertCell(); cell.textContent = value; if (sub) { const span = document.createElement('span'); span.className = 'table-sub'; span.textContent = sub; cell.append(span); } return cell; }
  function command(label, handler, danger = false) { const button = document.createElement('button'); button.type = 'button'; button.textContent = label; if (danger) button.className = 'danger'; button.onclick = () => execute(button, async () => { try { await handler(); } catch (error) { const dialog = document.querySelector('#room-dialog[open],#user-dialog[open]'); if (dialog) $(dialog.id + '-message').textContent = error.message; else throw error; } }); return button; }
  function actions(row) { const panel = document.createElement('div'); panel.className = 'row-actions'; row.insertCell().append(panel); return panel; }
  function status(row, value, bad) { const cell = row.insertCell(), span = document.createElement('span'); span.className = 'status-tag' + (bad ? ' bad' : ''); span.textContent = value; cell.append(span); }
  function empty(target, count) { target.replaceChildren(); const cell = target.insertRow().insertCell(); cell.colSpan = count; cell.textContent = '暂无符合条件的记录'; }
  function paginate(name, result) { pages[name] = result.page; $(name + '-page').textContent = `第 ${result.page} / ${result.pages} 页，共 ${result.total} 条`; $(name + '-prev').disabled = result.page <= 1; $(name + '-next').disabled = result.page >= result.pages; }
  function query(form, page) { const params = new URLSearchParams(new FormData($(form))); params.set('page', page); return params.toString(); }
  function selection() { $('selection-summary').textContent = `已选择 ${selected.size} 位用户`; $('batch-submit').disabled = selected.size === 0; const boxes = [...$('users').querySelectorAll('input[type=checkbox]')]; $('users-select-all').checked = boxes.length > 0 && boxes.every(box => box.checked); $('users-select-all').indeterminate = boxes.some(box => box.checked) && !boxes.every(box => box.checked); }
  async function loadUsers() {
    const generation = ++userGeneration;
    const result = await api('/api/admin/users?' + query('user-filters', pages.users));
    if (generation !== userGeneration) return;
    if (result.page > result.pages) { pages.users = result.pages; return loadUsers(); }
    selected.clear(); const target = $('users'); target.replaceChildren();
    $('user-summary').textContent = `账号总数 ${result.summary.total}，正常 ${result.summary.enabled}，停用 ${result.summary.disabled}，最近 5 分钟活动 ${result.summary.recent}`;
    if (!result.users.length) empty(target, 8);
    for (const user of result.users) {
      const row = target.insertRow(), cell = row.insertCell(), check = document.createElement('input');
      check.type = 'checkbox'; check.dataset.id = user.id; check.setAttribute('aria-label', '选择用户 ' + user.id); check.onchange = () => { check.checked ? selected.add(user.id) : selected.delete(user.id); selection(); }; cell.append(check);
      textCell(row, user.id); textCell(row, user.email, user.nickname); status(row, user.disabled ? '已停用' : '正常', user.disabled);
      textCell(row, date(user.createdAt)); textCell(row, date(user.lastLogin), user.lastSeen ? '最近活动：' + date(user.lastSeen) : ''); textCell(row, user.sessions);
      const panel = actions(row);
      panel.append(command('详情', () => openUser(user.id)), command(user.disabled ? '启用' : '停用', async () => {
        const label = user.disabled ? '启用账号' : '停用账号';
        const response = await confirmAction(label, `${user.email}（${user.nickname}）${user.disabled ? '' : '。该账号的登录和联机连接将失效。'}`);
        if (!response) return;
        await api('/api/admin/users/' + user.id, 'PATCH', { disabled: !user.disabled, reason: response.reason }); await loadUsers();
      }, !user.disabled));
    }
    paginate('users', result); selection();
  }
  async function openUser(id) {
    const result = await api('/api/admin/users/' + id); userId = id;
    $('user-dialog-title').textContent = `用户 #${id}`;
    $('user-detail-summary').textContent = `${result.user.email} · 注册 ${date(result.user.createdAt)} · 最近登录 ${date(result.user.lastLogin)}${result.user.disabledReason ? ' · 停用原因：' + result.user.disabledReason : ''}`;
    const form = $('user-edit'); form.elements.nickname.value = result.user.nickname; form.elements.disabled.value = String(Boolean(result.user.disabled)); form.elements.note.value = result.user.note; form.elements.reason.value = '';
    rows($('user-sessions'), result.sessions.map(value => ({ createdAt: date(value.createdAt), lastSeen: date(value.lastSeen), expires: date(value.expires) })), ['createdAt', 'lastSeen', 'expires']);
    $('user-rooms').replaceChildren();
    if (!result.rooms.length) $('user-rooms').textContent = '当前不在房间内';
    for (const room of result.rooms) { const line = document.createElement('div'); line.className = 'detail-link'; line.append(document.createTextNode(`${room.code} · ${room.role === 'host' ? '房主' : '成员'}`), command('查看房间', async () => { $('user-dialog').close(); await openRoom(room.code); })); $('user-rooms').append(line); }
    $('user-dialog-message').textContent = '';
    if (!$('user-dialog').open) $('user-dialog').showModal();
  }
  async function loadRooms() {
    if (roomLoading) return;
    roomLoading = true;
    try {
      const generation = ++roomGeneration, result = await api('/api/admin/rooms?' + query('room-filters', pages.rooms));
      if (generation !== roomGeneration) return;
      if (result.page > result.pages) { pages.rooms = result.pages; roomLoading = false; return loadRooms(); }
      const target = $('rooms'); target.replaceChildren();
      $('room-summary').textContent = `房间 ${result.summary.total}，运行中 ${result.summary.active}，成员 ${result.summary.members}，游戏连接 ${result.summary.connections}`;
      if (!result.rooms.length) empty(target, 8);
      for (const room of result.rooms) {
        const row = target.insertRow(); textCell(row, room.code); textCell(row, room.nickname, '#' + room.ownerId);
        status(row, room.locked ? '限制加入' : room.state === 'active' ? '运行中' : '等待连接', room.locked);
        textCell(row, `${room.members}/${room.capacity}`); textCell(row, `${room.readyConnections}/${room.connections}`); textCell(row, duration(room.duration)); textCell(row, bytes(room.uploadBytes + room.downloadBytes));
        const panel = actions(row); panel.append(command('详情', () => openRoom(room.code)), command('关闭', async () => {
          const response = await confirmAction('关闭房间', `${room.code} 的所有成员和游戏连接将立即断开。`); if (!response) return;
          await api('/api/admin/rooms/' + room.code, 'DELETE', { reason: response.reason }); await loadRooms();
        }, true));
      }
      paginate('rooms', result); $('room-updated').textContent = '更新时间：' + new Date().toLocaleTimeString('zh-CN');
    } finally { roomLoading = false; }
  }
  async function openRoom(code) {
    const result = await api('/api/admin/rooms/' + code); roomCode = code;
    $('room-dialog-title').textContent = '房间 ' + code;
    $('room-detail-summary').textContent = `${result.room.title} · ${result.room.public ? '公开房间' : '私密房间'} · ${result.room.passwordRequired ? '有密码' : '无密码'} · 房主 ${result.room.nickname}（#${result.room.ownerId}） · 端口 ${result.room.hostPort} · 创建 ${date(result.room.createdAt)} · 上行 ${bytes(result.room.uploadBytes)} / 下行 ${bytes(result.room.downloadBytes)} · 最近数据 ${date(result.room.lastActivity)}`;
    const form = $('room-edit'); form.elements.locked.checked = result.room.locked; form.elements.capacity.value = result.room.capacity; form.elements.reason.value = '';
    const members = $('room-members'); members.replaceChildren();
    for (const member of result.members) {
      const row = members.insertRow(); textCell(row, '#' + member.id, member.nickname); textCell(row, member.role === 'host' ? '房主' : '成员'); textCell(row, member.connected ? '已连接' : '未连接'); textCell(row, date(member.joinedAt)); textCell(row, date(member.lastHeartbeat));
      const panel = actions(row);
      if (member.role !== 'host') panel.append(command('踢出', async () => {
        const response = await confirmAction('踢出成员', `${member.nickname}（#${member.id}）的本房间连接将断开。`, true); if (!response) return;
        await api(`/api/admin/rooms/${code}/members/${member.id}`, 'DELETE', response); await openRoom(code); await loadRooms();
      }, true));
    }
    const connections = $('room-connections'); connections.replaceChildren();
    if (!result.connections.length) empty(connections, 5);
    for (const connection of result.connections) {
      const row = connections.insertRow(); textCell(row, connection.id.slice(0, 8), date(connection.createdAt)); textCell(row, connection.userId); textCell(row, connection.state === 'ready' ? '转发中' : '连接中'); textCell(row, bytes(connection.uploadBytes) + ' / ' + bytes(connection.downloadBytes));
      actions(row).append(command('断开', async () => { const response = await confirmAction('断开游戏连接', '该 TCP 连接将关闭，用户仍保留房间成员身份。'); if (!response) return; await api(`/api/admin/rooms/${code}/connections/${connection.id}`, 'DELETE', { reason: response.reason }); await openRoom(code); }));
    }
    $('room-blocked').replaceChildren();
    if (!result.blocked.length) $('room-blocked').textContent = '暂无加入限制';
    for (const id of result.blocked) { const line = document.createElement('div'); line.className = 'detail-link'; line.append(document.createTextNode('用户 #' + id), command('解除限制', async () => { const response = await confirmAction('解除加入限制', `用户 #${id} 可重新输入房间号加入。`); if (!response) return; await api(`/api/admin/rooms/${code}/blocked/${id}`, 'DELETE', { reason: response.reason }); await openRoom(code); })); $('room-blocked').append(line); }
    $('room-dialog-message').textContent = '';
    if (!$('room-dialog').open) $('room-dialog').showModal();
  }
  async function loadAudit() {
    const result = await api('/api/admin/audit?' + query('audit-filters', pages.audit));
    if (result.page > result.pages) { pages.audit = result.pages; return loadAudit(); }
    rows($('audit'), result.entries.map(entry => ({ ...entry, createdAt: date(entry.createdAt) })), ['createdAt', 'action', 'target', 'reason']);
    for (const row of $('audit').rows) if (row.cells.length > 3) row.cells[3].className = 'wrap-cell';
    paginate('audit', result);
  }
  function confirmAction(title, description, block = false) {
    $('action-title').textContent = title; $('action-description').textContent = description; $('action-reason').value = ''; $('action-error').textContent = ''; $('action-block').checked = false; $('action-block-label').hidden = !block;
    return new Promise(resolve => { action = resolve; $('action-dialog').showModal(); $('action-reason').focus(); });
  }
  function finishAction(value) { const resolve = action; action = null; $('action-dialog').close(); resolve?.(value); }
  function init() {
    $('action-form').onsubmit = event => { event.preventDefault(); const reason = $('action-reason').value.trim(); if (!reason) { $('action-error').textContent = '请填写操作原因'; return; } finishAction({ reason, block: $('action-block').checked }); };
    $('action-cancel').onclick = () => finishAction(null);
    $('action-dialog').addEventListener('cancel', event => { event.preventDefault(); finishAction(null); });
    document.querySelectorAll('[data-close]').forEach(button => { button.onclick = () => $(button.dataset.close).close(); });
    for (const [name, loader, form] of [['users', loadUsers, 'user-filters'], ['rooms', loadRooms, 'room-filters'], ['audit', loadAudit, 'audit-filters']]) {
      $(form).onsubmit = event => { event.preventDefault(); pages[name] = 1; execute(event.submitter, loader); };
      $(name + '-prev').onclick = event => { pages[name] = Math.max(1, pages[name] - 1); execute(event.currentTarget, loader); };
      $(name + '-next').onclick = event => { pages[name]++; execute(event.currentTarget, loader); };
    }
    $('refresh-rooms').onclick = event => execute(event.currentTarget, loadRooms);
    $('room-detail-refresh').onclick = event => execute(event.currentTarget, () => openRoom(roomCode));
    $('users-select-all').onchange = event => { for (const box of $('users').querySelectorAll('input[type=checkbox]')) { box.checked = event.target.checked; box.checked ? selected.add(Number(box.dataset.id)) : selected.delete(Number(box.dataset.id)); } selection(); };
    $('batch-submit').onclick = event => execute(event.currentTarget, async () => { const ids = [...selected], operation = $('batch-action').value; if (ids.length > 50) throw new Error('一次最多操作 50 位用户'); const response = await confirmAction('批量账号操作', `将对 ${ids.length} 位用户执行“${$('batch-action').selectedOptions[0].textContent}”。`); if (!response) return; await api('/api/admin/users/batch', 'POST', { ids, action: operation, reason: response.reason }); await loadUsers(); });
    $('user-edit').onsubmit = event => { event.preventDefault(); execute(event.submitter, async () => { try { const value = Object.fromEntries(new FormData(event.target)); value.disabled = value.disabled === 'true'; if (value.disabled) { const response = await confirmAction('保存并停用账号', `用户 #${userId} 的登录与联机连接将失效。`); if (!response) return; value.reason = response.reason; } await api('/api/admin/users/' + userId, 'PATCH', value); await openUser(userId); await loadUsers(); $('user-dialog-message').textContent = '账号信息已保存'; } catch (error) { $('user-dialog-message').textContent = error.message; } }); };
    $('user-revoke').onclick = event => execute(event.currentTarget, async () => { const response = await confirmAction('撤销全部登录', `用户 #${userId} 的全部登录与联机连接将失效。`); if (!response) return; await api(`/api/admin/users/${userId}/sessions`, 'DELETE', { reason: response.reason }); await openUser(userId); await loadUsers(); });
    $('room-edit').onsubmit = event => { event.preventDefault(); execute(event.submitter, async () => { try { const form = event.target; await api('/api/admin/rooms/' + roomCode, 'PATCH', { locked: form.elements.locked.checked, capacity: Number(form.elements.capacity.value), reason: form.elements.reason.value }); await openRoom(roomCode); await loadRooms(); $('room-dialog-message').textContent = '房间设置已保存'; } catch (error) { $('room-dialog-message').textContent = error.message; } }); };
    $('room-close').onclick = event => execute(event.currentTarget, async () => { const response = await confirmAction('关闭房间', roomCode + ' 的全部连接将断开。'); if (!response) return; await api('/api/admin/rooms/' + roomCode, 'DELETE', { reason: response.reason }); $('room-dialog').close(); await loadRooms(); });
    setInterval(() => { if (!$('dashboard').hidden && !$('rooms-view').hidden && $('rooms-auto').checked && !document.querySelector('dialog[open]') && !$('room-filters').contains(document.activeElement) && !$('rooms').contains(document.activeElement) && !$('rooms').matches(':hover') && document.visibilityState === 'visible') loadRooms().catch(error => { $('room-updated').textContent = '刷新失败：' + error.message; }); }, 10000);
  }
  if (document.body.dataset.page === 'admin') init();
  return { loadUsers, loadRooms, loadAudit };
})();
