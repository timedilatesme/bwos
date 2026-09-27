// Focus engine for 10-foot navigation.
//  • Rows (home screen) behave like Android TV / tvOS shelves: left/right walk the shelf,
//    up/down jump between shelves and remember the position in each one.
//  • Everything else uses geometric (spatial) navigation.
//  • The focused element gets a tvOS-style parallax tilt + glare.

import { $$ } from './util.js';

const memory = new Map();          // group name -> last focused element
let current = null;
let scopeFn = () => document.body;
let changeFn = () => {};
let nudgeTimer = 0;

export const focus = {
  get current() { return current; },
  setScope(fn) { scopeFn = fn; },
  onChange(fn) { changeFn = fn; },
  set,
  move,
  remembered(group) {
    const el = memory.get(group);
    return el && el.isConnected ? el : null;
  },
  forget(group) { memory.delete(group); },
  ensureVisible
};

function visible(el) {
  if (!el.isConnected || el.offsetParent === null) return false;
  const v = el.closest('.view');
  return !v || v.classList.contains('active');
}

function focusables() {
  return $$('[data-focus]', scopeFn()).filter(visible);
}

function set(el, opts = {}) {
  if (!el) return;
  if (current && current !== el) {
    current.classList.remove('is-focused');
    current.style.removeProperty('--rx');
    current.style.removeProperty('--ry');
  }
  current = el;
  el.classList.add('is-focused');
  if (!el.querySelector(':scope > .glare') && (el.classList.contains('card') || el.classList.contains('tile'))) {
    const g = document.createElement('i');
    g.className = 'glare';
    el.append(g);
  }
  const group = el.closest('[data-group]');
  if (group) memory.set(group.dataset.group, el);
  ensureVisible(el, opts.instant);
  if (opts.dir) nudge(el, opts.dir);
  if (!opts.silent) changeFn(el, opts);
}

/** Returns true if focus moved. */
function move(dir) {
  const from = current && current.isConnected && visible(current) ? current : null;
  if (!from) {
    const first = focusables()[0];
    if (first) set(first, { dir });
    return !!first;
  }

  const row = from.closest('.row');
  if (row) {
    const target = rowMove(from, row, dir);
    if (target === 'edge') { bump(from, dir); return false; }
    if (target) { set(target, { dir }); return true; }
  }

  const target = spatial(from, dir, focusables());
  if (!target) { bump(from, dir); return false; }

  // Entering the home shelves from the tab bar: land on the remembered shelf item.
  if (target.closest('.row') && !row) {
    const rows = target.closest('.rows');
    const remembered = rows && focus.remembered(rows.dataset.memo || 'home-rows');
    if (remembered && visible(remembered)) { set(remembered, { dir }); return true; }
  }

  // Entering a group that remembers its last position.
  const tg = target.closest('[data-group][data-remember]');
  const fg = from.closest('[data-group]');
  if (tg && tg !== fg) {
    const rem = focus.remembered(tg.dataset.group);
    if (rem && visible(rem)) { set(rem, { dir }); return true; }
  }
  set(target, { dir });
  return true;
}

function rowMove(from, row, dir) {
  const items = $$('[data-focus]', row).filter(visible);
  const i = items.indexOf(from);
  if (dir === 'left') return i > 0 ? items[i - 1] : 'edge';
  if (dir === 'right') return i < items.length - 1 ? items[i + 1] : 'edge';

  const rows = $$('.row', row.parentElement).filter((r) => r.querySelector('[data-focus]'));
  const ri = rows.indexOf(row);
  const next = rows[ri + (dir === 'down' ? 1 : -1)];
  if (!next) return dir === 'up' ? null /* fall through to spatial → tab bar */ : 'edge';
  const nextItems = $$('[data-focus]', next).filter(visible);
  const mem = memory.get('row:' + next.dataset.row);
  if (mem && mem.isConnected && nextItems.includes(mem)) return mem;
  // Otherwise the first fully visible item of that shelf
  const track = next.querySelector('.row-track');
  const shift = track ? -(new DOMMatrixReadOnly(getComputedStyle(track).transform).m41 || 0) : 0;
  return nextItems.find((it) => it.offsetLeft >= shift - 4) || nextItems[0];
}

