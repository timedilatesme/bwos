import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
let keydown,listener,message;
const calls=[],posts=[];
let focused,clicked=0,sliderKeys=[];
const element=(name)=>({name,isConnected:true,focus(){focused=this;},blur(){focused=null;},click(){clicked++;},closest(){return null;},getBoundingClientRect:()=>({width:40,height:40}),dispatchEvent:event=>{if(name==='timeline')sliderKeys.push(event.key);}});
const play=element('play'),settings=element('settings'),bar=element('timeline'),caption=element('captions');
let menuOpen=false;
const menuItem=element('Quality'),menu={...element('menu'),querySelectorAll:()=>[menuItem],querySelector:()=>null};
settings.click=()=>{menuOpen=!menuOpen;clicked++;};
const parent={postMessage:(data,origin)=>posts.push({data,origin})};
const document={activeElement:play,querySelector:selector=>({'#movie_player':element('player'),'.ytp-settings-button,.player-settings-icon':settings,'.ytp-progress-bar[role="slider"],.ytPlayerProgressBarDragContainer[role="slider"]':bar,video:{paused:false}}[selector] || null),
  querySelectorAll:selector=>selector.startsWith('.ytp-settings-menu')?(menuOpen?[menu]:[]):selector.startsWith('.ytp-chrome-controls button')?[play,caption,settings]:[]};
const source=fs.readFileSync(new URL('../extensions/bwos-tv/remote.js',import.meta.url),'utf8');
vm.runInNewContext(source,{location:{hostname:'www.youtube.com',pathname:'/embed/M7lc1UVf-VE'},
  window:{parent},document,innerWidth:1920,innerHeight:1080,getComputedStyle:()=>({display:'block',visibility:'visible'}),setTimeout:()=>1,clearTimeout(){},MouseEvent:class{},KeyboardEvent:class{constructor(type,options){Object.assign(this,options);}},
  addEventListener:(type,callback)=>{if(type==='keydown')keydown=callback;if(type==='message')message=callback;},chrome:{runtime:{sendMessage:message=>calls.push(message)}}});
for(const key of ['ArrowLeft','ArrowRight','Enter','Escape',' ','p','o']) {
  let prevented=false;
  keydown({key,preventDefault(){prevented=true;},stopImmediatePropagation(){}});
  assert.ok(prevented,'Iframe input is consumed once');
  assert.equal(calls.at(-1).type,'key-to-top');assert.equal(calls.at(-1).key,key);
}
const before=calls.length;
keydown({key:'ArrowLeft',altKey:true});assert.equal(calls.length,before,'Browser modifier shortcuts are preserved');
keydown({key:'F24',preventDefault(){}});assert.equal(calls.at(-1).type,'home');
const command=action=>message({source:parent,origin:'http://127.0.0.1:47913',data:{type:'bwos-native-controls',action}});
command('up');assert.equal(focused,bar,'Up focuses the native timeline');
command('right');assert.deepEqual(sliderKeys,['ArrowRight'],'Timeline receives the native seek shortcut once');
command('down');assert.equal(focused,play,'Down reaches on-video buttons');
command('right');assert.equal(focused,caption,'D-pad reaches captions');
command('accept');assert.equal(clicked,1,'A activates the actual provider button');
command('settings');assert.equal(menuOpen,true,'Options opens the native settings');
command('down');assert.equal(focused,menuItem,'D-pad reaches menu items');
command('back');assert.equal(menuOpen,false,'Back closes the settings before leaving controls');
command('back');assert.equal(posts.at(-1).data.active,false,'Back leaves native control navigation');
const posted=posts.length;
message({source:{},origin:'http://127.0.0.1:47913',data:{type:'bwos-native-controls',action:'accept'}});
message({source:parent,origin:'https://untrusted.example',data:{type:'bwos-native-controls',action:'accept'}});
assert.equal(posts.length,posted,'Only the BWOS parent can drive embedded native menus');
const window={postMessage:(data,origin)=>posts.push({data,origin})};
vm.runInNewContext(fs.readFileSync(new URL('../extensions/bwos-tv/home.js',import.meta.url),'utf8'),{window,location:{origin:'http://127.0.0.1:47913',hash:''},sessionStorage:{getItem:()=>null},
  setInterval(){},addEventListener(){},chrome:{storage:{local:{set(){}}},runtime:{onMessage:{addListener:callback=>{listener=callback;}}}}});
listener({type:'key',key:'ArrowLeft'});
assert.equal(posts.at(-1).data.type,'bwos-player-key');assert.equal(posts.at(-1).origin,'http://127.0.0.1:47913');
const count=posts.length;listener({type:'key',key:'unrecognized'});assert.equal(posts.length,count,'Only player keys cross the Home bridge');
console.log('PASS: native timeline, captions and settings navigation, bounded parent messages, iframe key relay and Home bridge');
