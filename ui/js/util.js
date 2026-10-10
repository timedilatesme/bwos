// Small DOM + formatting helpers shared by the UI modules.

export const $ = (sel, root = document) => root.querySelector(sel);
export const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));

/** h('div.card.card--land', {dataset:{key}}, child, 'text') */
export function h(tag, props, ...children) {
  const [name, ...classes] = tag.split('.');
  const node = document.createElement(name || 'div');
  if (classes.length) node.className = classes.join(' ');
  if (props) {
    for (const [k, v] of Object.entries(props)) {
      if (v == null || v === false) continue;
      if (k === 'dataset') Object.assign(node.dataset, v);
      else if (k === 'style' && typeof v === 'object') {
        for (const [sk, sv] of Object.entries(v)) node.style.setProperty(sk, sv);
      } else if (k === 'html') node.innerHTML = v;
      else if (k.startsWith('on')) node.addEventListener(k.slice(2), v);
      else if (v === true) node.setAttribute(k, '');
      else node.setAttribute(k, v);
    }
  }
  for (const c of children.flat()) {
    if (c == null || c === false) continue;
    node.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return node;
}

export function hashHue(text) {
  let x = 0;
  for (let i = 0; i < text.length; i++) x = (x * 31 + text.charCodeAt(i)) | 0;
  return Math.abs(x) % 360;
}

export function timeAgo(unixSeconds) {
  if (!unixSeconds) return null;
  const s = Date.now() / 1000 - unixSeconds;
  if (s < 0) return 'Just now';
  if (s < 3600) return 'Played just now';
  if (s < 86400) { const hrs = Math.floor(s / 3600); return `Played ${hrs} hour${hrs > 1 ? 's' : ''} ago`; }
  const d = Math.floor(s / 86400);
  if (d === 1) return 'Played yesterday';
  if (d < 30) return `Played ${d} days ago`;
  return 'Played ' + new Date(unixSeconds * 1000).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
}

export function playtime(minutes) {
  if (!minutes) return null;
  if (minutes < 60) return `${minutes} min played`;
  const hrs = minutes / 60;
  return `${hrs < 10 ? hrs.toFixed(1) : Math.round(hrs)} h played`;
}

export function size(bytes) {
  if (!bytes) return null;
  const gb = bytes / 1024 ** 3;
  return gb >= 1 ? `${gb.toFixed(gb < 10 ? 1 : 0)} GB` : `${Math.round(bytes / 1024 ** 2)} MB`;
}

export const SOURCE_LABEL = { steam: 'Steam', epic: 'Epic Games', gog: 'GOG', ubisoft: 'Ubisoft', custom: 'PC' };

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** Load an image and resolve with it (or null on failure). */
export function preload(src) {
  return new Promise((resolve) => {
    if (!src) return resolve(null);
    const img = new Image();
    img.decoding = 'async';
    img.onload = () => resolve(img);
    img.onerror = () => resolve(null);
    img.src = src;
  });
}

/** Average "vivid" colour of an image as an "r, g, b" string, or null (cross-origin etc.). */
export function ambientColor(img) {
  try {
    const c = document.createElement('canvas');
    c.width = c.height = 24;
    const g = c.getContext('2d', { willReadFrequently: true });
    g.drawImage(img, 0, 0, 24, 24);
    const d = g.getImageData(0, 0, 24, 24).data;
    let r = 0, gr = 0, b = 0, w = 0;
    for (let i = 0; i < d.length; i += 4) {
      const max = Math.max(d[i], d[i + 1], d[i + 2]);
      const min = Math.min(d[i], d[i + 1], d[i + 2]);
      const weight = (max - min) / 255 + 0.05; // favour saturated pixels
      r += d[i] * weight; gr += d[i + 1] * weight; b += d[i + 2] * weight; w += weight;
    }
    const boost = (v) => Math.min(255, Math.round((v / w) * 1.25));
    return `${boost(r)}, ${boost(gr)}, ${boost(b)}`;
  } catch {
    return null;
  }
}

// Minimal line icon set (24×24, stroke = currentColor)
const P = {
  tiles: '<rect x="3" y="3" width="7" height="7" rx="2"/><rect x="14" y="3" width="7" height="7" rx="2"/><rect x="3" y="14" width="7" height="7" rx="2"/><rect x="14" y="14" width="7" height="7" rx="2"/>',
  mic: '<rect x="9" y="3" width="6" height="11" rx="3"/><path d="M5 10v1a7 7 0 0 0 14 0v-1M12 18v3M8 21h8"/>',
  bluetooth: '<path d="m7 7 10 10-5 4V3l5 4L7 17"/>',
  home: '<path d="M3 11.5 12 4l9 7.5"/><path d="M5.5 10v9.5h13V10"/>',
  gamepad: '<path d="M7 8h10a4 4 0 0 1 4 4.2l-.4 3.6a2.6 2.6 0 0 1-4.6 1.4L14.6 16H9.4L8 17.2a2.6 2.6 0 0 1-4.6-1.4L3 12.2A4 4 0 0 1 7 8Z"/><path d="M8 11v3M6.5 12.5h3"/><circle cx="15.5" cy="11.6" r=".6" fill="currentColor"/><circle cx="17.4" cy="13.4" r=".6" fill="currentColor"/>',
  tv: '<rect x="3" y="5" width="18" height="12" rx="2"/><path d="M8 20.5h8"/>',
  settings: '<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1Z"/>',
  power: '<path d="M12 3v8"/><path d="M6.3 6.8a8 8 0 1 0 11.4 0"/>',
  moon: '<path d="M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5Z"/>',
  restart: '<path d="M3.5 12a8.5 8.5 0 1 0 2.6-6.1"/><path d="M3 4v4.5h4.5"/>',
  desktop: '<rect x="3" y="4" width="18" height="12" rx="2"/><path d="M9 20h6M12 16v4"/>',
  bigpicture: '<rect x="2.5" y="4" width="19" height="13" rx="2"/><path d="M10 8.5v5l4.5-2.5Z" fill="currentColor"/><path d="M8 20.5h8"/>',
  play: '<path d="M7 4.5v15l12.5-7.5Z" fill="currentColor" stroke="none"/>',
  stop: '<rect x="6" y="6" width="12" height="12" rx="2" fill="currentColor" stroke="none"/>',
  resume: '<path d="M5 12h12M12 6l6 6-6 6"/>',
  eyeoff: '<path d="M3 3l18 18"/><path d="M10.6 5.1A10 10 0 0 1 12 5c5 0 9 4.5 10 7a13 13 0 0 1-2.6 3.7M6.6 6.6A13 13 0 0 0 2 12c1 2.5 5 7 10 7a9.6 9.6 0 0 0 5.4-1.6"/><path d="M9.9 9.9a3 3 0 0 0 4.2 4.2"/>',
  eye: '<path d="M2 12c1-2.5 5-7 10-7s9 4.5 10 7c-1 2.5-5 7-10 7S3 14.5 2 12Z"/><circle cx="12" cy="12" r="3"/>',
  refresh: '<path d="M20.5 12a8.5 8.5 0 1 1-2.6-6.1"/><path d="M21 4v4.5h-4.5"/>',
  exit: '<path d="M14 4h4a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-4"/><path d="M9 8l-4 4 4 4M5 12h10"/>',
  mouse: '<rect x="6" y="3" width="12" height="18" rx="6"/><path d="M12 7v3"/>',
  sound: '<path d="M4 9.5v5h3.5L12 18.5v-13L7.5 9.5Z"/><path d="M16 9a4 4 0 0 1 0 6M18.5 6.5a7.5 7.5 0 0 1 0 11"/>',
  swap: '<path d="M7 7h12l-3-3M17 17H5l3 3"/>',
  rocket: '<path d="M12 15l-3-3a15 15 0 0 1 9-9c.5 3.5-1.5 7-6 12Z"/><path d="M9 12H5l2.5-3.5H11M12 15v4l3.5-2.5V13"/><path d="M6.5 16.5c-1.5 1-2 3-2 3s2-.5 3-2"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 7.5v.5"/>',
  check: '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
  alert: '<path d="M12 3.5 2.5 20h19Z"/><path d="M12 10v4.5M12 17.5v.5"/>',
  user: '<circle cx="12" cy="8.5" r="3.8"/><path d="M4.5 20a7.5 7.5 0 0 1 15 0"/>',
  edit: '<path d="M4 20h4L19 9l-4-4L4 16v4Z"/><path d="M13.5 6.5l4 4"/>',
  cast: '<path d="M3 8V6a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-6"/><path d="M3 12a8 8 0 0 1 8 8M3 16a4 4 0 0 1 4 4"/><circle cx="3.6" cy="19.6" r=".6" fill="currentColor"/>',
  wifi: '<path d="M2.5 9a14 14 0 0 1 19 0M5.5 12.5a9.5 9.5 0 0 1 13 0M8.8 16a5 5 0 0 1 6.4 0"/><circle cx="12" cy="19" r=".8" fill="currentColor"/>',
  store: '<path d="M4 8h16l-1 12H5Z"/><path d="M9 8V6a3 3 0 0 1 6 0v2"/>'
};

export function icon(name, cls = '') {
  const s = `<svg class="${cls}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${P[name] || ''}</svg>`;
  const t = document.createElement('template');
  t.innerHTML = s;
  return t.content.firstChild;
}
