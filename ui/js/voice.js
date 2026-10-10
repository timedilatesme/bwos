// Number overlays follow the currently visible scope, including dialogs.
let numbered = false;
let choices = [];
let scope = () => document.body;
let overlays = [];
export function setVoiceScope(fn) { scope = fn; }
export function hideVoiceNumbers() {
  numbered = false;
  clearOverlays();
}
function clearOverlays() { overlays.forEach(el => el.remove()); overlays = []; choices = []; }
export function refreshVoiceNumbers() {
  if (!numbered) return;
  clearOverlays();
  choices = [...scope().querySelectorAll('[data-focus]')].filter(el => {
    const view = el.closest('.view');
    if (view && !view.classList.contains('active')) return false;
    const modal = el.closest('.modal');
    if (modal && !modal.classList.contains('show')) return false;
    const closed=el.closest('details:not([open])');
    if(closed && !closed.querySelector(':scope > summary')?.contains(el))return false;
    const r = el.getBoundingClientRect();
    return !el.disabled && r.width > 0 && r.height > 0 && r.bottom > 0 && r.top < innerHeight && r.right > 0 && r.left < innerWidth && getComputedStyle(el).visibility !== 'hidden';
  }).slice(0, 99);
  choices.forEach((el, i) => {
    const r = el.getBoundingClientRect();
    const badge = document.createElement('span');
    badge.className = 'voice-number'; badge.textContent = String(i + 1);
    badge.setAttribute('aria-hidden', 'true');
    badge.style.left = `${Math.max(2, r.left + 6)}px`;
    badge.style.top = `${Math.max(2, r.top + 6)}px`;
    document.body.append(badge); overlays.push(badge);
  });
}
export function showVoiceNumbers() { numbered = true; refreshVoiceNumbers(); }
export function voiceNumber(n) {
  const el = choices[Number(n) - 1];
  return el && el.isConnected ? el : null;
}
let frame = 0;
addEventListener('scroll', () => { if (numbered && !frame) frame = requestAnimationFrame(() => { frame = 0; refreshVoiceNumbers(); }); }, true);
addEventListener('resize', refreshVoiceNumbers);
addEventListener('toggle',refreshVoiceNumbers,true);

export function normalizeVoiceName(text) {
  return String(text || '').toLowerCase().replace(/[^\p{L}\p{M}\p{N}\s]/gu, ' ').replace(/\s+/g, ' ').trim();
}
export function matchVoiceNames(query, entries) {
  const q = normalizeVoiceName(query);
  if (!q) return [];
  const exact = entries.filter(e => normalizeVoiceName(e.name) === q);
  if (exact.length) return exact;
  return q.length >= 3 ? entries.filter(e => normalizeVoiceName(e.name).includes(q)) : [];
}

export function voiceAlias(text) {
  const name = normalizeVoiceName(text);
  if (name === 'net flix') return 'netflix';
  const aliases = { 'you tube': 'youtube', 'net flicks': 'netflix', 'यूट्यूब': 'youtube', 'यू ट्यूब': 'youtube', 'नेटफ्लिक्स': 'netflix', 'प्राइम वीडियो': 'prime video', 'होम': 'home', 'गेम्स': 'games', 'गेम': 'games', 'ऐप्स': 'apps', 'सेटिंग्स': 'settings', 'डिवाइस': 'devices', 'डिवाइसेस': 'devices', 'ब्लूटूथ': 'devices', 'कंट्रोलर': 'controllers', 'मूवीज': 'movies', 'फिल्में': 'movies' };
  return aliases[name] || name;
}
