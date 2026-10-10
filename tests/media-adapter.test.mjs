import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

// Probe real adapter snapshots before the periodic report, without launching media.
let listener, periodic;
const timers = [], events = {};
const reports = [];
const clip = { duration: 8, currentTime: 0, paused: false, ended: false,
  isConnected: true, clientWidth: 640, clientHeight: 360,
  pause() { this.paused = true; }, async play() { this.paused = false; } };
let elements = [clip];
const shadow = { querySelectorAll: selector => selector === 'video,audio' ? elements : [] };
const component = { shadowRoot: shadow };
let errorDialog = false;
const document = { title: 'TV clip', querySelector: () => null,
  querySelectorAll: selector => selector === '*' ? [component] : selector.startsWith('[role=') && errorDialog ? [{textContent:'Something went wrong. Try again or scan the QR code',getBoundingClientRect:()=>({width:600})}] : [], addEventListener: (type, handler) => { events[type] = handler; } };
const window = {}; window.top = window;
const context = vm.createContext({ document, window, navigator: {}, location: { href: 'https://www.youtube.com/tv' },
  setInterval: callback => { periodic = callback; },
  setTimeout: (callback, delay) => { timers.push({ callback, delay }); },
  chrome: { runtime: {
    sendMessage: (message, reply) => { if (message.type === 'media-frame') reply({ frameId: 0 }); else { reports.push(message); reply(); } },
    onMessage: { addListener: callback => { listener = callback; } }
  } }
});
vm.runInContext(fs.readFileSync(new URL('../extensions/bwos-tv/media.js', import.meta.url), 'utf8'), context);
const request = async message => new Promise(resolve => listener(message, {}, resolve));
const snapshot = await request({ type: 'media-snapshot' });
assert.equal(snapshot.data.title, 'TV clip', 'Home snapshot sees media inside open shadow roots before periodic reporting');
assert.equal(snapshot.data.paused, false, 'Short clips are included');
assert.equal(snapshot.data.position, 0, 'Timeline reports actual position');
assert.equal(snapshot.data.duration, clip.duration, 'Timeline reports finite duration');
clip.currentTime = 12.5;
assert.equal((await request({ type: 'media-snapshot' })).data.position, 12.5);
const duration = clip.duration; clip.duration = Infinity;
assert.equal((await request({ type: 'media-snapshot' })).data.duration, 0, 'Live streams have no finite progress bar');
clip.duration = duration;
assert.equal(reports.length, 0, 'Snapshot does not depend on a previous report');
await request({ type: 'media-control', action: 'pause' });
assert.equal(reports.at(-1).data.paused, true, 'Pause reports actual media state');
await request({ type: 'media-control', action: 'resume' });
assert.equal(reports.at(-1).data.paused, false, 'Resume reports actual media state');
const advance = async delay => { const index = timers.findIndex(timer => timer.delay === delay); assert.ok(index >= 0); timers.splice(index, 1)[0].callback(); for (let i = 0; i < 8; i++) await Promise.resolve(); };
clip.paused = true; // Simulate the TV client pausing on focus loss.
const handoff = request({ type: 'media-handoff', keepPlaying: true });
await advance(250);
assert.equal(clip.paused, false, 'Focus-loss pause is resumed during handoff');
clip.paused = true; // A provider may pause once more as visibility settles.
await advance(650);
assert.equal((await handoff).ok, true, 'A delayed visibility pause receives a bounded second recovery');
const cancelled = request({ type: 'media-handoff', keepPlaying: true });
await request({ type: 'media-control', action: 'pause' });
await advance(250);
assert.equal((await cancelled).ok, false);
assert.equal(clip.paused, true, 'Explicit cloud Pause cancels automatic recovery');
const manual = request({ type: 'media-handoff', keepPlaying: true });
events.pointerdown();
await advance(250);
assert.equal((await manual).ok, false, 'Manual page interaction cancels recovery');
const originalPlay = clip.play;
clip.play = async () => { throw Error('Provider blocked resume'); };
const rejected = request({ type: 'media-handoff', keepPlaying: true });
await advance(250);
assert.equal((await rejected).ok, false, 'Provider rejection reports a paused player honestly');
timers.length = 0;
clip.play = () => new Promise(() => {});
const stalled = request({ type: 'media-handoff', keepPlaying: true });
await advance(250);
await advance(1500);
assert.equal((await stalled).ok, false, 'A stalled play promise cannot block Home indefinitely');
clip.play = originalPlay;
clip.currentTime = 12.5;
await request({ type:'media-control', action:'seekBack', value:'10' });
assert.equal(clip.currentTime, 2.5, 'Seek uses the requested interval');
await request({ type:'media-control', action:'seekForward', value:'100' });
assert.equal(clip.currentTime, clip.duration - .1, 'Seek clamps to the playable end');
await request({ type:'media-control', action:'restart' });
assert.equal(clip.currentTime, 0, 'Restart seeks to the beginning');
assert.equal((await request({ type:'media-control', action:'seekForward', value:'-1' })).ok, false, 'Invalid seek is rejected');
clip.textTracks = [{ kind:'subtitles', mode:'disabled' }];
await request({ type:'media-control', action:'subtitlesOn' });
assert.equal(clip.textTracks[0].mode, 'showing');
await request({ type:'media-control', action:'subtitlesOff' });
assert.equal(clip.textTracks[0].mode, 'disabled');
clip.paused = true; await request({ type:'media-control', action:'toggle' });
assert.equal(clip.paused, false, 'Toggle follows actual paused state');
timers.length = 0; clip.play = () => new Promise(() => {});
const stalledResume = request({ type:'media-control', action:'resume' });
await advance(1500);
assert.equal((await stalledResume).ok, false, 'A stalled resume does not block the adapter forever');
clip.play = originalPlay;
clip.error = { code:2 }; clip.paused = true;
events.error();
assert.equal(reports.at(-1).data.canControl, false, 'A broken player disables ordinary Play');
assert.match(reports.at(-1).data.error, /connection failed/);
assert.ok(reports.some(report => report.type === 'media-diagnostic' && report.data.code === 'MEDIA_2'));
assert.equal((await request({type:'media-control',action:'resume'})).ok, false, 'Failed media is not force-resumed');
clip.error = null; clip.isConnected = false; elements = []; errorDialog = true;
periodic();
assert.match(reports.at(-1).data.error, /YouTube could not play/, 'TV error stays visible after YouTube removes the failed video');
assert.equal(reports.at(-1).data.ended, false, 'Provider failure retains a cloud with recovery controls');
errorDialog = false;
clip.isConnected = false; elements = [];
periodic();
assert.equal(reports.at(-1).data.ended, true, 'Removed players expire');
assert.equal((await request({ type: 'media-control', action: 'pause' })).ok, false, 'Removed players reject controls');
console.log('PASS: cold Home snapshots, shadow-root media, bounded focus-loss recovery, explicit pause cancellation and removed players');
