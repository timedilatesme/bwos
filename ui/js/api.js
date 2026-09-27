// Talks to the BWOS host (BWOS.exe) on 127.0.0.1. The per-session token arrives in the
// URL fragment (#t=...) when the host opens this window; fragments never hit the network.

const token = (() => {
  const m = location.hash.match(/t=([a-f0-9]+)/);
  if (m) {
    try { sessionStorage.setItem('bwos.token', m[1]); } catch {}
    return m[1];
  }
  try { return sessionStorage.getItem('bwos.token') || ''; } catch { return ''; }
})();

/** True when the UI is opened outside BWOS.exe (e.g. as a plain file) – enables demo data. */
export const standalone = location.protocol === 'file:' || !token;

async function call(method, path, body) {
  const res = await fetch(path, {
    method,
    headers: { 'X-BWOS-Token': token, 'Content-Type': 'application/json' },
    body: body ? JSON.stringify(body) : undefined
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok || data.ok === false) throw new Error(data.error || `Request failed (${res.status})`);
  return data;
}

export const api = {
  state: () => call('GET', '/api/state'),
  launch: (key) => call('POST', '/api/launch', { key }),
  resumeGame: () => call('POST', '/api/game/resume'),
  quitGame: () => call('POST', '/api/game/quit'),
  toggleHidden: (key) => call('POST', '/api/hide', { key }),
  dismiss: (id) => call('POST', '/api/progress/dismiss', { id }),
  refreshForYou: () => call('POST', '/api/foryou'),
  keyboard: () => call('POST', '/api/keyboard'),
  refresh: () => call('POST', '/api/refresh'),
  setting: (key, value) => call('POST', '/api/settings', { key, value }),
  system: (action) => call('POST', '/api/system', { action }),
  log: (message) => call('POST', '/api/log', { message }).catch(() => {})
};

/** Server-sent events from the host: running, media, library, controller, home, focus. */
export function listen(handlers) {
  if (standalone) return;
  const es = new EventSource(`/api/events?t=${token}`);
  for (const [name, fn] of Object.entries(handlers)) {
    es.addEventListener(name, (e) => {
      let data = {};
      try { data = JSON.parse(e.data); } catch {}
      fn(data);
    });
  }
  return es;
}
