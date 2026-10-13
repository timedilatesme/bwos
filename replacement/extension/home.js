// Runs on the BWOS home screen: tell the extension where BWOS lives and hand it the session
// token, so watch progress on streaming sites can be reported back to BWOS.
(() => {
  // The candidate has a fixed local endpoint. Never forward its token to another local app.
  if(location.origin !== 'http://127.0.0.1:47923')return;
  const save = async () => {
    let token = null;
    const m = location.hash.match(/t=([a-f0-9]{32})/);
    if (m) token = m[1];
    if (!token) { try { token = sessionStorage.getItem('bwos.token'); } catch (_) {} }
    if(!token)return;
    try { await chrome.storage.local.set({ home: location.origin + '/', token }); return true; } catch (_) {}
  };
  const connect=()=>save().then(saved=>{if(saved)chrome.runtime.sendMessage({type:'core-bind'},()=>void chrome.runtime.lastError);}).catch(()=>{});
  connect();
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
  chrome.runtime.onMessage.addListener((message,sender,reply) => {
    if(message?.type === "core-input") {
      const inputId=crypto.randomUUID();
      const finish=event=>{if(event.source!==window || event.origin!==location.origin || event.data?.type!=='bwos-input-result' || event.data.inputId!==inputId)return;clearTimeout(timer);removeEventListener('message',finish);reply(event.data.result);};
      const timer=setTimeout(()=>{removeEventListener('message',finish);reply({ok:false,error:'Shell input timed out'});},900);
      addEventListener('message',finish);window.postMessage({...message,inputId},location.origin);return true;
    }
    if (message?.type === 'shell-home') window.postMessage({ type: 'bwos-shell-home' }, location.origin);
    if(message?.type==='key' && ['ArrowLeft','ArrowRight','ArrowUp','ArrowDown','Enter','Escape',' ','x','p','f','o','m','ContextMenu','F23'].includes(message.key))
      window.postMessage({type:'bwos-player-key',key:message.key},location.origin);
  });
  addEventListener('load', connect);
  setInterval(() => { try { chrome.runtime.sendMessage({ type: 'media-poll' }, () => void chrome.runtime.lastError); } catch {} }, 1000);
})();
