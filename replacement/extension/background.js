// New core: explicit ownership, independent action lanes, bounded acknowledgements.
importScripts('kernel.js','discovery.js');
const {ActionKernel,SessionRegistry,providerUrl}=BwosCore;
const kernel=new ActionKernel();let registry,media={},persist=Promise.resolve(),polling=false,navEpoch=0;const openings=new Map();
const ready=(async()=>{
  const state=await chrome.storage.session.get(['ownership','media']);
  registry=new SessionRegistry(state.ownership);media=state.media || {};
  registry.reconcile(await chrome.tabs.query({}));
  // Interrupted collectors are never adopted by playback after worker restart.
  for(const id of Object.keys(registry.state.collectors).map(Number)) {await chrome.tabs.remove(id).catch(()=>{});registry.release(id);}
  await save();
  await chrome.declarativeNetRequest.updateDynamicRules({removeRuleIds:[1]});
})();
function save(){const state=structuredClone({ownership:registry.state,media});persist=persist.catch(()=>{}).then(()=>chrome.storage.session.set(state));return persist;}
async function settings(){return chrome.storage.local.get(['home','token']);}
async function bwos(path,body={}){
  const {home,token}=await settings();if(!home || !token)return null;
  const response=await fetch(home+path,{method:'POST',headers:{'X-BWOS-Token':token,'Content-Type':'application/json'},body:JSON.stringify(body),signal:AbortSignal.timeout(3500)});
  if(!response.ok)throw Error('BWOS host unavailable');return response.json();
}
function sendToTab(id,message,frame,timeout=1000){return new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(Error('Provider response timed out')),timeout);chrome.tabs.sendMessage(id,message,frame).then(result=>{clearTimeout(timer);resolve(result);},error=>{clearTimeout(timer);reject(error);});});}
async function reportPlayer(id,player,ended=false){return bwos('api/playback/report',{...player.data,id:String(id),local:false,background:!!player.background,ended});}
function homeSender(sender,home){return sender.frameId===0 && sender.tab && new URL(sender.tab.url).origin===new URL(home).origin && sender.tab.windowId===registry.state.windowId;}
async function bind(sender){const {home}=await settings();if(!home || sender.frameId!==0 || !sender.tab || new URL(sender.tab.url).origin!==new URL(home).origin)throw Error('Untrusted shell');registry.bindHome(sender.tab);await save();return home;}
async function open(command,ctx){
  const epoch=navEpoch;
  try {
  const parsed=providerUrl(command.url);const {home}=await settings();if(!home || registry.state.homeId===null)throw Error('Shell not connected');
  const tabs=await chrome.tabs.query({windowId:registry.state.windowId});registry.reconcile(tabs);ctx.check();
  let tab=tabs.find(t=>t.id===registry.state.playback[parsed.provider]);
  if(tab){ctx.check();tab=await chrome.tabs.update(tab.id,{active:true,autoDiscardable:false,...(!parsed.landing && tab.url!==parsed.url?{url:parsed.url}:{})});}
  else {
    ctx.check();
    if(!openings.has(parsed.provider))openings.set(parsed.provider,chrome.tabs.create({windowId:registry.state.windowId,url:parsed.url,active:false}).then(async created=>{try{registry.ownPlayer(parsed.provider,created);await save();return created;}catch(error){registry.release(created.id);await chrome.tabs.remove(created.id).catch(()=>{});throw error;}}).finally(()=>openings.delete(parsed.provider)));
    tab=await openings.get(parsed.provider);ctx.check();
    if(epoch!==navEpoch)throw Error('Opening cancelled by Home');
    await chrome.tabs.update(tab.id,{active:true});
  }
  await chrome.tabs.update(tab.id,{autoDiscardable:false});
  if(tab.mutedInfo?.muted && tab.mutedInfo.reason==='extension' && tab.mutedInfo.extensionId===chrome.runtime.id)await chrome.tabs.update(tab.id,{muted:false});
  ctx.check();if(epoch!==navEpoch)throw Error('Opening cancelled by Home');await chrome.windows.update(tab.windowId,{state:'fullscreen',focused:true});
  if(media[tab.id]){media[tab.id].background=false;await save();await reportPlayer(tab.id,media[tab.id]);}
  return {ok:true,tabId:tab.id};
  } finally {
    if(epoch!==navEpoch && registry.state.homeId!==null) {
      await chrome.tabs.update(registry.state.homeId,{active:true}).catch(()=>{});
      await chrome.windows.update(registry.state.windowId,{state:'fullscreen',focused:true}).catch(()=>{});
    }
  }
}
async function home(command,ctx){
  const id=registry.state.homeId;if(id===null)throw Error('Retained Home is missing. Reopen BWOS.');
  const [active]=await chrome.tabs.query({active:true,windowId:registry.state.windowId});ctx.check();
  const prior=active && media[active.id],wasPlaying=prior?!prior.data.paused:!!active?.audible;
  // Activate before talking to provider content. A frozen renderer cannot delay Home.
  await chrome.tabs.update(id,{active:true,autoDiscardable:false});ctx.check();
  await chrome.windows.update(registry.state.windowId,{state:'fullscreen',focused:true});
  sendToTab(id,{type:'shell-home'},{frameId:0}).catch(()=>{});
  bwos('api/browser/context',{kind:'home',foreground:true}).catch(()=>{});
  if(active && registry.role(active.id)==='playback'){
    const player=media[active.id];if(player){player.background=true;await save();reportPlayer(active.id,player).catch(()=>{});}
    // Snapshot and continuity repair are separate, cancellable playback work.
    execute({id:'handoff:'+crypto.randomUUID(),kind:'handoff',target:String(active.id),keepPlaying:wasPlaying,timeout:1800}).catch(()=>{});
  }
  return {ok:true};
}
const allowedMedia=new Set(['pause','resume','toggle','focus','stop','retry','restart','seekForward','seekBack','nextMedia','previousMedia','subtitlesOn','subtitlesOff']);
async function playback(command,ctx){
  const id=Number(command.target),player=media[id];if(registry.role(id)!=='playback')throw Error('Playback session unavailable');
  if(command.kind==='handoff'){
    const snapshot=await sendToTab(id,{type:'media-snapshot'},player?{frameId:player.frameId}:undefined,650);ctx.check();
    if(snapshot?.data){media[id]={data:snapshot.data,frameId:snapshot.frameId || 0,background:true,seen:Date.now()};await save();}
    const current=media[id];if(current && command.keepPlaying && current.data.canControl!==false){const result=await sendToTab(id,{type:'media-handoff',keepPlaying:true},{frameId:current.frameId},900);ctx.check();if(result?.data)current.data=result.data;if(result?.ok===false)current.data.error='This provider paused on Home. Reopen it to continue.';await reportPlayer(id,current);}
    return {ok:true};
  }
  if(!allowedMedia.has(command.action))throw Error('Unsupported playback action');
  if(command.action==='focus'){ctx.check();await chrome.tabs.update(id,{active:true});await chrome.windows.update(registry.state.windowId,{state:'fullscreen',focused:true});if(player){player.background=false;await save();await reportPlayer(id,player);}return {ok:true};}
  if(command.action==='stop'){await home(command,ctx);ctx.check();await chrome.tabs.remove(id);registry.release(id);delete media[id];await save();if(player)await reportPlayer(id,player,true);return {ok:true};}
  if(command.action==='retry'){ctx.check();await chrome.tabs.reload(id);return {ok:true};}
  if(!player)throw Error('Wait for the provider player to load');
  return sendToTab(id,{type:'media-control',action:command.action,value:command.value || ''},{frameId:player.frameId},1800);
}
async function execute(command){
  await ready;
  const lane=command.kind==='home'?'navigation':command.kind==='open'?'open:'+providerUrl(command.url).provider:command.kind==='playback' || command.kind==='handoff'?'playback:'+command.target:'input';
  // Home cancels a stale opening action before it can reselect a tab.
  if(command.kind==='home'){navEpoch++;for(const [id,r] of kernel.records)if(!r.done && r.kind==='open')kernel.cancel(id);}
  if(command.kind==='playback' && ['pause','toggle','stop'].includes(command.action))for(const [id,r] of kernel.records)if(!r.done && r.kind==='handoff' && r.target===command.target)kernel.cancel(id);
  const result=kernel.run({...command,lane},async ctx=>{
    if(command.kind==='home')return home(command,ctx);
    if(command.kind==='open')return open(command,ctx);
    if(command.kind==='playback' || command.kind==='handoff')return playback(command,ctx);
    if(command.kind==='cancel'){kernel.cancel(command.target);return {ok:true};}
    if(command.kind==='navigate' || command.kind==='text'){
      const [tab]=await chrome.tabs.query({active:true,windowId:registry.state.windowId});ctx.check();
      if(!tab || !['home','playback'].includes(registry.role(tab.id)))throw Error('No owned surface');
      return sendToTab(tab.id,{type:'core-input',kind:command.kind,action:command.action,value:command.value},{frameId:0},1200);
    }
    throw Error('Unknown action');
  });
  const record=kernel.records.get(command.id);if(record){record.kind=command.kind;record.target=command.target;}
  return result;
}
async function mediaReport(msg,sender){
  const id=sender.tab?.id;if(registry.role(id)!=='playback' || !msg.data)return {ok:false};
  const old=media[id];if(old && old.frameId!==sender.frameId && (!old.data.paused || msg.data.paused) && Date.now()-old.seen<10000)return {ok:true};
  const player={data:{...msg.data,url:sender.tab.url},frameId:sender.frameId,background:!sender.tab.active,seen:Date.now()};media[id]=player;await save();await reportPlayer(id,player,!!msg.data.ended);return {ok:true};
}
async function poll(){
  if(polling)return;polling=true;
  try{await ready;if(registry.state.homeId===null)return;
    const batch=await bwos('api/core/claim');
    for(const command of batch?.commands || [])execute(command).then(result=>bwos('api/core/ack',result)).catch(()=>{});
    await bwos('api/browser/ping');
    // Compatibility ingress for retained native playback adapters; one kernel dispatch.
    const legacy=await bwos('api/playback/claim');
    for(const command of legacy?.commands || [])execute({id:crypto.randomUUID(),kind:'playback',target:command.id,action:command.action,value:command.value}).catch(()=>{});
    const [tab]=await chrome.tabs.query({active:true,windowId:registry.state.windowId});
    if(tab)await bwos('api/browser/context',{kind:registry.role(tab.id)==='home'?'home':providerUrl(tab.url).provider==='youtube'?'youtube':'service',foreground:(await chrome.windows.get(tab.windowId)).focused});
  }catch(error){console.warn('BWOS core poll',error.message);}finally{polling=false;}
}
setInterval(poll,200);
chrome.tabs.onRemoved.addListener(async id=>{await ready;const old=media[id];registry.release(id);delete media[id];await save();if(old)await reportPlayer(id,old,true).catch(()=>{});});
chrome.commands.onCommand.addListener(command=>{if(command==='bwos-home')execute({id:crypto.randomUUID(),kind:'home'});});
chrome.runtime.onMessage.addListener((msg,sender,reply)=>{
  const handle=async()=>{
    await ready;const id=sender.tab?.id,role=registry.role(id);
    if(msg.type==='core-bind'){await bind(sender);poll();return {ok:true};}
    if(role==='discovery')return {ok:false,error:'Discovery has no action capability'};
    if(!role)return {ok:false,error:'Unowned surface'};
    if(msg.type==='home')return execute({id:msg.id || crypto.randomUUID(),kind:'home'});
    if(msg.type==='core-navigation')return execute({id:msg.id || crypto.randomUUID(),kind:'navigate',action:msg.action});
    if(msg.type==='media-frame')return {frameId:sender.frameId};
    if(msg.type==='media-report')return mediaReport(msg,sender);
    if(msg.type==='media-intent')return execute({id:msg.id || crypto.randomUUID(),kind:'playback',target:String(id),action:msg.action,value:msg.value});
    if(msg.type==='open-service'){if(role!=='home')throw Error('Open from Home');return execute({id:msg.id || crypto.randomUUID(),kind:'open',url:msg.url});}
    if(msg.type==='sync-recommendations' || msg.type==='search-recommendations'){
      if(role!=='home')throw Error('Discover from Home');
      return discoverRecommendations(msg.service,sender,msg.type==='search-recommendations'?msg.query:null,msg.target);
    }
    if(msg.type==='voice-poll'){return bwos('api/voice/service');}
    if(msg.type==='show-keyboard')return sendToTab(id,{type:'show-keyboard'},{frameId:0});
    if(msg.type==='voice-type')return bwos('api/voice/type',{text:msg.text});
    if(msg.type==='keyboard')return bwos('api/keyboard');
    if(msg.type==='progress')return bwos('api/progress',msg.data);
    if(msg.type==='lookup')return bwos('api/progress/lookup',msg.data);
    if(msg.type==='voice-error')return bwos('api/browser/error',{message:msg.message});
    if(msg.type==='media-diagnostic')return bwos('api/playback/diagnostic',{...msg.data,id:String(id),service:'youtube',category:'player'});
    if(msg.type==='reload-service' && role==='playback')return execute({id:crypto.randomUUID(),kind:'playback',target:String(id),action:'retry'});
    if(msg.type==='key-to-top' && sender.frameId!==0)return sendToTab(id,{type:'key',key:msg.key},{frameId:0});
    if(msg.type==='media-poll'){poll();return {ok:true};}
    return {ok:false,error:'Unsupported adapter message'};
  };
  handle().then(reply,error=>reply({ok:false,error:error.message}));return true;
});
