// Retain the original interface and controller behavior while using acknowledged
// host actions. Loaded before app.js; both modules share the same api/input objects.
import { api } from './api.js';
import { input } from './input.js';
import { focus } from './focus.js';

async function request(method, body) {
  const response = await fetch('/api/core', {
    method,
    headers: { 'Content-Type': 'application/json', 'X-BWOS-Token': sessionStorage.getItem('bwos.token') || '' },
    body: body ? JSON.stringify(body) : undefined,
    signal: AbortSignal.timeout(5000)
  });
  const result = await response.json();
  if (!response.ok || result.ok === false) throw Error(result.error || 'Host action failed');
  return result;
}

export async function action(kind, data = {}) {
  const id = crypto.randomUUID();
  let result = await request('POST', { id, kind, ...data });
  const deadline = Date.now() + 4500;
  while (['accepted', 'dispatched'].includes(result.status)) {
    if (Date.now() >= deadline) throw Error('Action was not acknowledged. Try again.');
    await new Promise(resolve => setTimeout(resolve, 100));
    result = (await request('GET')).find(record => record.id === id);
    if (!result) throw Error('Action is no longer available. Try again.');
  }
  if (!result.ok) throw Error(result.error || result.status);
  return result;
}

api.launch = key => action('native', { action: 'launch', value: key });
api.resumeGame = () => action('native', { action: 'resumeGame' });
api.quitGame = () => action('native', { action: 'quitGame' });
api.playbackControl = (target, command) => action('playback', { target, action: command });

const navigation = {
  up: 'up', down: 'down', left: 'left', right: 'right', select: 'accept',
  accept: 'accept', back: 'back', home: 'home', player: 'secondary',
  playerControls: 'secondary', play: 'playPause', playPause: 'playPause',
  options: 'options', menu: 'menu', prevTab: 'prevTab', nextTab: 'nextTab'
};

async function edit(message) {
  const field = focus.current;
  if (!field?.isConnected || !field.matches('input,textarea') || field.disabled || field.readOnly)
    throw Error('Select a text field first');
  if (message.action === 'enter') return input.dispatch('accept');
  if (message.action === 'tab') {
    const scope = field.closest('#modal.show') || document.querySelector('.view.active') || document.body;
    const fields = [...scope.querySelectorAll('input[data-focus],textarea[data-focus]')].filter(el => el.offsetParent !== null && !el.disabled);
    focus.set(fields[(fields.indexOf(field) + 1) % fields.length]);
    return;
  }
  const start = field.selectionStart ?? field.value.length, end = field.selectionEnd ?? start;
  let from = start, insert = String(message.value || '');
  if (message.action === 'clear') {
    field.value = ''; field.setSelectionRange(0, 0);
  } else {
    if (message.action === 'backspace') {
      insert = '';
      if (start === end) from = start - (Array.from(field.value.slice(0, start)).at(-1)?.length || 0);
    }
    const limit = field.maxLength > -1 ? field.maxLength : 200;
    insert = insert.slice(0, Math.max(0, limit - (field.value.length - (end - from))));
    field.value = field.value.slice(0, from) + insert + field.value.slice(end);
    field.setSelectionRange(from + insert.length, from + insert.length);
  }
  field.dispatchEvent(new Event('input', { bubbles: true }));
}

addEventListener('message', async event => {
  const message = event.data;
  if (event.source !== window || event.origin !== location.origin || message?.type !== 'core-input' || typeof message.inputId !== 'string') return;
  let result;
  try {
    if (message.kind === 'text') await edit(message);
    else if (message.kind === 'navigate' && navigation[message.action]) await input.dispatch(navigation[message.action]);
    else throw Error('Unsupported shell input');
    result = { ok: true };
  } catch (error) { result = { ok: false, error: error.message }; }
  window.postMessage({ type: 'bwos-input-result', inputId: message.inputId, result }, location.origin);
});
