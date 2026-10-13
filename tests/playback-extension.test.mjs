import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

// Exercise the real worker with isolated Chrome/host adapters. No browser or media is launched.
const local = { home: 'http://127.0.0.1:47913/', token: 'test-token' }, session = {};
const tabs = new Map([[42, { id: 42, windowId: 7, active: true, url: 'https://www.youtube.com/watch?v=music_12345&list=playlist&index=2' }], [43, { id: 43, windowId: 7, active: false, url: 'https://www.netflix.com/watch/film' }]]);
const snapshots = new Map();
const updates = [], reports = [], controls = [], removed = [], errors = [], handoffs = [];
const reloaded = [], diagnostics = [], tvRules = [], network = {};
let batch = [], message, command, nextTab = 50;
const event = handler => ({ addListener: callback => handler?.(callback) });
const clone = value => structuredClone(value);
const chrome = {
  declarativeNetRequest: { updateDynamicRules: async rules => { tvRules.push(rules); } },
  webRequest: { onResponseStarted: event(callback => { network.response = callback; }), onErrorOccurred: event(callback => { network.error = callback; }) },
  storage: {
    local: { get: async () => clone(local) },
    session: { get: async () => clone(session), set: async value => Object.assign(session, clone(value)) },
    onChanged: event()
  },
  commands: { onCommand: event(callback => { command = callback; }) },
  runtime: { onMessage: event(callback => { message = callback; }) },
  tabs: {
    query: async query => { assert.equal(query.url, undefined, 'Home lookup avoids port-specific match patterns'); return [...tabs.values()].filter(tab => (query.windowId == null || tab.windowId === query.windowId) && (!query.active || tab.active)); },
    create: async value => {
      assert.ok(value.windowId, 'Home is created inside the existing window');
      if (value.active) for (const tab of tabs.values()) if (tab.windowId === value.windowId) tab.active = false;
      const tab = { id: nextTab++, ...value }; tabs.set(tab.id, tab); return clone(tab);
    },
    get: async id => clone(tabs.get(id)),
    update: async (id, value) => {
      updates.push({ id, ...value });
      if (value.active) for (const tab of tabs.values()) if (tab.windowId === tabs.get(id).windowId) tab.active = false;
      Object.assign(tabs.get(id), value); return tabs.get(id);
    },
    sendMessage: async (id, value, frame) => {
      if (value.type === 'media-snapshot') return { frameId: 0, data: snapshots.get(id) || null };
      if (value.type === 'media-handoff') { handoffs.push({ id, ...value, frame }); return { ok: true, data: snapshots.get(id) ? { ...snapshots.get(id), paused: false } : null }; }
      controls.push({ id, ...value, frame }); return { ok: true };
    },
    remove: async id => { removed.push(id); tabs.delete(id); },
    reload: async id => { reloaded.push(id); },
    onRemoved: event(), onUpdated: event()
  },
  windows: {
    get: async id => ({ id, focused: true }),
    update: async (id, value) => { updates.push({ window: id, ...value }); },
    create: async () => { throw Error('Home must not open a second window'); }
  }
};
class Socket { constructor() { this.readyState = 1; } close() {} }
const deadlines = new Map(); let nextDeadline = 0;
const context = vm.createContext({ chrome, navigator:{userAgent:'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/142.0.0.0 Edg/142.0.0.0'}, WebSocket: Socket, URL, URLSearchParams, console, setInterval() {},
  setTimeout(callback) { const id = ++nextDeadline; deadlines.set(id, callback); return id; }, clearTimeout(id) { deadlines.delete(id); },
  fetch: async (url, request) => {
    const body = JSON.parse(request.body);
    assert.equal(request.headers['X-BWOS-Token'], local.token);
    if (url.endsWith('/report')) reports.push(body);
    if (url.endsWith('/error')) errors.push(body);
    if (url.endsWith('/diagnostic')) diagnostics.push(body);
    const data = url.endsWith('/claim') ? { commands: batch.splice(0) } : url.endsWith('/preview') ? {ok:true,rows:body.rows,count:1} : { ok: true };
    return { ok: true, json: async () => data };
  }
});
vm.runInContext(fs.readFileSync(new URL('../extensions/bwos-tv/background.js', import.meta.url), 'utf8'), context);
const send = (data, tab = tabs.get(42), frameId = 0) => new Promise(resolve => message(data, { tab, frameId }, resolve));
const flush = async () => { for (let i = 0; i < 15; i++) await new Promise(resolve => setImmediate(resolve)); };
await flush();
assert.deepEqual(clone(tvRules[0].removeRuleIds), [1], 'Stale dynamic desktop identity is removed on upgrades');
assert.equal(tvRules[0].addRules, undefined, 'Startup no longer installs the redirect-causing desktop identity');
const rule = JSON.parse(fs.readFileSync(new URL('../extensions/bwos-tv/rules.json', import.meta.url), 'utf8'))[0];
assert.match(rule.action.requestHeaders[0].value, /Xbox/);
assert.deepEqual(clone(rule.condition.resourceTypes), ['main_frame','sub_frame'], 'Player API and media headers are untouched');
const match = new RegExp(rule.condition.regexFilter);
assert.ok(match.test('https://www.youtube.com/tv') && match.test('https://www.youtube.com/tv?x=1'));
assert.ok(!match.test('https://www.youtube.com/tvextra') && !match.test('https://www.youtube.com/youtubei/v1/player'));
network.response({tabId:42,url:'https://r1.googlevideo.com/videoplayback?secret=never-log',statusCode:403});
await flush();
assert.equal(diagnostics.at(-1).code, 'HTTP_403');
assert.ok(!JSON.stringify(diagnostics).includes('secret'), 'Signed URLs are never sent to the host');
const count = diagnostics.length;
network.response({tabId:42,url:'https://r1.googlevideo.com/videoplayback',statusCode:403});
network.error({tabId:42,url:'https://r1.googlevideo.com/videoplayback',error:'net::ERR_ABORTED'});
network.response({tabId:42,url:'https://www.youtube.com/youtubei/v1/log_event',statusCode:500});
await flush();
assert.equal(diagnostics.length, count, 'Repeated failures, normal cancellation and unrelated requests are ignored');
await Promise.all([
  send({ type: 'media-report', data: { title: 'Music', paused: false, url: 'https://fake.example/' } }),
  send({ type: 'media-report', data: { title: 'Film', paused: false } }, tabs.get(43))
]);
assert.equal(Object.keys(session.players).length, 2, 'Concurrent reports preserve both players');
assert.equal(reports[0].url, tabs.get(42).url, 'Source URL belongs to the actual tab');
await send({ type: 'media-report', data: { title: 'Ad frame', paused: true } }, tabs.get(42), 2);
assert.equal(session.players[42].data.title, 'Music', 'Paused frame cannot replace active player');
message({ type: 'home' }, { tab: tabs.get(42), frameId: 0 }, () => {});
await flush();
assert.equal(tabs.get(42).url, 'https://www.youtube.com/watch?v=music_12345&list=playlist&index=2', 'Home preserves the media tab');
assert.ok([...tabs.values()].some(tab => tab.url === local.home + '#t=' + local.token), 'New home receives auth token');
assert.ok(!updates.some(update => update.state === 'minimized'), 'Home does not minimize the live player');
assert.ok(handoffs.some(item => item.id === 42 && item.keepPlaying), 'Playing media receives bounded handoff recovery');
assert.ok(updates.some(update => update.window === 7 && update.state === 'fullscreen' && update.focused === undefined), 'Original window remains fullscreen');
assert.equal(tabs.get(50).windowId, 7, 'Home and player share one window');
tabs.get(50).audible=true;
snapshots.set(50,{title:'BWOS embedded video',paused:false});
await send({type:'home'},tabs.get(50));
assert.equal(session.players[50],undefined,'Native Home player is never duplicated in the external-tab inventory');
assert.ok(!handoffs.some(item=>item.id===50),'Native iframe is not force-resumed by external-tab handoff');
assert.equal(tabs.get(42).active, false, 'The player remains alive in a background tab');
assert.equal(session.players[42].background, true);
message({ type: 'home' }, { tab: tabs.get(43), frameId: 0 }, () => {});
await flush();
assert.equal([...tabs.values()].filter(tab => tab.url.startsWith(local.home)).length, 1, 'Home window is reused');
assert.ok(updates.filter(update => update.window === 7 && update.state === 'fullscreen').length >= 2, 'Reused Home also restores fullscreen');
for (let attempt = 0; attempt < 3; attempt++) {
  const opened = await send({ type: 'open-service', url: 'https://www.youtube.com/tv' }, tabs.get(50));
  assert.equal(opened.ok, true);
  assert.equal(tabs.get(42).active, true, 'Opening the app focuses its existing player');
  assert.equal(tabs.get(42).url, 'https://www.youtube.com/tv#/watch?v=music_12345&list=playlist&index=2', 'Opening the TV app migrates a retained desktop video once and preserves its TV session afterwards');
  await send({ type: 'home' }, tabs.get(42));
}
assert.equal([...tabs.values()].filter(tab => tab.url.includes('youtube.com')).length, 1, 'Repeated app/Home cycles never duplicate YouTube');
assert.equal(updates.filter(update => update.id === 42 && update.url).length, 1, 'A retained TV player is not repeatedly reloaded');
assert.ok(!updates.some(update => update.id === 50 && update.url), 'Home is activated without navigating/reloading its document');
assert.equal((await send({ type: 'open-service', url: 'https://www.youtube.com/tv' }, tabs.get(43))).ok, false, 'Streaming pages cannot impersonate Home');
assert.equal((await send({ type: 'open-service', url: 'javascript:alert(1)' }, tabs.get(50))).ok, false, 'Only HTTPS apps can be opened');
batch.push({ id: '42', action: 'pause' });
await send({ type: 'media-poll' }, { id: 50, url: local.home }, 0);
assert.deepEqual(clone(controls.at(-1)), { id: 42, type: 'media-control', action: 'pause', value: '', frame: { frameId: 0 } });
batch.push({ id: '42', action: 'focus' });
await send({ type: 'media-poll' }, { id: 50, url: local.home }, 0);
assert.equal(session.players[42].background, false, 'Reopening resets background state');
batch.push({ id: '42', action: 'stop' });
batch.unshift({ id: '42', action: 'retry' });
await send({ type: 'media-poll' }, { id: 50, url: local.home }, 0);
assert.deepEqual(removed, [42], 'Stop closes only the selected media tab');
assert.deepEqual(reloaded, [42], 'Retry reloads only the player, without reloading Home or creating a tab');
assert.ok(reports.at(-1).ended);
assert.ok(session.players[43], 'Other player remains alive');
assert.equal(errors.length, 0);
tabs.set(44, { id: 44, windowId: 9, active: true, url: 'https://www.youtube.com/watch?v=fresh' });
snapshots.set(44, { title: 'Fresh song', paused: false });
await send({ type: 'home' }, tabs.get(44));
assert.equal(session.players[44].data.title, 'Fresh song', 'Home probes playback before any periodic report');
assert.equal(tabs.get(44).url, 'https://www.youtube.com/watch?v=fresh', 'Early Home does not destroy player');
tabs.set(45, { id: 45, windowId: 12, active: true, audible: true, title: 'Audible player', url: 'https://www.youtube.com/watch?v=audio' });
await send({ type: 'home' }, tabs.get(45));
assert.equal(session.players[45].data.canControl, false, 'Audible tab survives when DOM controls are unavailable');
assert.ok(reports.some(report => report.id === '45' && report.background));
assert.ok(!handoffs.some(item => item.id === 45), 'Uncontrollable audible media does not receive a resume attempt');
tabs.set(46, { id: 46, windowId: 13, active: true, url: 'https://www.youtube.com/tv' });
snapshots.set(46, { title: 'Already paused', paused: true });
await send({ type: 'home' }, tabs.get(46));
assert.ok(!handoffs.some(item => item.id === 46), 'Home preserves a deliberate preexisting pause');
assert.equal(session.players[46].data.paused, true);
tabs.set(47, { id: 47, windowId: 14, active: true, url: 'https://example.com/' });
await send({ type: 'home' }, tabs.get(47));
assert.equal(tabs.get(47).url, 'https://example.com/', 'Non-media Home preserves the service too');
assert.ok([...tabs.values()].some(tab => tab.windowId === 14 && tab.url.startsWith(local.home)), 'Non-media Home selects a Home tab in the same window');
assert.ok(updates.some(update => update.window === 14 && update.state === 'fullscreen'), 'Non-media Home also restores fullscreen');
const oldUpdate = chrome.windows.update;
chrome.windows.update = async () => { throw Error('Window handoff failed'); };
const failed = await send({ type: 'home' }, tabs.get(44));
assert.equal(failed.ok, false, 'Home failure is reported');
assert.ok(tabs.has(44), 'Home failure retains media');
chrome.windows.update = oldUpdate;
const voiceContext = await send({ type: 'voice-poll' }, tabs.get(43));
assert.ok(voiceContext);
assert.equal(message({ type: 'media-poll' }, { tab: { url: 'https://untrusted.example/' }, frameId: 0 }, () => {}), false);

