import {ScreenPeer} from './rtc.js';
const $=selector=>document.querySelector(selector);
const video=$('#screen');
let token='',peer,stopped=true,polling=false;
const received=new MediaStream();
const code=new URLSearchParams(location.hash.slice(1)).get('code') || '';
history.replaceState(null,'',location.pathname);
async function call(action,body={}) {
  const response=await fetch('/v/'+action,{method:'POST',headers:{'Content-Type':'application/json','X-BWOS-Remote':token},body:JSON.stringify(body),keepalive:action==='leave'});
  const data=await response.json();if(!response.ok || data.ok===false){const error=Error(data.error || 'Connection unavailable');error.rejected=true;throw error;}return data;
}
function status(text){$('#status').textContent=text;}
function disconnect(message='Disconnected. Start a new sharing session on your PC to reconnect.') {
  if(token)call('leave').catch(()=>{});
  stopped=true;peer?.close();peer=null;token='';
  received.getTracks().forEach(track=>{track.stop();received.removeTrack(track);});video.srcObject=null;
  $('#toolbar').hidden=true;$('#hint').hidden=true;$('#welcome').hidden=false;$('#connect').disabled=true;$('#message').textContent=message;
}
async function poll() {
  let cursor=0,failures=0;
  while(!stopped) {
    try {
      const result=await call('poll',{after:cursor});if(stopped)return;
      await peer.receive(result.messages || []);cursor=result.cursor;failures=0;
    } catch(error){if(stopped)return;if(error.rejected || ++failures>=5){disconnect(error.message);return;}}
    await new Promise(resolve=>setTimeout(resolve,1000));
  }
}
$('#connect').addEventListener('click',async()=>{
  if(polling)return;polling=true;$('#connect').disabled=true;
  try {
    token=(await call('pair',{code})).token;stopped=false;
    peer=new ScreenPeer({send:(type,data)=>call('signal',{type,data}),received:track=>{
      received.addTrack(track);video.srcObject=received;video.play().catch(()=>status('Select Enable sound to play.'));
      if(track.kind==='audio')$('#sound').disabled=false;
    },state:state=>{status(state==='connected'?'Live · '+(received.getAudioTracks().length?'audio available':'screen only'):state==='failed'?'Connection failed. Check that both devices are on the same LAN.':state==='disconnected'?'Connection interrupted…':'Connecting…');},failed:error=>disconnect(error.message)});
    $('#sound').disabled=true;$('#welcome').hidden=true;$('#toolbar').hidden=false;$('#hint').hidden=false;poll();
  } catch(error){$('#message').textContent=error.message;}
});
$('#disconnect').addEventListener('click',()=>disconnect());
$('#sound').addEventListener('click',async()=>{video.muted=!video.muted;try{await video.play();$('#sound').textContent=video.muted?'Enable sound':'Mute';}catch{video.muted=true;status('Select Enable sound again to allow playback.');}});
$('#fullscreen').addEventListener('click',()=>{const operation=document.fullscreenElement?document.exitFullscreen():document.documentElement.requestFullscreen?.();operation?.catch(()=>status('Full screen is unavailable in this browser.'));});
const press=action=>{if(!stopped)call('key',{action}).catch(error=>status(error.message));};
document.querySelectorAll('[data-key]').forEach(button=>button.addEventListener('click',()=>press(button.dataset.key)));
$('#typing').addEventListener('submit',async event=>{event.preventDefault();if(stopped)return;try{await call('type',{text:$('#text').value,action:'enter'});$('#text').value='';}catch(error){status(error.message);}});
const keys={ArrowLeft:'left',ArrowRight:'right',ArrowUp:'up',ArrowDown:'down',Enter:'ok',Escape:'back',Home:'home',' ':'play'};
addEventListener('keydown',event=>{if(event.target.matches('input,textarea') || !keys[event.key] || stopped)return;event.preventDefault();press(keys[event.key]);});
const pads=new Map();
function gamepads() {
  requestAnimationFrame(gamepads);if(stopped || document.hidden || !document.hasFocus())return;
  for(const pad of navigator.getGamepads?.() || []) {
    if(!pad || pad.mapping!=='standard')continue;
    const previous=pads.get(pad.index) || [];const buttons=pad.buttons.map(button=>button.pressed);
    for(const [index,action] of [[0,'ok'],[1,'back'],[3,'options'],[9,'home'],[12,'up'],[13,'down'],[14,'left'],[15,'right']])if(buttons[index] && !previous[index])press(action);
    pads.set(pad.index,buttons);
  }
}
gamepads();addEventListener('pagehide',()=>disconnect());
if(!code){$('#connect').disabled=true;$('#message').textContent='Open Settings → Connections → Stream BWOS on your Windows PC, then use its link.';}
