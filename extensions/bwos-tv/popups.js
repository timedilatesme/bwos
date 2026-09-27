// BWOS TV – pop-up guard (runs in the page's own JS world before its scripts).
// Free streaming sites often open ad tabs/windows when you click anything. Inside BWOS that
// would pull you out of the TV experience, so window.open to other sites is refused.
// Sign-in windows (Google, Apple, Amazon, Netflix, Microsoft, Facebook) are still allowed.
(() => {
  'use strict';
  const ALLOW = /(^|\.)(google\.com|accounts\.google\.[a-z.]+|apple\.com|amazon\.[a-z.]+|primevideo\.com|netflix\.com|microsoft\.com|live\.com|facebook\.com|youtube\.com)$/i;
  const site = location.hostname.replace(/^www\./, '');
  const nativeOpen = window.open;

  window.open = function (url, target, features) {
    try {
      const u = new URL(url == null ? '' : String(url), location.href);
      const host = u.hostname.replace(/^www\./, '');
      const sameSite = host === site || host.endsWith('.' + site) || site.endsWith('.' + host);
      if ((sameSite && u.protocol.startsWith('http')) || ALLOW.test(u.hostname)) return nativeOpen.apply(this, arguments);
    } catch (_) { /* malformed URL → block */ }
    console.info('[BWOS] blocked pop-up:', url);
    return null;
  };
})();
