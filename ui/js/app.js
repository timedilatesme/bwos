import { $, $$, h, icon, hashHue, timeAgo, playtime, size, SOURCE_LABEL, preload, ambientColor, sleep } from './util.js';
import { api, listen, standalone } from './api.js';
import { input } from './input.js';
import { focus } from './focus.js';
import { sound } from './sound.js';

// ------------------------------------------------------------------ state

let S = { games: [], watch: [], settings: {}, controllers: [], running: null, user: null };
let view = 'home';
let gamesFilter = 'all';
let gamesSort = 'recent';
let modalOpen = null;          // { restore: element }
let launching = null;          // { key, timer }
const TABS = ['home', 'foryou', 'games', 'watch', 'settings'];
const HERO_VIEWS = ['home', 'foryou'];

const gameByKey = (k) => S.games.find((g) => g.key === k);
const watchById = (id) => S.watch.find((w) => w.id === id);
const visibleGames = () => S.games.filter((g) => !g.hidden);

// ------------------------------------------------------------------ boot

async function boot() {
  const intro = startIntro();
  focus.setScope(() => (modalOpen ? $('#modal') : document.body));
  focus.onChange(onFocusChange);
  input.on(onAction);
  document.addEventListener('focus-edge', () => sound.edge());

  $$('#tabs .tab').forEach((t) => {
    t.addEventListener('click', () => switchTab(t.dataset.tab));
  });
  document.addEventListener('click', onClick);
  // The pointer may be resting anywhere when BWOS opens: ignore hover until the mouse really moves.
  document.body.classList.add('hide-cursor');
  document.addEventListener('mouseover', (e) => {
    const el = e.target.closest('[data-focus]');
    if (el && el !== focus.current && !document.body.classList.contains('hide-cursor')) focus.set(el, { silent: false });
  });

  tickClock();
  setInterval(tickClock, 10000);
  setInterval(checkIdle, 5000);

  await reload();
  switchTab('home', { silent: true });
  const first = $('#home-rows [data-focus]');
  if (first) focus.set(first, { instant: true });
  restorePlace();
  intro.done();
  // Coming back from a streaming service via the back/forward cache: tidy up.
  addEventListener('pageshow', (e) => { if (e.persisted) { hideLaunch(); restorePlace(); reload(); claimCast(); } });
  claimCast();

  listen({
    running: async (d) => {
      await reload();
      if (launching && d.key === launching.key) launchDone();
    },
    library: () => reload(),
    foryou: () => reloadSoon(),
    upnext: () => reloadSoon(),
    controller: (d) => {
      toast(d.connected ? 'Controller connected' : 'Controller disconnected', d.connected ? 'gamepad' : 'alert');
      reload();
    },
    home: () => goHome(),
    cast: () => claimCast(),
    phone: () => {
      if (modalOpen && $('#modal .modal-qr')) closeModal();
      toast('Phone connected. Swipe to move, tap to select.', 'check');
      reload();
    },
    focus: () => { exitRest(); exitAmbient(); reload(); sound.tab(); }
  });

  addEventListener('focus', ambientSync);
  addEventListener('blur', ambientSync);
  document.addEventListener('visibilitychange', () => { ambientSync(); syncWakeLock(); });
  // Real mouse movement wakes the screen (Chromium also sends zero-distance moves on layout changes).
  addEventListener('mousemove', (e) => { if (resting() && (e.movementX || e.movementY)) exitRest(); });
  addEventListener('pagehide', () => sound.stopAmbient(true));
  ambientSync();

  // Keep "now playing" etc. fresh even if an event was missed.
  setInterval(() => { if (!document.hidden) reload(); }, 30000);
}

/**
 * The start-up animation. The full version plays once per BWOS window; coming back from a
 * streaming app (same window, same session) only fades in.
 */
function startIntro() {
  const el = $('#intro');
  let full = true;
  try { full = !sessionStorage.getItem('bwos.intro'); sessionStorage.setItem('bwos.intro', '1'); } catch {}
  let finished = false;
  const finish = () => {
    if (finished) return;
    finished = true;
    document.body.classList.remove('booting');
    if (full) {
      document.body.classList.add('opening');
      setTimeout(() => document.body.classList.remove('opening'), 1000);
    }
    el.classList.add('out');
    setTimeout(() => el.remove(), 800);
  };
  setTimeout(finish, 10000); // never stay on the intro, even if start-up failed
  if (!full) {
    el.classList.add('quick');
    return { done: () => requestAnimationFrame(finish) };
  }
  // Each trace draws half the outline (pathLength 2 → dash of 1); the mirrored pair draws the other side.
  const body = $('#i-body').getAttribute('d');
  $$('.i-trace').forEach((p) => { p.setAttribute('d', body); p.setAttribute('pathLength', '2'); });
  el.classList.add('play');
  setTimeout(() => sound.intro(), 200);
  const shown = sleep(2900);
  return { done: () => shown.then(finish) };
}

/** A phone cast to BWOS: open YouTube's TV app with the phone's pairing code (claimed once from the host). */
async function claimCast() {
  if (standalone || document.hidden) return;
  let r = null;
  try { r = await api.claimCast(); } catch { return; }
  if (!r || !r.url) return;
  exitRest();
  closeModal();
  openInside({ url: r.url, title: 'YouTube', sub: 'Connecting to your phone…', brand: '#ff0033', returnKey: 'watch:youtube' });
}

/** After returning from a streaming service, put focus back on the tile that opened it. */
function restorePlace() {
  let ret = null;
  try { ret = JSON.parse(sessionStorage.getItem('bwos.return') || 'null'); sessionStorage.removeItem('bwos.return'); } catch {}
  if (!ret) return;
  switchTab(ret.view || 'home', { silent: true });
  const el = $(`#view-${ret.view || 'home'} [data-key="${CSS.escape(ret.key)}"]`);
  if (el) focus.set(el, { instant: true });
}

let reloadTimer = 0;
/** Coalesces bursts of server events (progress reports arrive every few seconds). */
function reloadSoon() {
  clearTimeout(reloadTimer);
  reloadTimer = setTimeout(reload, 800);
}

async function reload() {
  if (standalone) {
    toast('Open BWOS from BWOS.exe to load your library', 'info');
    return;
  }
  try {
    S = await api.state();
  } catch (e) {
    toast('Can’t reach the BWOS service — ' + e.message, 'alert', true);
    return;
  }
  renderAll();
}

// ------------------------------------------------------------------ rendering

function renderAll() {
  // Remember what was focused so a refresh never "loses" the user's place.
  const cur = focus.current;
  const key = cur && cur.dataset.key;
  const rowName = cur && cur.closest('.row') && cur.closest('.row').dataset.row;
  const inView = cur && cur.closest('.view');

  renderStatus();
  renderHome();
  renderForYou();
  renderGames();
  renderWatch();
  renderSettings();

  if (cur && !cur.isConnected) {
    let target = null;
    if (key) {
      const scope = inView ? $('#' + inView.id) : document;
      target = (rowName && scope.querySelector(`.row[data-row="${rowName}"] [data-key="${CSS.escape(key)}"]`)) ||
        scope.querySelector(`[data-key="${CSS.escape(key)}"]`);
    }
    if (!target && rowName) target = $(`.row[data-row="${rowName}"] [data-focus]`);
    if (!target) target = $(`#view-${view} [data-focus]`) || $('#tabs .tab.active');
    if (target) focus.set(target, { instant: true, silent: false });
  } else if (cur) {
    focus.ensureVisible(cur, true);
  }
}

