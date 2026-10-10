// BWOS TV – YouTube (runs in the page's own JS world before YouTube's scripts)
//  TV-only compatibility identity. The real browser engine is unchanged.
//  Player responses and playback timing remain under YouTube's control.
//  Global JSON/fetch hooks and forced ad seeking caused compatibility risks.
(() => {
  'use strict';
  if (location.hostname !== 'www.youtube.com' || !/^\/tv(?:\/|$)/.test(location.pathname)) return;
  // Modern desktop Edge identification makes YouTube redirect /tv to /watch.
  // Match the TV document header in rules.json; never apply this to desktop pages.
  const TV_UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; Xbox; Xbox One) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/70.0.3538.102 Safari/537.36 Edge/18.19041';
  try {
    const nav = Navigator.prototype;
    Object.defineProperty(nav, 'userAgent', { get: () => TV_UA, configurable: true });
    Object.defineProperty(nav, 'appVersion', { get: () => TV_UA.replace('Mozilla/', ''), configurable: true });
    Object.defineProperty(nav, 'userAgentData', { get: () => undefined, configurable: true });
  } catch (_) {}
})();
