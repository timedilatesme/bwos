import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

// Real streaming adapter, isolated DOM/host fixtures. No live website or keyboard is controlled.
let fieldVisible = false, focused = false, poll;
const typed = [], navigations = [], commands = [{ action: 'search', value: 'Arijit Singh songs' }];
const field = { getBoundingClientRect: () => ({ width: fieldVisible ? 300 : 0, height: 40, top: 50, bottom: 90, left: 20, right: 320 }), focus: () => { focused = true; }, select() {} };
const button = { getBoundingClientRect: () => ({ width: 50, height: 40, top: 50, bottom: 90, left: 20, right: 70 }), getAttribute: name => name === 'aria-label' ? 'Search' : null, textContent: 'Search', click: () => { fieldVisible = true; } };
const document = { hidden: false, hasFocus: () => true, body: {}, addEventListener() {}, querySelectorAll: selector => selector === '*' ? [field, button] : selector.includes('input[type=search]') ? [field] : selector.includes('[aria-label],[title]') ? [button] : [] };
const window = {}; window.top = window;
const location = { hostname: 'www.youtube.com', pathname: '/tv', hash: '', search: '', assign: url => navigations.push(url) };
const context = vm.createContext({ document, window, location, history: { replaceState() {} }, innerHeight: 720, innerWidth: 1280, getComputedStyle: () => ({ visibility: 'visible' }),
  setInterval: callback => { poll = callback; }, setTimeout: callback => queueMicrotask(callback), addEventListener() {},
  chrome: { runtime: { sendMessage: (message, reply) => {
    if (message.type === 'voice-poll') reply({ enabled: true, commands: commands.splice(0) });
    else if (message.type === 'voice-type') { typed.push(message.text); reply({ ok: true }); }
    else reply(null);
  } } }
});
vm.runInContext(fs.readFileSync(new URL('../extensions/bwos-tv/voice.js', import.meta.url), 'utf8'), context);
await poll();
assert.equal(new URLSearchParams(location.hash.split('?')[1]).get('q'), 'Arijit Singh songs', 'YouTube TV receives a native search deep link');
assert.ok(location.hash.startsWith('#/search?'), 'Search uses the TV search route');
assert.deepEqual(typed, [], 'TV search does not depend on keyboard injection');
assert.equal(navigations.length, 0, 'Search stays in the current YouTube TV interface');
commands.push({ action: 'search', value: 'हिंदी गाने' });
await poll();
assert.equal(new URLSearchParams(location.hash.split('?')[1]).get('q'), 'हिंदी गाने', 'Hindi query is preserved');
location.pathname = '/results';
commands.push({ action: 'youtubeSearch', value: 'a & b # c' });
await poll();
const redirected = new URL(navigations[0]);
assert.equal(redirected.pathname, '/tv', 'Desktop YouTube returns to the TV interface');
assert.equal(new URLSearchParams(redirected.hash.split('?')[1]).get('q'), 'a & b # c', 'Search punctuation is encoded without injecting URL parameters');

location.hostname = 'example.com'; location.pathname = '/';
commands.push({ action: 'search', value: 'A movie' });
await poll();
assert.ok(fieldVisible && focused, 'Other services retain search-button and field support');
assert.deepEqual(typed, ['A movie'], 'Other services use trusted Unicode input');
console.log('PASS: native TV search, Hindi and punctuation, desktop-to-TV routing, and other-service search');