function renderStatus() {
  // Controller + battery (host reads XInput; falls back to the browser's view)
  const pad = $('#pad-status');
  pad.replaceChildren();
  const ctrl = S.controllers && S.controllers[0];
  const count = Math.max(S.controllers ? S.controllers.length : 0, input.padCount());
  pad.classList.toggle('off', count === 0);
  pad.append(icon('gamepad'));
  if (ctrl && ctrl.battery && ctrl.battery !== 'unknown' && ctrl.battery !== 'wired') {
    const levels = { empty: 0, low: 1, medium: 2, full: 3 }[ctrl.battery] || 0;
    const b = h('span.battery' + (levels <= 1 ? '.low' : ''), null, h('i'), h('i'), h('i'));
    [...b.children].forEach((i, n) => i.classList.toggle('on', n < Math.max(levels, 1)));
    pad.append(b);
  }
  if (count > 1) pad.append(h('span', null, '×' + count));

  const user = $('#user');
  user.replaceChildren();
  const name = (S.user && S.user.name) || '';
  const initial = () => h('div.avatar-fallback', null, name ? name[0].toUpperCase() : icon('user'));
  if (S.user && S.user.avatar) {
    const img = h('img', { src: S.user.avatar, alt: '' });
    img.onerror = () => img.replaceWith(initial());
    user.append(img);
  } else user.append(initial());
  user.append(h('span', null, name || 'Add your name'));
}

function tickClock() {
  const now = new Date();
  const t = now.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
  $('#clock').textContent = t;
  $('.ambient-time').textContent = t;
}

// ---------- cards

function artImg(src, cls = '') {
  const img = h('img' + (cls ? '.' + cls : ''), { alt: '', decoding: 'async', draggable: 'false' });
  img.onload = () => img.classList.add('ready');
  img.src = src;
  if (img.complete && img.naturalWidth) img.classList.add('ready');
  return img;
}

function gameCard(g, shape, row) {
  const card = h(`div.card.card--${shape}`, {
    'data-focus': true,
    dataset: { kind: 'game', key: g.key },
    style: { '--h': String(hashHue(g.name)) }
  });
  const art = h('div.card-art', { 'data-title': g.name });
  card.append(art);
  const src = shape === 'portrait' ? g.art.portrait : g.art.hero;
  const noart = () => {
    card.classList.add('noart');
    art.replaceChildren();
    const l = card.querySelector('.card-logo');
    if (l) l.remove();
  };
  if (src) {
    const img = artImg(src);
    img.onerror = noart;
    art.append(img);
  } else noart();

  if (shape === 'land') {
    if (g.art.logo) {
      const logo = h('img.card-logo', { alt: '', src: g.art.logo });
      logo.onerror = () => logo.replaceWith(h('div.card-label', null, g.name));
      card.append(logo);
    } else if (src) card.append(h('div.card-label', null, g.name));
  }
  if (g.running) card.append(h('div.badge.live', null, h('span.dot'), 'Running'));
  else if (g.updating) card.append(h('div.badge.warn', null, 'Update'));
  if (row !== 'library' && g.source !== 'steam') card.append(h('div.source-tag', null, SOURCE_LABEL[g.source] || g.source));
  if (g.hidden) card.classList.add('dim');
  return card;
}

function watchTile(w) {
  const t = h('div.tile.tile--service', {
    'data-focus': true,
    dataset: { kind: 'watch', key: 'watch:' + w.id, id: w.id },
    style: { '--brand': w.color || '#555' }
  });
  // Official app icon as a badge, plus a soft blurred wash of its own colours behind it.
  if (w.iconUrl) {
    const glow = h('img.tile-wash', { alt: '', src: w.iconUrl, draggable: 'false' });
    const icon = h('img.tile-icon', { alt: '', src: w.iconUrl, draggable: 'false' });
    icon.onerror = () => { t.classList.add('noicon'); glow.remove(); icon.remove(); };
    t.append(glow, icon);
  } else t.classList.add('noicon');
  // tvOS-style: the tile is just the app's icon; its name appears underneath when focused.
  t.append(h('div.tile-text', null, h('div.tile-name', null, w.name)));
  t.append(h('div.tile-caption', null, w.name));
  if (w.section === 'free') t.append(h('span.tile-flag.free', null, 'Free'));
  if (w.hidden) t.classList.add('dim');
  return t;
}

/** Poster card for a movie/series from the recommendations. */
function titleCard(t) {
  const card = h('div.card.card--portrait.card--title', {
    'data-focus': true,
    dataset: { kind: 'title', key: 'title:' + t.id, id: t.id },
    style: { '--h': String(hashHue(t.title)) }
  });
  const art = h('div.card-art', { 'data-title': t.title });
  card.append(art);
  if (t.poster) {
    const img = artImg(t.poster);
    img.onerror = () => { card.classList.add('noart'); img.remove(); };
    art.append(img);
  } else card.classList.add('noart');
  const apps = t.offers.map((o) => watchById(o.app)).filter(Boolean);
  if (apps.length) card.append(h('div.card-apps', null, apps.slice(0, 3).map((a) => h('img', { src: a.iconUrl, alt: a.name }))));
  if (t.offers.length && t.offers.every((o) => o.free)) card.append(h('div.badge', null, 'Free'));
  return card;
}

/** Landscape card for something you're part-way through, with a progress bar. */
function resumeCard(e) {
  const card = h('div.card.card--land.card--resume', {
    'data-focus': true,
    dataset: { kind: 'resume', key: 'resume:' + e.id, id: e.id },
    style: { '--h': String(hashHue(e.title)) }
  });
  const art = h('div.card-art', { 'data-title': e.title });
  card.append(art);
  const src = e.backdrop || e.artwork || e.poster;
  if (src) {
    const img = artImg(src);
    img.onerror = () => { card.classList.add('noart'); img.remove(); };
    art.append(img);
  } else card.classList.add('noart');
  if (src) card.append(h('div.card-label', null, e.title, e.subtitle ? h('small', null, e.subtitle) : null));
  const app = e.app && watchById(e.app);
  if (app) card.append(h('img.card-appicon', { src: app.iconUrl, alt: app.name }));
  const pct = e.duration ? Math.min(100, Math.round((e.position / e.duration) * 100)) : 0;
  card.append(h('div.progress', null, h('i', { style: { width: pct + '%' } })));
  return card;
}

function renderForYou() {
  const rows = $('#foryou-rows');
  const parts = [row('upnext', 'Up Next', (S.upNext || []).map(resumeCard))];
  for (const r of S.forYou || []) parts.push(row('fy-' + r.id, r.title, r.items.map(titleCard)));
  const built = parts.filter(Boolean);
  if (!built.length) {
    rows.replaceChildren(h('div.empty', null,
      'Picking a few things for you… Recommendations appear here in a moment, and grow as you watch.'));
    return;
  }
  rows.replaceChildren(...built);
}

function actionTile(action, label, ico, sub) {
  return h('div.tile.tile--action', { 'data-focus': true, dataset: { kind: 'action', action, key: 'action:' + action } },
    icon(ico),
    h('div', null, h('div.tile-name', null, label), sub ? h('div.tile-sub', null, sub) : null));
}

function row(name, title, items, sub) {
  if (!items.length) return null;
  const apps = items.some((i) => i.classList && i.classList.contains('tile--service'));
  return h('section.row' + (apps ? '.row--apps' : ''), { dataset: { row: name } },
    h('div.row-title', null, title, sub ? h('small', null, sub) : null),
    h('div.row-viewport', null, h('div.row-track', null, items)));
}

// ---------- home

function renderHome() {
  const rows = $('#home-rows');
  const games = visibleGames();
  const byName = [...games].sort((a, b) => a.name.localeCompare(b.name));

  // Continue: running game, open streaming app, then recently played
  const cont = [];
  const running = S.running && gameByKey(S.running);
  if (running) cont.push(gameCard(running, 'land', 'continue'));
  const recent = games.filter((g) => g.lastPlayed && g.key !== S.running).sort((a, b) => b.lastPlayed - a.lastPlayed);
  const fill = recent.length ? recent : byName;
  fill.slice(0, 10).forEach((g) => cont.push(gameCard(g, 'land', 'continue')));

  const watch = S.watch.filter((w) => !w.hidden);
  const actions = [
    actionTile('settings', 'Settings', 'settings'),
    S.settings.steamFound ? actionTile('bigpicture', 'Big Picture', 'bigpicture', 'Steam') : null,
    actionTile('desktop', 'Desktop', 'desktop'),
    actionTile('sleep', 'Sleep', 'moon'),
    actionTile('restart', 'Restart', 'restart'),
    actionTile('shutdown', 'Shut down', 'power')
  ].filter(Boolean);

  rows.replaceChildren(...[
    row('continue', running ? 'Now playing' : 'Jump back in', cont),
    row('upnext', 'Up Next', (S.upNext || []).slice(0, 10).map(resumeCard)),
    row('watch', 'Watch', watch.filter((w) => w.section === 'stream').map((w) => watchTile(w))),
    row('library', 'Your games', byName.map((g) => gameCard(g, 'portrait', 'library')), `${games.length}`),
    row('free', 'Free to watch', watch.filter((w) => w.section === 'free').map((w) => watchTile(w))),
    row('music', 'Listen', watch.filter((w) => w.section === 'music').map((w) => watchTile(w))),
    row('actions', 'Quick actions', actions)
  ].filter(Boolean));

  if (!games.length) {
    const hint = h('div.empty', null, S.settings.steamFound
      ? 'No installed games found yet. Install something in Steam, Epic or GOG and it will appear here.'
      : 'Steam wasn’t found on this PC. Epic, GOG and custom games still work — see Settings.');
    rows.prepend(hint);
  }
}

