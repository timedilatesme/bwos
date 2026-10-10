// Voice capture belongs to BWOS.exe. This isolated-world adapter only receives approved intents.
(() => {
  if (window !== window.top) return;
  const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
  const send = (type, data = {}) => new Promise(resolve => {
    try { chrome.runtime.sendMessage({ type, ...data }, result => { void chrome.runtime.lastError; resolve(result); }); }
    catch { resolve(null); }
  });
  const visible = el => {
    const r = el.getBoundingClientRect();
    return r.width > 0 && r.height > 0 && r.bottom > 0 && r.right > 0 && r.top < innerHeight && r.left < innerWidth && getComputedStyle(el).visibility !== 'hidden';
  };
  // Walk open shadow roots as TV clients can put the search field inside a component.
  const all = selector => {
    const result = [], roots = [document];
    while (roots.length) {
      const root = roots.pop();
      result.push(...root.querySelectorAll(selector));
      for (const el of root.querySelectorAll('*')) if (el.shadowRoot) roots.push(el.shadowRoot);
    }
    return result;
  };
  let pill, numbers = [], targets = [], polling = false, numbered = false, lastTranscript = '';
  function status(message, active) {
    if (!document.body) return;
    if (!pill) {
      pill = document.createElement('div'); pill.id = 'bwos-voice-status';
      pill.setAttribute('role', 'status'); document.body.append(pill);
    }
    pill.textContent = message; pill.classList.toggle('listening', active); pill.hidden = !message;
  }
  function clearNumbers() { numbers.forEach(n => n.remove()); numbers = []; targets = []; }
  function hideNumbers() { numbered = false; clearNumbers(); }
  function showNumbers() {
    numbered = true; clearNumbers();
    const dialog = all('[role=dialog],dialog[open]').find(visible);
    let selectable = all('a[href],button,input:not([type=hidden]),[role=button],[role=link],[role=option],[tabindex]:not([tabindex="-1"]),ytlr-guide-entry-renderer,ytlr-tile-renderer');
    if (dialog) selectable = selectable.filter(el => dialog.contains(el));
    targets = selectable.filter(el => visible(el) && !el.disabled && !el.closest('#bwos-voice-status,#bwos-keyboard')).filter((el, i, list) => !list.some((other, j) => j < i && other.contains(el))).slice(0, 99);
    targets.forEach((el, i) => {
      const r = el.getBoundingClientRect(), badge = document.createElement('span');
      badge.className = 'bwos-voice-number'; badge.textContent = String(i + 1); badge.setAttribute('aria-hidden', 'true');
      badge.style.left = `${Math.max(2, r.left + 6)}px`; badge.style.top = `${Math.max(2, r.top + 6)}px`;
      document.body.append(badge); numbers.push(badge);
    });
  }
  async function command({ action, value }) {
    if (action === 'reloadPage') { await send('reload-service'); return; }
    if (action === 'keyboard') { await send('show-keyboard'); return; }
    if (['pause', 'resume', 'toggle', 'stop', 'restart', 'seekForward', 'seekBack', 'nextMedia', 'previousMedia', 'subtitlesOn', 'subtitlesOff'].includes(action)) {
      const result = await send('media-intent', { action, value });
      if (!result?.ok) status(result?.error || 'This player could not complete the command.', false);
      return;
    }
    if (action === 'search' || action === 'youtubeSearch') { await search(value); return; }
    if (action === 'numbers') { showNumbers(); return; }
    if (action === 'hideNumbers') { hideNumbers(); return; }
    if (action === 'selectNumber') {
      const el = targets[Number(value) - 1];
      if (!el || !el.isConnected) { status('Say “Show numbers” first', false); return; }
      hideNumbers(); el.focus(); el.click(); return;
    }
    if (action === 'pause' || action === 'resume') {
      const videos = all('video').filter(visible).sort((a, b) => b.clientWidth * b.clientHeight - a.clientWidth * a.clientHeight);
      const video = videos[0];
      if (!video) { status('No video player found on this page', false); return; }
      if (action === 'pause') video.pause(); else await video.play().catch(() => status('Select Play to resume this video', false));
    }
  }
  async function poll() {
    if (polling || document.hidden || !document.hasFocus()) return;
    polling = true;
    try {
      const r = await send('voice-poll');
      if (!r) return;
      // The Windows host shows one transient assistant over every screen.
      if (pill) pill.hidden = true;
      for (const c of r.commands || []) await command(c);
    } finally { polling = false; }
  }
  setInterval(poll, 750);
  let frame;
  addEventListener('scroll', () => { if (numbered && !frame) frame = requestAnimationFrame(() => { frame = 0; showNumbers(); }); }, true);
  addEventListener('resize', () => { if (numbered) showNumbers(); });
  addEventListener('keydown', () => { if (numbered) hideNumbers(); }, true);

  // Carry only search text through navigation. Host API credentials stay in extension storage.
  let query = '';
  const match = location.hash.match(/(?:^#|&)bwos-search=([^&]+)/);
  if (match && location.hostname === 'www.youtube.com') {
    try { query = decodeURIComponent(match[1]).slice(0, 300); } catch {}
    history.replaceState(null, '', location.pathname + location.search);
  }
  async function search(query) {
      const youtube = /(^|\.)youtube\.com$/.test(location.hostname);
      if (youtube) {
        // YouTube's TV client handles q through its native deep-link resolver
        // (KEYBOARD_DEEPLINK). Keep results in Leanback, without DOM/keypad guessing.
        const route = '#/search?q=' + encodeURIComponent(query) + '&bwos-request=' + Date.now();
        if (location.hostname === 'www.youtube.com' && location.pathname.startsWith('/tv')) location.hash = route;
        else location.assign('https://www.youtube.com/tv' + route);
        return;
      }
      for (let i = 0; i < 20; i++) {
        if (document.hidden || !document.hasFocus()) return;
        let field = all('input[type=search],input[placeholder*="search" i],input[aria-label*="search" i],input#search,ytlr-search-page input,ytlr-search-page textarea').find(visible);
        if (field) {
          field.focus(); field.select?.();
          const r = await send('voice-type', { text: query });
          if (r && r.ok) return;
          status('Select the search field, then use the phone keyboard', false); return;
        }
        if (i === 0 || i === 6) {
          const target = all('[aria-label],[title],ytlr-guide-entry-renderer,a,button').find(el => visible(el) && /^(search|खोजें|खोज|खोजो)$/i.test((el.getAttribute('aria-label') || el.getAttribute('title') || el.textContent).trim()));
          if (target) target.click();
        }
        await wait(150);
      }
      const error = 'This app does not expose a search box. Open Search first, then repeat your command.';
      status(error, false); await send('voice-error', { message: error });
  }
  if (query) search(query);
})();
