// BWOS TV background worker.
//  • Relays watch progress from streaming pages to the BWOS host on 127.0.0.1
//    (pages can't call it themselves: different origin, and the token must not leak to websites).
//  • Home via the page's F24 adapter or the optional browser shortcut.
//  • Retains Home and one tab per service instead of navigating/reloading the shell.
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
  if (!res.ok) throw new Error('BWOS returned ' + res.status + ' for ' + path);
  return res.json();
}
console.info('BWOS streaming adapter 2.10.0 started');

// Retire 2.5.1's dynamic desktop identity. The packaged TV navigation rule is
// available before the worker starts; APIs/scripts/media keep native headers.
const tvCompatibilityReady = (async () => {
  if (!chrome.declarativeNetRequest) return;
  await chrome.declarativeNetRequest.updateDynamicRules({ removeRuleIds: [1] });
})();
tvCompatibilityReady.catch(error => diagnose('YouTube compatibility', error));

let homeTask, polling = false, work = Promise.resolve(), playbackSocket;
function schedule(task) { const result = work.then(task); work = result.catch(() => {}); return result; }
async function connectPlayback() {
  if (playbackSocket && playbackSocket.readyState < 2) return;
  const { home, token } = await settings();
  if (!home || !token) return;
  const socket = playbackSocket = new WebSocket(home.replace(/^http:/, 'ws:') + 'api/playback-events?t=' + token);
  socket.onmessage = event => { if (event.data === 'playback') schedule(pollPlayback).catch(() => {}); };
  socket.onopen = () => schedule(pollPlayback).catch(() => {});
  socket.onclose = () => { if (playbackSocket === socket) playbackSocket = null; setTimeout(() => connectPlayback().catch(() => {}), 2000); };
}
connectPlayback().catch(() => {});
chrome.storage.onChanged.addListener((changes, area) => {
  if (area === 'local' && (changes.home || changes.token)) { playbackSocket?.close(); playbackSocket = null; connectPlayback().catch(() => {}); }
});
const players = () => chrome.storage.session.get('players').then(s => s.players || {});
function sendToTab(id, message, frame, timeout = 2500) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Player did not respond')), timeout);
    chrome.tabs.sendMessage(id, message, frame).then(value => { clearTimeout(timer); resolve(value); }, error => { clearTimeout(timer); reject(error); });
  });
}
function serviceKey(url) {
  const parsed = new URL(url);
  if (parsed.protocol !== 'https:' || parsed.username || parsed.password) throw new Error('A streaming app needs a secure URL');
  return parsed.hostname.replace(/^www\./, '');
}
async function activateHome(sourceTab, base, token) {
  const homeTabs = (await chrome.tabs.query({ windowId: sourceTab.windowId })).filter(tab => tab.url?.startsWith(base));
  let homeTab = homeTabs[0];
  if (homeTab) await chrome.tabs.update(homeTab.id, { active: true, autoDiscardable: false });
  else homeTab = await chrome.tabs.create({ windowId: sourceTab.windowId, url: base + (token ? '#t=' + token : ''), active: true });
  if (homeTab.windowId !== sourceTab.windowId) throw new Error('Home must stay in the BWOS window');
  await chrome.tabs.update(homeTab.id, { autoDiscardable: false });
  // This message switches the retained UI to Home without reloading its document.
  await sendToTab(homeTab.id, { type: 'shell-home' }, { frameId: 0 }, 1000).catch(() => {});
}
async function openService(url, sender) {
  if (/^https:\/\/www\.youtube\.com\/tv(?:[/?#]|$)/.test(url)) await tvCompatibilityReady;
  const { home } = await settings();
  if (!home || sender.frameId !== 0 || !sender.tab?.url?.startsWith(home)) throw new Error('Open apps from BWOS Home');
  const key = serviceKey(url), parsed = new URL(url);
  const tabs = await chrome.tabs.query({ windowId: sender.tab.windowId });
  const candidates = tabs.filter(tab => { try { return serviceKey(tab.pendingUrl || tab.url) === key; } catch { return false; } });
  const all = await players();
  const existing = candidates.find(tab => all[tab.id] && !all[tab.id].data.paused) || candidates.sort((a, b) => b.id - a.id)[0];
  // Opening the app's landing page resumes its existing session. An explicit search/title
  // request can navigate the retained tab, but never creates another player for that service.
  const landing = !parsed.search && !parsed.hash && ['/', '/tv', '/tv/'].includes(parsed.pathname);
  let destination = url, repairTv = false;
  if (existing && landing && parsed.hostname === 'www.youtube.com' && /^\/tv\/?$/.test(parsed.pathname)) {
    const retained = new URL(existing.pendingUrl || existing.url);
    repairTv = !/^\/tv(?:\/|$)/.test(retained.pathname);
    // Reusing a desktop tab must not defeat a request to open the TV app.
    // Carry its video/playlist into the TV client without creating a second tab.
    if (repairTv && retained.pathname === '/watch' && /^[\w-]{11}$/.test(retained.searchParams.get('v') || '')) {
      const watch = new URLSearchParams({ v: retained.searchParams.get('v') });
      for (const name of ['list', 'index']) if (retained.searchParams.has(name)) watch.set(name, retained.searchParams.get(name));
      destination = 'https://www.youtube.com/tv#/watch?' + watch;
    }
  }
  await chrome.tabs.update(sender.tab.id, { autoDiscardable: false });
  let tab;
  if (existing) tab = await chrome.tabs.update(existing.id, { active: true, autoDiscardable: false, ...((!landing || repairTv) && existing.url !== destination ? { url: destination } : {}) });
  else tab = await chrome.tabs.create({ windowId: sender.tab.windowId, url, active: true });
  await chrome.tabs.update(tab.id, { autoDiscardable: false });
  if (all[tab.id] && !repairTv) { all[tab.id].background = false; await chrome.storage.session.set({ players: all }); await reportPlayer(tab.id, all[tab.id]); }
  await chrome.windows.update(tab.windowId, { state: 'fullscreen' });
  await chrome.windows.update(tab.windowId, { focused: true });
  await reportContext({ ...tab, active: true });
  return { ok: true };
}
async function reportContext(tab) {
  if (!tab?.url || !tab.active) return;
  const window = await chrome.windows.get(tab.windowId);
  const { home } = await settings();
  const url = new URL(tab.url);
  const kind = home && url.origin === new URL(home).origin ? 'home' : /(^|\.)youtube\.com$/.test(url.hostname) ? 'youtube' : url.protocol === 'https:' ? 'service' : null;
  if (kind) await bwos('api/browser/context', { kind, foreground: !!window.focused });
}
const recommendationSources={youtube:'https://www.youtube.com/',netflix:'https://www.netflix.com/browse',prime:'https://www.primevideo.com/'};
const recommendationTasks=new Map();
const recommendationTabs=new Set();
function recommendationProvider(url) {
  try {const host=new URL(url).hostname;return /^(www\.)?youtube\.com$/.test(host)?'youtube':/^(www\.)?netflix\.com$/.test(host)?'netflix':/^(www\.)?primevideo\.com$/.test(host)?'prime':null;}catch{return null;}
}
async function recommendationsReport(data,sender,complete=false) {
  if(sender.frameId!==0 || !sender.tab?.url?.startsWith('https://') || !data?.rows?.length || recommendationProvider(sender.tab.url)!==data.service) throw Error('Invalid recommendation source');
  // Bind the source to the browser-reported URL rather than page-provided metadata.
  return bwos('api/recommendations/report',{service:data.service,url:sender.tab.url,rows:data.rows,complete,collector:3,stats:data.stats});
}
async function syncRecommendations(service,sender,target=60) {
  return discoverRecommendations(service,sender,null,target);
}
async function discoverRecommendations(service,sender,query=null,target=60) {
  const {home}=await settings();
  if(sender.frameId!==0 || !home || !sender.tab?.url?.startsWith(home) || !Object.hasOwn(recommendationSources,service)) throw Error('Sync recommendations from BWOS');
  if(query!==null && (typeof query!=='string' || !query.trim() || query.length>200))throw Error('Enter a search of up to 200 characters');
  const taskKey=service+':'+(query===null?'home':query.trim());
  if(recommendationTasks.has(taskKey)) return recommendationTasks.get(taskKey);
  const task=(async()=>{
    // Only a temporary discovery tab is created. Existing service/player tabs are untouched.
    const destinations={youtube:'https://www.youtube.com/results?search_query=',netflix:'https://www.netflix.com/search?q=',prime:'https://www.primevideo.com/search?phrase='};
    let url=query===null?recommendationSources[service]:destinations[service]+encodeURIComponent(query.trim());
    const tab=await chrome.tabs.create({windowId:sender.tab.windowId,url,active:false});
    recommendationTabs.add(tab.id);
    try {
      await chrome.tabs.update(tab.id,{muted:true,autoDiscardable:false});
      const deadline=Date.now()+(service==='youtube' && query===null?65000:35000);
      let reason='no-cards',stats=null,source='other';
      let desktop=null,triedTv=false;
      while(Date.now()<deadline) {
        const current=await chrome.tabs.get(tab.id);
        source=new URL(current?.url || current?.pendingUrl || url).pathname;
        if(current?.status==='complete') {
          const result=await sendToTab(tab.id,{type:'recommendations-discover',target:query===null?Math.min(360,Math.max(60,Number(target) || 60)):60},{frameId:0},30000).catch(()=>null);
          if(result?.data?.reason)reason=result.data.reason;
          if(result?.data?.stats)stats=result.data.stats;
          if(/\/(?:login|signin)(?:\/|$)/.test(new URL(current.url).pathname))reason='sign-in-required';
          if(result?.data?.rows?.length) {
            if(query===null) {
              const count=result.data.rows.reduce((sum,row)=>sum+row.items.length,0);
              if(service==='youtube' && !triedTv && count<Math.min(360,Math.max(60,Number(target)||60))) {
                desktop={data:result.data,tab:current};triedTv=true;url='https://www.youtube.com/tv';
                await chrome.tabs.update(tab.id,{url});continue;
              }
              if(desktop) {
                const seen=new Set(),rows=[];
                for(const row of [...desktop.data.rows,...result.data.rows]) {
                  const items=row.items.filter(item=>{if(seen.has(item.url))return false;seen.add(item.url);return true;});
                  if(items.length)rows.push({...row,items});
                }
                result.data={...result.data,rows,complete:seen.size>=Math.min(360,Math.max(60,Number(target)||60))};
              }
              return recommendationsReport(result.data,{tab:current,frameId:0},result.data.complete===true);
            }
            if(recommendationProvider(current.url)!==service)throw Error('Open the app and sign in before searching');
            return bwos('api/recommendations/preview',{service,url:current.url,rows:result.data.rows});
          }
          if(service==='youtube' && query===null && !triedTv && result?.data) {
            triedTv=true;url='https://www.youtube.com/tv';await chrome.tabs.update(tab.id,{url});continue;
          }
          if(result?.data || reason==='sign-in-required')break;
        }
        await new Promise(resolve=>setTimeout(resolve,800));
      }
      if(desktop)return recommendationsReport(desktop.data,{tab:desktop.tab,frameId:0},false);
      throw Object.assign(Error(reason==='profile-required'?'Select your profile in the app, then refresh recommendations.':reason==='sign-in-required'?'Sign in inside the BWOS app session, then refresh recommendations.':reason==='home-unavailable'?'YouTube is not showing a Home feed in this session. Open YouTube and check your sign-in and watch history.':query===null?'No recommendation cards found. Open the app, select your profile or sign in, then refresh again.':'No searchable cards were available. Open the app to check your sign-in or try another search.'),{reason,stats,source});
    } finally {await chrome.tabs.remove(tab.id).catch(()=>{});recommendationTabs.delete(tab.id);}
  })();
  recommendationTasks.set(taskKey,task);
  try{return await task;}catch(error){
    if(query===null)await bwos('api/recommendations/status',{service,status:error.reason || 'unavailable',stats:error.stats,source:error.source}).catch(()=>{});
    throw error;
  }finally{recommendationTasks.delete(taskKey);}
}
async function pollContext() {
  await bwos('api/browser/ping', {});
  const tabs = await chrome.tabs.query({ active: true });
  for (const tab of tabs) await reportContext(tab);
}
let lastDiagnostic = '';
function diagnose(stage, error) {
  const message = String(error?.message || error).replace(/[a-f0-9]{32}/gi, '[redacted]').slice(0, 300);
  const detail = stage + ': ' + message;
  if (detail === lastDiagnostic) return;
  lastDiagnostic = detail;
  console.error('BWOS integration ' + detail);
  chrome.storage.local.set({ integrationDiagnostic: { stage, message, at: Date.now() } }).catch(() => {});
}
async function reportPlayer(id, player, ended = false) {
  return bwos('api/playback/report', { ...player.data, id: String(id), local:false, background: !!player.background, ended });
}
// Observe failures only. No blocking handlers, response bodies, signed URLs,
// cookies or request headers are read or persisted.
const networkFailures = new Map();
function youtubeNetworkFailure(details) {
  let url;
  try { url = new URL(details.url); } catch { return; }
  const category = /(^|\.)googlevideo\.com$/.test(url.hostname) && url.pathname === '/videoplayback' ? 'media-network' :
    url.hostname === 'www.youtube.com' && url.pathname === '/youtubei/v1/player' ? 'player-network' : null;
  if (!category || details.tabId < 0) return;
  const code = details.statusCode >= 400 ? 'HTTP_' + details.statusCode : /^net::ERR_[A-Z_]+$/.test(details.error || '') && details.error !== 'net::ERR_ABORTED' ? details.error : '';
  if (!code) return; // Aborted range requests are normal when switching videos/seeking.
  const key = details.tabId + ':' + category + ':' + code, now = Date.now();
  if (now - (networkFailures.get(key) || 0) < 10000) return;
  if (networkFailures.size >= 64) networkFailures.clear();
  networkFailures.set(key, now);
  chrome.tabs.get(details.tabId).then(tab => {
    if (!/^https:\/\/www\.youtube\.com\//.test(tab.url || '')) return;
    return bwos('api/playback/diagnostic', { id: String(details.tabId), service: 'youtube', category, code });
  }).catch(() => {});
}
if (chrome.webRequest) {
  const filter = { urls: ['https://*.googlevideo.com/videoplayback*', 'https://www.youtube.com/youtubei/v1/player*'] };
  chrome.webRequest.onResponseStarted.addListener(youtubeNetworkFailure, filter);
  chrome.webRequest.onErrorOccurred.addListener(youtubeNetworkFailure, filter);
}
async function mediaReport(msg, sender) {
  if (!sender.tab || !msg.data || !sender.tab.url?.startsWith('https://')) return;
  const all = await players(), id = sender.tab.id, old = all[id];
  // A playing top-level player takes precedence over a paused embedded frame.
  if (old && old.frameId !== sender.frameId && (!old.data.paused || msg.data.paused) && Date.now() - old.seen < 10000) return;
  const data = { ...msg.data, url: sender.tab.url };
  const player = { data, frameId: sender.frameId, windowId: sender.tab.windowId, background: old?.background || false, seen: Date.now() };
  if (data.ended) delete all[id]; else all[id] = player;
  await chrome.storage.session.set({ players: all });
  await reportPlayer(id, player, data.ended);
  await pollPlayback();
}
async function pollPlayback() {
  if (polling) return;
  polling = true;
  try {
    const batch = await bwos('api/playback/claim', {});
    for (const command of batch?.commands || []) {
      const all = await players(), player = all[command.id], id = Number(command.id);
      if (!player) continue;
      try {
        if (command.action === 'focus') {
          await chrome.windows.update(player.windowId, { state: 'fullscreen', focused: true });
          await chrome.tabs.update(id, { active: true });
          player.background = false; all[id] = player;
          await chrome.storage.session.set({ players: all });
          await reportPlayer(id, player);
        } else if (command.action === 'retry') {
          // Reload this service only. Keep Home, the same tab, and the TV watch route.
          await chrome.tabs.reload(id);
        } else if (command.action === 'stop') {
          await chrome.tabs.remove(id);
          delete all[id]; await chrome.storage.session.set({ players: all });
          await reportPlayer(id, player, true);
        } else {
          const result = await sendToTab(id, { type: 'media-control', action: command.action, value: command.value || '' }, { frameId: player.frameId });
          if (!result?.ok) await bwos('api/playback/error', { id: String(id), error: result?.error || 'This app requires you to reopen the player to continue.' });
        }
      } catch {
        await bwos('api/playback/error', { id: String(id), error: 'Could not control this player. Reopen it and try again.' });
      }
    }
  } finally { polling = false; }
}
async function returnHome(tabId) {
  const { home, token } = await settings();
  const base = home || 'http://127.0.0.1:47913/';
  if (tabId == null) {
    const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
    if (!tab) return;
    tabId = tab.id;
  }
  const sourceTab = await chrome.tabs.get(tabId);
  if(sourceTab.url?.startsWith(base)) {
    // The retained Home document owns its embedded player. Never inventory it
    // again as an external tab or try to resume its iframe during a handoff.
    await activateHome(sourceTab,base,token);
    await chrome.windows.update(sourceTab.windowId,{state:'fullscreen',focused:true});
    await bwos('api/browser/context',{kind:'home',foreground:true});
    return;
  }
  let all = await players(), player = all[tabId];
  // Probe at the moment Home is pressed, including before the first periodic report.
  try {
    const snapshot = await sendToTab(tabId, { type: 'media-snapshot' }, player ? { frameId: player.frameId } : undefined, 1250);
    if (snapshot?.data && !snapshot.data.ended) {
      const tab = await chrome.tabs.get(tabId);
      player = { data: { ...snapshot.data, url: tab.url }, frameId: snapshot.frameId, windowId: tab.windowId, background: false, seen: Date.now() };
      all[tabId] = player; await chrome.storage.session.set({ players: all });
    }
  } catch { /* Retain an already known player when its frame is temporarily unavailable. */ }
  if (!player && sourceTab.audible) {
    player = { data: { url: sourceTab.url, title: sourceTab.title || 'Media', paused: false, canControl: false, error: 'Reopen the player for playback controls.' }, frameId: 0, windowId: sourceTab.windowId, background: false, seen: Date.now() };
    all[tabId] = player; await chrome.storage.session.set({ players: all });
  }
  if (player) {
    const keepPlaying = !player.data.paused && !player.data.ended;
    const windowId = sourceTab.windowId;
    await activateHome(sourceTab, base, token);
    player.background = true;
    all[tabId] = player; await chrome.storage.session.set({ players: all });
    await chrome.tabs.update(tabId, { autoDiscardable: false });
    // Leave the player in an inactive tab instead of creating another window. Some TV clients pause
    // on blur/visibility changes; recover only playback that was active at handoff.
    if (keepPlaying && player.data.canControl !== false) {
      try {
        const result = await sendToTab(tabId, { type: 'media-handoff', keepPlaying: true }, { frameId: player.frameId }, 2000);
        if (result?.data) player.data = { ...result.data, url: sourceTab.url };
        if (!result?.ok) player.data.error = 'Playback paused while returning Home. Use Play or reopen the player.';
      } catch { player.data.error = 'Could not keep this player playing. Reopen it to continue.'; }
    }
    all[tabId] = player; await chrome.storage.session.set({ players: all });
    // Switching away from a site's HTML fullscreen player can exit fullscreen.
    // Reassert browser fullscreen on the same window after tab activation.
    await chrome.windows.update(windowId, { state: 'fullscreen' });
    await chrome.windows.update(windowId, { focused: true });
    await bwos('api/browser/context', { kind: 'home', foreground: true });
    await reportPlayer(tabId, player);
  } else {
    await activateHome(sourceTab, base, token);
    await chrome.windows.update(sourceTab.windowId, { state: 'fullscreen' });
    await chrome.windows.update(sourceTab.windowId, { focused: true });
    await bwos('api/browser/context', { kind: 'home', foreground: true });
  }
}
function goHome(tabId) {
  if (homeTask) return homeTask;
  homeTask = schedule(() => returnHome(tabId)).then(() => ({ ok: true })).catch(async () => {
    const error = 'Could not return Home. Your player was kept open. Try opening BWOS again.';
    await bwos('api/browser/error', { message: error }).catch(() => {});
    return { ok: false, error };
  }).finally(() => { homeTask = null; });
  return homeTask;
}
setInterval(() => { connectPlayback().catch(error => diagnose('connection', error)); schedule(pollPlayback).catch(error => diagnose('playback', error)); pollContext().catch(error => diagnose('context', error)); }, 1000);
chrome.tabs.onRemoved.addListener(id => schedule(async () => {
  const all = await players(), player = all[id];
  if (!player) return;
  delete all[id]; await chrome.storage.session.set({ players: all });
  await reportPlayer(id, player, true).catch(() => {});
}).catch(() => {}));
chrome.tabs.onUpdated.addListener((id, change) => schedule(async () => {
  if (!change.url) return;
  const all = await players(), player = all[id];
  if (!player) return;
  delete all[id]; await chrome.storage.session.set({ players: all });
  await reportPlayer(id, player, true).catch(() => {});
}).catch(() => {}));

chrome.commands.onCommand.addListener((command) => {
  if (command === 'bwos-home') goHome();
});

chrome.runtime.onMessage.addListener((msg, sender, reply) => {
  if (!msg) return false;
  const tabId = sender.tab && sender.tab.id;
  if(recommendationTabs.has(tabId) && !['recommendations-report','recommendations-snapshot'].includes(msg.type)) {reply(null);return false;}
  switch (msg.type) {
    case 'recommendations-report':
      // Temporary discovery/search pages must not overwrite Home with a partial
      // lazy-loading snapshot or persist a user's search results as recommendations.
      if(recommendationTabs.has(tabId)){reply({ok:true});return false;}
      recommendationsReport(msg.data,sender).then(reply,error=>reply({ok:false,error:error.message}));return true;
    case 'sync-recommendations':
      syncRecommendations(msg.service,sender,msg.target).then(reply,error=>reply({ok:false,error:error.message}));return true;
    case 'search-recommendations':
      discoverRecommendations(msg.service,sender,msg.query).then(reply,error=>reply({ok:false,error:error.message}));return true;
    case 'media-frame': reply({ frameId: sender.frameId }); return false;
    case 'media-diagnostic':
      if (sender.frameId !== 0 || !/^https:\/\/www\.youtube\.com\//.test(sender.tab?.url || '')) return false;
      bwos('api/playback/diagnostic', { id: String(tabId), service: 'youtube', category: 'player', code: msg.data?.code }).then(reply, () => reply(null)); return true;
    case 'open-service':
      schedule(() => openService(msg.url, sender)).then(reply, error => reply({ ok: false, error: error.message }));
      return true;
    case 'media-intent':
      if (sender.frameId !== 0 || !sender.tab?.url?.startsWith('https://') || !['pause', 'resume', 'toggle', 'stop', 'restart', 'seekForward', 'seekBack', 'nextMedia', 'previousMedia', 'subtitlesOn', 'subtitlesOff'].includes(msg.action)) return false;
      schedule(async () => {
        if (msg.action === 'stop') {
          const all = await players();
          await sendToTab(tabId, { type: 'media-control', action: 'pause' }, all[tabId] ? { frameId: all[tabId].frameId } : undefined).catch(() => {});
          await returnHome(tabId); await chrome.tabs.remove(tabId); return { ok: true };
        }
        const all = await players();
        const result = await sendToTab(tabId, { type: 'media-control', action: msg.action, value: msg.value || '' }, all[tabId] ? { frameId: all[tabId].frameId } : undefined);
        if (!result?.ok) await bwos('api/browser/error', { message: result?.error || 'This player does not support that control. Reopen it to continue.' });
        return result;
      }).then(reply, () => reply({ ok: false, error: 'Player did not respond. Reopen it and try again.' }));
      return true;
    case 'reload-service':
      if (sender.frameId !== 0 || !sender.tab?.url?.startsWith('https://')) return false;
      chrome.tabs.reload(tabId).then(() => reply({ ok: true }), () => reply({ ok: false })); return true;
    case 'show-keyboard':
      if (sender.frameId !== 0 || !sender.tab?.url?.startsWith('https://')) return false;
      sendToTab(tabId, { type: 'show-keyboard' }, { frameId: 0 }).then(reply, () => reply({ ok: false })); return true;
    case 'media-report':
      schedule(() => mediaReport(msg, sender)).then(() => reply({ ok: true }), () => reply(null));
      return true;
    case 'media-poll':
      if (sender.frameId !== 0 || !sender.tab?.url?.startsWith('http://127.0.0.1:')) return false;
      schedule(pollPlayback).then(() => reply({ ok: true }), () => reply(null));
      return true;
    case 'progress':
      bwos('api/progress', msg.data).then(reply, () => reply(null));
      return true;
    case 'lookup':
      bwos('api/progress/lookup', msg.data).then(reply, () => reply(null));
      return true;
    case 'keyboard':
      bwos('api/keyboard/open', msg.data).then(reply, () => reply(null));
      return true;
    case 'voice-poll':
      if (sender.frameId !== 0) return false;
      reportContext(sender.tab).then(() => bwos('api/voice/service', {})).then(reply, () => reply(null));
      return true;
    case 'voice-type':
      if (sender.frameId !== 0 || typeof msg.text !== 'string' || msg.text.length > 300) return false;
      bwos('api/voice/type', { text: msg.text }).then(reply, () => reply(null));
      return true;
    case 'voice-error':
      if (sender.frameId !== 0) return false;
      bwos('api/browser/error', { message: String(msg.message || '').slice(0, 180) }).then(reply, () => reply(null)); return true;
    case 'home':
      goHome(tabId).then(reply, () => reply(null));
      return true;
    case 'key-to-top':
      if (tabId != null && sender.frameId!==0 && ['ArrowLeft','ArrowRight','ArrowUp','ArrowDown','Enter','Escape',' ','x','p','f','o','m','ContextMenu','F23'].includes(msg.key)) chrome.tabs.sendMessage(tabId, { type: 'key', key: msg.key }, { frameId: 0 }).catch(()=>{});
      return false;
  }
  return false;
});