function spatial(from, dir, all) {
  const r = from.getBoundingClientRect();
  const cx = r.left + r.width / 2, cy = r.top + r.height / 2;
  let best = null, bestScore = Infinity;
  for (const el of all) {
    if (el === from) continue;
    const q = el.getBoundingClientRect();
    const qx = q.left + q.width / 2, qy = q.top + q.height / 2;
    let primary, overlap, ortho;
    if (dir === 'left' || dir === 'right') {
      if (dir === 'right' ? qx <= cx + 2 : qx >= cx - 2) continue;
      primary = dir === 'right' ? Math.max(0, q.left - r.right) : Math.max(0, r.left - q.right);
      overlap = Math.min(r.bottom, q.bottom) - Math.max(r.top, q.top);
      ortho = overlap > 0 ? 0 : Math.abs(qy - cy);
    } else {
      if (dir === 'down' ? qy <= cy + 2 : qy >= cy - 2) continue;
      primary = dir === 'down' ? Math.max(0, q.top - r.bottom) : Math.max(0, r.top - q.bottom);
      overlap = Math.min(r.right, q.right) - Math.max(r.left, q.left);
      ortho = overlap > 0 ? 0 : Math.abs(qx - cx);
    }
    const score = primary + ortho * 2.2 + (overlap > 0 ? 0 : 40);
    if (score < bestScore) { bestScore = score; best = el; }
  }
  return best;
}

function ensureVisible(el, instant) {
  // Horizontal shelves: anchor the focused item to the left edge (clamped at the end).
  const track = el.closest('.row-track');
  if (track) {
    const vp = track.parentElement;
    const cs = getComputedStyle(vp);
    const avail = vp.clientWidth - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight);
    const max = Math.max(0, track.scrollWidth - avail);
    const x = Math.min(Math.max(0, el.offsetLeft), max);
    setTransform(track, `translateX(${-x}px)`, instant);
    const row = el.closest('.row');
    if (row) memory.set('row:' + row.dataset.row, el);
  }

  const scroller = el.closest('.scroller');
  if (!scroller) {
    // Focus went to the tab bar: bring the home shelves back to the top.
    for (const s of $$('.scroller[data-anchor="top"]')) {
      if (!s.closest('.view.active')) continue;
      setTransform(s, 'translateY(0px)', instant);
      $$('.row', s).forEach((r) => r.classList.remove('past', 'current'));
    }
    return;
  }

  if (scroller.dataset.anchor === 'top') {
    const row = el.closest('.row');
    if (!row) return;
    setTransform(scroller, `translateY(${-row.offsetTop}px)`, instant);
    const rows = $$('.row', scroller);
    const idx = rows.indexOf(row);
    rows.forEach((r, i) => {
      r.classList.toggle('past', i < idx);
      r.classList.toggle('current', i === idx);
    });
    const memo = scroller.dataset.memo || 'home-rows';
    memory.set(memo, el);
    return;
  }

  // Free vertical scrolling: keep the focused element in the comfortable middle band.
  const viewport = scroller.parentElement;
  const vh = viewport.clientHeight;
  const cur = -(new DOMMatrixReadOnly(getComputedStyle(scroller).transform).m42 || 0);
  const sr = scroller.getBoundingClientRect();
  const er = el.getBoundingClientRect();
  const top = er.top - sr.top;
  const bottom = top + er.height;
  let y = cur;
  if (top - cur < vh * 0.18 || bottom - cur > vh * 0.78) y = top + er.height / 2 - vh * 0.42;
  const max = Math.max(0, scroller.scrollHeight - vh);
  y = Math.min(Math.max(0, y), max);
  if (el.closest('.view-head')) y = 0;
  setTransform(scroller, `translateY(${-y}px)`, instant);
}

function setTransform(node, value, instant) {
  if (instant) {
    node.style.transition = 'none';
    node.style.transform = value;
    void node.offsetWidth;
    node.style.transition = '';
  } else {
    node.style.transform = value;
  }
}

// ---------------------------------------------------------------- tvOS-style motion

function nudge(el, dir) {
  const map = { left: [0, -6, 25, 45], right: [0, 6, 75, 45], up: [6, 0, 50, 10], down: [-6, 0, 50, 85] };
  const [rx, ry, gx, gy] = map[dir] || [0, 0, 30, 20];
  el.style.setProperty('--rx', rx + 'deg');
  el.style.setProperty('--ry', ry + 'deg');
  el.style.setProperty('--gx', gx + '%');
  el.style.setProperty('--gy', gy + '%');
  // Settle back; the idle "float" itself is a CSS animation (see app.css), no per-frame JS.
  clearTimeout(nudgeTimer);
  nudgeTimer = setTimeout(() => {
    if (el !== current) return;
    el.style.setProperty('--rx', '0deg');
    el.style.setProperty('--ry', '0deg');
  }, 200);
}

function bump(el, dir) {
  const d = { left: '-0.5rem 0', right: '0.5rem 0', up: '0 -0.5rem', down: '0 0.5rem' }[dir];
  if (el.animate) el.animate([{ translate: '0 0' }, { translate: d }, { translate: '0 0' }], { duration: 220, easing: 'cubic-bezier(.3,.7,.4,1)' });
  document.dispatchEvent(new CustomEvent('focus-edge'));
}