// ---------- games

function renderGames() {
  const all = S.games;
  const sources = [...new Set(all.map((g) => g.source))];
  const chips = $('#games-chips');
  const filterDefs = [['all', 'All'], ...sources.map((s) => [s, SOURCE_LABEL[s] || s])];
  if (all.some((g) => g.hidden)) filterDefs.push(['hidden', 'Hidden']);
  const sortDefs = [['recent', 'Recent'], ['az', 'A–Z'], ['played', 'Most played'], ['size', 'Size']];
  chips.replaceChildren(
    ...filterDefs.map(([id, label]) => h('button.chip' + (gamesFilter === id ? '.on' : ''), { 'data-focus': true, dataset: { kind: 'filter', id, key: 'filter:' + id } }, label)),
    h('span.chip-sep'),
    ...sortDefs.map(([id, label]) => h('button.chip' + (gamesSort === id ? '.on' : ''), { 'data-focus': true, dataset: { kind: 'sort', id, key: 'sort:' + id } }, label))
  );

  let list = all.filter((g) => (gamesFilter === 'hidden' ? g.hidden : !g.hidden && (gamesFilter === 'all' || g.source === gamesFilter)));
  const cmp = {
    recent: (a, b) => (b.lastPlayed || 0) - (a.lastPlayed || 0) || a.name.localeCompare(b.name),
    az: (a, b) => a.name.localeCompare(b.name),
    played: (a, b) => (b.playtime || 0) - (a.playtime || 0) || a.name.localeCompare(b.name),
    size: (a, b) => (b.size || 0) - (a.size || 0)
  }[gamesSort];
  list = list.sort(cmp);
  $('#games-count').textContent = list.length;
  const grid = $('#games-grid');
  grid.replaceChildren(...list.map((g) => gameCard(g, 'portrait', 'grid')));
  if (!list.length) grid.append(h('div.empty', null, 'Nothing here yet.'));
}

// ---------- watch

function renderWatch() {
  const root = $('#watch-scroller');
  const parts = [h('div.view-head', null, h('h2', null, 'Watch'))];
  const section = (id, title, sub, list) => list.length ? h('section.watch-section', { dataset: { group: 'watch-' + id } },
    h('h3.section-title', null, title),
    h('p.section-sub', null, sub),
    h('div.tile-grid', null, list.map((w) => watchTile(w)))) : null;
  const vis = S.watch.filter((w) => !w.hidden);
  parts.push(
    section('stream', 'Streaming', 'Opens right here in BWOS. Sign in once and you stay signed in; your controller drives everything.', vis.filter((w) => w.section === 'stream')),
    section('free', 'Free to watch', 'Free, legal and ad-supported. Availability depends on your country.', vis.filter((w) => w.section === 'free')),
    section('music', 'Music', 'Keep it playing while you browse.', vis.filter((w) => w.section === 'music')),
    section('hidden', 'Hidden', 'Press Y to show an app again.', S.watch.filter((w) => w.hidden))
  );
  root.replaceChildren(...parts.filter(Boolean));
}

// ---------- settings

const REGIONS = [['IN', 'India'], ['US', 'United States'], ['GB', 'United Kingdom'], ['CA', 'Canada'], ['AU', 'Australia'], ['AE', 'UAE']];

// Minutes; 0 = never.
const BLANK_AFTER = [[5, '5 minutes'], [10, '10 minutes'], [20, '20 minutes'], [30, '30 minutes'], [60, '1 hour'], [0, 'Never']];
const STANDBY_AFTER = [[0, 'Never'], [60, '1 hour'], [120, '2 hours'], [240, '4 hours']];
const cycleValue = (label) => h('span.cycle', null, h('span.arrow', null, '‹'), label, h('span.arrow', null, '›'));
const labelOf = (list, v) => (list.find((o) => o[0] === v) || list[0])[1];

const TRIGGERS = [
  ['guide', 'Home / Guide button'],
  ['back+start', 'View + Menu together'],
  ['hold-back', 'Hold View (Back)']
];

function setting({ key, title, desc, ico, value, kind = 'setting', danger }) {
  return h('button.setting' + (danger ? '.danger' : ''), { 'data-focus': true, dataset: { kind, setting: key, key: 'setting:' + key } },
    icon(ico, 'ico'),
    h('div.s-text', null, h('div.s-title', null, title), desc ? h('div.s-desc', null, desc) : null),
    value !== undefined ? h('div.s-value', null, value) : null);
}
const toggle = (on) => h('span.toggle' + (on ? '.on' : ''));

