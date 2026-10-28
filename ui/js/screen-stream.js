import {api} from './api.js';
import {ScreenPeer} from './screen-peer.js';

export class ScreenSharing {
  constructor(changed) {this.changed=changed;this.session=null;this.media=null;this.peer=null;this.generation=0;this.starting=false;}
  get active() {return !!this.session;}
  async start() {
    if(this.active)return this.session;
    if(this.starting)throw Error('A screen-sharing selection is already open.');
    if(!navigator.mediaDevices?.getDisplayMedia)throw Error('Screen sharing needs the Windows Edge or Chrome browser.');
    // Must run directly from Select/click, before any network awaits.
    this.starting=true;let media;
    const generation=++this.generation;
    try {
      media=await navigator.mediaDevices.getDisplayMedia({video:{displaySurface:'monitor',frameRate:{ideal:30,max:30},width:{max:1920},height:{max:1080}},audio:true,systemAudio:'include',selfBrowserSurface:'include',surfaceSwitching:'exclude'});
      if(generation!==this.generation)throw Error('Screen sharing was cancelled.');
      this.media=media;
      const started=await api.stream('start');
      if(generation!==this.generation) {
        await api.stream('stop',{session:started.session}).catch(()=>{});
        throw Error('Screen sharing was cancelled.');
      }
      this.session=started;
      const session=this.session.session;
      this.peer=new ScreenPeer({stream:media,send:(type,data)=>api.stream('signal',{session,type,data}),state:state=>this.changed({active:true,state,audio:media.getAudioTracks().length>0}),failed:error=>this.stop(error.message)});
      for(const track of media.getTracks())track.addEventListener('ended',()=>{if(generation===this.generation)this.stop();},{once:true});
      if(!media.getVideoTracks().some(track=>track.readyState==='live'))throw Error('Screen capture ended. Start sharing again.');
      this.changed({active:true,state:'waiting',audio:media.getAudioTracks().length>0});
      this.poll(generation,session);
      return {...this.session,audio:media.getAudioTracks().length>0};
    } catch(error) {
      media?.getTracks().forEach(track=>track.stop());
      if(generation===this.generation)await this.stop();
      throw error;
    }
    finally {this.starting=false;}
  }
  async poll(generation,session) {
    let cursor=0,offered=false,failures=0;
    while(generation===this.generation && this.session) {
      try {
        const result=await api.stream('poll',{session,after:cursor});
        if(generation!==this.generation)return;
        if(result.joined && !offered) {offered=true;await this.peer.offer();}
        await this.peer.receive(result.messages || []);cursor=result.cursor;failures=0;
      } catch(error) {
        if(generation!==this.generation)return;
        if(error.rejected || ++failures>=5){await this.stop('Sharing connection lost. Start a new session.');return;}
      }
      await new Promise(resolve=>setTimeout(resolve,1000));
    }
  }
  async stop(error='') {
    ++this.generation;
    const session=this.session?.session;this.session=null;
    this.peer?.close();this.peer=null;
    this.media?.getTracks().forEach(track=>track.stop());this.media=null;
    this.changed({active:false,error});
    if(session)await api.stream('stop',{session}).catch(()=>{});
  }
}
