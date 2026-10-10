// Runs on the BWOS home screen: tell the extension where BWOS lives and hand it the session
// token, so watch progress on streaming sites can be reported back to BWOS.
(() => {
  const save = () => {
    let token = null;
    const m = location.hash.match(/t=([a-f0-9]{32})/);
    if (m) token = m[1];
    if (!token) { try { token = sessionStorage.getItem('bwos.token'); } catch (_) {} }
    try { chrome.storage.local.set(token ? { home: location.origin + '/', token } : { home: location.origin + '/' }); } catch (_) {}
  };
  save();
  // Only a same-window page event reaches this isolated bridge. The worker additionally
  // validates that its sender is the configured BWOS Home tab and the URL is HTTPS.
  addEventListener('message', event => {
    if (event.source !== window || event.origin !== location.origin || !['bwos-open-service','bwos-sync-recommendations','bwos-search-recommendations'].includes(event.data?.type)) return;
    const { requestId, url } = event.data;
    if (typeof requestId !== 'string' || (event.data.type==='bwos-open-service' && (typeof url !== 'string' || url.length > 4000))) return;
    const request=event.data.type==='bwos-search-recommendations'?{type:'search-recommendations',service:event.data.service,query:event.data.query}:event.data.type==='bwos-sync-recommendations'?{type:'sync-recommendations',service:event.data.service,target:event.data.target}:{ type: 'open-service', url };
    chrome.runtime.sendMessage(request, result => {
      const error = chrome.runtime.lastError;
      window.postMessage({ type: 'bwos-open-result', requestId, result: result || { ok: false, error: error?.message || 'Browser adapter unavailable' } }, location.origin);
    });
  });
  chrome.runtime.onMessage.addListener(message => {
    if (message?.type === 'shell-home') window.postMessage({ type: 'bwos-shell-home' }, location.origin);
    if(message?.type==='key' && ['ArrowLeft','ArrowRight','ArrowUp','ArrowDown','Enter','Escape',' ','x','p','f','o','m','ContextMenu','F23'].includes(message.key))
      window.postMessage({type:'bwos-player-key',key:message.key},location.origin);
  });
  addEventListener('load', save);
  setInterval(() => { try { chrome.runtime.sendMessage({ type: 'media-poll' }, () => void chrome.runtime.lastError); } catch {} }, 1000);
})();
