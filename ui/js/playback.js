import { h } from './util.js';
import { focus } from './focus.js';

let signature = '';
let returnFocus;
export function playbackScope() {
  const player=document.querySelector('#embedded-player:not([hidden])');
  return player && (!player.classList.contains('docked') || focus.current?.closest('#embedded-player')) ? player : document.body;
}
export function focusPlayback() {
  const root = document.querySelector('#embedded-player:not([hidden])') || document.querySelector('#now-playing:not([hidden])');
  const target = root?.querySelector('[data-action="pause"][data-focus],[data-action="resume"][data-focus]') || root?.querySelector('[data-focus]');
  if (!target) return false;
  if (focus.current?.closest('#now-playing,#embedded-player')) return leavePlayback();
  returnFocus = focus.current; focus.set(target); return true;
}
export function leavePlayback() {
  const target = returnFocus?.isConnected ? returnFocus : document.querySelector('#tabs .tab.active');
  if (!target) return false;
  focus.set(target); return true;
}
const time = seconds => {
  seconds = Math.max(0, Math.floor(Number(seconds) || 0));
  return seconds >= 3600 ? `${Math.floor(seconds / 3600)}:${String(Math.floor(seconds / 60) % 60).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}` : `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
};
function updateTimeline(root, players) {
  for (const player of players) {
    const row = root.querySelector(`[data-player="${CSS.escape(String(player.id))}"]`);
    if (!row) continue;
    const duration = Number(player.duration) || 0, position = Math.max(0, Number(player.position) || 0);
    const elapsed = row.querySelector('.now-playing-time');
    elapsed.textContent = duration > 0 ? `${time(Math.min(position, duration))} / ${time(duration)}` : time(position);
    const progress = row.querySelector('progress');
    progress.hidden = duration <= 0; progress.max = duration || 1; progress.value = Math.min(position, duration);
  }
}
export function renderPlayback(players = []) {
  const root = document.getElementById('now-playing');
  const current = players.filter(player=>!player.local).sort((a, b) => Number(a.id) - Number(b.id));
  root.hidden = !current.length;
  const toggle = document.getElementById('playback-status');
  if (toggle) toggle.hidden = !current.length;
  if (!current.length && focus.current?.closest('#now-playing')) leavePlayback();
  // Heartbeats advance the timeline without restarting the animation or controller focus.
  const next = JSON.stringify(current.map(({ position, duration, ...details }) => details));
  if (next === signature) { updateTimeline(root, current); return; }
  signature = next;
  const active = focus.current?.closest('#now-playing') ? focus.current.dataset.key : null;
  root.replaceChildren(...current.map(player => {
    let service = 'Media';
    try { service = new URL(player.url).hostname.replace(/^www\./, ''); } catch {}
    const button = (action, label, glyph) => h('button.now-playing-button', {
      type: 'button', title: label, 'aria-label': label, disabled: ['pause', 'resume'].includes(action) && player.canControl === false, 'data-focus': !(['pause', 'resume'].includes(action) && player.canControl === false),
      dataset: { kind: 'playback', id: player.id, action, key: `player:${player.id}:${['pause', 'resume'].includes(action) ? 'toggle' : action}` }
    }, glyph);
    return h('div.now-playing-player', { dataset: { player: player.id } },
      h('div.now-playing-pulse', { 'aria-hidden': true, 'data-paused': player.paused }, h('i'), h('i'), h('i')),
      h('button.now-playing-copy', { type: 'button', title: 'Reopen player', 'data-focus': true,
        dataset: { kind: 'playback', id: player.id, action: 'focus', key: `player:${player.id}:title` } },
        h('span.now-playing-caption', null, player.paused ? 'Paused · ' + service : 'Now playing · ' + service),
        h('span.now-playing-title', null, player.title || 'Media'),
        h('span.now-playing-time'), h('progress.now-playing-progress', { max: 1, value: 0, 'aria-label': 'Playback progress' }),
        player.error && h('span.now-playing-error', { role: 'status' }, player.error)),
      button(player.paused ? 'resume' : 'pause', player.paused ? 'Play' : 'Pause', player.paused ? '▶' : 'Ⅱ'),
      player.error && button('retry', 'Retry player', '↻'),
      button('focus', 'Reopen player', '↗'), button('stop', 'Stop and close player', '×'));
  }));
  updateTimeline(root, current);
  if (active) {
    const target = root.querySelector(`[data-key="${CSS.escape(active)}"]`) || root.querySelector('[data-focus]') || document.querySelector('#tabs .tab.active');
    if (target) focus.set(target, { silent: true, instant: true });
  }
}
