// BWOS TV – YouTube (runs in the page's own JS world before YouTube's scripts)
//  1. Presents a TV user agent so www.youtube.com/tv serves the 10-foot "leanback" interface
//     (the matching request header is set by rules.json).
//  2. Removes ads: YouTube's clients receive ad schedules inside ordinary JSON API responses,
//     so those fields are stripped as the responses are parsed. Nothing is sent anywhere.
(() => {
  'use strict';
  if (location.hostname !== 'www.youtube.com') return; // YouTube Music keeps its normal interface
  const TV_UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; Xbox; Xbox One) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/70.0.3538.102 Safari/537.36 Edge/18.19041';
  try {
    const nav = Navigator.prototype;
    Object.defineProperty(nav, 'userAgent', { get: () => TV_UA, configurable: true });
    Object.defineProperty(nav, 'appVersion', { get: () => TV_UA.replace('Mozilla/', ''), configurable: true });
    Object.defineProperty(nav, 'userAgentData', { get: () => undefined, configurable: true });
  } catch (_) {}
})();
(() => {
  'use strict';

  // Keys that carry ad schedules / ad payloads on player responses.
  const AD_FIELDS = ['adPlacements', 'adSlots', 'playerAds', 'adBreakHeartbeatParams', 'adSignalsInfo'];

  // Renderers (feed items) that are ads or promotions. Items whose only key is one of these are removed.
  const JUNK_RENDERERS = new Set([
    'adSlotRenderer', 'tvMastheadRenderer', 'displayAdRenderer', 'promotedSparklesWebRenderer',
    'promotedVideoRenderer', 'compactPromotedVideoRenderer', 'searchPyvRenderer', 'bannerPromoRenderer',
    'statementBannerRenderer', 'brandVideoShelfRenderer', 'brandVideoSingletonRenderer',
    'inFeedAdLayoutRenderer', 'mealbarPromoRenderer', 'primetimePromoRenderer', 'movieOfferModuleRenderer',
    'adsEngagementPanelContentRenderer', 'engagementPanelAdRenderer'
  ]);

  const isJunk = (item) => {
    if (!item || typeof item !== 'object' || Array.isArray(item)) return false;
    const keys = Object.keys(item);
    if (keys.some((k) => JUNK_RENDERERS.has(k))) return true;
    // Rich feed items wrap the real renderer one level deeper.
    const rich = item.richItemRenderer && item.richItemRenderer.content;
    return !!(rich && Object.keys(rich).some((k) => JUNK_RENDERERS.has(k)));
  };

  const clean = (node, depth) => {
    if (!node || typeof node !== 'object' || depth > 40) return;
    if (Array.isArray(node)) {
      for (let i = node.length - 1; i >= 0; i--) {
        if (isJunk(node[i])) node.splice(i, 1);
        else clean(node[i], depth + 1);
      }
      return;
    }
    for (const f of AD_FIELDS) if (f in node) delete node[f];
    for (const k in node) {
      const v = node[k];
      if (v && typeof v === 'object') clean(v, depth + 1);
    }
  };

  const looksLikeYouTubeResponse = (o) =>
    o && typeof o === 'object' && !Array.isArray(o) &&
    ('responseContext' in o || 'playerResponse' in o || 'adPlacements' in o || 'playabilityStatus' in o);

  // 1) Every JSON.parse (XHR-based clients, the TV client, embedded data)
  const nativeParse = JSON.parse;
  JSON.parse = function (text, reviver) {
    const out = nativeParse.call(this, text, reviver);
    try {
      if (Array.isArray(out)) out.forEach((o) => looksLikeYouTubeResponse(o) && clean(o, 0));
      else if (looksLikeYouTubeResponse(out)) clean(out, 0);
    } catch (_) { /* never break the page */ }
    return out;
  };

  // 2) fetch() responses read with .json()
  const nativeJson = Response.prototype.json;
  Response.prototype.json = function () {
    return nativeJson.call(this).then((out) => {
      try { if (looksLikeYouTubeResponse(out)) clean(out, 0); } catch (_) {}
      return out;
    });
  };

  // 3) Data embedded in the first page load of the desktop site
  for (const name of ['ytInitialPlayerResponse', 'ytInitialData']) {
    let value;
    try {
      Object.defineProperty(window, name, {
        configurable: true,
        get: () => value,
        set: (v) => { try { clean(v, 0); } catch (_) {} value = v; }
      });
    } catch (_) {}
  }

  // 4) Safety net: if an ad still slips through on the desktop player, fast-forward and skip it.
  const skipper = () => {
    const player = document.querySelector('.html5-video-player.ad-showing, .html5-video-player.ad-interrupting');
    if (player) {
      const v = player.querySelector('video');
      if (v && isFinite(v.duration) && v.duration > 0) { v.muted = true; v.currentTime = v.duration; }
      const skip = document.querySelector('.ytp-ad-skip-button, .ytp-ad-skip-button-modern, .ytp-skip-ad-button');
      if (skip) skip.click();
    }
  };
  setInterval(skipper, 400);
})();
