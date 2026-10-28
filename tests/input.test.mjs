import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
let now=100,frame,pads=[],blocked=false;
const events=new Map(),actions=[],errors=[];
const target={addEventListener:(name,fn)=>events.set(name,fn),classList:{add(){},remove(){}}};
class Input{}
const context=vm.createContext({window:target,document:{...target,body:target,hidden:false,hasFocus:()=>true},
  navigator:{getGamepads:()=>{if(blocked)throw Error('not permitted');return pads;}},
  HTMLInputElement:Input,HTMLTextAreaElement:Input,performance:{now:()=>now},
  localStorage:{getItem(){},setItem(){}},console:{error:message=>errors.push(message)},
  requestAnimationFrame:fn=>frame=fn,setTimeout,clearTimeout});
vm.runInContext(fs.readFileSync(new URL('../ui/js/input.js',import.meta.url),'utf8').replace('export const input','const input'),context);
const input=vm.runInContext('input',context);
input.on((a,s)=>actions.push([a,s]));
const tick=async()=>{frame();await new Promise(resolve=>setImmediate(resolve));};
const key=options=>events.get('keydown')({key:'Enter',target:{},preventDefault(){},...options});
key({});await tick();assert.equal(actions.at(-1)[0],'accept','Early keyboard input is not mistaken for Steam duplicates');
const pad={index:0,id:'Xbox',connected:true,mapping:'standard',axes:[0,0],buttons:Array.from({length:17},()=>({pressed:false,value:0}))};
pads=[pad];pad.buttons[15]={pressed:true,value:1};await tick();assert.equal(actions.at(-1)[0],'right');
const count=actions.length;await tick();assert.equal(actions.length,count,'Held D-pad waits before repeating');
now+=400;await tick();assert.equal(actions.length,count+1);
pads=[];await tick();pads=[pad];await tick();assert.equal(actions.length,count+2,'Reconnecting a controller resets its held direction');
pad.buttons[15]={pressed:false,value:0};await tick();
const before=actions.length;
for(const flag of ['ctrlKey','altKey','metaKey','isComposing','defaultPrevented'])key({[flag]:true});
await tick();assert.equal(actions.length,before,'IME composition and OS/browser shortcuts are not activated');
blocked=true;await tick();assert.equal(input.padCount(),0,'An unavailable Gamepad API does not crash input');blocked=false;
const remove=input.on(()=>{throw Error('broken listener');});
pad.buttons[0]={pressed:true,value:1};await tick();assert.equal(errors.length,1,'Input errors are handled without stopping polling');
remove();pad.buttons[0]={pressed:false,value:0};await tick();pad.buttons[1]={pressed:true,value:1};await tick();assert.equal(actions.at(-1)[0],'back');
console.log('PASS: input repeat/reconnect, keyboard/IME guards, unavailable gamepads, and failed listener recovery');
