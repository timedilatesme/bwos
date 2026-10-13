/* Platform-independent action/session core. Provider DOM never owns navigation. */
(function(root) {
  class ActionKernel {
    constructor({limit=128, now=()=>Date.now()}={}) {this.limit=limit;this.now=now;this.records=new Map();this.lanes=new Map();}
    run(command, operation) {
      const {id,lane='navigation',timeout=3000}=command;
      if(typeof id!=='string' || !id || id.length>100)return Promise.reject(Error('Invalid action identity'));
      if(this.records.has(id))return this.records.get(id).promise;
      if(this.records.size>=this.limit) {
        for(const [key,record] of this.records)if(record.done){this.records.delete(key);break;}
        if(this.records.size>=this.limit)return Promise.resolve({ok:false,id,status:'busy',error:'Action capacity reached'});
      }
      const controller=new AbortController(),record={controller,done:false};
      this.records.set(id,record);
      const start=this.now(),deadline=start+Math.min(70000,Math.max(50,timeout));
      let finish;
      record.promise=new Promise(resolve=>{finish=resolve;});
      const settle=(status,value={})=>{
        if(record.done)return;
        record.done=true;clearTimeout(timer);controller.abort();
        finish({...value,id,ok:status==='completed' && value.ok!==false,status,elapsed:this.now()-start});
      };
      record.cancel=()=>settle('cancelled',{error:'Action cancelled'});
      const timer=setTimeout(()=>settle('timeout',{error:'Action deadline exceeded'}),deadline-this.now());
      const previous=this.lanes.get(lane) || Promise.resolve();
      // Expired queued work is skipped; late async work must fence side effects with check().
      const execution=previous.catch(()=>{}).then(async()=>{
        if(record.done)return;
        const check=()=>{if(controller.signal.aborted || this.now()>=deadline)throw Error('Action expired');};
        try {check();const value=await operation({signal:controller.signal,check,deadline});check();settle(value?.ok===false?'failed':'completed',value || {});}
        catch(error){settle(controller.signal.aborted?'cancelled':'failed',{error:String(error.message || error)});}
      });
      // The lane advances at the bounded outcome, even if an adapter promise freezes.
      const tail=record.promise.then(()=>{});this.lanes.set(lane,tail);
      tail.finally(()=>{if(this.lanes.get(lane)===tail)this.lanes.delete(lane);});
      void execution;return record.promise;
    }
    cancel(id){this.records.get(id)?.cancel();}
  }
  class SessionRegistry {
    constructor(state={}) {this.state={version:2,windowId:null,homeId:null,playback:{},collectors:{},...state};}
    bindHome(tab){if(this.state.windowId!==null && this.state.windowId!==tab.windowId)throw Error('Competing shell window');this.state.windowId=tab.windowId;this.state.homeId=tab.id;}
    ownPlayer(provider,tab){if(tab.windowId!==this.state.windowId || this.state.collectors[tab.id])throw Error('Invalid playback ownership');if(this.state.playback[provider]!==undefined && this.state.playback[provider]!==tab.id)throw Error('Duplicate playback session');this.state.playback[provider]=tab.id;}
    ownCollector(provider,tab){if(tab.windowId!==this.state.windowId || Object.values(this.state.playback).includes(tab.id) || tab.id===this.state.homeId)throw Error('Invalid discovery ownership');this.state.collectors[tab.id]={provider,at:Date.now()};}
    role(id){return id===this.state.homeId?'home':this.state.collectors[id]?'discovery':Object.values(this.state.playback).includes(id)?'playback':null;}
    release(id){delete this.state.collectors[id];for(const [p,t] of Object.entries(this.state.playback))if(t===id)delete this.state.playback[p];if(this.state.homeId===id)this.state.homeId=null;}
    reconcile(tabs){const ids=new Set(tabs.filter(t=>t.windowId===this.state.windowId).map(t=>t.id));for(const id of [...Object.values(this.state.playback),...Object.keys(this.state.collectors).map(Number)])if(!ids.has(id))this.release(id);if(!ids.has(this.state.homeId))this.state.homeId=null;}
  }
  function providerUrl(raw) {
    const url=new URL(raw);
    if(url.protocol!=='https:' || url.username || url.password)throw Error('A service requires an HTTPS URL');
    const host=url.hostname.replace(/^www\./,'');
    if(host==='youtube.com' || host==='music.youtube.com' || host==='youtu.be') {
      if(url.pathname==='/watch' || host==='youtu.be') {
        const id=host==='youtu.be'?url.pathname.slice(1):url.searchParams.get('v');
        if(!/^[\w-]{11}$/.test(id || ''))throw Error('Invalid YouTube video');
        const params=new URLSearchParams({v:id});
        for(const name of ['list','index','t']) {const value=url.searchParams.get(name);if(value && /^[\w-]{1,100}$/.test(value))params.set(name,value);}
        return {provider:'youtube',url:'https://www.youtube.com/tv#/watch?'+params,landing:false};
      }
      if(!/^\/tv(?:\/|$)/.test(url.pathname))throw Error('YouTube playback must use the TV route');
      return {provider:'youtube',url:url.href,landing:!url.hash && !url.search};
    }
    const provider=host==='netflix.com'?'netflix':host==='primevideo.com' || (/^amazon\.(?:com|in|co\.uk|de)$/.test(host) && /^\/(?:gp\/video|dp|detail|hz\/video)/.test(url.pathname))?'prime':host;
    return {provider,url:url.href,landing:['/','/browse'].includes(url.pathname) && !url.search && !url.hash};
  }
  root.BwosCore={ActionKernel,SessionRegistry,providerUrl};
})(globalThis);
