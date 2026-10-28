import { h } from './util.js';
import { api } from './api.js';
import { youtubeId } from './media-hub.js';
import {focus} from './focus.js';
import {videoQueue} from './playback-queue.js';

let sdk;
export function loadYouTubeAPI() {
  if (window.YT?.Player) return Promise.resolve(window.YT);
  if (sdk) return sdk;
  sdk = new Promise((resolve,reject) => {
    const previous = window.onYouTubeIframeAPIReady;
    const script = document.createElement('script');
    script.src = 'https://www.youtube.com/iframe_api';
    script.referrerPolicy = 'strict-origin-when-cross-origin';
    const cleanup = () => { clearTimeout(timer); window.onYouTubeIframeAPIReady = previous; };
    const fail = () => { cleanup(); script.remove(); sdk=null; reject(Error('YouTube could not connect. Retry or open YouTube directly.')); };
    const timer = setTimeout(fail, 10000);
    window.onYouTubeIframeAPIReady = () => { cleanup(); resolve(window.YT); if (typeof previous==='function') previous(); };
    script.onerror=fail; document.head.append(script);
  });
  return sdk;
}

export class YouTubePlayer {
  constructor(root, { openApp, changed=()=>{}, report=api.localPlaybackReport, progress=api.progress, sdk=loadYouTubeAPI } = {}) {
    this.root=root; this.openApp=openApp; this.changed=changed; this.report=report; this.progress=progress; this.sdk=sdk;
    this.generation=0; this.queue=[]; this.error=''; this.docked=false;
    this.title=h('div.embedded-title'); this.status=h('div.embedded-status', {role:'status'});
    this.slot=h('div.embedded-video'); this.controls=h('div.embedded-controls');
    this.root.replaceChildren(this.slot,h('div.embedded-bar',null,this.title,this.status,this.controls));
    this.instructions=h('p.embedded-instructions',null,'A / Cross · Play / pause    ◀ ▶ · Skip 10 s    ↑ / Menu · Controls    Home / Back · Browse');
    this.controls.parentElement.append(this.instructions);
    this.next=h('div.embedded-next',{'aria-live':'polite'});this.controls.parentElement.insertBefore(this.next,this.controls);
    for (const [action,label] of [['toggle','Play / pause'],['seekBack','−10s'],['seekForward','+10s'],['previousMedia','Previous'],['nextMedia','Next'],['focus','Expand'],['dock','Browse'],['app','Open YouTube'],['retry','Retry'],['stop','Close']]) {
      const button=h('button.btn', {type:'button','data-focus':true,dataset:{kind:'embedded',action,key:'embedded:'+action}},label);
      button.addEventListener('click',event=>{event.stopPropagation();this.showControls(true);focus.set(button);this.control(action).catch(error=>{this.status.textContent=error.message;this.showControls();});});
      this.controls.append(button);
    }
    this.root.hidden=true;
    this.nativeMessage=event=>{
      if(event.origin!=='https://www.youtube.com' || event.source!==this.player?.getIframe?.().contentWindow || event.data?.type!=='bwos-native-controls-state')return;
      this.controlsMode=!!event.data.active;
      this.nativeReply?.(event.data.ok!==false);
    };
    addEventListener('message',this.nativeMessage);
    this.visibility=()=>{ if(document.hidden) this.pause(); };
    document.addEventListener('visibilitychange',this.visibility);
    this.timer=setInterval(()=>this.heartbeat(),2000);
    this.root.addEventListener('pointermove',()=>this.showControls());
  }
  async play(video, queue = []) {
    const id=youtubeId(video.url || video.id);
    if (!id) throw Error('This is not a valid YouTube video.');
    const generation=++this.generation;
    this.queue=videoQueue(video,queue);this.video=this.queue.find(item=>item.id===id); this.lastProgress=0;this.playlistReady=false;
    this.ready=false;
    this.error=''; this.root.classList.add('connecting');this.root.classList.remove('player-error');this.setDocked(false); this.root.hidden=false; this.title.textContent=video.title || 'YouTube video'; this.status.textContent='Connecting to YouTube…'; this.changed();
    this.player?.destroy(); this.player=null;
    const mount=h('div'); this.slot.replaceChildren(mount);
    this.heartbeat();
    try {
      const YT=await this.sdk();
      if(generation!==this.generation) return;
      let ready;
      await new Promise((resolve,reject)=>{
        ready=setTimeout(()=>reject(Error('YouTube player did not respond. Retry or open YouTube.')),10000);
        this.player=new YT.Player(mount, {width:'100%',height:'100%',videoId:id,
          playerVars:{enablejsapi:1,origin:location.origin,playsinline:1,controls:1,autoplay:0,start:Math.max(0,Math.floor(Number(video.position)||0))},
          events:{
            onReady:event=>{
              if(generation!==this.generation || this.error){try{event.target.destroy();}catch{} if(generation===this.generation){this.player=null;this.ready=false;}clearTimeout(ready); resolve(); return;}
              clearTimeout(ready); this.player=event.target;this.ready=true;
              this.root.classList.remove('connecting');
              const iframe=event.target.getIframe(); iframe.referrerPolicy='strict-origin-when-cross-origin'; iframe.title=video.title || 'YouTube player'; iframe.setAttribute('allow','autoplay; encrypted-media; picture-in-picture; fullscreen');
              this.status.textContent='Ready · press Play';
              let playlistStarted=false;
              if(this.queue.length>1 && typeof event.target.cuePlaylist==='function') {
                this.playlistReady=true;
                const args=[this.queue.map(item=>item.id),this.queue.findIndex(item=>item.id===id),Math.max(0,Number(video.position)||0)];
                if(!document.hidden && !this.covered && typeof event.target.loadPlaylist==='function'){event.target.loadPlaylist(...args);playlistStarted=true;}
                else event.target.cuePlaylist(...args);
                event.target.setLoop?.(false);
                event.target.setShuffle?.(false);
              }
              if(!playlistStarted && !document.hidden && !this.covered) event.target.playVideo();this.heartbeat();this.showControls();resolve();
            },
            onStateChange:event=>{ if(generation!==this.generation) return; if((document.hidden || this.covered) && [1,3].includes(event.data)) this.pause(); if(event.data===1)this.showControls();this.heartbeat(); },
            onError:event=>{ if(generation!==this.generation) return; clearTimeout(ready); this.fail([101,150].includes(event.data) ? 'This video must be watched on YouTube. Use Open YouTube.' : event.data===100 ? 'This video is unavailable. Choose another or open YouTube.' : `YouTube playback failed (${event.data}). Retry or open YouTube.`); resolve(); },
            onAutoplayBlocked:()=>{if(generation===this.generation){this.status.textContent='Press Play to start.';this.showControls(true);}}
          }
        });
      }).finally(()=>clearTimeout(ready));
    } catch(error) { if(generation===this.generation) this.fail(error.message); }
  }
  setDocked(docked) {
    if(docked)this.nativeControl('hide');
    this.docked=docked;this.controlsMode=false;
    if(docked && document.fullscreenElement) document.exitFullscreen?.().catch(()=>{});
    this.root.classList.toggle('docked',docked); document.body.classList.toggle('has-embedded-player',!!this.video); document.body.classList.toggle('embedded-expanded',!!this.video && !docked);
    this.showControls();this.changed();
  }
  showControls(navigate=false) {
    clearTimeout(this.controlsTimer);this.root.classList.remove('controls-hidden');
    if(!this.docked && !this.error && this.ready) {
      if(navigate)this.nativeControl('show');
      return;
    }
    if(navigate) {
      if(document.fullscreenElement) document.exitFullscreen?.().catch(()=>{});
      this.controlsMode=true;
      const target=this.controls.querySelector('[data-action="toggle"][data-focus]') || this.controls.querySelector('[data-action="app"]');
      if(target) focus.set(target);
    }
    if(!this.docked && !this.controlsMode && !this.error && this.player?.getPlayerState?.()===1) this.controlsTimer=setTimeout(()=>this.hideControls(),3500);
  }
  hideControls() {
    clearTimeout(this.controlsTimer);this.controlsMode=false;
    if(!this.docked)this.nativeControl('hide');
    if(!this.docked && !this.error) this.root.classList.add('controls-hidden');
  }
  async handleInput(action) {
    if(!this.video) return false;
    if(action==='playPause' || (!this.docked && action==='secondary')) {await this.control('toggle');return true;}
    if(this.docked) return false;
    if(action==='prevTab' || action==='nextTab') {await this.control(action==='prevTab'?'previousMedia':'nextMedia');return true;}
    if(!this.error && this.ready && ((this.controlsMode && ['accept','left','right','up','down','back','menu','options'].includes(action)) || ['menu','options','up','down'].includes(action))) {
      if(!await this.nativeControl(action==='options' || action==='menu'?'settings':action)) {
        this.controlsMode=false;
        throw Error('YouTube’s controller menu is unavailable. Restart BWOS to reload its browser adapter. Play/pause and seeking still work.');
      }
      return true;
    }
    if(this.error && ['menu','options','up','down'].includes(action)){this.showControls(true);return true;}
    if(action==='back' && this.controlsMode) {this.hideControls();return true;}
    if(this.controlsMode) return false;
    const command={accept:'toggle',left:'seekBack',right:'seekForward',prevTab:'previousMedia',nextTab:'nextMedia'}[action];
    if(command) {await this.control(command);return true;}
    return false;
  }
  nativeControl(action) {
    const frame=this.player?.getIframe?.();
    if(!frame?.contentWindow)return Promise.resolve(false);
    // The isolated browser adapter focuses the provider's own controls; no BWOS layer covers the video.
    if(action==='hide' || action==='show') {frame.contentWindow.postMessage({type:'bwos-native-controls',action},'https://www.youtube.com');return Promise.resolve(true);}
    return new Promise(resolve=>{
      this.nativeReply?.(false);
      const timer=setTimeout(()=>finish(false),700);
      const finish=ok=>{clearTimeout(timer);if(this.nativeReply===finish)this.nativeReply=null;resolve(ok);};
      this.nativeReply=finish;
      frame.contentWindow.postMessage({type:'bwos-native-controls',action},'https://www.youtube.com');
    });
  }
  pause() { try { this.player?.pauseVideo?.(); } catch {} }
  syncVideo() {
    if(!this.playlistReady || !this.ready)return;
    try {
      const index=this.player.getPlaylistIndex?.(),ids=this.player.getPlaylist?.();
      const id=youtubeId(ids?.[index] || this.player.getVideoUrl?.());
      const video=this.queue.find(item=>item.id===id);
      if(video && video.id!==this.video?.id) {
        this.video=video;this.lastProgress=0;this.controlsMode=false;
        this.title.textContent=video.title || 'YouTube video';this.player.getIframe().title=video.title || 'YouTube player';this.changed();
      }
    }catch{}
  }
  fail(message) { this.error=message;this.root.classList.remove('connecting');this.root.classList.add('player-error'); this.pause(); this.status.textContent=message;this.heartbeat();this.showControls(true); }
  async control(action,value='10') {
    if(!this.video) return false;
    if(action==='stop'){await this.close(); return true;}
    if(action==='dock'){this.setDocked(true); return true;}
    if(action==='focus'){this.setDocked(false); return true;}
    if(action==='app'){this.pause(); await this.openApp?.('https://www.youtube.com/tv#/watch?v='+this.video.id); return true;}
    if(action==='retry'){await this.play(this.video,this.queue); return true;}
    const player=this.player;
    if(!this.ready && !this.error)throw Error('Wait for the video to finish connecting.');
    if(this.error || !player?.getPlayerState) throw Error('Retry the player or open YouTube to continue.');
    if(action==='pause') player.pauseVideo();
    else if(action==='resume' || action==='toggle'){if(document.hidden || this.covered) throw Error('Return to the visible player to play this video.'); if(action==='toggle' && [1,3].includes(player.getPlayerState())) player.pauseVideo(); else player.playVideo();}
    else if(['seekForward','seekBack','restart'].includes(action)) {
      const duration=player.getDuration();if(!Number.isFinite(duration) || duration<=0)throw Error('This video cannot seek yet. Wait for it to load.');
      const seconds=Number(value || 10); if(!Number.isFinite(seconds) || seconds<1 || seconds>100) throw Error('Choose a seek interval from 1 to 100 seconds.');
      player.seekTo(action==='restart' ? 0 : Math.max(0,Math.min(Math.max(0,duration-.1),player.getCurrentTime()+(action==='seekForward'?seconds:-seconds))),true);
    } else if(action==='nextMedia' || action==='previousMedia') {
      const index=this.queue.findIndex(video=>video.id===this.video.id), next=index+(action==='nextMedia'?1:-1);
      if(index<0 || next<0 || next>=this.queue.length) throw Error('No more videos in this list.');
      if(this.playlistReady && typeof player.playVideoAt==='function') {
        if(document.hidden || this.covered)throw Error('Return to the visible player to play the next video.');
        player.playVideoAt(next);this.syncVideo();
      }else await this.play(this.queue[next],this.queue);
    } else throw Error('Use YouTube’s player menu for this control.');
    if(['toggle','resume'].includes(action))this.showControls();
    this.heartbeat(); return true;
  }
  async heartbeat() {
    if(!this.video) return;
    this.syncVideo();
    let state,position=0,duration=0;
    try { state=this.player?.getPlayerState?.(); position=this.player?.getCurrentTime?.() || 0; duration=this.player?.getDuration?.() || 0; } catch {}
    if((document.hidden || this.covered) && [1,3].includes(state)) this.pause();
    const paused=document.hidden || this.covered || ![1,3].includes(state) || !!this.error;
    if(paused && !this.docked)this.showControls();
    const data={...this.video,subtitle:this.video.author || '',position,duration,paused,canControl:this.ready && !this.error,error:this.error,background:this.docked};
    if(!this.error && this.ready) this.status.textContent=(document.hidden || this.covered?'Paused':({0:'Finished',1:'Playing',2:'Paused',3:'Buffering…',5:'Ready · press Play'})[state] || 'Connecting…')+(duration ? ` · ${Math.floor(position/60)}:${String(Math.floor(position%60)).padStart(2,'0')}` : '');
    this.controls.querySelector('[data-action="toggle"]').textContent=paused?'Play':'Pause';
    for(const action of ['toggle','seekBack','seekForward']) {
      const button=this.controls.querySelector(`[data-action="${action}"]`);
      button.disabled=!!this.error || !this.ready;
      if(button.disabled) button.removeAttribute('data-focus');else button.setAttribute('data-focus','');
    }
    this.controls.querySelector('[data-action="retry"]').hidden=!this.error;
    const index=this.queue.findIndex(video=>video.id===this.video.id);
    const next=this.queue[index+1];this.next.hidden=!next;this.next.textContent=next?'Up next · '+next.title:'';
    for(const [action,available] of [['previousMedia',index>0],['nextMedia',index>=0 && index<this.queue.length-1]]) {
      const button=this.controls.querySelector(`[data-action="${action}"]`);button.disabled=!available || !this.ready || !!this.error;
      button.hidden=!available;
      if(!button.disabled)button.setAttribute('data-focus','');else button.removeAttribute('data-focus');
    }
    if(!this.reporting){this.reporting=true; try {await this.queueReport(data);} finally {this.reporting=false;}}
    if(position>0 && Date.now()-(this.lastProgress || 0)>15000){this.lastProgress=Date.now(); this.progress(data).catch(()=>{});}
  }
  async close() {
    clearTimeout(this.controlsTimer);
    this.nativeReply?.(false);this.controlsMode=false;
    ++this.generation; this.player?.destroy(); this.player=null; const old=this.video; this.video=null; this.error=''; this.root.hidden=true; document.body.classList.remove('embedded-expanded','has-embedded-player'); this.slot.replaceChildren(); this.changed();
    if(old) await this.queueReport({...old,ended:true});
  }
  queueReport(data) { this.reportQueue=(this.reportQueue || Promise.resolve()).then(()=>this.report(data)).catch(()=>{}); return this.reportQueue; }
  cover(covered) { this.covered=covered; if(covered) this.pause(); this.root.classList.toggle('covered',covered); }
}
