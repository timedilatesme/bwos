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
const fixture={version:'fixture',user:{name:'Fixture verification'},games:Array.from({length:8},(_,i)=>({key:'fixture:'+i,name:'Fixture game '+i,source:'Fixture',art:{header:'/art.svg'}})),watch:[{id:'youtube',name:'YouTube',url:'https://www.youtube.com/tv',iconUrl:'/art.svg'},{id:'netflix',name:'Netflix',url:'https://www.netflix.com/browse',iconUrl:'/art.svg'},{id:'prime',name:'Prime Video',url:'https://www.primevideo.com',iconUrl:'/art.svg'}],playback:[{id:'42',title:'Fixture playing music',paused:false,position:25,duration:180}],upNext:[],settings:{voiceEnabled:false,voiceReplies:true,voiceLanguage:'auto',voiceMode:'wake',voiceMicrophone:-1,trigger:'guide',controllerBridge:true,openOnWake:true,castEnabled:false,airplayEnabled:false},voice:{microphones:[{id:0,name:'Fixture microphone'}]},controllers:[{slot:0,battery:'wired'}],devices:{audio:[{id:'fixture',name:'Fixture speakers',connected:true,canControl:true}],bluetooth:[]},browserIntegration:{connected:true},recommendations:{}};
for(const service of ['youtube','netflix','prime'])fixture.recommendations[service]={updated:Date.now()/1000,partial:false,rows:[{title:'Fixture shelf',items:Array.from({length:24},(_,i)=>({id:service+i,title:'Fixture '+service+' title '+i,url:service==='youtube'?'https://www.youtube.com/watch?v=abcdefgh'+String(i).padStart(3,'0'):'https://www.'+(service==='prime'?'primevideo':'netflix')+'.com/title/'+i,artwork:'/art.svg'}))}]};
const actions=[],events=new Set(),paths=[];
const server=http.createServer(async(req,res)=>{
 const url=new URL(req.url,'http://localhost');paths.push(url.pathname);let body='';for await(const chunk of req)body+=chunk;const data=body?JSON.parse(body):{};
 if(url.pathname==='/api/events'){res.writeHead(200,{'Content-Type':'text/event-stream'});res.write(': fixture\n\n');events.add(res);req.on('close',()=>events.delete(res));return;}
 if(url.pathname.startsWith('/api/')){
  let result={ok:true};if(url.pathname==='/api/state')result=fixture;if(url.pathname==='/api/playback')result=fixture.playback;
  if(url.pathname==='/api/core'){if(req.method==='POST'){result={...data,status:'completed',ok:true};actions.push(result);}else result=actions;}
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
await page.addInitScript(()=>{
 addEventListener('message',e=>{if(e.data?.type==='bwos-search-recommendations')window.postMessage({type:'bwos-open-result',requestId:e.data.requestId,result:{ok:true,rows:[{title:'Fixture search',items:[{title:e.data.query,url:'https://www.youtube.com/watch?v=abcdefghijk',artwork:'/art.svg'}]}]}},location.origin);});
 // Generated tracks only: no OS capture picker, system audio, or microphone is touched.
 navigator.mediaDevices.getDisplayMedia=async options=>{globalThis.fixtureCapture={options,active:navigator.userActivation.isActive};const canvas=document.createElement('canvas');canvas.width=640;canvas.height=360;canvas.getContext('2d').fillRect(0,0,640,360);const video=canvas.captureStream(10),audioContext=new AudioContext(),destination=audioContext.createMediaStreamDestination(),oscillator=audioContext.createOscillator();oscillator.connect(destination);oscillator.start();globalThis.fixtureAudio=audioContext;return new MediaStream([...video.getTracks(),...destination.stream.getTracks()]);};
});
try{
 await page.goto('http://127.0.0.1:'+server.address().port+'/#t='+'a'.repeat(32));await page.getByText('Your evening, together.').waitFor();
 await page.locator('.card img').first().waitFor();await page.waitForFunction(()=>document.querySelector('.card img')?.naturalWidth>0);await page.screenshot({path:path.join(output,'home-fixture.png'),fullPage:true});
 for(const name of ['For You','Games','Apps','Devices','Settings']){await page.locator('nav').getByRole('button',{name,exact:true}).click();await page.screenshot({path:path.join(output,name.toLowerCase().replaceAll(' ','-')+'-fixture.png'),fullPage:true});assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true,name+' does not overflow');}
 await page.locator('nav').getByRole('button',{name:'For You',exact:true}).click();await page.getByRole('button',{name:'Search',exact:true}).click();assert.equal(await page.locator('#keyboard').evaluate(d=>d.open),true);await page.evaluate(()=>window.postMessage({type:'core-input',inputId:'fixture-phone',kind:'text',value:'हिंदी गाने'},location.origin));await page.waitForFunction(()=>document.getElementById('query').value==='हिंदी गाने');await page.locator('#keyboard').getByRole('button',{name:'Search',exact:true}).click();await page.getByText('हिंदी गाने',{exact:true}).first().waitFor();
 await page.getByRole('button',{name:'Return to recommendations'}).click();await page.getByRole('button',{name:'Show more (24)'}).first().click();assert.ok(await page.locator('.card').count()>36,'Paging exposes additional cards');
 const pause=page.locator('#cloud button').first();await pause.focus();const before=await page.locator('#cloud').evaluate(e=>e.children.length);fixture.playback[0].position=35;for(const e of events)e.write('event: playback\ndata: {}\n\n');await page.waitForTimeout(100);assert.equal(await pause.evaluate(e=>e===document.activeElement),true,'Progress preserves cloud focus');assert.equal(await page.locator('#cloud').evaluate(e=>e.children.length),before,'Exactly one stable cloud');
 await pause.click();assert.equal(actions.at(-1).kind,'playback');assert.equal(actions.at(-1).action,'toggle');
 await page.locator('nav').getByRole('button',{name:'Apps',exact:true}).click();await page.getByRole('button',{name:'YouTube',exact:true}).click();assert.equal(actions.at(-1).url,'https://www.youtube.com/tv');
 await page.locator('nav').getByRole('button',{name:'Settings',exact:true}).click();await page.getByRole('button',{name:'Pair phone remote'}).click();await page.locator('#details .qr svg').waitFor();assert.equal(await page.locator('#details .qr svg').count(),1,'Local QR renders');
 await page.locator('#detail-close').click();await page.getByRole('button',{name:'Stream entire Windows screen'}).click();await page.locator('#details .qr svg').waitFor();const capture=await page.evaluate(()=>fixtureCapture);assert.equal(capture.active,true,'Capture starts under Select user activation');assert.equal(capture.options.video.displaySurface,'monitor');assert.equal(capture.options.systemAudio,'include');await page.locator('#detail-close').click();await page.getByRole('button',{name:'Stop sharing',exact:true}).click();await page.waitForTimeout(100);assert.ok(paths.includes('/api/stream/stop'),'Stopping capture revokes sender session');await page.evaluate(()=>fixtureAudio.close());
 await page.locator('nav').getByRole('button',{name:'Games',exact:true}).click();await page.getByRole('button',{name:'Fixture game 0 Fixture',exact:true}).click();assert.equal(actions.at(-1).kind,'native');assert.equal(actions.at(-1).action,'launch');
 assert.deepEqual(errors,[]);console.log('PASS: six screens, layout, Unicode keyboard/remote text/search, paging, stable cloud, shared game/playback/open actions, QR, capture user activation + requested system audio, and sender revocation (generated fixture tracks)');
}finally{await browser.close();for(const e of events)e.end();await new Promise(resolve=>server.close(resolve));}