function renderSettings() {
  const st = S.settings || {};
  const trig = TRIGGERS.find((t) => t[0] === st.trigger) || TRIGGERS[0];
  const pads = S.controllers || [];

  const list = h('div', { dataset: { group: 'settings', remember: '' } },
    h('div.settings-group', null, h('h3', null, 'Controller'),
      setting({ key: 'trigger', title: 'Open BWOS with', desc: 'Works everywhere — on the desktop and inside games.', ico: 'gamepad', value: h('span.cycle', null, h('span.arrow', null, '‹'), trig[1], h('span.arrow', null, '›')) }),
      setting({ key: 'swapAB', title: 'Nintendo-style confirm', desc: 'Swap the confirm and back buttons.', ico: 'swap', value: toggle(input.swapAB) }),
      setting({ key: 'controllerBridge', title: 'Controller as remote in streaming apps', desc: 'D-pad = arrows, A = select, right stick = mouse, B = back.', ico: 'mouse', value: toggle(st.controllerBridge) }),
      setting({ key: 'castEnabled', title: 'Cast from your phone', desc: `In the YouTube app on your phone, tap Cast and choose “${st.castName || 'BWOS'}”. Same Wi-Fi as this PC.`, ico: 'cast', value: toggle(st.castEnabled) }),
      setting({ key: 'airplayEnabled', title: 'AirPlay from iPhone or Mac',
        desc: st.airplayInstalled ? `Control Centre → Screen Mirroring → “${st.castName || 'BWOS'}”. Press Home to come back.` : 'Needs UxPlay; see the README (AirPlay).',
        ico: 'cast', value: st.airplayInstalled ? toggle(st.airplayEnabled) : h('span', null, 'Not installed') }),
      setting({ key: 'phoneKeyboard', title: 'Phone remote', desc: 'Scan once with any phone: swipe to move, tap to select, Back, Home, volume and a keyboard. In apps, press Menu.', ico: 'wifi' }),
      st.phones ? setting({ key: 'forgetPhones', title: 'Forget paired phones', desc: `${st.phones} phone${st.phones === 1 ? '' : 's'} can control BWOS without scanning again.`, ico: 'eyeoff' }) : null),
    h('div.settings-group', null, h('h3', null, 'For You'),
      setting({ key: 'country', title: 'Recommendations region', desc: 'Which country’s Netflix / Prime Video catalogue to recommend from.', ico: 'tv', value: h('span.cycle', null, h('span.arrow', null, '‹'), REGIONS.find((r) => r[0] === st.country)?.[1] || st.country || 'Auto', h('span.arrow', null, '›')) }),
      setting({ key: 'refreshForYou', title: 'Refresh recommendations', desc: 'They also refresh by themselves every few hours and as you watch.', ico: 'refresh' })),
    h('div.settings-group', null, h('h3', null, 'Library'),
      setting({ key: 'showTools', title: 'Show tools & runtimes', desc: 'SteamVR, redistributables, Proton…', ico: 'eye', value: toggle(st.showTools) }),
      setting({ key: 'rescan', title: 'Rescan game libraries', desc: 'Steam, Epic Games, GOG and custom games.', ico: 'refresh', kind: 'setting' })),
    h('div.settings-group', null, h('h3', null, 'Screen'),
      setting({ key: 'blankAfter', title: 'Fade to black when idle', desc: 'The screen goes dark but keeps its signal, so the projector stays on. Any button wakes it.', ico: 'eyeoff', value: cycleValue(labelOf(BLANK_AFTER, st.blankAfter)) }),
      setting({ key: 'standbyAfter', title: 'Let the display switch off after', desc: 'Hands over to Windows’ power settings, so the projector can go to standby.', ico: 'power', value: cycleValue(labelOf(STANDBY_AFTER, st.standbyAfter)) }),
      setting({ key: 'openOnWake', title: 'Open BWOS when the PC wakes', desc: 'After sleep or hibernation, BWOS comes straight up.', ico: 'rocket', value: toggle(st.openOnWake) })),
    h('div.settings-group', null, h('h3', null, 'System'),
      setting({ key: 'autostart', title: 'Start BWOS with Windows', desc: 'Runs quietly in the tray so the home button always works.', ico: 'rocket', value: toggle(st.autostart) }),
      setting({ key: 'sounds', title: 'Interface sounds', ico: 'sound', value: toggle(sound.enabled) }),
      setting({ key: 'ambient', title: 'Ambient music', desc: 'A soft background pad on the BWOS screens. Stops for games and videos.', ico: 'sound', value: toggle(sound.ambient) }),
      setting({ key: 'desktop', title: 'Go to the desktop', ico: 'desktop' }),
      setting({ key: 'close', title: 'Close this screen', desc: 'BWOS keeps listening for the home button.', ico: 'exit' }),
      setting({ key: 'exit', title: 'Quit BWOS completely', ico: 'power', danger: true })));

  const info = h('div', null,
    h('div.info-card', null, h('h4', null, 'Status'),
      h('dl.kv', null,
        h('dt', null, 'Controllers'), h('dd', null, pads.length ? pads.map((p) => p.battery === 'wired' ? 'Wired' : `Battery ${p.battery}`).join(', ') : (input.padCount() ? 'Connected' : 'None')),
        h('dt', null, 'Home button'), h('dd', { class: st.guideSupported ? 'ok-text' : 'warn-text' }, st.guideSupported ? 'Supported' : 'Use View + Menu'),
        h('dt', null, 'Steam'), h('dd', { class: st.steamFound ? 'ok-text' : 'warn-text' }, st.steamFound ? 'Found' : 'Not found'),
        h('dt', null, 'Games'), h('dd', null, String(S.games.length)),
        h('dt', null, 'Player'), h('dd', null, st.browser ? st.browser.replace('msedge', 'Microsoft Edge').replace('chrome', 'Google Chrome') : 'Not found'),
        h('dt', null, 'Version'), h('dd', null, S.version || '—'))),
    h('div.info-card', null, h('h4', null, 'Make the home button open BWOS'),
      h('ol', { html:
        '<li>In <b>Steam → Settings → Controller</b>, turn off <b>Guide Button Focuses Steam</b>.</li>' +
        '<li>In <b>Windows Settings → Gaming → Game Bar</b>, turn off <b>Open Game Bar using this button on a controller</b>.</li>' +
        '<li>Turn on <b>Start BWOS with Windows</b> (left).</li>' +
        '<li>Keyboard fallback: <b>Ctrl + Alt + Home</b>.</li>' })));

  $('#settings-scroller').replaceChildren(
    h('div.view-head', null, h('h2', null, 'Settings')),
    h('div.settings-layout', null, list, info));
}

// ------------------------------------------------------------------ hero & background

let heroTimer = 0;
function onFocusChange(el, opts) {
  if (opts && opts.dir) sound.move();
  updateHints();
  if (view === 'games' && el.dataset.kind === 'game') {
    const g = gameByKey(el.dataset.key);
    clearTimeout(heroTimer);
    heroTimer = setTimeout(() => setBackground(g && g.art.hero, null), 120);
    return;
  }
  if (!HERO_VIEWS.includes(view)) return;
  clearTimeout(heroTimer);
  heroTimer = setTimeout(() => updateHero(el), opts && opts.instant ? 0 : 90);
}

const titleById = (id) => {
  for (const r of S.forYou || []) for (const t of r.items) if (t.id === id) return t;
  return null;
};
const upNextById = (id) => (S.upNext || []).find((e) => e.id === id);

function minutesLeft(e) {
  if (!e.duration) return null;
  const left = Math.max(0, Math.round((e.duration - e.position) / 60));
  if (e.position / e.duration > 0.93) return e.type === 'show' ? 'Next episode' : 'Finished';
  return left >= 60 ? `${Math.floor(left / 60)} h ${left % 60} min left` : `${left} min left`;
}

function updateHero(el) {
  const kind = el.dataset.kind;
  const hero = $(`#view-${view} .hero`);
  if (!hero) return;
  let kicker = [], title = '', logo = null, meta = [], desc = '', bg = null, ambient = null, iconMode = false, softBg = false;

  if (kind === 'game') {
    const g = gameByKey(el.dataset.key);
    if (!g) return;
    kicker.push(h('span.pill', null, SOURCE_LABEL[g.source] || g.source));
    if (g.running) kicker.push(h('span.pill.live', null, 'Running'));
    else if (g.updating) kicker.push(h('span', null, 'Update pending'));
    title = g.name;
    logo = g.art.logo;
    meta = [timeAgo(g.lastPlayed), playtime(g.playtime), size(g.size)].filter(Boolean);
    if (!meta.length) meta = ['Ready to play'];
    desc = g.running ? 'Running now — press ' + label('accept') + ' to jump back in.' : 'Press ' + label('accept') + ' to play · ' + label('options') + ' for options';
    bg = g.art.hero;
  } else if (kind === 'watch') {
    const w = watchById(el.dataset.id);
    if (!w) return;
    kicker.push(h('span.pill', null, { stream: 'Streaming', free: 'Free to watch', music: 'Music' }[w.section] || 'App'));
    title = w.name;
    meta = [w.tagline].filter(Boolean);
    desc = 'Press ' + label('accept') + ' to open · the Home button brings you back here';
    ambient = hexToRgb(w.color);
    logo = w.iconUrl;
    iconMode = true;
    bg = w.iconUrl;
  } else if (kind === 'action') {
    const map = {
      settings: ['Settings', 'Controller, library and startup options.'],
      bigpicture: ['Steam Big Picture', 'Open Steam’s own TV mode.'],
      desktop: ['Desktop', 'Minimise BWOS. Press the home button to come back.'],
      sleep: ['Sleep', 'Put the PC to sleep.'],
      restart: ['Restart', 'Restart the PC.'],
      shutdown: ['Shut down', 'Turn the PC off.']
    };
    [title, desc] = map[el.dataset.action] || ['', ''];
    kicker.push(h('span.pill', null, 'Quick action'));
  } else if (kind === 'title') {
    const t = titleById(el.dataset.id);
    if (!t) return;
    kicker.push(h('span.pill', null, t.type === 'show' ? 'Series' : 'Movie'));
    if (t.imdb) kicker.push(h('span', null, `IMDb ${Number(t.imdb).toFixed(1)}`));
    title = t.title;
    meta = [t.year ? String(t.year) : null, (t.genres || []).join(', ') || null, 'On ' + t.offers.map((o) => o.name).join(' · ')].filter(Boolean);
    desc = t.description ? (t.description.length > 230 ? t.description.slice(0, 227).trimEnd() + '…' : t.description) : '';
    bg = t.backdrop || t.poster;
    softBg = !t.backdrop; // a portrait cover makes a better colour wash than a cropped picture
  } else if (kind === 'resume') {
    const e = upNextById(el.dataset.id);
    if (!e) return;
    const app = e.app && watchById(e.app);
    kicker.push(h('span.pill.live', null, 'Continue watching'));
    if (app) kicker.push(h('span', null, app.name));
    title = e.title;
    meta = [e.subtitle || null, minutesLeft(e)].filter(Boolean);
    desc = 'Press ' + label('accept') + ' to pick up where you left off · ' + label('options') + ' to remove';
    bg = e.backdrop || e.artwork || e.poster;
  } else return;

  hero.classList.add('swap');
  setTimeout(() => {
    $('.hero-kicker', hero).replaceChildren(...kicker);
    const lg = $('.hero-logo', hero), tt = $('.hero-title', hero);
    tt.textContent = title;
    tt.classList.remove('hidden');
    lg.classList.remove('show');
    lg.classList.toggle('as-icon', iconMode);
    $('.hero-titlebox', hero).classList.toggle('with-icon', iconMode);
    // Game logos replace the title; service icons sit next to it.
    const shown = () => { lg.classList.add('show'); if (!iconMode) tt.classList.add('hidden'); };
    if (logo) {
      lg.onload = () => { if (lg.dataset.for === title) shown(); };
      lg.onerror = () => {};
      lg.dataset.for = title;
      lg.src = logo;
      if (lg.complete && lg.naturalWidth) shown();
    } else lg.removeAttribute('src');
    $('.hero-meta', hero).replaceChildren(...meta.map((m) => h('span', null, m)));
    $('.hero-desc', hero).textContent = desc;
    hero.classList.remove('swap');
  }, 140);

  if (kind !== 'action') setBackground(bg, ambient, iconMode || softBg);
}

