// Unified input: any gamepad the browser can see (Xbox, PlayStation, Switch Pro, generic
// HID pads), keyboard and mouse → a small set of semantic actions.
//
// actions: up down left right accept back options secondary prevTab nextTab menu view

const REPEAT_DELAY = 380;     // ms before a held direction starts repeating
const REPEAT_START = 150;     // first repeat interval
const REPEAT_MIN = 55;        // fastest repeat after acceleration
const AXIS = 0.55;

const listeners = new Set();
const state = new Map();      // gamepad index -> { buttons:[], dir, next, interval }
let lastPadInput = -Infinity;
let lastAny = performance.now();
let family = 'xbox';          // glyph style of the most recently used controller
let swapAB = false;
try { swapAB = localStorage.getItem('bwos.swapAB') === '1'; } catch {}

export const input = {
  on(fn) { listeners.add(fn); return () => listeners.delete(fn); },
  // Phone input uses the same semantic listeners and focus engine as the controller.
  async dispatch(action, source = 'remote') {
    lastAny = performance.now();
    await Promise.all(Array.from(listeners, fn => Promise.resolve().then(() => fn(action, source))));
  },
  get family() { return family; },
  get idleMs() { return performance.now() - lastAny; },
  get swapAB() { return swapAB; },
  set swapAB(v) { swapAB = v; try { localStorage.setItem('bwos.swapAB', v ? '1' : '0'); } catch {} },
  padCount() { return Array.from(gamepads()).filter(pad => pad?.connected).length; }
};

function gamepads() {
  try { return navigator.getGamepads ? navigator.getGamepads() : []; }
  catch { return []; }
}
function emit(action, source) {
  // A failed async handler must not stop polling or become an unhandled rejection.
  input.dispatch(action, source).catch(error => console.error('BWOS input failed:', error));
}

function familyOf(pad) {
  const id = pad.id.toLowerCase();
  if (/054c|dualsense|dualshock|wireless controller|playstation/.test(id)) return 'playstation';
  if (/057e|pro controller|joy-con|nintendo/.test(id)) return 'nintendo';
  return 'xbox';
}

/** Decode a DirectInput POV hat reported as an axis (-1..1 in 8 steps, >1 = centred). */
function hatDir(v) {
  if (v === undefined || v > 1.05 || v < -1.05) return null;
  const steps = [
    [-1, 'up'], [-0.714, 'up'], [-0.428, 'right'], [-0.142, 'down'],
    [0.142, 'down'], [0.428, 'left'], [0.714, 'left'], [1, 'up']
  ];
  let best = null, dist = 1;
  for (const [pos, d] of steps) { const dd = Math.abs(v - pos); if (dd < dist) { dist = dd; best = d; } }
  return dist < 0.1 ? best : null;
}

function readDir(pad, standard) {
  const b = (i) => pad.buttons[i] && pad.buttons[i].pressed;
  if (standard) {
    if (b(12)) return 'up';
    if (b(13)) return 'down';
    if (b(14)) return 'left';
    if (b(15)) return 'right';
  }
  const x = pad.axes[0] || 0, y = pad.axes[1] || 0;
  if (Math.abs(x) > AXIS || Math.abs(y) > AXIS) {
    if (Math.abs(y) >= Math.abs(x)) return y < 0 ? 'up' : 'down';
    return x < 0 ? 'left' : 'right';
  }
  // Generic DirectInput pads: Chromium reports the POV hat as axis 9. (Other axes can rest
  // at -1, e.g. triggers, so they must not be read as a hat.)
  if (!standard && pad.axes.length > 9) {
    const hd = hatDir(pad.axes[9]);
    if (hd) return hd;
  }
  return null;
}

// Button index → action, W3C "standard" layout (Xbox positions)
const BUTTONS = { 0: 'accept', 1: 'back', 2: 'secondary', 3: 'options', 4: 'prevTab', 5: 'nextTab', 8: 'view', 9: 'menu' };

