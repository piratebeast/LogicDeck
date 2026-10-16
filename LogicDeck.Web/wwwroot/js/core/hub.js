import { toast } from './dom.js';

export function createHub() {
  const conn = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/collab')
    .withAutomaticReconnect([0, 1000, 2000, 4000, 8000, 15000, 30000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  const listeners = new Map();
  const on = (event, fn) => {
    if (!listeners.has(event)) {
      const set = new Set();
      listeners.set(event, set);
      conn.on(event, (...args) => { for (const f of [...set]) f(...args); });
    }
    listeners.get(event).add(fn);
    return () => listeners.get(event)?.delete(fn);
  };

  const state = {
    sessionId: sessionStorage.getItem('ld.sid') ?? null,
    name: sessionStorage.getItem('ld.name') ?? null,
    color: sessionStorage.getItem('ld.color') ?? null,
    status: 'idle',
  };

  async function joinLobby(requestedName) {
    let sid = state.sessionId;
    if (!sid) {
      sid = crypto.randomUUID().replaceAll('-', '').slice(0, 24);
      sessionStorage.setItem('ld.sid', sid);
      state.sessionId = sid;
    }
    try {
      const info = await conn.invoke('JoinLobby', sid, requestedName ?? '');
      applyIdentity(info);
      return info;
    } catch (err) {
      throw normalize(err);
    }
  }

  function applyIdentity(info) {
    state.sessionId = info.sessionId;
    state.name = info.name;
    state.color = info.color;
    sessionStorage.setItem('ld.sid', info.sessionId);
    sessionStorage.setItem('ld.name', info.name);
    sessionStorage.setItem('ld.color', info.color);
  }

  function normalize(err) {
    const msg = err?.message ?? String(err);
    return new Error(msg.replace(/^An unexpected error occurred.*?:\s*/i, '')
      .replace(/^.*HubException:\s*/i, '').trim() || 'Something went wrong.');
  }

  const api = {
    conn, on, state, joinLobby, normalize,
    invoke: (method, ...args) => conn.invoke(method, ...args).catch((e) => Promise.reject(normalize(e))),
    onReconnecting: null,
    onReconnected: null,
  };

  conn.onreconnecting(() => { state.status = 'reconnecting'; api.onReconnecting?.(); });
  conn.onreconnected(async () => {
    state.status = 'online';
    api.onReconnected?.();
  });
  conn.onclose(() => { state.status = 'offline'; api.onClose?.(); });

  api.start = async () => {
    try {
      await conn.start();
      state.status = 'online';
      return true;
    } catch (err) {
      toast('Cannot reach the server. Retrying…', 'error');
      setTimeout(() => api.start(), 2500);
      return false;
    }
  };

  return api;
}
