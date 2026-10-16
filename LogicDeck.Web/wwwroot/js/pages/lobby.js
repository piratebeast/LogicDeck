import { $, el, toast, timeAgo, initials } from '../core/dom.js';
import { createHub } from '../core/hub.js';

const hub = createHub();

const ui = {
  connState: $('#connState'),
  identityChip: $('#identityChip'),
  gateCard: $('#gateCard'),
  joinForm: $('#joinForm'),
  nameInput: $('#nameInput'),
  dash: $('#dash'),
  roomGrid: $('#roomGrid'),
  roomCount: $('#roomCount'),
  emptyDash: $('#emptyDash'),
  newRoomBtn: $('#newRoomBtn'),
  newRoomDlg: $('#newRoomDlg'),
  newRoomForm: $('#newRoomForm'),
  dlgTitle: $('#dlgTitle'),
};

function setConnStatus(status) {
  const map = {
    online: ['', 'LINK OK'],
    reconnecting: ['warn', 'RECONNECTING'],
    offline: ['off', 'OFFLINE'],
  };
  const [cls, label] = map[status] ?? map.offline;
  ui.connState.className = `dot-status ${cls}`;
  ui.connState.lastElementChild.textContent = label;
}

hub.conn.onreconnecting(() => { setConnStatus('reconnecting'); });
hub.conn.onreconnected(() => {
  setConnStatus('online');
  refreshRooms();
});
hub.conn.onclose(() => setConnStatus('offline'));

function renderIdentity() {
  ui.identityChip.hidden = false;
  ui.identityChip.replaceChildren(
    el('span', { class: 'avatar', style: `background:${hub.state.color}` }, initials(hub.state.name)),
    el('span', {}, hub.state.name),
    el('button', {
      class: 'dlg-close', type: 'button', title: 'Switch name',
      style: 'width:22px;height:22px;margin-left:2px',
      onclick: switchName,
    }, el('i', { class: 'bi bi-person-fill-gear', html: '' })),
  );
}

function switchName() {
  sessionStorage.removeItem('ld.name');
  sessionStorage.removeItem('ld.color');
  sessionStorage.removeItem('ld.sid');
  location.reload();
}

function avatarStack(members) {
  const shown = members.slice(0, 3);
  const nodes = shown.map((m) =>
    el('span', { class: 'avatar', title: m.name, style: `background:${m.color}` }, initials(m.name)));
  if (members.length > 3) {
    nodes.push(el('span', { class: 'more' }, `+${members.length - 3}`));
  }
  return el('div', { class: 'rc-members' }, ...nodes);
}

async function deleteRoom(room) {
  if (!window.confirm(`Delete “${room.title}”? This cannot be undone.`)) return;
  try {
    await hub.invoke('DeleteRoom', room.id);
    toast(`Circuit “${room.title}” deleted.`, 'ok');
  } catch (err) {
    toast(err.message, 'error');
  }
}

function renderRooms(rooms) {
  ui.roomCount.textContent = String(rooms.length);
  ui.emptyDash.hidden = rooms.length > 0;
  ui.roomGrid.replaceChildren(...rooms.map((r) => {
    const meta = el('div', { class: 'rc-meta' },
      el('span', {}, 'GRID ', el('b', {}, r.gridSize)),
      el('span', {}, '·'),
      el('span', {}, `${r.nodeCount} PARTS`),
      el('span', {}, '·'),
      el('span', {}, timeAgo(r.createdUtc).toUpperCase()),
    );
    const foot = el('div', { class: 'rc-foot' },
      avatarStack(r.members ?? []),
      r.running ? el('span', { class: 'badge-running' }, '● RUNNING') : el('span'),
    );
    const openRoom = () => { location.href = `/circuit/${r.id}`; };
    const deleteButton = r.ownerSessionId === hub.state.sessionId
      ? el('button', {
        class: 'room-delete',
        type: 'button',
        title: 'Delete circuit',
        'aria-label': `Delete ${r.title}`,
        onclick: (e) => { e.stopPropagation(); void deleteRoom(r); },
      }, el('i', { class: 'bi bi-trash3' }))
      : null;
    return el('div', {
      class: 'room-card',
      role: 'button',
      tabindex: '0',
      onclick: openRoom,
      onkeydown: (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openRoom(); } },
    },
      el('div', { class: 'rc-top' },
        el('span', { class: 'rc-title' }, r.title),
        el('span', { class: 'rc-actions' }, deleteButton, el('i', { class: 'bi bi-arrow-right rc-arrow' }))),
      meta,
      foot);
  }));
}

async function refreshRooms() {
  try {
    const state = await hub.invoke('FetchRooms');
    renderRooms(state.rooms ?? []);
  } catch (err) {
    toast(err.message, 'error');
  }
}

function enterDash() {
  ui.gateCard.hidden = true;
  ui.dash.hidden = false;
  renderIdentity();
  refreshRooms();
}

ui.joinForm.addEventListener('submit', async (e) => {
  e.preventDefault();
  const name = ui.nameInput.value.trim();
  if (!name) return;
  const btn = $('#joinBtn');
  btn.disabled = true;
  try {
    const info = await hub.joinLobby(name);
    toast(`Welcome aboard, ${info.name}.`, 'ok');
    enterDash();
  } catch (err) {
    toast(err.message, 'error');
    btn.disabled = false;
  }
});

ui.newRoomBtn.addEventListener('click', () => {
  ui.dlgTitle.value = '';
  ui.newRoomDlg.showModal();
  ui.dlgTitle.focus();
});

for (const b of ui.newRoomForm.querySelectorAll('[data-close]')) {
  b.addEventListener('click', () => ui.newRoomDlg.close());
}
ui.newRoomDlg.addEventListener('click', (e) => {
  if (e.target === ui.newRoomDlg) ui.newRoomDlg.close();
});

ui.newRoomForm.addEventListener('submit', async (e) => {
  e.preventDefault();
  const grid = Number(ui.newRoomForm.querySelector('input[name=grid]:checked').value);
  const title = ui.dlgTitle.value.trim() || 'Untitled circuit';
  try {
    const summary = await hub.invoke('CreateRoom', title, grid);
    toast(`Circuit “${summary.title}” created.`, 'ok');
    setTimeout(() => { location.href = `/circuit/${summary.id}`; }, 250);
  } catch (err) {
    toast(err.message, 'error');
  }
});

hub.on('RoomsChanged', (rooms) => renderRooms(rooms));
hub.on('RoomClosed', () => toast('A circuit was closed after going idle.', 'warn'));

(async function boot() {
  setConnStatus((await hub.start()) ? 'online' : 'offline');
  if (hub.state.name) {
    try {
      await hub.joinLobby(hub.state.name);
      enterDash();
    } catch {
      showGateOnly();
    }
  }
})();

function showGateOnly() {
  ui.gateCard.hidden = false;
  ui.nameInput.focus();
}