function hexToRgb(hex) {
  const m = /^#?([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(hex || '');
  return m ? `${parseInt(m[1], 16)}, ${parseInt(m[2], 16)}, ${parseInt(m[3], 16)}` : null;
}

let bgToken = 0, bgShown = null;
async function setBackground(src, ambient, soft = false) {
  const token = ++bgToken;
  if (src === bgShown) return;
  const layers = $$('#bg .bg-layer');
  const img = src ? await preload(src) : null;
  if (token !== bgToken) return; // focus moved on meanwhile
  bgShown = src;
  const next = layers.find((l) => !l.classList.contains('show')) || layers[0];
  const prev = layers.find((l) => l !== next);
  if (img) {
    next.style.backgroundImage = `url("${src}")`;
    next.classList.toggle('soft', soft); // service icons become a blurred colour wash, not a picture
    next.classList.add('show');
    prev.classList.remove('show');
    const c = ambient || ambientColor(img);
    if (c) document.documentElement.style.setProperty('--ambient', c);
  } else {
    layers.forEach((l) => l.classList.remove('show'));
    document.documentElement.style.setProperty('--ambient', ambient || '110, 90, 255');
  }
}

// ------------------------------------------------------------------ hints (button prompts)

const GLYPHS = {
  xbox: { labels: ['A', 'B', 'X', 'Y'], cls: ['xa', 'xb', 'xx', 'xy'], bumpers: ['LB', 'RB'] },
  playstation: { labels: ['✕', '○', '□', '△'], cls: ['ps cross', 'ps circle', 'ps square', 'ps triangle'], bumpers: ['L1', 'R1'] },
  nintendo: { labels: ['B', 'A', 'Y', 'X'], cls: ['', '', '', ''], bumpers: ['L', 'R'] }
};
function buttonIndex(action) {
  if (action === 'accept') return input.swapAB ? 1 : 0;
  if (action === 'back') return input.swapAB ? 0 : 1;
  return action === 'secondary' ? 2 : 3;
}
function label(action) {
  return GLYPHS[input.family].labels[buttonIndex(action)];
}
function glyph(action) {
  const g = GLYPHS[input.family];
  if (action === 'tabs') return h('span', null, h('span.glyph.bumper', null, g.bumpers[0]), ' ', h('span.glyph.bumper', null, g.bumpers[1]));
  const i = buttonIndex(action);
  return h('span.glyph.round' + (g.cls[i] ? '.' + g.cls[i].split(' ').join('.') : ''), null, g.labels[i]);
}
const hint = (action, text) => h('div.hint', null, glyph(action), text);

function updateHints() {
  const el = focus.current;
  const k = el && el.dataset.kind;
  const items = [];
  if (modalOpen) items.push(hint('accept', 'Select'), hint('back', 'Cancel'));
  else if ($('#launch').classList.contains('show')) items.push(hint('back', 'Hide'));
  else {
    if (k === 'game') {
      const g = gameByKey(el.dataset.key);
      items.push(hint('accept', g && g.running ? 'Resume' : 'Play'), hint('options', 'Options'));
    } else if (k === 'watch') items.push(hint('accept', 'Open'), hint('options', 'Options'));
    else if (k === 'title') items.push(hint('accept', 'Watch'), hint('options', 'More'));
    else if (k === 'resume') items.push(hint('accept', 'Resume'), hint('options', 'Options'));
    else if (k === 'setting') items.push(hint('accept', 'Change'));
    else if (k === 'controls') items.push(hint('accept', 'Controller settings'));
    else if (k === 'profile') items.push(hint('accept', 'Change name'));
    else items.push(hint('accept', 'Select'));
    if (view !== 'home' || (el && !el.classList.contains('tab'))) items.push(hint('back', 'Back'));
    items.push(hint('tabs', 'Switch'));
  }
  $('#hints').replaceChildren(...items);
}

// ------------------------------------------------------------------ navigation

function switchTab(name, opts = {}) {
  if (!TABS.includes(name)) return;
  const changed = view !== name;
  view = name;
  document.body.dataset.view = name;
  $$('.view').forEach((v) => v.classList.toggle('active', v.dataset.view === name));
  $$('#tabs .tab').forEach((t) => t.classList.toggle('active', t.dataset.tab === name));
  if (changed && !opts.silent) sound.tab();
  if (name === 'watch' || name === 'settings') setBackground(null, null);
  else if (HERO_VIEWS.includes(name)) {
    // Show the hero for the focused (or remembered / first) item of that view.
    const v = $('#view-' + name);
    const heroFor = (focus.current && v.contains(focus.current) && focus.current) ||
      focus.remembered(name + '-rows') || v.querySelector('.rows [data-focus]');
    if (heroFor) updateHero(heroFor);
  }
  if (opts.focusContent) {
    const v = $('#view-' + name);
    const target = (name === 'home' && focus.remembered('home-rows')) ||
      (name === 'foryou' && focus.remembered('foryou-rows')) ||
      (name === 'games' && focus.remembered('games-grid')) ||
      (name === 'settings' && focus.remembered('settings')) ||
      v.querySelector(name === 'games' ? '#games-grid [data-focus]' : '[data-focus]');
    if (target) focus.set(target, { instant: false });
  }
  updateHints();
}

function goHome() {
  exitRest();
  exitAmbient();
  closeModal();
  hideLaunch();
  switchTab('home');
  const first = $('#home-rows [data-focus]');
  if (first) focus.set(first);
}

function describePads() {
  return Array.from(navigator.getGamepads ? navigator.getGamepads() : []).filter(Boolean)
    .map((p) => `#${p.index} "${p.id}" mapping=${p.mapping || 'none'} buttons=${p.buttons.length}`).join('; ') || 'none';
}

