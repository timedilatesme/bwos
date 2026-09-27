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
  addEventListener('load', save);
})();