// Exercise the actual page key handler through to the real background handoff.
// This catches a missing Home signal before worker-only tests can mask it.
const remoteSource = fs.readFileSync(new URL('../extensions/bwos-tv/remote.js', import.meta.url), 'utf8');
async function testPageHome(id, embedded) {
  const tab = { id, windowId: id + 10, active: true, url: 'https://www.youtube.com/tv' };
  tabs.set(id, tab); snapshots.set(id, { title: 'Home signal fixture', paused: false });
  const listeners = [], calls = [];
  const window = {}; window.top = embedded ? { focus() {} } : window;
  vm.runInContext(remoteSource, vm.createContext({ window, location: { hostname: 'www.youtube.com', pathname: '/tv', hash: '', href: tab.url },
    document: { readyState: 'loading', body: null, addEventListener() {}, querySelectorAll: () => [], querySelector: () => null },
    addEventListener: (type, callback) => { if (type === 'keydown') listeners.push(callback); },
    requestAnimationFrame() {}, setInterval() {}, setTimeout() {},
    chrome: { storage: { local: { get: (_key, reply) => reply({ home: local.home }) } }, runtime: {
      onMessage: event(), sendMessage: (data, reply = () => {}) => { calls.push(data); message(data, { tab, frameId: embedded ? 1 : 0 }, reply); }
    } }
  }));
  let stopped = false, prevented = false;
  const key = { key: 'F24', preventDefault: () => { prevented = true; }, stopImmediatePropagation: () => { stopped = true; } };
  for (const listener of listeners) { listener(key); if (stopped) break; }
  await flush();
  assert.ok(prevented, 'Home key is consumed by the streaming adapter');
  assert.equal(calls.filter(call => call.type === 'home').length, 1, 'F24 sends exactly one Home request');
  assert.equal(session.players[id].background, true, 'F24 reaches the worker and preserves playback');
}
await testPageHome(48, false);
await testPageHome(49, true);
const originalSend = chrome.tabs.sendMessage;
chrome.tabs.sendMessage = async (id, value, frame) => value.type === 'media-control' ? new Promise(() => {}) : originalSend(id, value, frame);
batch.push({ id:'43', action:'resume' });
const stalledControl = send({ type:'media-poll' }, tabs.get(50));
await flush();
assert.equal(deadlines.size, 1, 'A stalled player gets a bounded response deadline');
[...deadlines.values()][0](); deadlines.clear();
await stalledControl;
assert.ok(errors.some(error => error.id === '43'), 'Stalled playback produces a visible error');
chrome.tabs.sendMessage = originalSend;
assert.equal((await send({ type:'home' }, tabs.get(43))).ok, true, 'Home still works after a stalled playback command');
// A frozen site must not hold the shared worker queue indefinitely during Home.
const stall = new Set(['media-snapshot', 'shell-home', 'media-handoff']);
chrome.tabs.sendMessage = async (id, value, frame) => stall.has(value.type) ? new Promise(() => {}) : originalSend(id, value, frame);
let recoveredHome = false;
const stalledHome = send({ type:'home' }, tabs.get(43)).then(result => { recoveredHome = true; return result; });
for (const stage of ['media-snapshot', 'shell-home', 'media-handoff']) {
  await flush();
  assert.equal(recoveredHome, false, stage + ' is awaiting its response deadline');
  assert.equal(deadlines.size, 1, stage + ' has exactly one bounded deadline');
  const [deadline, expire] = [...deadlines.entries()][0]; deadlines.delete(deadline); expire();
}
assert.equal((await stalledHome).ok, true, 'Home recovers from a frozen provider without restarting BWOS');
assert.ok(tabs.has(43), 'A frozen provider tab is retained');
assert.equal(session.players[43].background, true, 'The retained player remains in the playback cloud');
assert.match(session.players[43].data.error, /Reopen/, 'Failed playback recovery is reported instead of pretending music continued');
chrome.tabs.sendMessage = originalSend;
assert.equal((await send({type:'open-service',url:'https://www.netflix.com/browse'},tabs.get(50))).ok, true, 'App opening remains usable after stalled Home recovery');
assert.equal(deadlines.size, 0, 'Recovery releases every bridge deadline');
const recommendationHome=[...tabs.values()].find(tab=>tab.url?.startsWith(local.home));
const originalCreate=chrome.tabs.create;
chrome.tabs.create=async value=>({...await originalCreate(value),status:'complete'});
const originalGet=chrome.tabs.get;
chrome.tabs.get=async id=>({...await originalGet(id),status:'complete'});
chrome.tabs.sendMessage=async (id,value,frame)=>value.type==='recommendations-discover'?{data:{service:'netflix',rows:[{title:'Picked for you',items:[{title:'A film',url:'https://www.netflix.com/title/123'}]}]}}:originalSend(id,value,frame);
const before=new Set(tabs.keys());
assert.equal((await send({type:'sync-recommendations',service:'netflix'},recommendationHome)).ok,true,'Recommendation sync reads the loaded provider tab');
const syncTab=removed.at(-1);
assert.ok(!before.has(syncTab) && tabs.has(43),'Sync closes only the temporary tab, preserving playback');
assert.ok(updates.some(update=>update.id===syncTab && update.muted),'Temporary recommendation tab is muted');
assert.ok(reports.some(report=>report.service==='netflix' && report.rows?.[0].title==='Picked for you'),'Rendered shelf titles reach the authenticated host');
assert.equal((await send({type:'sync-recommendations',service:'netflix'},tabs.get(43))).ok,false,'Provider pages cannot create sync tabs');
assert.equal((await send({type:'sync-recommendations',service:'arbitrary'},recommendationHome)).ok,false,'Sync destinations are allowlisted');
assert.equal((await send({type:'recommendations-report',data:{service:'youtube',rows:[{items:[]}] }},tabs.get(43))).ok,false,'Provider identity cannot be forged');
const searchResult=await send({type:'search-recommendations',service:'netflix',query:'हिंदी & drama'},recommendationHome);
assert.equal(searchResult.rows[0].items[0].title,'A film','Native search returns rendered, sanitized results to BWOS');
assert.equal(reports.at(-1).complete,false,'Sparse discovery is not marked complete');
assert.equal((await send({type:'search-recommendations',service:'netflix',query:'film'},tabs.get(43))).ok,false,'A provider page cannot trigger arbitrary search tabs');
assert.equal((await send({type:'search-recommendations',service:'arbitrary',query:'film'},recommendationHome)).ok,false,'Search services are allowlisted');
assert.equal((await send({type:'search-recommendations',service:'netflix',query:'x'.repeat(201)},recommendationHome)).ok,false,'Search input has a bounded length');
console.log('PASS: background playback handoff, multiple players, controls, token transfer and sender checks');

