// BWOS TV background worker.
//  • Relays watch progress from streaming pages to the BWOS host on 127.0.0.1
//    (pages can't call it themselves: different origin, and the token must not leak to websites).
//  • "Go home" as a browser-level shortcut (Ctrl+Shift+Home, pressed by BWOS.exe for the controller's
//    Home button). Browser shortcuts can't be swallowed by a page or an ad frame, so Home always works.
//  • Lets embedded frames (e.g. video ads) hand keys like Back to the page's controller layer.

async function settings() {
  return chrome.storage.local.get(['home', 'token']);
}

async function bwos(path, body) {
  const { home, token } = await settings();
  if (!home || !token) return null;
  const res = await fetch(home + path, {
    method: 'POST',
    headers: { 'X-BWOS-Token': token, 'Content-Type': 'application/json' },
    body: JSON.stringify(body || {})
  });
  return res.ok ? res.json() : null;
}

async function goHome(tabId) {
  const { home } = await settings();
  const url = home || 'http://127.0.0.1:47913/';
  if (tabId == null) {
    const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
    if (!tab) return;
    tabId = tab.id;
  }
  chrome.tabs.update(tabId, { url });
}

chrome.commands.onCommand.addListener((command) => {
  if (command === 'bwos-home') goHome();
});

chrome.runtime.onMessage.addListener((msg, sender, reply) => {
  if (!msg) return false;
  const tabId = sender.tab && sender.tab.id;
  switch (msg.type) {
    case 'progress':
      bwos('api/progress', msg.data).then(reply, () => reply(null));
      return true;
    case 'lookup':
      bwos('api/progress/lookup', msg.data).then(reply, () => reply(null));
      return true;
    case 'keyboard':
      bwos('api/keyboard/open', msg.data).then(reply, () => reply(null));
      return true;
    case 'home':
      goHome(tabId);
      return false;
    case 'key-to-top':
      if (tabId != null) chrome.tabs.sendMessage(tabId, { type: 'key', key: msg.key }, { frameId: 0 });
      return false;
  }
  return false;
});
