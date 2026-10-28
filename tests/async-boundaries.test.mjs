import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const deferred=()=>{let resolve,reject;const promise=new Promise((r,j)=>{resolve=r;reject=j;});return {promise,resolve,reject};};
const source=fs.readFileSync(new URL('../ui/js/screen-stream.js',import.meta.url),'utf8').replace(/^import .*;\r?\n/gm,'').replace('export class','class');
function sharingFixture(){
  const picker=deferred(),start=deferred(),calls=[],changes=[];
  const track={readyState:'live',stop(){this.readyState='ended';},addEventListener(){}};
  const media={getTracks:()=>[track],getVideoTracks:()=>[track],getAudioTracks:()=>[]};
  let peers=0;
  const context=vm.createContext({navigator:{mediaDevices:{getDisplayMedia:()=>picker.promise}},
    api:{stream:async(action,data)=>{calls.push({action,data});return action==='start'?start.promise:{ok:true};}},
    ScreenPeer:class{constructor(){peers++;}close(){}},setTimeout,clearTimeout});
  vm.runInContext(source,context);
  const Sharing=vm.runInContext('ScreenSharing',context),sharing=new Sharing(value=>changes.push(value));
  return {picker,start,calls,changes,track,media,sharing,peers:()=>peers};
}
{
  const f=sharingFixture(),pending=f.sharing.start();
  await f.sharing.stop();f.picker.resolve(f.media);
  await assert.rejects(pending,/cancelled/);
  assert.equal(f.track.readyState,'ended');assert.equal(f.sharing.active,false);
  assert.equal(f.calls.length,0,'Cancelling the picker cannot create a host session');
}
{
  const f=sharingFixture(),pending=f.sharing.start();f.picker.resolve(f.media);
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(f.calls[0].action,'start');await f.sharing.stop();
  f.start.resolve({session:'late-session'});await assert.rejects(pending,/cancelled/);
  assert.equal(f.calls.at(-1).action,'stop');assert.equal(f.calls.at(-1).data.session,'late-session');
  assert.equal(f.track.readyState,'ended');assert.equal(f.sharing.active,false);assert.equal(f.peers(),0);
}
{
  const timers=new Map();let timerId=0;
  const context=vm.createContext({location:new URL('http://127.0.0.1/#t='+'a'.repeat(32)),URL,URLSearchParams,AbortController,
    sessionStorage:{setItem(){},getItem(){}},history:{replaceState(){}},
    setTimeout:fn=>{timers.set(++timerId,fn);return timerId;},clearTimeout:id=>timers.delete(id),
    fetch:(_path,{signal})=>new Promise((_resolve,reject)=>signal.addEventListener('abort',()=>reject(new Error('aborted'))))});
  vm.runInContext(fs.readFileSync(new URL('../ui/js/api.js',import.meta.url),'utf8').replaceAll('export const','const').replaceAll('export function','function'),context);
  const request=vm.runInContext('api.state()',context);[...timers.values()][0]();
  await assert.rejects(request,/did not respond/);assert.equal(timers.size,0,'Network deadlines release their timers');
}
console.log('PASS: cancelled screen pickers, late host sessions, capture cleanup, and API request deadlines');