// A sparse desktop home gets a second rendered source from YouTube TV; content
// remains deduplicated and the real player tab is never navigated.
chrome.tabs.sendMessage=async(id,value,frame)=>{
  if(value.type!=='recommendations-discover')return originalSend(id,value,frame);
  const tv=tabs.get(id).url.includes('/tv');
  return {data:{service:'youtube',rows:[{title:tv?'From YouTube TV':'From YouTube home',items:Array.from({length:tv?60:2},(_,i)=>({title:'Rendered video '+i,url:'https://www.youtube.com/watch?v='+String(i).padStart(11,'0')}))}],stats:{anchors:tv?60:2}}};
};
const playerUrl=tabs.get(43).url;
assert.equal((await send({type:'sync-recommendations',service:'youtube'},recommendationHome)).ok,true);
const youtubeReport=reports.at(-1);
assert.equal(youtubeReport.rows.flatMap(row=>row.items).length,60,'TV source expands a sparse home with actual rendered cards');
assert.equal(youtubeReport.complete,true,'Combined feed is complete only at the requested count');
assert.equal(new Set(youtubeReport.rows.flatMap(row=>row.items).map(item=>item.url)).size,60,'Desktop and TV results are deduplicated');
assert.ok(updates.some(update=>update.url==='https://www.youtube.com/tv'),'Only the temporary discovery tab changes to TV');
assert.equal(tabs.get(43).url,playerUrl,'Live playback is untouched by TV fallback');
chrome.tabs.sendMessage=async(id,value,frame)=>value.type==='recommendations-discover'?{data:{service:'youtube',rows:tabs.get(id).url.includes('/tv')?[]:[{title:'Home',items:[{title:'A rendered video',url:'https://www.youtube.com/watch?v=00000000000'}]}],reason:'no-cards'}}:originalSend(id,value,frame);
assert.equal((await send({type:'sync-recommendations',service:'youtube'},recommendationHome)).ok,true,'Unavailable TV source preserves desktop cards');
assert.equal(reports.at(-1).complete,false,'Failed expansion stays partial');
console.log('PASS: YouTube TV fallback, cross-source deduplication and sparse-source preservation');
