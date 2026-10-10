import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
class Navigator { get userAgent() { return 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/142.0.0.0 Safari/537.36 Edg/142.0.0.0'; } get userAgentData() { return 'native-client-hints'; } }
class Response { async json() { return { playabilityStatus:{ status:'OK' }, adPlacements:[{ untouched:true }], responseContext:{} }; } }
const nativeJson = Response.prototype.json;
const window = {};
const navigator = new Navigator();
const context = vm.createContext({ Navigator, navigator, Response, window, location:{ hostname:'www.youtube.com', pathname:'/tv' },
  setInterval() { throw Error('Playback must not be force-seeked'); } });
const parser = vm.runInContext('JSON.parse', context);
vm.runInContext(fs.readFileSync(new URL('../extensions/bwos-tv/youtube.js', import.meta.url), 'utf8'), context);
assert.equal(vm.runInContext('JSON.parse', context), parser, 'Native JSON parsing remains intact');
assert.equal(Response.prototype.json, nativeJson, 'Player responses are not intercepted');
assert.equal((await new Response().json()).adPlacements.length, 1, 'YouTube owns its full player response');
assert.equal(Object.getOwnPropertyDescriptor(window, 'ytInitialPlayerResponse'), undefined, 'Embedded player data is not rewritten');
assert.match(new Navigator().userAgent, /Xbox/, 'The existing TV compatibility user agent is retained');
const navigationRule = JSON.parse(fs.readFileSync(new URL('../extensions/bwos-tv/rules.json', import.meta.url), 'utf8'))[0];
assert.equal(navigator.userAgent, navigationRule.action.requestHeaders.find(header=>header.header==='user-agent').value, 'TV page and document header identities match');
assert.equal(navigator.userAgentData, undefined, 'Desktop client hints do not undo TV detection');
assert.deepEqual(navigationRule.condition.resourceTypes, ['main_frame','sub_frame'], 'Compatibility identity never reaches player APIs, scripts or media');
class DesktopNavigator { get userAgent() { return 'native-desktop'; } }
const desktop = new DesktopNavigator();
vm.runInContext(fs.readFileSync(new URL('../extensions/bwos-tv/youtube.js', import.meta.url), 'utf8'), vm.createContext({ Navigator:DesktopNavigator, navigator:desktop, location:{hostname:'www.youtube.com',pathname:'/watch'} }));
assert.equal(desktop.userAgent, 'native-desktop', 'Desktop YouTube is not given a TV identity');
console.log('PASS: TV-only compatibility identity without response rewriting or forced seeking');