function onAction(action, source) {
  if (action === 'padconnected' || action === 'paddisconnected') {
    api.log(`gamepad ${action === 'padconnected' ? 'connected' : 'disconnected'} → ${describePads()} (glyphs: ${input.family})`);
    renderStatus(); updateHints();
    return;
  }
  if (S.settings && S.settings.dev && !source.endsWith('repeat')) {
    const cur = focus.current;
    api.log(`action ${action} via ${source} on ${view} / ${cur ? (cur.dataset.key || cur.textContent.trim().slice(0, 24)) : '-'}`);
  }
  syncWakeLock();
  // The first press only wakes the screen.
  if (resting()) { exitRest(); return; }
  if (document.body.classList.contains('ambient')) { exitAmbient(); return; }
  if ($('#launch').classList.contains('show')) {
    if (action === 'back') { sound.back(); hideLaunch(); }
    return;
  }

  switch (action) {
    case 'up': case 'down': case 'left': case 'right': {
      const was = focus.current;
      focus.move(action);
      // Moving onto a tab switches to it (tvOS behaviour)
      const cur = focus.current;
      if (cur && cur !== was && cur.classList.contains('tab') && (action === 'left' || action === 'right')) switchTab(cur.dataset.tab);
      break;
    }
    case 'accept': activate(focus.current); break;
    case 'back': goBack(); break;
    case 'prevTab':
    case 'nextTab': {
      if (modalOpen) return;
      const i = TABS.indexOf(view);
      const next = TABS[(i + (action === 'nextTab' ? 1 : TABS.length - 1)) % TABS.length];
      switchTab(next, { focusContent: true });
      break;
    }
    case 'options': openOptions(focus.current); break;
    case 'menu': if (!modalOpen) switchTab('settings', { focusContent: true }); break;
    case 'home': goHome(); break;
  }
}

function goBack() {
  if (modalOpen) { sound.back(); closeModal(); return; }
  const cur = focus.current;
  sound.back();
  if (cur && !cur.classList.contains('tab')) {
    // Content → tab bar (keeps your place in the content)
    const tab = $(`#tabs .tab[data-tab="${view}"]`);
    focus.set(tab);
    return;
  }
  if (view !== 'home') { switchTab('home'); focus.set($('#tabs .tab[data-tab="home"]')); }
}

function onClick(e) {
  const el = e.target.closest('[data-focus]');
  if (!el) return;
  if (el.classList.contains('tab')) return;
  focus.set(el, { silent: true });
  activate(el);
}

async function activate(el) {
  if (!el) return;
  const kind = el.dataset.kind;
  try {
    if (el.classList.contains('tab')) {
      sound.select();
      switchTab(el.dataset.tab, { focusContent: true });
      return;
    }
    if (modalOpen && el.closest('#modal')) {
      sound.select();
      const fn = modalOpen.handlers[el.dataset.action];
      // data-stay: the panel updates in place (settings toggles, showing a QR code…)
      if (!el.dataset.stay) closeModal();
      if (fn) await fn();
      return;
    }
    switch (kind) {
      case 'game': return playGame(el.dataset.key);
      case 'watch': return openWatch(el.dataset.id);
      case 'title': return playTitle(el.dataset.id);
      case 'resume': return resumeWatching(el.dataset.id);
      case 'action': sound.select(); return quickAction(el.dataset.action);
      case 'filter': sound.select(); gamesFilter = el.dataset.id; renderGames(); focus.set($(`#games-chips [data-key="filter:${el.dataset.id}"]`), { silent: true }); updateHints(); return;
      case 'sort': sound.select(); gamesSort = el.dataset.id; renderGames(); focus.set($(`#games-chips [data-key="sort:${el.dataset.id}"]`), { silent: true }); updateHints(); return;
      case 'setting': sound.select(); return changeSetting(el.dataset.setting);
      case 'controls': sound.select(); return openControls();
      case 'profile': sound.select(); return openProfile();
    }
  } catch (err) {
    sound.error();
    toast(err.message, 'alert', true);
  }
}

// ------------------------------------------------------------------ actions

async function playGame(key) {
  const g = gameByKey(key);
  if (!g) return;
  if (g.running) {
    sound.select();
    await api.resumeGame();
    return;
  }
  if (S.running && S.running !== key) {
    const other = gameByKey(S.running);
    return confirm(`${other ? other.name : 'Another game'} is still running`, 'Most PCs can only run one game at a time comfortably.', [
      ['launch', `Play ${g.name} anyway`, 'play', () => startGame(g)],
      ['resume', 'Go back to ' + (other ? other.name : 'it'), 'resume', () => api.resumeGame()],
      ['cancel', 'Cancel', null, null]
    ]);
  }
  return startGame(g);
}

async function startGame(g) {
  sound.launch();
  showLaunch({
    key: g.key,
    title: g.name,
    logo: g.art.logo,
    bg: g.art.hero || g.art.portrait,
    sub: 'Starting via ' + (SOURCE_LABEL[g.source] || 'PC') + '…',
    tip: [h('div.hint', null, 'Press the controller’s home button any time to come back here')]
  });
  await api.launch(g.key);
  launching.timer = setTimeout(() => {
    if (!launching) return;
    hideLaunch();
    toast(`${g.name} is taking a while to start`, 'info');
  }, 60000);
}

async function openWatch(id) {
  const w = watchById(id);
  if (!w) return;
  return openInside({ url: w.url, title: w.name, sub: 'Opening…', brand: w.color, returnKey: 'watch:' + id });
}

/** Opens a streaming page inside this same window, remembering where we were for the way back. */
async function openInside({ url, title, sub, brand, bg, returnKey }) {
  sound.launch();
  const tips = [['accept', 'Select'], ['back', 'Back'], ['secondary', 'Play / pause']].map(([a, t]) => hint(a, t));
  tips.push(h('div.hint', null, 'Home button returns to BWOS'));
  showLaunch({ key: returnKey, title, sub, brand, bg, tip: tips });
  const cur = focus.current;
  try { sessionStorage.setItem('bwos.return', JSON.stringify({ view, key: (cur && cur.dataset.key) || returnKey })); } catch {}
  await sleep(700);
  location.href = url;
}

async function playTitle(id) {
  const t = titleById(id);
  if (!t || !t.offers.length) return;
  const go = (o) => {
    const app = watchById(o.app);
    return openInside({ url: withAutoplay(o.url), title: t.title, sub: 'Starting on ' + o.name + '…', brand: app && app.color, bg: t.backdrop || t.poster, returnKey: 'title:' + t.id });
  };
  if (t.offers.length === 1) return go(t.offers[0]);
  return confirm(t.title, 'Where would you like to watch it?', [
    ...t.offers.map((o, i) => ['offer' + i, (o.free ? o.name + ' · Free' : o.name), 'play', () => go(o)]),
    ['cancel', 'Cancel']
  ]);
}

async function resumeWatching(id) {
  const e = upNextById(id);
  if (!e) return;
  const app = e.app && watchById(e.app);
  return openInside({ url: withAutoplay(e.url), title: e.title, sub: e.subtitle ? 'Resuming ' + e.subtitle : 'Resuming…', brand: app && app.color, bg: e.backdrop || e.artwork, returnKey: 'resume:' + e.id });
}

/** Tells the bwos-tv extension to press the site's own Play button once the page loads. */
function withAutoplay(url) {
  return url.includes('#') ? url + '&bwos-play' : url + '#bwos-play';
}

async function quickAction(action) {
  switch (action) {
    case 'settings': return switchTab('settings', { focusContent: true });
    case 'bigpicture': return api.system('bigpicture');
    case 'desktop': return api.system('desktop');
    case 'sleep': return confirm('Put the PC to sleep?', '', [['go', 'Sleep', 'moon', () => api.system('sleep')], ['cancel', 'Cancel']]);
    case 'restart': return confirm('Restart the PC?', 'Unsaved work in other apps may be lost.', [['go', 'Restart', 'restart', () => api.system('restart')], ['cancel', 'Cancel']]);
    case 'shutdown': return confirm('Shut down the PC?', 'Unsaved work in other apps may be lost.', [['go', 'Shut down', 'power', () => api.system('shutdown')], ['cancel', 'Cancel']]);
  }
}

