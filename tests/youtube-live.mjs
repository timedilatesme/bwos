// Optional live-provider smoke test. Uses production UI/extension files, a disposable
// loopback host and a fresh Edge profile; never uses the signed-in BWOS profile.
// Requires internet, Edge, and Playwright on NODE_PATH. Not a deterministic CI test.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {createRequire} from 'node:module';
const {chromium}=createRequire(import.meta.url)('playwright');
const root=fileURLToPath(new URL('../',import.meta.url));
const output=path.join(root,'dist/verification/youtube-live');
fs.mkdirSync(output,{recursive:true});
const token='a'.repeat(32), video='jNQXAC9IVRw', mode=process.env.BWOS_YOUTUBE_INTERFACE==='web'?'web':'tv', players=new Map(), commands=[], clients=new Set();
const state={version:'live-test',user:{name:'Test player'},games:[],controllers:[],forYou:[],videos:[],recommendations:{},
  watch:[{id:'youtube',name:'YouTube',section:'stream',url:'https://www.youtube.com/tv',color:'#ff0033'}],
  upNext:[{id:'live-video',app:'youtube',title:'Public playback test',url:'https://www.youtube.com/watch?v='+video,position:1,duration:19}],
  settings:{trigger:'guide',youtubeInterface:mode,blankAfter:0,voiceEnabled:false},devices:{audio:[],bluetooth:[]},voice:{microphones:[]}};
