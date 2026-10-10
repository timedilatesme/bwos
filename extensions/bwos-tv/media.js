// Keep the live player in a background tab when BWOS returns home.
(() => {
  let started = false, player, frameId = 0, intent = 0;
  const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
  try { chrome.runtime.sendMessage({ type: 'media-frame' }, result => { void chrome.runtime.lastError; frameId = result?.frameId || 0; }); } catch {}
  const send = (type, data) => { try { chrome.runtime.sendMessage({ type, data }, () => void chrome.runtime.lastError); } catch {} };
  let lastError = '';
  function playbackError() {
    const code = Number(player?.error?.code || 0);
    const messages = { 1: 'Playback was interrupted.', 2: 'The video connection failed.', 3: 'The browser could not decode this video.', 4: 'The video source could not be loaded.' };
    if (messages[code]) return { code: 'MEDIA_' + code, message: messages[code] + ' Retry the player.' };
    // YouTube can remove its failed video without setting HTMLMediaElement.error.
    // Read only the visible TV error dialog; never modify player responses or timing.
    if (/^https:\/\/www\.youtube\.com\/tv(?:[\/#?]|$)/.test(location.href)) {
      const dialog = [...document.querySelectorAll('[role="dialog"],[id^="ytlr-overlay-section-renderer__aria-id"]')].find(el =>
        el.getBoundingClientRect().width > 0 && /something went wrong/i.test(el.textContent || '') && /try again/i.test(el.textContent || ''));
      if (dialog) return { code: 'TV_PLAYER_ERROR', message: 'YouTube could not play this video. Retry the player.' };
    }
    return null;
  }
  function mediaElements() {
    const items = [], roots = [document];
    while (roots.length) {
      const root = roots.pop(); items.push(...root.querySelectorAll('video,audio'));
      for (const node of root.querySelectorAll('*')) if (node.shadowRoot) roots.push(node.shadowRoot);
    }
    return items;
  }
  function snapshot() {
    const items = mediaElements().filter(v => !v.ended && (v.error || v.duration > 0 || !isFinite(v.duration)));
    const playing = items.filter(v => !v.paused).sort((a, b) => (b.clientWidth || 0) * (b.clientHeight || 0) - (a.clientWidth || 0) * (a.clientHeight || 0));
    player = playing[0] || (player?.isConnected ? player : items.find(v => v.error || v.currentTime > 0));
    const failure = playbackError();
    if (failure && failure.code !== lastError) send('media-diagnostic', { category: 'player', code: failure.code });
    lastError = failure?.code || '';
    if (!player && !failure) return null;
    if (failure) started = true;
    if (player && (!player.paused || player.currentTime > 0)) started = true;
    if (!started) return null;
    const meta = navigator.mediaSession?.metadata;
    return { url: location.href, title: meta?.title || document.querySelector('meta[property="og:title"]')?.content || document.title,
      subtitle: meta?.artist || '', artwork: meta?.artwork?.[0]?.src || document.querySelector('meta[property="og:image"]')?.content || '', paused: !!failure || player.paused, ended: !failure && player.ended, canControl: !failure,
      error: failure?.message || '',
      position: Number.isFinite(player?.currentTime) ? Math.max(0, player.currentTime) : 0,
      duration: Number.isFinite(player?.duration) ? Math.max(0, player.duration) : 0 };
  }
  function report() {
    const data = snapshot();
    if (data) send('media-report', data);
    else if (started) { send('media-report', { url: location.href, ended: true }); started = false; }
  }
  for (const type of ['play', 'pause', 'ended', 'error', 'loadedmetadata']) document.addEventListener(type, report, true);
  // An explicit user action or cloud control cancels the short handoff recovery.
  for (const type of ['keydown', 'pointerdown']) document.addEventListener(type, () => { intent++; }, true);
  setInterval(report, 2000);
  chrome.runtime.onMessage.addListener((msg, sender, reply) => {
    if (msg?.type === 'media-snapshot') {
      const data = snapshot();
      if (data || window === window.top) reply({ frameId, data });
      return false;
    }
    if (msg?.type === 'media-handoff') {
      const target = player, originalIntent = intent;
      (async () => {
        for (const delay of [250, 650]) {
          await wait(delay);
          if (!msg.keepPlaying || intent !== originalIntent || target !== player || !target?.isConnected || target.ended || playbackError()) break;
          if (target.paused) {
            try { await Promise.race([target.play(), wait(1500).then(() => { throw new Error('Playback did not start'); })]); }
            catch { break; }
          }
        }
        const data = snapshot(); report();
        reply({ ok: !!data && !data.paused && !data.ended, data });
      })().catch(() => reply({ ok: false, data: snapshot() }));
      return true;
    }
    if (msg?.type !== 'media-control') return false;
    intent++;
    snapshot(); // Cold commands work before the first heartbeat.
    if (playbackError()) { reply({ ok: false, error: 'Playback failed. Retry the player.' }); return false; }
    if (!player?.isConnected) { reply({ ok: false, error: 'No media player found.' }); return false; }
    (async () => {
      const action = msg.action === 'toggle' ? player.paused ? 'resume' : 'pause' : msg.action;
      if (action === 'pause') player.pause();
      else if (action === 'resume') await Promise.race([player.play(), wait(1500).then(() => { throw Error('Playback did not start. Reopen the player.'); })]);
      else if (action === 'seekForward' || action === 'seekBack' || action === 'restart') {
        const seconds = Number(msg.value || 10);
        if (!Number.isInteger(seconds) || seconds < 1 || seconds > 100) throw Error('Seek needs 1–100 seconds.');
        if (!Number.isFinite(player.duration)) throw Error('This live stream does not support seeking.');
        player.currentTime = action === 'restart' ? 0 : Math.max(0, Math.min(Math.max(0, player.duration - .1), player.currentTime + (action === 'seekForward' ? seconds : -seconds)));
      } else if (action === 'nextMedia' || action === 'previousMedia') {
        const next = action === 'nextMedia';
        const pattern = next ? /^(next|next video|next track|next song|अगला)(?:\s*\(.*\))?$/i : /^(previous|previous video|previous track|previous song|पिछला)(?:\s*\(.*\))?$/i;
        const button = [...document.querySelectorAll('button,[role=button],[aria-label],[title]')].find(el => pattern.test((el.getAttribute('aria-label') || el.getAttribute('title') || '').trim()) && !el.disabled && el.getBoundingClientRect().width > 0);
        if (!button) throw Error('This app does not expose a next/previous control.');
        button.click();
      } else if (action === 'subtitlesOn' || action === 'subtitlesOff') {
        const tracks = [...(player.textTracks || [])].filter(track => track.kind === 'subtitles' || track.kind === 'captions');
        if (!tracks.length) throw Error('Use this app’s subtitle menu; it does not expose captions to BWOS.');
        const selected = tracks.find(track => track.mode === 'showing') || tracks[0];
        for (const track of tracks) track.mode = action === 'subtitlesOn' && track === selected ? 'showing' : 'disabled';
      } else throw Error('Unknown playback command.');
      report(); reply({ ok: true });
    })().catch(error => reply({ ok: false, error: error.message }));
    return true;
  });
})();