async function changeSetting(key) {
  const st = S.settings;
  switch (key) {
    case 'trigger': {
      const i = TRIGGERS.findIndex((t) => t[0] === st.trigger);
      await api.setting('trigger', TRIGGERS[(i + 1) % TRIGGERS.length][0]);
      break;
    }
    case 'swapAB': input.swapAB = !input.swapAB; break;
    case 'country': {
      const i = REGIONS.findIndex((r) => r[0] === st.country);
      await api.setting('country', REGIONS[(i + 1) % REGIONS.length][0]);
      toast('Updating recommendations…', 'refresh');
      break;
    }
    case 'phoneKeyboard': {
      const r = await api.keyboard();
      showQr(r);
      return;
    }
    case 'forgetPhones':
      return confirm('Forget paired phones?', 'They will need to scan the QR code on the TV again.', [
        ['go', 'Forget phones', 'eyeoff', async () => { await api.forgetPhones(); toast('Phones forgotten', 'check'); reload(); }],
        ['cancel', 'Cancel']]);
    case 'refreshForYou':
      await api.refreshForYou();
      toast('Refreshing recommendations…', 'refresh');
      break;
    case 'sounds': sound.enabled = !sound.enabled; break;
    case 'ambient': sound.ambient = !sound.ambient; break;
    case 'blankAfter':
    case 'standbyAfter': {
      const list = key === 'blankAfter' ? BLANK_AFTER : STANDBY_AFTER;
      const i = list.findIndex((o) => o[0] === st[key]);
      await api.setting(key, list[(i + 1) % list.length][0]);
      break;
    }
    case 'controllerBridge':
    case 'showTools':
    case 'airplayEnabled':
      if (!st.airplayInstalled) { toast('AirPlay needs UxPlay. See the README.', 'info'); return; }
      await api.setting(key, !st[key]);
      break;
    case 'openOnWake':
    case 'castEnabled':
    case 'autostart':
      await api.setting(key, !st[key]);
      break;
    case 'rescan':
      await api.refresh();
      toast('Libraries rescanned', 'check');
      break;
    case 'desktop': return api.system('desktop');
    case 'close': return api.system('close');
    case 'exit':
      return confirm('Quit BWOS?', 'The home button will stop opening BWOS until you start it again.', [
        ['go', 'Quit BWOS', 'power', () => api.system('exit')], ['cancel', 'Cancel']]);
  }
  await reload();
  updateHints();
}

