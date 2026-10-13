// Isolated headless development fixture. No signed-in provider, microphone, or actual app UI.
import assert from 'node:assert/strict';
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {createRequire} from 'node:module';
const require=createRequire(import.meta.url);
const {chromium}=require('C:/Users/nutan/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root=fileURLToPath(new URL('../../dist/BWOS-Next/ui/',import.meta.url));
const output=fileURLToPath(new URL('../verification/',import.meta.url));fs.mkdirSync(output,{recursive:true});
const fixture={version:'fixture',user:{name:'Fixture verification'},games:Array.from({length:8},(_,i)=>({key:'fixture:'+i,name:'Fixture game '+i,source:'Fixture',art:{header:'/art.svg',hero:'/art.svg',portrait:'/art.svg'}})),watch:[{id:'youtube',name:'YouTube',url:'https://www.youtube.com/tv',iconUrl:'/art.svg'},{id:'netflix',name:'Netflix',url:'https://www.netflix.com/browse',iconUrl:'/art.svg'},{id:'prime',name:'Prime Video',url:'https://www.primevideo.com',iconUrl:'/art.svg'}],playback:[{id:'42',title:'Fixture playing music',paused:false,position:25,duration:180}],upNext:[],settings:{voiceEnabled:false,voiceReplies:true,voiceLanguage:'auto',voiceMode:'wake',voiceMicrophone:-1,trigger:'guide',controllerBridge:true,openOnWake:true,castEnabled:false,airplayEnabled:false},voice:{microphones:[{id:0,name:'Fixture microphone'}]},controllers:[{slot:0,battery:'wired'}],devices:{audio:[{id:'fixture',name:'Fixture speakers',connected:true,canControl:true}],bluetooth:[]},browserIntegration:{connected:true},recommendations:{}};
for(const service of ['youtube','netflix','prime'])fixture.recommendations[service]={updated:Date.now()/1000,partial:false,rows:[{title:'Fixture shelf',items:Array.from({length:24},(_,i)=>({id:service+i,title:'Fixture '+service+' title '+i,url:service==='youtube'?'https://www.youtube.com/watch?v=abcdefgh'+String(i).padStart(3,'0'):'https://www.'+(service==='prime'?'primevideo':'netflix')+'.com/title/'+i,artwork:'/art.svg'}))}]};
const actions=[],events=new Set(),paths=[];
const server=http.createServer(async(req,res)=>{
 const url=new URL(req.url,'http://localhost');paths.push(url.pathname);let body='';for await(const chunk of req)body+=chunk;const data=body?JSON.parse(body):{};
 if(url.pathname==='/api/events'){res.writeHead(200,{'Content-Type':'text/event-stream'});res.write(': fixture\n\n');events.add(res);req.on('close',()=>events.delete(res));return;}
 if(url.pathname.startsWith('/api/')){
  let result={ok:true};if(url.pathname==='/api/state')result=fixture;if(url.pathname==='/api/playback')result=fixture.playback;
  if(url.pathname==='/api/core'){if(req.method==='POST'){actions.push({...data,status:'completed',ok:true});result={...data,status:'accepted',ok:true};}else {await new Promise(resolve=>setTimeout(resolve,350));result=actions;}}
  if(url.pathname==='/api/voice/claim')result={commands:[]};if(url.pathname==='/api/cast/claim')result={url:null};
  if(url.pathname==='/api/keyboard')result={ok:true,url:'http://192.0.2.1:47924/k/FIXTURE'};
  if(url.pathname==='/api/stream/start')result={ok:true,session:'fixture-stream',url:'http://192.0.2.1:47924/view#code=FIXTURE'};
  if(url.pathname==='/api/stream/poll')result={ok:true,joined:false,messages:[],cursor:0};
  res.writeHead(200,{'Content-Type':'application/json'});res.end(JSON.stringify(result));return;
 }
 if(url.pathname==='/art.svg'){res.writeHead(200,{'Content-Type':'image/svg+xml'});res.end('<svg xmlns="http://www.w3.org/2000/svg" width="640" height="360"><rect width="640" height="360" fill="#30445e"/><text x="40" y="200" fill="white" font-size="40">Fixture artwork</text></svg>');return;}
 const file=path.resolve(root,'.'+(url.pathname==='/'?'/index.html':url.pathname));if(!file.startsWith(root) || !fs.existsSync(file)){res.writeHead(404);res.end();return;}
 const type=file.endsWith('.js')?'text/javascript':file.endsWith('.css')?'text/css':file.endsWith('.svg')?'image/svg+xml':'text/html';res.writeHead(200,{'Content-Type':type});res.end(fs.readFileSync(file));
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const browser=await chromium.launch({headless:true,executablePath:'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'});const page=await browser.newPage({viewport:{width:1920,height:1080}}),errors=[];
page.on('pageerror',error=>errors.push(error.message));
for(const file of ['css/app.css','js/focus.js'])assert.equal(fs.readFileSync(path.join(root,file),'utf8'),fs.readFileSync(new URL('../../ui/'+file,import.meta.url),'utf8'),file+' preserves original UI');
await page.addInitScript(()=>{
 sessionStorage.setItem('bwos.intro','1');
 globalThis.fixturePad={index:0,id:'Xbox simulated standard controller',connected:true,mapping:'standard',buttons:Array.from({length:17},()=>({pressed:false,value:0})),axes:[0,0]};
 Object.defineProperty(navigator,'getGamepads',{value:()=>[fixturePad]});document.hasFocus=()=>true;
});
const pulse=async i=>{await page.evaluate(i=>fixturePad.buttons[i]={pressed:true,value:1},i);await page.waitForTimeout(90);await page.evaluate(i=>fixturePad.buttons[i]={pressed:false,value:0},i);await page.waitForTimeout(90);};
const selected=()=>page.locator('.is-focused').evaluate(el=>el.dataset.key || el.dataset.tab || el.dataset.kind);
const current=()=>page.locator('#tabs .active').getAttribute('data-tab');
async function remote(message){return page.evaluate(data=>new Promise(resolve=>{const inputId=crypto.randomUUID();const listener=e=>{if(e.data?.type==='bwos-input-result' && e.data.inputId===inputId){removeEventListener('message',listener);resolve(e.data.result);}};addEventListener('message',listener);window.postMessage({type:'core-input',inputId,...data},location.origin);}),message);}
try{
 await page.goto('http://127.0.0.1:'+server.address().port+'/#t='+'a'.repeat(32));
 await page.waitForFunction(()=>!document.body.classList.contains('booting') && document.querySelector('#home-rows .is-focused'));
 assert.equal(await selected(),'fixture:0','Initial card selected without a mouse');
 await page.screenshot({path:path.join(output,'restored-home.png')});
 await pulse(0);await page.waitForFunction(()=>document.querySelector('#launch.show'));
 assert.equal(actions.at(-1).action,'launch','A activates highlighted card with native DOM focus on body');
 await pulse(1);assert.equal(await page.locator('#launch.show').count(),0,'B dismisses overlay');await page.waitForTimeout(500);assert.equal(await page.locator('#launch.show').count(),0,'Late acknowledgement leaves dismissed overlay alone');
 await pulse(15);assert.equal(await selected(),'fixture:1','D-pad walks shelf');await pulse(14);assert.equal(await selected(),'fixture:0');
 await page.evaluate(()=>fixturePad.axes=[0.8,0]);await page.waitForTimeout(90);await page.evaluate(()=>fixturePad.axes=[0,0]);await page.waitForTimeout(90);assert.equal(await selected(),'fixture:1','Analog stick walks shelf');
 await page.evaluate(async()=>{globalThis.fixtureActions=[];(await import('/js/input.js')).input.on(a=>fixtureActions.push(a));});
 await pulse(15);const before=await selected();await page.keyboard.press('ArrowRight');assert.equal(await selected(),before,'Steam synthetic key does not select twice');
 await page.evaluate(()=>fixturePad.buttons[0]={pressed:true,value:1});await page.waitForTimeout(500);assert.equal(await page.evaluate(()=>fixtureActions.filter(a=>a==='accept').length),1,'Held A emits once');
 await page.evaluate(()=>fixturePad.buttons[0]={pressed:false,value:0});await page.waitForTimeout(90);await pulse(1);
 for(const tab of ['foryou','games','watch','devices','settings','home']){await pulse(5);assert.equal(await current(),tab,'Right bumper opens '+tab);assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);await page.screenshot({path:path.join(output,'restored-'+tab+'.png')});}
 await pulse(4);assert.equal(await current(),'settings','Left bumper wraps');
 await page.evaluate(async()=>{fixturePad.id='Nintendo Pro Controller';(await import('/js/input.js')).input.swapAB=true;});await pulse(0);assert.equal(await page.locator('.is-focused').getAttribute('data-tab'),'settings','Swapped A acts as Back');await pulse(1);assert.equal(await page.locator('.is-focused').getAttribute('data-tab'),null,'Swapped B enters tab content');assert.equal(await page.evaluate(async()=>(await import('/js/input.js')).input.family),'nintendo');
 await page.evaluate(async()=>{fixturePad.id='DualSense Wireless Controller';(await import('/js/input.js')).input.swapAB=false;});await pulse(5);assert.equal(await current(),'home');assert.equal(await page.evaluate(async()=>(await import('/js/input.js')).input.family),'playstation');
 const povBefore=await selected();await page.evaluate(()=>{fixturePad.mapping='';fixturePad.axes=[0,0,0,0,0,0,0,0,0,-0.428];});await page.waitForTimeout(90);assert.notEqual(await selected(),povBefore,'Generic DirectInput POV advances shelf');await page.evaluate(()=>fixturePad.axes[9]=3.28);await page.waitForTimeout(90);
 assert.deepEqual(await remote({kind:'navigate',action:'home'}),{ok:true});
 await page.locator('#user').click();await page.locator('#modal.show input').waitFor();await remote({kind:'text',action:'clear'});assert.deepEqual(await remote({kind:'text',value:'हिंदी 🎮'}),{ok:true});assert.equal(await page.locator('#modal input').inputValue(),'हिंदी 🎮');await remote({kind:'text',action:'backspace'});assert.equal(await page.locator('#modal input').inputValue(),'हिंदी ');
 await remote({kind:'navigate',action:'back'});assert.equal(await page.locator('#modal.show').count(),0);assert.equal((await remote({kind:'text',value:'wrong field'})).ok,false,'Text cannot target a card');
 assert.deepEqual(errors,[]);console.log('PASS: original Liquid Glass CSS/focus engine; initial selection; A/B; D-pad; analog stick; bumpers; held-button edges; Steam duplicate suppression; Nintendo swap; Sony hints; generic POV; six screens; Unicode phone input. Physical controller verification still required.');
}finally{await browser.close();for(const e of events)e.end();await new Promise(resolve=>server.close(resolve));}
