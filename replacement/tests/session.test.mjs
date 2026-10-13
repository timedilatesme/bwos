import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import {test} from 'node:test';
import {webcrypto} from 'node:crypto';
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
function harness(saved={}){
 const home='http://127.0.0.1:47923/';let listener,next=10,frozen=false,collectResolve,collectId,delayedCreate,pausedOnHide=false;const handoffs=[];
 const tabs=new Map([[1,{id:1,windowId:7,url:home,active:true,status:'complete'}]]),updates=[],removed=[],reports=[],storage={...structuredClone(saved)},handlers={};
 const event=name=>({addListener:fn=>{handlers[name]=fn;}});
 const chrome={runtime:{id:'next-test',onMessage:{addListener:fn=>listener=fn}},commands:{onCommand:event('command')},declarativeNetRequest:{updateDynamicRules:async()=>{}},storage:{local:{get:async()=>({home,token:'secret'})},session:{get:async()=>structuredClone(storage),set:async value=>Object.assign(storage,structuredClone(value))}},tabs:{
   query:async q=>[...tabs.values()].filter(t=>(q.windowId===undefined || t.windowId===q.windowId) && (!q.active || t.active)),
   get:async id=>{if(!tabs.has(id))throw Error('Missing tab');return structuredClone(tabs.get(id));},
   create:async data=>{if(delayedCreate)await delayedCreate;const tab={...data,id:next++,status:'complete',mutedInfo:{muted:false}};tabs.set(tab.id,tab);if(data.active)for(const t of tabs.values())t.active=t.id===tab.id;return structuredClone(tab);},
   update:async (id,data)=>{const t=tabs.get(id);if(!t)throw Error('Missing tab');updates.push({id,...data});if(data.active)for(const tab of tabs.values())tab.active=tab.id===id;Object.assign(t,data);return structuredClone(t);},
   remove:async id=>{removed.push(id);tabs.delete(id);handlers.removed?.(id);},reload:async()=>{},onRemoved:event('removed'),
   sendMessage:async (id,msg)=>{if(msg.type==='recommendations-discover'){collectId=id;return new Promise(resolve=>{collectResolve=resolve;});}if(frozen && id!==1)return new Promise(()=>{});if(msg.type==='media-snapshot')return {frameId:0,data:{title:'Test music',paused:pausedOnHide,canControl:true,url:tabs.get(id).url}};if(msg.type==='media-handoff'){handoffs.push(id);return {ok:true,data:{title:'Test music',paused:false,canControl:true,url:tabs.get(id).url}};}return {ok:true};}
 },windows:{update:async(id,data)=>{assert.equal(id,7,'Permanent window');updates.push({window:id,...data});},get:async()=>({focused:true}),create:async()=>{throw Error('Second window forbidden');}}};
 const scope=vm.createContext({chrome,URL,URLSearchParams,AbortSignal,AbortController,structuredClone,crypto:webcrypto,console,setTimeout,clearTimeout,setInterval(){},fetch:async(url,request)=>{const body=JSON.parse(request.body);if(url.endsWith('/report'))reports.push(body);return {ok:true,json:async()=>url.endsWith('/claim')?{commands:[]}:url.endsWith('/preview')?{ok:true,rows:body.rows}:{ok:true}};}});
 scope.importScripts=(...files)=>files.forEach(file=>vm.runInContext(fs.readFileSync(new URL(file==='kernel.js'?'../core/kernel.js':'../extension/'+file,import.meta.url),'utf8'),scope));
 vm.runInContext(fs.readFileSync(new URL('../extension/background.js',import.meta.url),'utf8'),scope);
 const send=(msg,id=1)=>new Promise(resolve=>listener(msg,{tab:structuredClone(tabs.get(id)),frameId:0},resolve));
 return {scope,tabs,storage,updates,removed,reports,handoffs,pauseOnHide:()=>pausedOnHide=true,send,bind:()=>send({type:'core-bind'}),freeze:()=>frozen=true,collector:()=>collectId,releaseCollection:()=>collectResolve({data:{service:'youtube',complete:true,rows:[{title:'Collected',items:Array.from({length:60},(_,i)=>({url:'https://www.youtube.com/watch?v=abcdefgh'+String(i).padStart(3,'0'),title:'Test '+i}))}]}}),delayCreate:p=>delayedCreate=p};
}
test('concurrent discovery/open/Home/cleanup keeps live TV session and fullscreen',{timeout:5000},async()=>{
 const h=harness();await h.bind();
 const collection=h.send({type:'sync-recommendations',service:'youtube'});
 while(!h.collector())await sleep(1);
 const collector=h.collector();assert.equal(h.storage.ownership.collectors[collector].provider,'youtube');
 const opened=await h.send({type:'open-service',id:'open',url:'https://www.youtube.com/watch?v=abcdefghijk'});assert.equal(opened.ok,true);assert.notEqual(opened.tabId,collector);assert.match(h.tabs.get(opened.tabId).url,/\/tv#/);
 await h.send({type:'media-report',data:{title:'Video',paused:false,canControl:true}},opened.tabId);
 const home=await h.send({type:'home',id:'home'},opened.tabId);assert.equal(home.ok,true);assert.equal(h.tabs.get(1).active,true);
 assert.equal((await h.send({type:'home'},collector)).ok,false,'Collectors have no Home capability');
 h.releaseCollection();await collection;assert.ok(h.removed.includes(collector));assert.ok(!h.removed.includes(opened.tabId));
 for(let i=0;i<8;i++){const reopen=await h.send({type:'open-service',id:'reopen'+i,url:'https://www.youtube.com/tv'});assert.equal(reopen.tabId,opened.tabId);assert.equal((await h.send({type:'home',id:'home'+i},opened.tabId)).ok,true);}
 assert.equal(h.tabs.size,2);assert.equal(Object.values(h.storage.ownership.playback).length,1);assert.ok(h.updates.filter(u=>u.window).every(u=>u.state==='fullscreen'));assert.ok(h.reports.some(r=>r.background));
});
test('Home acknowledges before frozen player responds',{timeout:5000},async()=>{const h=harness();await h.bind();const open=await h.send({type:'open-service',url:'https://www.netflix.com/browse'});h.freeze();const start=Date.now();assert.equal((await h.send({type:'home'},open.tabId)).ok,true);assert.ok(Date.now()-start<200);assert.equal(h.tabs.get(1).active,true);});
test('duplicate open and deliberate mute are preserved',{timeout:5000},async()=>{const h=harness();await h.bind();const open=await h.send({type:'open-service',id:'same',url:'https://www.youtube.com/tv'});const duplicate=await h.send({type:'open-service',id:'same',url:'https://www.youtube.com/tv'});assert.equal(open.tabId,duplicate.tabId);h.tabs.get(open.tabId).mutedInfo={muted:true,reason:'user'};await h.send({type:'open-service',url:'https://www.youtube.com/tv'});assert.ok(!h.updates.some(u=>u.muted===false));h.tabs.get(open.tabId).mutedInfo={muted:true,reason:'extension',extensionId:'next-test'};await h.send({type:'open-service',url:'https://www.youtube.com/tv'});assert.ok(h.updates.some(u=>u.id===open.tabId && u.muted===false));});
test('Home fences delayed tab creation without duplicate session or focus resurrection',{timeout:5000},async()=>{const h=harness();await h.bind();let release;h.delayCreate(new Promise(r=>release=r));const open=h.send({type:'open-service',id:'slow',url:'https://www.youtube.com/tv'});await sleep(5);assert.equal((await h.send({type:'home',id:'interrupt'})).ok,true);release();await open;await sleep(5);assert.equal(h.tabs.get(1).active,true);assert.equal(h.tabs.size,2);});
test('worker restart retains owned sessions and removes interrupted collectors',{timeout:5000},async()=>{const h=harness({ownership:{version:2,windowId:7,homeId:1,playback:{youtube:20},collectors:{21:{provider:'youtube'}}}});h.tabs.set(20,{id:20,windowId:7,url:'https://www.youtube.com/tv',active:false});h.tabs.set(21,{id:21,windowId:7,url:'https://www.youtube.com/',active:false});await h.bind();assert.ok(h.removed.includes(21));assert.equal(h.storage.ownership.playback.youtube,20);assert.equal((await h.send({type:'open-service',url:'https://www.youtube.com/tv'})).tabId,20);});
test('Home preserves pre-handoff playing intent across a provider focus pause',{timeout:5000},async()=>{const h=harness();await h.bind();const open=await h.send({type:'open-service',url:'https://www.youtube.com/tv'});await h.send({type:'media-report',data:{title:'Music',paused:false,canControl:true}},open.tabId);h.pauseOnHide();await h.send({type:'home'},open.tabId);await sleep(20);assert.deepEqual(h.handoffs,[open.tabId]);assert.ok(h.reports.some(r=>r.background && !r.paused));});
