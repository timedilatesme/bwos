// Browser regressions against the production UI and a disposable local host.
// Run with Playwright available on NODE_PATH: node tests/ui-stability.mjs
import assert from 'node:assert/strict';
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {createRequire} from 'node:module';
const {chromium} = createRequire(import.meta.url)('playwright');
const root = fileURLToPath(new URL('../ui/', import.meta.url));
const output = fileURLToPath(new URL('../dist/verification/stability/', import.meta.url));
fs.mkdirSync(output, {recursive:true});
const state = {
  version:'test', user:{name:'Test player'},
  games:Array.from({length:40}, (_,i)=>({key:'test:'+i,name:'Test game '+i,source:'custom',art:{}})),
  watch:['youtube','netflix','prime'].map(id=>({id,name:id,section:'stream',url:id==='youtube'?'https://www.youtube.com/tv':'https://www.'+(id==='prime'?'primevideo':id)+'.com/',color:'#555'})),
  settings:{trigger:'guide',controllerBridge:true,showTools:false,airplayInstalled:false,voiceEnabled:false,blankAfter:0},
  controllers:[], forYou:[], upNext:[], videos:[], recommendations:{},
  devices:{audio:[],bluetooth:[]}, voice:{microphones:[]},
  playback:[{id:'42',title:'Test music',url:'https://www.youtube.com/tv',paused:false,position:1,duration:180}],
};
const clients = new Set(), requests = [], settings = [];
let stateCalls=0, inFlight=0, maxInFlight=0, delay=0;
function broadcast(name){for(const client of clients)client.write(`event: ${name}\ndata: {}\n\n`);}
const server=http.createServer(async(req,res)=>{
  const url=new URL(req.url,'http://localhost');
  if(url.pathname==='/api/events'){
    res.writeHead(200,{'Content-Type':'text/event-stream'});res.write('event: hello\ndata: {}\n\n');clients.add(res);res.on('close',()=>clients.delete(res));return;
  }
  let body='';for await(const chunk of req)body+=chunk;
  const data=JSON.parse(body || '{}');requests.push(url.pathname);
  const json=(value,status=200)=>{res.writeHead(status,{'Content-Type':'application/json'});res.end(JSON.stringify(value));};
  if(url.pathname==='/test-art.svg'){res.writeHead(200,{'Content-Type':'image/svg+xml'});res.end('<svg xmlns="http://www.w3.org/2000/svg" width="640" height="360"><rect width="640" height="360" fill="#253d58"/></svg>');return;}
  if(url.pathname==='/api/state'){
    stateCalls++;inFlight++;maxInFlight=Math.max(maxInFlight,inFlight);
    await new Promise(resolve=>setTimeout(resolve,delay));inFlight--;return json(state);
  }
  if(url.pathname==='/api/settings'){settings.push(data);state.settings[data.key]=data.value;return json({ok:true});}
  if(url.pathname==='/api/playback')return json(state.playback);
  if(url.pathname==='/api/playback/control')return json({ok:false,error:'Test player unavailable'},503);
  if(url.pathname==='/api/voice')return json(state.voice);
  if(url.pathname.startsWith('/api/'))return json({ok:true});
  const file=path.resolve(root,'.'+(url.pathname==='/'?'/index.html':url.pathname));
  if(!file.startsWith(root) || !fs.existsSync(file)){res.writeHead(404);res.end();return;}
  const types={'.js':'text/javascript','.css':'text/css','.svg':'image/svg+xml','.png':'image/png','.html':'text/html'};
  res.writeHead(200,{'Content-Type':types[path.extname(file)] || 'application/octet-stream'});res.end(fs.readFileSync(file));
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const browser=await chromium.launch({headless:true,...(process.env.BWOS_BROWSER?{executablePath:process.env.BWOS_BROWSER}:{channel:'msedge'})});
const page=await browser.newPage({viewport:{width:1920,height:1080}}), errors=[], failures=[];
page.on('pageerror',error=>errors.push(error.message));
await page.addInitScript(()=>{
  sessionStorage.setItem('bwos.intro','1');localStorage.setItem('bwos.ambient','off');
  globalThis.testPad={index:0,id:'Xbox test controller',connected:true,mapping:'standard',axes:[0,0],buttons:Array.from({length:17},()=>({pressed:false,value:0}))};
  Object.defineProperty(navigator,'getGamepads',{value:()=>[testPad]});document.hasFocus=()=>true;
  addEventListener('message',event=>{
    if(event.source!==window || event.origin!==location.origin || !['bwos-open-service','bwos-sync-recommendations','bwos-search-recommendations'].includes(event.data?.type))return;
    if(event.data.type==='bwos-open-service')(globalThis.openedServices ||= []).push(event.data.url);
    window.postMessage({type:'bwos-open-result',requestId:event.data.requestId,result:{ok:true,count:0,rows:[]}},location.origin);
  });
});
async function check(name,fn){try{await fn();console.log('PASS: '+name);}catch(error){failures.push(name+': '+error.message);console.error('FAIL: '+name+': '+error.message);}}
async function dispatch(action){await page.evaluate(async action=>(await import('/js/input.js')).input.dispatch(action),action);}
const focused=()=>page.locator('.is-focused').evaluate(el=>el.dataset.key || el.dataset.tab);
const selectTab=async name=>{await page.locator(`#tabs [data-tab="${name}"]`).click();};
async function refresh(name='library') {
  const response=page.waitForResponse(response=>new URL(response.url()).pathname==='/api/state');
  broadcast(name);await (await response).finished();
  await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
}
try{
  await page.goto(`http://127.0.0.1:${server.address().port}/#t=${'a'.repeat(32)}`);
  await page.waitForFunction(()=>!document.body.classList.contains('booting') && document.querySelector('#home-rows .is-focused'));
  await check('controller navigation survives disconnect/reconnect',async()=>{
    try {
      await page.evaluate(async()=>{globalThis.padActions=[];(await import('/js/input.js')).input.on((a,s)=>padActions.push([a,s]));});
      await dispatch('right');assert.equal(await focused(),'test:1');
      await page.evaluate(()=>testPad.buttons[15]={pressed:true,value:1});
      await page.waitForFunction(()=>padActions.filter(([a,s])=>a==='right' && s==='pad').length===1);
      await page.evaluate(()=>{testPad.connected=false;return new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));});
      const before=await focused();
      await page.evaluate(()=>testPad.connected=true);
      await page.waitForFunction(()=>padActions.filter(([a,s])=>a==='right' && s==='pad').length===2);
      assert.notEqual(await focused(),before,'A reconnected held D-pad must emit a fresh press');
    } finally { await page.evaluate(()=>{testPad.connected=true;testPad.buttons[15]={pressed:false,value:0};}); }
  });
  await check('unchanged refresh retains shelf nodes and scroll positions',async()=>{
    await dispatch('home');await dispatch('right');await dispatch('right');
    await page.evaluate(()=>globalThis.savedCard=document.querySelector('#home-rows .is-focused'));
    await refresh();
    assert.equal(await page.evaluate(()=>savedCard.isConnected),true,'A state heartbeat should not replace unchanged cards');
  });
  await check('focused cards settle without perpetual movement',async()=>{
    await dispatch('home');await page.waitForTimeout(500);
    const before=await page.locator('#home-rows .is-focused').boundingBox();
    await page.waitForTimeout(500);
    const after=await page.locator('#home-rows .is-focused').boundingBox();
    assert.ok(Math.abs(after.y-before.y)<.1 && Math.abs(after.height-before.height)<.1,'A resting focused card stays in place');
  });
  await check('watch history refreshes Home independently of the game library',async()=>{
    state.upNext=[{id:'history-test',app:'youtube',title:'New history entry',url:'https://www.youtube.com/watch?v=00000000000',position:25,duration:100}];
    await refresh();
    const card=page.locator('#home-rows [data-kind="resume"][data-id="history-test"]');
    assert.ok((await card.innerText()).includes('New history entry'));
    state.upNext[0].position=90;await refresh();
    assert.equal(await card.locator('.progress i').evaluate(el=>el.style.width),'90%','Updated watch progress reaches Home');
    state.upNext=[];await refresh();assert.equal(await card.count(),0,'Dismissed history disappears without a library change');
  });
  await check('settings toggles work without AirPlay installed',async()=>{
    await selectTab('settings');await page.getByText('Controller',{exact:true}).click();
    await page.locator('[data-setting="controllerBridge"]').click();await page.waitForTimeout(100);
    assert.ok(settings.some(x=>x.key==='controllerBridge' && x.value===false));
    await page.getByText('Library',{exact:true}).click();
    await page.locator('[data-setting="showTools"]').click();await page.waitForTimeout(100);
    assert.ok(settings.some(x=>x.key==='showTools' && x.value===true));
  });
  await check('YouTube keeps TV as default and Standard selectable',async()=>{
    await selectTab('watch');
    await page.locator('#view-watch [data-kind="watch"][data-id="youtube"]').click();
    await page.waitForFunction(()=>openedServices?.length===1);
    assert.equal(await page.evaluate(()=>openedServices[0]),'https://www.youtube.com/tv','Existing installations retain the TV interface');
    await selectTab('settings');
    if(!await page.locator('[data-setting="youtubeInterface"]').isVisible())await page.getByText('Connections',{exact:true}).click();
    await page.locator('[data-setting="youtubeInterface"]').click();
    await page.waitForFunction(()=>document.querySelector('[data-setting="youtubeInterface"] .s-value')?.textContent.includes('Standard'));
    assert.ok(settings.some(x=>x.key==='youtubeInterface' && x.value==='web'));
    await selectTab('watch');await page.locator('#view-watch [data-kind="watch"][data-id="youtube"]').click();
    await page.waitForFunction(()=>openedServices.length===2);
    assert.equal(await page.evaluate(()=>openedServices[1]),'https://www.youtube.com/');
    await selectTab('settings');
    if(!await page.locator('[data-setting="youtubeInterface"]').isVisible())await page.getByText('Connections',{exact:true}).click();
    await page.locator('[data-setting="youtubeInterface"]').click();
    await page.waitForFunction(()=>document.querySelector('[data-setting="youtubeInterface"] .s-value')?.textContent.includes('TV'));
  });
  await check('refresh bursts have one active state request',async()=>{
    delay=200;maxInFlight=0;const before=stateCalls;
    for(let i=0;i<12;i++)broadcast('library');
    await page.waitForTimeout(700);delay=0;
    assert.equal(maxInFlight,1);assert.ok(stateCalls-before<=2,'Bursts need at most one trailing refresh');
  });
  await check('modal focus stays inside modal through background refresh',async()=>{
    await page.locator('#user').click();await page.locator('#modal.show input').fill('Draft name');
    state.games[0].name='Updated game';await refresh();
    assert.equal(await page.locator('#modal.show input').inputValue(),'Draft name');
    assert.equal(await page.locator('#modal.show .is-focused').count(),1);
    await dispatch('back');assert.equal(await page.locator('.is-focused').count(),1);
  });
  await check('closing a modal restores a replaced game card',async()=>{
    await dispatch('home');const key=await focused();
    assert.equal(await page.locator('.is-focused').getAttribute('data-kind'),'game','Home selects a game card before opening options');
    await dispatch('options');
    await page.locator('#modal.show').waitFor();state.games.push({key:'new-game',name:'New game',source:'custom',art:{}});
    await refresh();await dispatch('back');
    assert.equal(await focused(),key);assert.equal(await page.locator('#view-home .is-focused').count(),1);
  });
  await check('large recommendation refreshes retain the grid and text selection',async()=>{
    state.recommendations.youtube={updated:Date.now()/1000,collector:3,rows:[{id:'home',title:'Videos',items:Array.from({length:360},(_,i)=>({id:'feed:youtube:'+i,title:'Test video '+i,url:'https://www.youtube.com/watch?v='+String(i).padStart(11,'0'),artwork:'/test-art.svg',offers:[{app:'youtube',name:'youtube',url:'https://www.youtube.com/watch?v='+String(i).padStart(11,'0')}]}))}]};
    await refresh();await selectTab('foryou');
    await page.locator('.service-switcher [data-id="youtube"]').click();
    assert.equal(await page.locator('.media-video').count(),360);
    await page.evaluate(()=>globalThis.savedVideo=document.querySelector('.media-video'));
    await refresh();assert.equal(await page.evaluate(()=>savedVideo.isConnected),true);
    await page.locator('#hub-query').focus();await page.locator('#hub-query').fill('Test');
    await page.locator('#hub-query').evaluate(el=>el.setSelectionRange(1,3));
    state.recommendations.youtube.updated++;await refresh();
    assert.deepEqual(await page.locator('#hub-query').evaluate(el=>[el.value,el.selectionStart,el.selectionEnd]),['Test',1,3]);
    await page.locator('#hub-query').fill('');
    await page.locator('.media-video').first().click();
    await page.waitForFunction(()=>openedServices?.length===3);
    assert.equal(await page.evaluate(()=>openedServices[2]),'https://www.youtube.com/tv#/watch?v=00000000000','Video cards also use the selected TV player');
  });
  await check('failed async playback controls show a recoverable error',async()=>{
    await page.locator('#now-playing [data-action="pause"]').click();
    await page.waitForFunction(()=>document.querySelector('#toasts').textContent.includes('Test player unavailable'),null,{timeout:5000});
    assert.deepEqual(errors,[],'No unhandled promise rejections');
  });
  await check('all six screens support controller traversal without errors',async()=>{
    await page.evaluate(()=>document.querySelector('#toasts').replaceChildren());
    for(const name of ['home','foryou','games','watch','devices','settings']){
      await selectTab(name);await dispatch('accept');
      assert.equal(await page.locator('#tabs .active').getAttribute('data-tab'),name,'Select enters the selected screen');
      assert.ok(await page.locator('.is-focused').count());assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
      await page.waitForTimeout(450);await page.screenshot({path:path.join(output,name+'.png')});
      await dispatch('down');await dispatch('right');await dispatch('up');await dispatch('back');
    }
    assert.deepEqual(errors,[]);
  });
}finally{
  await browser.close();for(const client of clients)client.end();await new Promise(resolve=>server.close(resolve));
}
assert.deepEqual(failures,[]);