state.recommendations.youtube={updated:Date.now()/1000,collector:3,rows:[{id:'fixture',title:'Test videos',items:Array.from({length:48},(_,i)=>({id:String(i).padStart(11,'0'),title:'Test video '+i,url:'https://www.youtube.com/watch?v='+String(i).padStart(11,'0')}))}]};
function broadcast(name){for(const client of clients)client.write('event: '+name+'\ndata: {}\n\n');}
const server=http.createServer(async(req,res)=>{
  try {
    const url=new URL(req.url,'http://localhost');
    const json=(value,status=200)=>{res.writeHead(status,{'Content-Type':'application/json'});res.end(JSON.stringify(value));};
    if(url.pathname.startsWith('/api/') && req.headers['x-bwos-token']!==token && url.searchParams.get('t')!==token)return json({ok:false},403);
    if(url.pathname==='/api/events'){
      res.writeHead(200,{'Content-Type':'text/event-stream'});res.write('event: hello\ndata: {}\n\n');clients.add(res);res.on('close',()=>clients.delete(res));return;
    }
    let raw='';for await(const chunk of req)raw+=chunk;
    const data=JSON.parse(raw || '{}');
    if(url.pathname==='/api/state')return json({...state,playback:[...players.values()]});
    if(url.pathname==='/api/playback')return json([...players.values()]);
    if(url.pathname==='/api/playback/report'){
      if(data.ended)players.delete(data.id);else players.set(data.id,data);
      broadcast('playback');return json({ok:true});
    }
    if(url.pathname==='/api/playback/control'){commands.push(data);return json({ok:true});}
    if(url.pathname==='/api/playback/claim')return json({commands:commands.splice(0)});
    if(url.pathname==='/api/voice')return json(state.voice);
    if(url.pathname==='/api/voice/service')return json({enabled:false,commands:[]});
    if(url.pathname==='/api/progress/lookup')return json({});
    if(url.pathname.startsWith('/api/'))return json({ok:true});
    const ui=path.join(root,'ui'),file=path.resolve(ui,'.'+(url.pathname==='/'?'/index.html':url.pathname));
    if(!file.startsWith(ui+path.sep) || !fs.existsSync(file)){res.writeHead(404);res.end();return;}
    res.writeHead(200,{'Content-Type':{'.html':'text/html','.js':'text/javascript','.css':'text/css','.svg':'image/svg+xml','.png':'image/png'}[path.extname(file)] || 'application/octet-stream'});
    res.end(fs.readFileSync(file));
  } catch {res.writeHead(500);res.end();}
});
server.on('upgrade',(_req,socket)=>socket.destroy());
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const extension=path.join(root,'extensions/bwos-tv');
let context;
try {
  context=await chromium.launchPersistentContext(path.join(output,'profile-'+Date.now()),{
    channel:'msedge',headless:true,viewport:{width:1280,height:720},
    args:['--load-extension='+extension,'--disable-extensions-except='+extension,'--disable-features=DisableLoadExtensionCommandLineSwitch','--autoplay-policy=no-user-gesture-required','--mute-audio']
  });
  await context.addInitScript(()=>{
    if(location.hostname==='127.0.0.1') {sessionStorage.setItem('bwos.intro','1');localStorage.setItem('bwos.ambient','off');}
  });
  const home=await context.newPage(),homeErrors=[];
  home.on('pageerror',error=>homeErrors.push(error.message));
  await home.goto(`http://127.0.0.1:${server.address().port}/#t=${token}`);
  await home.waitForFunction(()=>!document.body.classList.contains('booting'));
  const worker=context.serviceWorkers()[0] || await context.waitForEvent('serviceworker');
  const playerReady=context.waitForEvent('page',{predicate:page=>page!==home,timeout:15000});
  // Exercise the same focus/activation path as a controller.
  await home.evaluate(async()=>{const {input}=await import('/js/input.js');await input.dispatch('home');await input.dispatch('accept');});
  const player=await playerReady;
  await player.waitForURL(mode==='tv'?'https://www.youtube.com/tv#/watch?v='+video:'https://www.youtube.com/watch?v='+video,{timeout:30000});
  if(mode==='tv') {
    await player.waitForTimeout(4000);
    for(const label of ['Get started','Watch as guest']) {
      const button=player.getByText(label,{exact:true}).first();
      if(await button.isVisible()){await button.click();await player.waitForTimeout(1500);}
    }
    console.log(JSON.stringify({tvStartup:await player.evaluate(()=>({url:location.href,text:document.body.innerText.slice(0,600),ua:navigator.userAgent}))}));
  }
  await player.waitForFunction(()=>{const v=document.querySelector('video');return v && v.readyState>=3 && !v.paused && v.currentTime>1;},null,{timeout:45000});
  console.log('PASS: production UI and extension open a saved video in the real '+mode+' player');
  const before=await player.locator('video').evaluate(video=>video.currentTime);
  // Playwright does not recognize F24 by name; send the browser's actual key code.
  const keys=await context.newCDPSession(player);
  for(const type of ['keyDown','keyUp'])await keys.send('Input.dispatchKeyEvent',{type,key:'F24',code:'F24',windowsVirtualKeyCode:135,nativeVirtualKeyCode:135});
  await keys.detach();
  await home.waitForFunction(()=>document.querySelector('#tabs .active')?.dataset.tab==='home');
  const active=async()=>worker.evaluate(async()=>{const tabs=await chrome.tabs.query({active:true});return tabs.map(tab=>tab.url);});
  for(let i=0;i<30 && !(await active()).some(url=>url.startsWith(home.url().split('#')[0]));i++)await new Promise(resolve=>setTimeout(resolve,100));
  assert.ok((await active()).some(url=>url.startsWith(home.url().split('#')[0])),'F24 activates the retained Home tab');
  await player.waitForFunction(time=>{const v=document.querySelector('video');return v && !v.paused && v.currentTime>time+.5;},before,{timeout:5000});
  assert.equal(context.pages().filter(page=>page.url().startsWith(mode==='tv'?'https://www.youtube.com/tv':'https://www.youtube.com/watch')).length,1,'Home preserves one live YouTube player');
  console.log('PASS: F24 returns Home while real video playback continues');
  await home.locator('#now-playing [data-action="pause"]').click({timeout:10000});
  await player.waitForFunction(()=>document.querySelector('video')?.paused,null,{timeout:5000});
  await home.locator('#now-playing [data-action="resume"]').click({timeout:10000});
  await player.waitForFunction(()=>{const v=document.querySelector('video');return v && !v.paused;},null,{timeout:5000});
  console.log('PASS: BWOS playback controls pause and resume the real provider video');
  assert.deepEqual(homeErrors,[],'Home has no unhandled errors');
  await home.screenshot({path:path.join(output,'home.png')});
  await player.screenshot({path:path.join(output,'player.png')});
} finally {
  if(context)await context.close();
  for(const client of clients)client.end();
  await new Promise(resolve=>server.close(resolve));
}
