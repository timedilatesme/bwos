// Talks to the BWOS host (BWOS.exe) on 127.0.0.1. The per-session token arrives in the
// URL fragment (#t=...) when the host opens this window; fragments never hit the network.

const token = (() => {
  const m = location.hash.match(/t=([a-f0-9]+)/);
  if (m) {
    try { sessionStorage.setItem('bwos.token', m[1]); } catch {}
    // Player SDKs include the embedding document URL in their requests. Keep
    // host authentication in session storage, never in a third-party URL.
    const clean = new URL(location.href);
    const fragment = new URLSearchParams(clean.hash.slice(1));
    fragment.delete('t'); clean.hash = fragment.toString();
    history.replaceState(history.state, '', clean);
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
    body: body ? JSON.stringify(body) : undefined,
    keepalive: path==='/api/stream/stop'
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok || data.ok === false) {const error=new Error(data.error || `Request failed (${res.status})`);error.rejected=true;throw error;}
  return data;
}

export const api = {
  stream: (action,body={}) => call('POST','/api/stream/'+action,body),
  state: () => call('GET', '/api/state'),
  playback: () => call('GET', '/api/playback'),
  localPlaybackReport: data => call('POST', '/api/playback/local-report', data),
  localPlaybackClaim: () => call('POST', '/api/playback/local-claim'),
  addVideo: url => call('POST', '/api/videos/add', { url }),
  removeVideo: id => call('POST', '/api/videos/remove', { id }),
  progress: data => call('POST', '/api/progress', data),
  playbackControl: (id, action) => call('POST', '/api/playback/control', { id, action }),
  voiceState: () => call('GET', '/api/voice'),
  voiceClaim: () => call('POST', '/api/voice/claim'),
  installVoice: () => call('POST', '/api/voice/install'),
  voiceReply: (text) => call('POST', '/api/voice/reply', { text }),
  refreshDevices: () => call('POST', '/api/devices/refresh'),
  deviceConnection: (id, connected) => call('POST', connected ? '/api/devices/connect' : '/api/devices/disconnect', { id }),
  deviceSettings: (kind = 'bluetooth') => call('POST', '/api/devices/settings', { kind }),
  launch: (key) => call('POST', '/api/launch', { key }),
  resumeGame: () => call('POST', '/api/game/resume'),
  quitGame: () => call('POST', '/api/game/quit'),
  toggleHidden: (key) => call('POST', '/api/hide', { key }),
  dismiss: (id) => call('POST', '/api/progress/dismiss', { id }),
  refreshForYou: () => call('POST', '/api/foryou'),
  keyboard: () => call('POST', '/api/keyboard'),
  claimCast: () => call('POST', '/api/cast/claim'),
  forgetPhones: () => call('POST', '/api/phones/forget'),
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
