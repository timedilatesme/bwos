// BWOS TV – controller layer for streaming websites shown inside the BWOS window.
//
// BWOS.exe turns the controller into real key presses while a site is open
// (D-pad → arrows, A → Enter, B → Escape, X → Space, Menu → F23, Home → F24 + Ctrl+Shift+Home,
// right stick → mouse). This script turns those keys into TV-style navigation on *any* site:
//   arrows  move a focus ring between the things you can select (spatial navigation)
//   Enter   selects / plays;  inside a video: play / pause
//   Escape  goes back (closes dialogs, leaves the player, returns to BWOS from the first page)
//   F23     "type on your phone" QR code
//   F24     straight back to the BWOS home screen
// It also fills the screen with in-page players, blocks new-tab ads, reports watch progress
// (Up Next) and presses Play for titles opened from For You. YouTube's TV interface already
// understands the keys natively, so there only Home/Back/Menu are added.

(() => {
  'use strict';

  // Inside an embedded frame (video ads love to grab keyboard focus): never let it trap the
  // controller. Home goes straight home; Back and the keyboard key are handed to the page.
  if (window.top !== window) {
    addEventListener('keydown', (e) => {
      if (e.key === 'F24') { e.preventDefault(); chrome.runtime.sendMessage({ type: 'home' }); return; }
      if (e.key === 'Escape' || e.key === 'F23') {
        e.preventDefault();
        e.stopImmediatePropagation();
        try { window.top.focus(); } catch (_) {}
        chrome.runtime.sendMessage({ type: 'key-to-top', key: e.key });
      }
    }, true);
    return;
  }

  // Keys handed over from embedded frames arrive here and are replayed as if pressed on the page.
  try {
    chrome.runtime.onMessage.addListener((msg) => {
      if (msg && msg.type === 'key') {
        window.dispatchEvent(new KeyboardEvent('keydown', { key: msg.key, bubbles: true, cancelable: true }));
      }
    });
  } catch (_) {}

  const YT_TV = location.hostname === 'www.youtube.com' && location.pathname.startsWith('/tv');
  let HOME = 'http://127.0.0.1:47913/';
  try { chrome.storage.local.get('home', (v) => { if (v && v.home) HOME = v.home; }); } catch (_) {}

  const goHome = () => { location.href = HOME; };

  // ------------------------------------------------------------------ helpers

  const SELECTABLE = [
    'a[href]', 'button', 'input:not([type=hidden])', 'select', 'textarea', 'summary',
    '[role=button]', '[role=link]', '[role=tab]', '[role=menuitem]', '[role=option]',
    '[role=checkbox]', '[role=switch]', '[role=radio]', '[tabindex]:not([tabindex="-1"])', '[onclick]'
  ].join(',');

  let current = null;      // element with the focus ring
  let navigating = false;  // user is driving with the controller
  let ring, hint;

  function mountUi() {
    if (ring || !document.body) return;
    ring = document.createElement('div');
    ring.id = 'bwos-ring';
    hint = document.createElement('div');
    hint.id = 'bwos-hint';
    hint.innerHTML = YT_TV
      ? '<b class="g">B</b> Back <span></span><b class="g home">⌂</b> BWOS home'
      : '<b class="g a">A</b> Select <span></span><b class="g">B</b> Back <span></span><b class="g x">X</b> Play/Pause <span></span>☰ Type on phone <span></span><b class="g home">⌂</b> BWOS home';
    document.documentElement.append(ring, hint);
    showHint(6000);
  }

  let hintTimer = 0;
  function showHint(ms) {
    if (!hint) return;
    hint.classList.add('on');
    clearTimeout(hintTimer);
    hintTimer = setTimeout(() => hint.classList.remove('on'), ms);
  }

  function isTyping() {
    const a = document.activeElement;
    return !!a && (a.matches('input:not([type=button]):not([type=submit]):not([type=checkbox]):not([type=radio]), textarea, select') || a.isContentEditable);
  }

  /** The dominant video when a player is showing (≥ 45 % of the screen). */
  function playerVideo() {
    const area = innerWidth * innerHeight;
    for (const v of document.querySelectorAll('video')) {
      const r = v.getBoundingClientRect();
      if (r.width * r.height >= area * 0.45 && v.readyState > 0) return v;
    }
    return null;
  }

  /** Open modal dialog, if any – navigation is kept inside it. */
  function openDialog() {
    const ds = document.querySelectorAll('dialog[open], [role=dialog], [role=alertdialog], [aria-modal=true]');
    for (let i = ds.length - 1; i >= 0; i--) {
      const r = ds[i].getBoundingClientRect();
      if (r.width > 40 && r.height > 40 && getComputedStyle(ds[i]).visibility !== 'hidden') return ds[i];
    }
    return null;
  }

  function usable(el) {
    const r = el.getBoundingClientRect();
    if (r.width < 8 || r.height < 8) return null;
    if (r.bottom < -innerHeight * 2 || r.top > innerHeight * 3) return null;       // far away: ignore
    if (r.right < -innerWidth * 1.5 || r.left > innerWidth * 2.5) return null;
    if (el.disabled || el.closest('[aria-hidden=true], [inert], #bwos-hint')) return null;
    const cs = getComputedStyle(el);
    if (cs.visibility === 'hidden' || cs.display === 'none' || parseFloat(cs.opacity) < 0.05) return null;
    // On screen: make sure it isn't covered by something else (e.g. a sticky header).
    const cx = r.left + r.width / 2, cy = r.top + r.height / 2;
    if (cx > 0 && cy > 0 && cx < innerWidth && cy < innerHeight) {
      const hit = document.elementFromPoint(cx, cy);
      if (hit && hit !== el && !el.contains(hit) && !hit.contains(el)) {
        // allow if the covering element is part of the same clickable card
        const hitSel = hit.closest(SELECTABLE);
        if (!hitSel || (!el.contains(hitSel) && !hitSel.contains(el))) return null;
      }
    }
    return r;
  }

  /**
   * Many streaming sites build posters as plain <div>s with click handlers and no role.
   * The one reliable tell is the hand cursor: collect the outermost `cursor: pointer`
   * element of each clickable area (its inner buttons are found by SELECTABLE anyway).
   */
  function pointerRoots(scope) {
    const out = [];
    const root = scope === document ? document.body : scope;
    if (!root) return out;
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT, {
      acceptNode(node) {
        if (node.id === 'bwos-ring' || node.id === 'bwos-hint' || node.id === 'bwos-kbd') return NodeFilter.FILTER_REJECT;
        const tag = node.tagName;
        if (tag === 'SCRIPT' || tag === 'STYLE' || tag === 'svg' || tag === 'VIDEO' || tag === 'NOSCRIPT') return NodeFilter.FILTER_REJECT;
        if (getComputedStyle(node).cursor !== 'pointer') return NodeFilter.FILTER_SKIP;
        return NodeFilter.FILTER_ACCEPT;
      }
    });
    for (let n = walker.nextNode(); n; n = walker.nextNode()) {
      const parent = n.parentElement;
      if (parent && getComputedStyle(parent).cursor === 'pointer') continue; // not the outermost
      out.push(n);
    }
    return out;
  }

  function candidates() {
    const scope = openDialog() || document;
    const found = [];
    const seen = new Set();
    for (const el of [...scope.querySelectorAll(SELECTABLE), ...pointerRoots(scope)]) {
      if (seen.has(el)) continue;
      seen.add(el);
      const r = usable(el);
      if (r) found.push({ el, r });
    }
    // A clickable card often contains a clickable image/title of nearly the same size: keep one.
    const set = new Set(found.map((c) => c.el));
    return found.filter(({ el, r }) => {
      for (let p = el.parentElement; p; p = p.parentElement) {
        if (set.has(p)) {
          const pr = p.getBoundingClientRect();
          return r.width * r.height < 0.6 * pr.width * pr.height;
        }
      }
      return true;
    });
  }

  /** Inside a fixed/sticky layer (site header, cookie or app banner)? */
  const pinnedCache = new WeakMap();
  function pinned(el) {
    if (pinnedCache.has(el)) return pinnedCache.get(el);
    let result = false;
    for (let p = el; p && p !== document.body; p = p.parentElement) {
      const pos = getComputedStyle(p).position;
      if (pos === 'fixed' || pos === 'sticky') { result = true; break; }
    }
    pinnedCache.set(el, result);
    return result;
  }

  function spatial(fromRect, dir, list) {
    const cx = fromRect.left + fromRect.width / 2, cy = fromRect.top + fromRect.height / 2;
    const fromPinned = current ? pinned(current) : false;
    let best = null, bestScore = Infinity;
    for (const c of list) {
      if (c.el === current) continue;
      const q = c.r;
      const qx = q.left + q.width / 2, qy = q.top + q.height / 2;
      let primary, overlap;
      if (dir === 'left' || dir === 'right') {
        if (dir === 'right' ? qx <= cx + 2 : qx >= cx - 2) continue;
        primary = dir === 'right' ? q.left - fromRect.right : fromRect.left - q.right;
        overlap = Math.min(fromRect.bottom, q.bottom) - Math.max(fromRect.top, q.top);
      } else {
        if (dir === 'down' ? qy <= cy + 2 : qy >= cy - 2) continue;
        primary = dir === 'down' ? q.top - fromRect.bottom : fromRect.top - q.bottom;
        overlap = Math.min(fromRect.right, q.right) - Math.max(fromRect.left, q.left);
      }
      primary = Math.max(0, primary);
      const ortho = overlap > 0 ? 0 : (dir === 'left' || dir === 'right' ? Math.abs(qy - cy) : Math.abs(qx - cx));
      let score = primary + ortho * 2.5 + (overlap > 0 ? 0 : 60);
      // Don't wander from the content into a pinned header/banner unless nothing else is that way.
      if (!fromPinned && pinned(c.el)) score += 5000;
      if (score < bestScore) { bestScore = score; best = c.el; }
    }
    return best;
  }

  /** First thing to focus: something big near the top-left of the main content. */
  function visibleShare(r) {
    const w = Math.max(0, Math.min(r.right, innerWidth) - Math.max(r.left, 0));
    const h = Math.max(0, Math.min(r.bottom, innerHeight) - Math.max(r.top, 0));
    return (w * h) / Math.max(1, r.width * r.height);
  }

  function startTarget(list) {
    const onScreen = list.filter(({ r }) => visibleShare(r) >= 0.6);
    const pool = onScreen.length ? onScreen : list;
    // Posters and cards – not headings, menu links, slim carousel arrows or banner buttons.
    const big = pool.filter(({ el, r }) => r.width >= 100 && r.height >= 70 && r.width * r.height >= 140 * 110 && !pinned(el));
    const choose = (big.length ? big : pool).sort((a, b) => (a.r.top - b.r.top) || (a.r.left - b.r.left));
    // Skip the site's top navigation bar if there is content below it.
    const content = choose.find(({ r }) => r.top > innerHeight * 0.15);
    return (content || choose[0] || {}).el;
  }

  // ------------------------------------------------------------------ focus ring

  function focusEl(el) {
    current = el;
    try { el.focus({ preventScroll: true }); } catch (_) {}
    const r = el.getBoundingClientRect();
    const margin = 90;
    if (r.top < margin || r.bottom > innerHeight - margin || r.left < 0 || r.right > innerWidth) {
      el.scrollIntoView({ block: 'center', inline: 'center', behavior: 'smooth' });
    }
    const cs = getComputedStyle(el);
    ring.style.borderRadius = `max(${cs.borderRadius || '0px'}, 8px)`;
    ring.classList.add('on');
    ring.classList.remove('press');
    // Carousels built with CSS transforms ignore scrollIntoView: use the row's own arrow.
    if (r.right > innerWidth + 4 || r.left < -4) {
      setTimeout(() => {
        if (current !== el) return;
        const now = el.getBoundingClientRect();
        if (now.right > innerWidth + 4) pressRowArrow(el, 'right');
        else if (now.left < -4) pressRowArrow(el, 'left');
      }, 320);
    }
  }

  function findRowArrow(from, dir) {
    const re = dir === 'right' ? /(next|right|forward|handlenext)/ : /(prev|left|back|handleprev)/;
    for (let p = from.parentElement, depth = 0; p && depth < 12; p = p.parentElement, depth++) {
      const arrow = [...p.querySelectorAll('button, [role=button], a, span, div')].find((b) => {
        const label = `${b.getAttribute('aria-label') || ''} ${typeof b.className === 'string' ? b.className : ''}`.toLowerCase();
        return re.test(label) && !b.contains(from) && b.getBoundingClientRect().width > 0;
      });
      if (arrow) return arrow;
    }
    return null;
  }

  function pressRowArrow(from, dir) {
    const arrow = findRowArrow(from, dir);
    if (arrow) activate(arrow, true);
    return !!arrow;
  }

  function trackRing() {
    requestAnimationFrame(trackRing);
    if (!ring) return;
    if (!navigating || !current || !current.isConnected || playerVideo()) {
      ring.classList.remove('on');
      return;
    }
    const r = current.getBoundingClientRect();
    ring.style.transform = `translate(${r.left - 5}px, ${r.top - 5}px)`;
    ring.style.width = `${r.width + 10}px`;
    ring.style.height = `${r.height + 10}px`;
  }
  requestAnimationFrame(trackRing);

  // ------------------------------------------------------------------ actions

  function move(dir, retry) {
    const list = candidates();
    if (!current || !current.isConnected || !list.some((c) => c.el === current)) {
      const start = startTarget(list);
      if (start) focusEl(start);
      return;
    }
    const target = spatial(current.getBoundingClientRect(), dir, list);
    if (target) { focusEl(target); return; }
    if (retry) return;
    if (dir === 'left' || dir === 'right') scrollCarousel(dir);
    else window.scrollBy({ top: (dir === 'down' ? 1 : -1) * innerHeight * 0.6, behavior: 'smooth' });
  }

  /** At the end of a row: scroll the row itself, or press the site's own "next" arrow. */
  function scrollCarousel(dir) {
    const sign = dir === 'right' ? 1 : -1;
    for (let p = current.parentElement, depth = 0; p && depth < 12; p = p.parentElement, depth++) {
      const cs = getComputedStyle(p);
      if (p.scrollWidth > p.clientWidth + 8 && /(auto|scroll)/.test(cs.overflowX)) {
        p.scrollBy({ left: sign * p.clientWidth * 0.8, behavior: 'smooth' });
        setTimeout(() => move(dir, true), 450);
        return;
      }
    }
    if (pressRowArrow(current, dir)) setTimeout(() => move(dir, true), 650);
  }

  function activate(el, quiet) {
    const r = el.getBoundingClientRect();
    const x = r.left + r.width / 2, y = r.top + r.height / 2;
    const base = { bubbles: true, cancelable: true, composed: true, clientX: x, clientY: y, view: window, button: 0 };
    try { el.focus({ preventScroll: true }); } catch (_) {}
    el.dispatchEvent(new PointerEvent('pointerdown', { ...base, pointerType: 'mouse', isPrimary: true }));
    el.dispatchEvent(new MouseEvent('mousedown', base));
    el.dispatchEvent(new PointerEvent('pointerup', { ...base, pointerType: 'mouse', isPrimary: true }));
    el.dispatchEvent(new MouseEvent('mouseup', base));
    el.click();
    if (ring && !quiet) {
      ring.classList.add('press');
      setTimeout(() => ring && ring.classList.remove('press'), 160);
    }
  }

  /** Show the player's controls, like wiggling the mouse. */
  function pokePlayer(v) {
    const r = v.getBoundingClientRect();
    const x = r.left + r.width / 2, y = r.top + r.height * 0.6;
    const hit = document.elementFromPoint(x, y) || v;
    hit.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: x, clientY: y, view: window }));
  }

  function togglePlay(v) {
    pokePlayer(v);
    if (adShowing(v)) { osd('Advertisement – it will continue by itself'); return; } // never click into an ad
    const r = v.getBoundingClientRect();
    const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    // Prefer the site's own click handling (keeps its UI in sync); fall back to the element API.
    const wasPaused = v.paused;
    if (hit) activate(hit);
    setTimeout(() => { if (v.paused === wasPaused) { if (wasPaused) v.play().catch(() => {}); else v.pause(); } }, 250);
  }

  function back() {
    const dialog = openDialog();
    if (dialog) {
      const close = dialog.querySelector('[aria-label*="close" i], [aria-label*="dismiss" i], [data-uia*="close" i], button[class*="close" i]');
      if (close) { activate(close); return; }
    }
    if (document.fullscreenElement) { document.exitFullscreen().catch(() => {}); return; }
    const before = location.href;
    history.back();
    // Nothing to go back to inside the service → return to BWOS.
    setTimeout(() => { if (location.href === before && document.visibilityState === 'visible') goHome(); }, 700);
  }

  // ------------------------------------------------------------------ key handling

  const DIRS = { ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right' };

  addEventListener('keydown', (e) => {
    if (e.key === 'F24') {                       // Home button → BWOS home screen
      e.preventDefault(); e.stopImmediatePropagation();
      goHome();
      return;
    }
    if (e.key === 'F23') {                       // Menu button → type on your phone
      e.preventDefault(); e.stopImmediatePropagation();
      if (kbd) hideKeyboard(); else showKeyboard();
      return;
    }
    if (e.key === 'Escape' && kbd) { e.preventDefault(); e.stopImmediatePropagation(); hideKeyboard(); return; }
    if (YT_TV || e.ctrlKey || e.altKey || e.metaKey) return;
    if (filled) return; // the full-screen player handles keys itself (see below)
    mountUi();

    const dir = DIRS[e.key];
    const video = playerVideo();

    if (dir) {
      if (video) { pokePlayer(video); return; }  // let the player seek / change volume
      if (isTyping() && (dir === 'left' || dir === 'right')) return;
      e.preventDefault(); e.stopImmediatePropagation();
      document.documentElement.classList.add('bwos-pad');
      navigating = true;
      move(dir);
      return;
    }

    if (e.key === 'Enter') {
      if (video && !openDialog()) { e.preventDefault(); e.stopImmediatePropagation(); togglePlay(video); return; }
      if (isTyping()) return;
      if (navigating && current && current.isConnected) {
        e.preventDefault(); e.stopImmediatePropagation();
        activate(current);
      }
      return;
    }

    if (e.key === 'Escape') {
      if (isTyping()) { document.activeElement.blur(); e.preventDefault(); return; }
      e.preventDefault(); e.stopImmediatePropagation();
      back();
    }
  }, true);

  // A real mouse takes over from the controller (and the ring hides).
  addEventListener('mousemove', (e) => {
    if (!e.isTrusted || (e.movementX === 0 && e.movementY === 0)) return;
    navigating = false;
    document.documentElement.classList.remove('bwos-pad');
  }, true);

  // Keep everything in this one window. Links that would open a new tab: same-site ones open
  // here instead; ones to other sites are almost always ads ("opens new tab") and are dropped.
  const SIGN_IN = /(^|\.)(google\.com|apple\.com|amazon\.[a-z.]+|primevideo\.com|netflix\.com|microsoft\.com|live\.com|facebook\.com)$/i;
  const sameSite = (u) => {
    const a = u.hostname.replace(/^www\./, ''), b = location.hostname.replace(/^www\./, '');
    return a === b || a.endsWith('.' + b) || b.endsWith('.' + a);
  };
  addEventListener('click', (e) => {
    const a = e.target.closest && e.target.closest('a[target=_blank][href]');
    if (!a || !/^https?:/.test(a.href)) return;
    e.preventDefault();
    const u = new URL(a.href);
    if (sameSite(u) || SIGN_IN.test(u.hostname)) location.href = a.href;
  }, true);

  // ------------------------------------------------------------------ auto full screen
  // Sites without their own TV player (Einthusan, Tubi, Plex…) play video inside the page.
  // As soon as the film starts, its player fills the whole screen; B leaves full screen.
  const OWN_PLAYER = /(netflix|primevideo|amazon|youtube)\.com$/;
  let filled = null;   // { box, video }

  // Whole-player containers of sites we know (they hold the film AND the ad layer).
  const KNOWN_PLAYERS = '#html5-player, #UIVideoPlayer';
  const AD_LAYER = '#ad-container, [id*="ad-container"], [class*="ad-container"], [class*="ima-ad"], [class*="adsbygoogle"]';

  /** Is an advertisement showing on top of the film right now? */
  function adShowing(v) {
    const r = v.getBoundingClientRect();
    const top = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    if (!top) return false;
    if (top.tagName === 'IFRAME' || top.closest(AD_LAYER)) return true;
    return [...document.querySelectorAll('video')].some((a) => a !== v && !a.paused && /advert/i.test(a.title || '') && a.getBoundingClientRect().width > 0);
  }

  function playerBox(v) {
    const known = v.closest(KNOWN_PLAYERS);
    if (known) return known;
    // The smallest wrapper around the video that also holds the site's own controls…
    const vr = v.getBoundingClientRect();
    let box = v;
    for (let p = v.parentElement; p && p !== document.body; p = p.parentElement) {
      const r = p.getBoundingClientRect();
      if (r.width > vr.width + 60 || r.height > vr.height + 140) break;
      box = p;
    }
    // …and its ad layer, if the ad sits next to the video (otherwise the ad would play hidden).
    if (box.parentElement && box.parentElement !== document.body && box.parentElement.querySelector(AD_LAYER)) box = box.parentElement;
    return box;
  }

  function enterFill(v) {
    if (filled || OWN_PLAYER.test(HOST)) return;
    const r = v.getBoundingClientRect();
    if (r.width * r.height > innerWidth * innerHeight * 0.92) return; // already full screen
    const box = playerBox(v);
    box.classList.add('bwos-fill');
    if (box !== v) v.classList.add('bwos-fill-video');
    document.documentElement.classList.add('bwos-filled');
    filled = { box, video: v };
    if (ring) ring.classList.remove('on');
    if (hint) {
      hint.innerHTML = '<b class="g a">A</b> Play/Pause <span></span>◀ ▶ Skip 10 s <span></span><b class="g">B</b> Exit full screen <span></span>LT / RT Volume';
      showHint(4000);
    }
  }

  function exitFill() {
    if (!filled) return;
    filled.box.classList.remove('bwos-fill');
    filled.video.classList.remove('bwos-fill-video');
    document.documentElement.classList.remove('bwos-filled');
    filled = null;
  }

  document.addEventListener('playing', (e) => {
    const v = e.target;
    if (v instanceof HTMLVideoElement && v === watchedVideo()) enterFill(v);
  }, true);
  document.addEventListener('ended', (e) => { if (filled && e.target === filled.video) exitFill(); }, true);

  function osd(text) {
    if (!hint) return;
    hint.textContent = text;
    showHint(1200);
  }

  // Keys while a film fills the screen (runs before the general handler above).
  addEventListener('keydown', (e) => {
    if (!filled || !filled.video.isConnected) { if (filled) exitFill(); return; }
    const v = filled.video;
    const fmt = (s) => `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, '0')}`;
    let handled = true;
    switch (e.key) {
      case 'Escape': exitFill(); break;
      case 'Enter': case ' ': {
        if (adShowing(v)) { osd('Advertisement – it will continue by itself'); break; } // never fight the ad player
        if (v.paused) v.play().catch(() => {}); else v.pause();
        osd(v.paused ? 'Paused' : 'Playing');
        break;
      }
      case 'ArrowLeft': v.currentTime = Math.max(0, v.currentTime - 10); osd('◀ ' + fmt(v.currentTime)); break;
      case 'ArrowRight': v.currentTime = Math.min(v.duration - 1, v.currentTime + 10); osd('▶ ' + fmt(v.currentTime)); break;
      case 'ArrowUp': case 'ArrowDown': pokePlayer(v); osd(fmt(v.currentTime) + ' / ' + fmt(v.duration)); break;
      default: handled = false;
    }
    if (handled) { e.preventDefault(); e.stopImmediatePropagation(); }
  }, true);

  // ------------------------------------------------------------------ watch progress (Up Next)
  // Reports what is playing to BWOS so "Up Next" can follow the show across services.

  const HOST = location.hostname.replace(/^www\./, '');
  const textOf = (sel) => { const el = document.querySelector(sel); return el ? el.textContent.replace(/\s+/g, ' ').trim() : ''; };
  const metaOf = (name) => { const m = document.querySelector(`meta[property="${name}"], meta[name="${name}"]`); return m ? (m.content || '').trim() : ''; };

  function send(type, data) {
    return new Promise((resolve) => {
      try { chrome.runtime.sendMessage({ type, data }, (r) => { void chrome.runtime.lastError; resolve(r); }); }
      catch (_) { resolve(null); }
    });
  }

  /** The main video that is actually being watched (not a muted hover preview). */
  function watchedVideo() {
    let best = null, area = 0;
    for (const v of document.querySelectorAll('video')) {
      const r = v.getBoundingClientRect();
      const a = r.width * r.height;
      if (a > area && v.duration > 120 && isFinite(v.duration) && !v.muted) { best = v; area = a; }
    }
    return best;
  }

  /** Show title, episode line and artwork: site-specific first, then Media Session, then page metadata. */
  function nowPlaying() {
    let title = '', subtitle = '', artwork = '';
    if (/netflix\.com$/.test(HOST)) {
      const box = document.querySelector('[data-uia="video-title"]');
      if (box) {
        const h = box.querySelector('h4');
        title = (h || box).textContent.trim();
        subtitle = [...box.querySelectorAll('span')].map((s) => s.textContent.trim()).filter(Boolean).join(' · ');
      }
    } else if (/(primevideo|amazon)\.com$/.test(HOST)) {
      title = textOf('.atvwebplayersdk-title-text');
      subtitle = textOf('.atvwebplayersdk-subtitle-text');
    }
    const ms = navigator.mediaSession && navigator.mediaSession.metadata;
    if (!title && ms && ms.title) {
      title = ms.title;
      subtitle = ms.artist || ms.album || '';
    }
    if (ms && ms.artwork && ms.artwork.length) {
      const big = [...ms.artwork].sort((a, b) => parseInt(b.sizes || '0', 10) - parseInt(a.sizes || '0', 10))[0];
      artwork = big && big.src || '';
    }
    if (!title) title = metaOf('og:title') || document.title;
    if (!artwork) artwork = metaOf('og:image');
    return { title: title.trim(), subtitle: subtitle.trim(), artwork };
  }

  function report() {
    const v = watchedVideo();
    if (!v || !v.currentTime) return;
    const np = nowPlaying();
    if (!np.title) return;
    send('progress', { url: location.href, title: np.title, subtitle: np.subtitle, artwork: np.artwork, position: v.currentTime, duration: v.duration });
  }

  setInterval(() => { const v = watchedVideo(); if (v && !v.paused) report(); }, 15000);
  document.addEventListener('pause', (e) => { if (e.target === watchedVideo()) report(); }, true);
  document.addEventListener('ended', (e) => { if (e.target === watchedVideo()) report(); }, true);
  addEventListener('pagehide', report);

  // Resume where you left off – for sites that don't remember it themselves.
  const SELF_RESUMING = /(netflix|primevideo|amazon|youtube)\.com$/;
  const resumed = new Set();
  document.addEventListener('playing', async (e) => {
    const v = e.target;
    if (SELF_RESUMING.test(HOST) || resumed.has(location.href) || v !== watchedVideo()) return;
    resumed.add(location.href);
    const r = await send('lookup', { url: location.href });
    const pos = r && r.position;
    if (pos > 30 && pos < v.duration - 60 && v.currentTime < 15) {
      v.currentTime = pos;
      if (hint) {
        const m = Math.floor(pos / 60), s = String(Math.floor(pos % 60)).padStart(2, '0');
        hint.innerHTML = `Resumed at ${m}:${s} <span></span><b class="g">B</b> Back <span></span><b class="g home">⌂</b> BWOS home`;
        showHint(4000);
      }
    }
  }, true);

  // ------------------------------------------------------------------ type on your phone
  let kbd = null;

  async function showKeyboard() {
    mountUi();
    // If the focus ring is on a text box, put the cursor there so the phone types into it.
    if (current && current.isConnected && current.matches('input, textarea, [contenteditable="true"]')) {
      try { current.focus(); current.click(); } catch (_) {}
    }
    const r = await send('keyboard', {});
    if (!r || !r.url) { osd((r && r.error) || 'Phone remote unavailable – is BWOS running?'); return; }
    hideKeyboard();
    kbd = document.createElement('div');
    kbd.id = 'bwos-kbd';
    kbd.innerHTML =
      `<div class="bwos-kbd-qr">${self.BWOSQR ? self.BWOSQR.svg(r.url, { dark: '#0b0c12' }) : ''}</div>` +
      '<div class="bwos-kbd-text"><b>Your phone as a remote</b>' +
      '<span>Scan with your phone camera (same Wi-Fi). Swipe to move, tap to select, or use the Keyboard tab to type into the box picked on the TV.</span>' +
      `<small>${r.url.replace(/^http:\/\//, '')}</small><small>Menu or B to close</small></div>`;
    document.documentElement.append(kbd);
    requestAnimationFrame(() => kbd && kbd.classList.add('on'));
  }

  function hideKeyboard() {
    if (!kbd) return;
    kbd.remove();
    kbd = null;
  }

  // ------------------------------------------------------------------ one-press play
  // Titles opened from For You / Up Next arrive with #bwos-play: press the site's own Play button
  // (Einthusan, Netflix and Prime title pages…) so the film starts – and fills the screen – by itself.
  const PLAY_WORDS = /^\s*(play|resume|watch now|play now|start watching|continue watching|watch|play movie|play episode|watch free)\b/i;

  function findPlayButton() {
    const scope = document.querySelector('#UIVideoPlayer, #html5-player') || document;
    const els = scope.querySelectorAll('button, [role=button], a, div, span');
    let best = null, bestScore = 0;
    for (const el of els) {
      const r = el.getBoundingClientRect();
      if (r.width < 24 || r.height < 24 || r.width * r.height > innerWidth * innerHeight * 0.5) continue;
      if (r.bottom < 0 || r.top > innerHeight) continue;
      const cs = getComputedStyle(el);
      if (cs.visibility === 'hidden' || cs.display === 'none' || +cs.opacity < 0.2) continue;
      const label = (el.getAttribute('aria-label') || el.getAttribute('title') || el.textContent || '').trim().slice(0, 40);
      const hint = `${el.id} ${typeof el.className === 'string' ? el.className : ''} ${el.getAttribute('data-uia') || ''}`.toLowerCase();
      if (/trailer|preview|teaser|playlist|player-settings/i.test(label + hint)) continue;
      let score = 0;
      if (PLAY_WORDS.test(label)) score += 3;
      if (/(^|[\s_-])(play|big-play|vjs-big-play|play-button|playbutton)([\s_-]|$)/.test(hint)) score += 2;
      if (scope !== document) score += 1;
      if (score > bestScore) { bestScore = score; best = el; }
    }
    return bestScore >= 2 ? best : null;
  }

  function anyPlaying() {
    return [...document.querySelectorAll('video')].some((v) => !v.paused && v.currentTime > 0.5);
  }

  function autoPlay() {
    let attempts = 0;
    const started = Date.now();
    const tick = () => {
      if (anyPlaying() || Date.now() - started > 30000) return;
      const btn = findPlayButton();
      if (btn && attempts < 3) {
        attempts++;
        activate(btn, true);
      } else if (!btn) {
        const v = document.querySelector('#UIVideoPlayer video, video');
        if (v && v.readyState > 0 && v.paused) v.play().catch(() => {});
      }
      setTimeout(tick, 2500);
    };
    setTimeout(tick, 5000); // sites like Einthusan run their own "starting in 4 seconds" first
  }

  if (/bwos-play/.test(location.hash)) {
    history.replaceState(history.state, '', location.href.replace(/#bwos-play$|bwos-play&?/, '').replace(/#$/, ''));
    if (document.readyState === 'complete') autoPlay();
    else addEventListener('load', autoPlay, { once: true });
  }

  if (document.readyState === 'loading') addEventListener('DOMContentLoaded', mountUi, { once: true });
  else mountUi();
})();