function poll() {
  requestAnimationFrame(poll);
  const pads = gamepads();
  const connected = new Set(Array.from(pads).filter(pad => pad?.connected).map(pad => pad.index));
  for (const index of state.keys()) if (!connected.has(index)) state.delete(index);
  // Never react while BWOS is in the background (a game has focus).
  const focused = document.hasFocus() && !document.hidden;
  const now = performance.now();

  for (const pad of pads) {
    if (!pad || !pad.connected) continue;
    let s = state.get(pad.index);
    if (!s) { s = { buttons: [], dir: null, next: 0, interval: REPEAT_START }; state.set(pad.index, s); }
    const standard = pad.mapping === 'standard';

    // Buttons (edge-triggered)
    for (let i = 0; i < pad.buttons.length; i++) {
      const pressed = pad.buttons[i].pressed || pad.buttons[i].value > 0.6;
      const was = s.buttons[i];
      s.buttons[i] = pressed;
      if (!pressed || was || !focused) continue;
      let action = BUTTONS[i];
      if (!action) continue;
      if (swapAB && (action === 'accept' || action === 'back')) action = action === 'accept' ? 'back' : 'accept';
      family = familyOf(pad);
      lastPadInput = now;
      emit(action, 'pad');
    }

    // Directions with accelerating auto-repeat
    const dir = focused ? readDir(pad, standard) : null;
    if (dir !== s.dir) {
      s.dir = dir;
      if (dir) {
        family = familyOf(pad);
        lastPadInput = now;
        s.next = now + REPEAT_DELAY;
        s.interval = REPEAT_START;
        emit(dir, 'pad');
      }
    } else if (dir && now >= s.next) {
      lastPadInput = now;
      s.interval = Math.max(REPEAT_MIN, s.interval * 0.82);
      s.next = now + s.interval;
      emit(dir, 'pad-repeat');
    }
  }
}
requestAnimationFrame(poll);

window.addEventListener('gamepadconnected', (e) => {
  family = familyOf(e.gamepad);
  state.delete(e.gamepad.index);
  emit('padconnected', 'pad');
});
window.addEventListener('gamepaddisconnected', (e) => {
  state.delete(e.gamepad.index);
  emit('paddisconnected', 'pad');
});

// Keyboard. If a pad was used a moment ago, ignore synthetic keys that Steam's
// "desktop configuration" may generate for the same button press.
const KEYS = {
  ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right',
  Enter: 'accept', ' ': 'playPause', Escape: 'back', Backspace: 'back',
  q: 'prevTab', e: 'nextTab', PageUp: 'prevTab', PageDown: 'nextTab',
  o: 'options', ContextMenu: 'options', x: 'secondary', p: 'playPause', m: 'menu', Home: 'home'
};
window.addEventListener('keydown', (e) => {
  if (e.defaultPrevented || e.isComposing || e.ctrlKey || e.altKey || e.metaKey) return;
  // In a text box, keys type; only Enter, Escape and up/down still navigate.
  const typing = e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement;
  if (typing && !['Enter', 'Escape', 'ArrowUp', 'ArrowDown'].includes(e.key)) return;
  const action = KEYS[e.key] || KEYS[e.key.toLowerCase()];
  if (!action) return;
  e.preventDefault();
  if (performance.now() - lastPadInput < 250) return;
  emit(action, e.repeat ? 'key-repeat' : 'key');
});

// Time spent in a game or a streaming app isn't "idle" time on the BWOS screen.
const resetIdle = () => { if (!document.hidden) lastAny = performance.now(); };
window.addEventListener('focus', resetIdle);
document.addEventListener('visibilitychange', resetIdle);

// Mouse: reveal the cursor on movement, hide it again when a controller is used.
let cursorTimer = 0;
window.addEventListener('mousemove', () => {
  document.body.classList.remove('hide-cursor');
  lastAny = performance.now();
  clearTimeout(cursorTimer);
  cursorTimer = setTimeout(() => document.body.classList.add('hide-cursor'), 2500);
});
input.on((a, src) => { if (src.startsWith('pad') || src.startsWith('key') || src === 'remote' || src === 'voice') document.body.classList.add('hide-cursor'); });