function openOptions(el) {
  if (!el || modalOpen) return;
  const kind = el.dataset.kind;
  if (kind === 'game') {
    const g = gameByKey(el.dataset.key);
    if (!g) return;
    sound.select();
    const opts = [];
    if (g.running) {
      opts.push(['resume', 'Resume', 'resume', () => api.resumeGame()]);
      opts.push(['quit', 'Quit game', 'stop', () => api.quitGame().then(() => toast(`Closing ${g.name}…`, 'check'))]);
    } else opts.push(['play', 'Play', 'play', () => playGame(g.key)]);
    opts.push(['hide', g.hidden ? 'Show in library' : 'Hide from library', g.hidden ? 'eye' : 'eyeoff', () => api.toggleHidden(g.key).then(reload)]);
    opts.push(['cancel', 'Cancel']);
    const meta = [SOURCE_LABEL[g.source], playtime(g.playtime), size(g.size)].filter(Boolean).join(' · ');
    return confirm(g.name, meta, opts);
  }
  if (kind === 'resume') {
    const e = upNextById(el.dataset.id);
    if (!e) return;
    sound.select();
    return confirm(e.title, [e.subtitle, minutesLeft(e)].filter(Boolean).join(' · '), [
      ['resume', 'Resume', 'play', () => resumeWatching(e.id)],
      ['remove', 'Remove from Up Next', 'eyeoff', () => api.dismiss(e.id).then(reload)],
      ['cancel', 'Cancel']
    ]);
  }
  if (kind === 'title') {
    const t = titleById(el.dataset.id);
    if (!t) return;
    sound.select();
    return confirm(t.title, [t.year, (t.genres || []).join(', ')].filter(Boolean).join(' · '), [
      ...t.offers.map((o, i) => ['offer' + i, 'Watch on ' + o.name, 'play', () => openInside({ url: withAutoplay(o.url), title: t.title, sub: 'Starting on ' + o.name + '…', bg: t.backdrop || t.poster, returnKey: 'title:' + t.id })]),
      ['cancel', 'Cancel']
    ]);
  }
  if (kind === 'watch') {
    const w = watchById(el.dataset.id);
    if (!w) return;
    sound.select();
    const opts = [['open', 'Open', 'play', () => openWatch(w.id)]];
    opts.push(['hide', w.hidden ? 'Show app' : 'Hide app', w.hidden ? 'eye' : 'eyeoff', () => api.toggleHidden('watch:' + w.id).then(reload)]);
    opts.push(['cancel', 'Cancel']);
    return confirm(w.name, w.url.replace(/^https?:\/\//, ''), opts);
  }
}

// ------------------------------------------------------------------ modal

function confirm(title, text, options) {
  const modal = $('#modal');
  const handlers = {};
  const buttons = options.map(([id, label, ico, fn]) => {
    handlers[id] = fn;
    return h('button.btn' + (id === 'quit' || (ico === 'power' && id === 'go') ? '.danger' : ''), { 'data-focus': true, dataset: { kind: 'btn', action: id } }, ico ? icon(ico) : null, label);
  });
  modal.replaceChildren(h('div.modal-panel', null,
    h('div.modal-title', null, title),
    text ? h('div.modal-text', null, text) : h('div', { style: { height: '1.2rem' } }),
    h('div.modal-actions', null, buttons)));
  modalOpen = { restore: focus.current, handlers };
  modal.classList.add('show');
  focus.set(buttons[0], { silent: true });
  updateHints();
}

/** Phone remote QR code in a modal (Settings → Phone remote). Closes by itself once a phone connects. */
function showQr(r) {
  if (!r || !r.url) { toast((r && r.error) || 'Phone remote unavailable', 'alert', true); return; }
  const modal = $('#modal');
  const done = h('button.btn', { 'data-focus': true, dataset: { kind: 'btn', action: 'cancel' } }, 'Done');
  const qr = h('div.qr-box', { html: window.BWOSQR ? window.BWOSQR.svg(r.url, { dark: '#0b0c12' }) : '' });
  modal.replaceChildren(h('div.modal-panel.modal-qr', null,
    qr,
    h('div', null,
      h('div.modal-title', null, 'Your phone as a remote'),
      h('div.modal-text', null, 'Scan with your phone’s camera (same Wi-Fi as this PC). Swipe to move, tap to select, and use the Keyboard tab to type. Your phone stays paired; add the page to your home screen to open it in one tap.'),
      h('div.qr-url', null, r.url.replace(/^http:\/\//, '')),
      h('div.modal-actions', null, done))));
  modalOpen = { restore: focus.current, handlers: {} };
  modal.classList.add('show');
  focus.set(done, { silent: true });
  updateHints();
}

function showPanel(panel, handlers, first) {
  const modal = $('#modal');
  modal.replaceChildren(panel);
  // Re-rendering a panel in place keeps the original place to return to.
  modalOpen = { restore: modalOpen ? modalOpen.restore : focus.current, handlers };
  modal.classList.add('show');
  focus.set(first, { silent: true });
  updateHints();
}

/** Top bar → your name. Type it with a keyboard, or on your phone. */
function openProfile(qr = null, draft = null) {
  const u = S.user || {};
  const field = h('input.field', {
    type: 'text', maxlength: 24, spellcheck: 'false', autocomplete: 'off',
    placeholder: u.steamName || 'Your name', 'data-focus': true, dataset: { kind: 'btn', action: 'save' }
  });
  field.value = draft != null ? draft : (u.name || '');
  const btn = (action, label, ico, stay) => h('button.btn', { 'data-focus': true, dataset: stay ? { kind: 'btn', action, stay: '1' } : { kind: 'btn', action } }, icon(ico), label);
  const save = async () => {
    // Leaving the Steam name as it was keeps following Steam (no custom name is stored).
    let v = field.value.trim();
    if (!u.custom && v === u.steamName) v = '';
    await api.setting('userName', v);
    await reload();
    toast(v ? `Hi, ${v}` : 'Using your Steam name', 'check');
  };
  const handlers = {
    save,
    phone: async () => {
      const r = await api.keyboard();
      if (!r || !r.url) { toast((r && r.error) || 'Phone keyboard unavailable', 'alert', true); return; }
      openProfile(r, field.value);
    },
    steam: async () => { field.value = ''; await save(); }
  };
  const actions = [btn('save', 'Save', 'check'), qr ? null : btn('phone', 'Type on your phone', 'wifi', true),
    u.steamName && u.custom ? btn('steam', `Use my Steam name (${u.steamName})`, 'refresh') : null, btn('cancel', 'Cancel', 'exit')];
  const form = h('div', null,
    h('div.modal-title', null, 'Your name'),
    h('div.modal-text', null, 'Shown in the top corner. Type with a keyboard, or on your phone.'),
    field,
    h('div.modal-actions', null, actions));
  const panel = qr && qr.url
    ? h('div.modal-panel.modal-name.with-qr', null, form,
      h('div', null, h('div.qr-box', { html: window.BWOSQR ? window.BWOSQR.svg(qr.url, { dark: '#0b0c12' }) : '' }),
        h('div.qr-note', null, 'Scan, then type. It fills in the box.')))
    : h('div.modal-panel.modal-name', null, form);
  showPanel(panel, handlers, field);
}

/** Top bar → controller: connected pads, the home button, and button behaviour. */
function openControls(focusKey) {
  const st = S.settings || {};
  const handlers = {};
  const row = (opts) => {
    const b = setting(opts);
    b.dataset.action = opts.key;
    b.dataset.stay = '1';
    handlers[opts.key] = async () => { await changeSetting(opts.key); openControls(opts.key); };
    return b;
  };
  const trig = TRIGGERS.find((t) => t[0] === st.trigger) || TRIGGERS[0];
  const pads = S.controllers || [];
  const chips = pads.length
    ? pads.map((p, i) => h('div.pad-chip', null, icon('gamepad'), `Controller ${i + 1} · ${p.battery === 'wired' ? 'wired' : 'battery ' + p.battery}`))
    : [h('div.pad-chip', null, icon('gamepad'), input.padCount() ? 'Controller connected' : 'No controller found')];
  const ps = input.family === 'playstation';
  const map = [
    [ps ? '✕' : 'A', 'Select'], [ps ? '○' : 'B', 'Back'], [ps ? '△' : 'Y', 'Options'], [ps ? 'L1 R1' : 'LB RB', 'Switch tab'],
    [ps ? 'L2 R2' : 'LT RT', 'Volume'], [ps ? 'Options' : 'Menu', 'Settings'], [ps ? 'PS' : 'Home', 'Open BWOS'], ['R3', 'Click (in apps)']
  ];
  const rows = [
    row({ key: 'trigger', title: 'Open BWOS with', desc: 'Works everywhere, even inside games.', ico: 'gamepad', value: cycleValue(trig[1]) }),
    row({ key: 'swapAB', title: 'Nintendo-style confirm', desc: 'Swap the confirm and back buttons.', ico: 'swap', value: toggle(input.swapAB) }),
    row({ key: 'controllerBridge', title: 'Controller as remote in apps', desc: 'D-pad = arrows, A = select, right stick = mouse, B = back.', ico: 'mouse', value: toggle(st.controllerBridge) })
  ];
  const done = h('button.btn', { 'data-focus': true, dataset: { kind: 'btn', action: 'cancel' } }, icon('check'), 'Done');
  const panel = h('div.modal-panel.modal-controls', null,
    h('div.modal-title', null, 'Controller'),
    h('div.pad-list', null, chips),
    rows,
    h('div.button-map', null, map.map(([b, what]) => h('div', null, h('b', null, b), what))),
    h('div.modal-actions', null, done));
  showPanel(panel, handlers, rows.find((r) => r.dataset.action === focusKey) || rows[0]);
}

function closeModal() {
  if (!modalOpen) return;
  const { restore } = modalOpen;
  modalOpen = null;
  $('#modal').classList.remove('show');
  if (restore && restore.isConnected) focus.set(restore, { silent: true });
  updateHints();
}

// ------------------------------------------------------------------ launch overlay

function showLaunch({ key, title, logo, bg, sub, tip, brand }) {
  const el = $('#launch');
  if (launching && launching.timer) clearTimeout(launching.timer);
  launching = { key, timer: 0 };
  el.classList.remove('done');
  const bgEl = $('.launch-bg', el);
  bgEl.style.backgroundImage = bg ? `url("${bg}")` : 'none';
  bgEl.style.backgroundColor = brand || '#1a1c26';
  const lg = $('.launch-logo', el), tt = $('.launch-title', el);
  tt.textContent = title;
  tt.classList.remove('hidden');
  lg.classList.remove('show');
  if (logo) {
    lg.onload = () => { lg.classList.add('show'); tt.classList.add('hidden'); };
    lg.src = logo;
  }
  $('.launch-sub', el).textContent = sub;
  $('.launch-tip', el).replaceChildren(...(tip || []));
  el.classList.add('show');
  updateHints();
}

function launchDone() {
  const el = $('#launch');
  el.classList.add('done');
  $('.launch-sub', el).textContent = 'Have fun!';
  setTimeout(hideLaunch, 1400);
}

function hideLaunch() {
  if (launching && launching.timer) clearTimeout(launching.timer);
  launching = null;
  $('#launch').classList.remove('show');
  updateHints();
}

// ------------------------------------------------------------------ toasts

function toast(text, ico = 'info', error = false) {
  const t = h('div.toast' + (error ? '.error' : ''), null, icon(ico), text);
  $('#toasts').append(t);
  setTimeout(() => { t.classList.add('out'); setTimeout(() => t.remove(), 400); }, error ? 5000 : 3200);
}

// ------------------------------------------------------------------ ambient (idle) mode

let ambientTimer = 0;
function checkIdle() {
  const st = S.settings || {};
  const onScreen = document.hasFocus() && !document.hidden;
  const idle = input.idleMs;
  if (idle > 150000 && !document.body.classList.contains('ambient') && view === 'home' && !modalOpen && !launching && onScreen) enterAmbient();
  if (st.blankAfter > 0 && idle > st.blankAfter * 60000 && onScreen && !launching && !resting()) enterRest();
  syncWakeLock();
}

/** Ambient music only while BWOS itself is on screen (not for games, streaming apps or the black screen). */
function ambientSync() {
  if (!document.hidden && document.hasFocus() && !resting()) sound.startAmbient();
  else sound.stopAmbient();
}

// ------------------------------------------------------------------ rest (black screen)
// Instead of letting Windows switch the display off (a projector then loses its signal and
// goes to standby, which usually needs its remote), BWOS fades to black and keeps the signal.
// A screen wake lock stops Windows from cutting it, until the "switch off after" time passes.

const resting = () => document.body.classList.contains('resting');

function enterRest() {
  exitAmbient();
  document.body.classList.add('resting', 'hide-cursor');
  ambientSync();
}

function exitRest() {
  if (!resting()) return;
  document.body.classList.remove('resting');
  ambientSync();
}

let wakeLock = null;
async function syncWakeLock() {
  if (!navigator.wakeLock) return;
  const st = S.settings || {};
  const want = !document.hidden && !(st.standbyAfter > 0 && input.idleMs > st.standbyAfter * 60000);
  if (want && !wakeLock) {
    wakeLock = 'pending';
    try {
      const lock = await navigator.wakeLock.request('screen');
      wakeLock = lock;
      // Chromium drops the lock when the page is hidden; it's requested again when BWOS is back.
      lock.addEventListener('release', () => { if (wakeLock === lock) wakeLock = null; });
    } catch { wakeLock = null; }
  } else if (!want && wakeLock && wakeLock !== 'pending') {
    const lock = wakeLock;
    wakeLock = null;
    lock.release().catch(() => {});
  }
}

function enterAmbient() {
  const art = visibleGames().filter((g) => g.art.hero);
  if (!art.length) return;
  document.body.classList.add('ambient');
  let i = Math.floor(Math.random() * art.length);
  const show = () => {
    const g = art[i++ % art.length];
    $('.ambient-caption').textContent = g.name;
    setBackground(g.art.hero, null);
  };
  show();
  ambientTimer = setInterval(show, 14000);
}

function exitAmbient() {
  if (!document.body.classList.contains('ambient')) return;
  document.body.classList.remove('ambient');
  clearInterval(ambientTimer);
  if (focus.current) updateHero(focus.current);
}

boot();
