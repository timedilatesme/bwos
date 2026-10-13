const recommendationSources={youtube:'https://www.youtube.com/',netflix:'https://www.netflix.com/browse',prime:'https://www.primevideo.com/'};
const recommendationTasks=new Map();
const recommendationTabs=new Set();
function recommendationProvider(url) {
  try {const host=new URL(url).hostname;return /^(www\.)?youtube\.com$/.test(host)?'youtube':/^(www\.)?netflix\.com$/.test(host)?'netflix':/^(www\.)?primevideo\.com$/.test(host)?'prime':null;}catch{return null;}
}
async function recommendationsReport(data,sender,complete=false) {
  if(sender.frameId!==0 || !sender.tab?.url?.startsWith('https://') || !data?.rows?.length || recommendationProvider(sender.tab.url)!==data.service) throw Error('Invalid recommendation source');
  // Bind the source to the browser-reported URL rather than page-provided metadata.
  return bwos('api/recommendations/report',{service:data.service,url:sender.tab.url,rows:data.rows,complete,collector:3,stats:data.stats});
}
async function syncRecommendations(service,sender,target=60) {
  return discoverRecommendations(service,sender,null,target);
}
async function discoverRecommendations(service,sender,query=null,target=60) {
  const {home}=await settings();
  if(sender.frameId!==0 || !home || !sender.tab?.url?.startsWith(home) || !Object.hasOwn(recommendationSources,service)) throw Error('Sync recommendations from BWOS');
  if(query!==null && (typeof query!=='string' || !query.trim() || query.length>200))throw Error('Enter a search of up to 200 characters');
  const taskKey=service+':'+(query===null?'home':query.trim());
  if(recommendationTasks.has(taskKey)) return recommendationTasks.get(taskKey);
  if(recommendationTasks.size>=3)throw Error('Discovery is busy. Cached shelves remain available; try again after this refresh.');
  const task=(async()=>{
    // Only a temporary discovery tab is created. Existing service/player tabs are untouched.
    const destinations={youtube:'https://www.youtube.com/results?search_query=',netflix:'https://www.netflix.com/search?q=',prime:'https://www.primevideo.com/search?phrase='};
    let url=query===null?recommendationSources[service]:destinations[service]+encodeURIComponent(query.trim());
    const tab=await chrome.tabs.create({windowId:sender.tab.windowId,url,active:false});
    try {
      recommendationTabs.add(tab.id); registry.ownCollector(service,tab); await save();
      await chrome.tabs.update(tab.id,{muted:true,autoDiscardable:false});
      const deadline=Date.now()+(service==='youtube' && query===null?65000:35000);
      let reason='no-cards',stats=null,source='other';
      let desktop=null,triedTv=false;
      while(Date.now()<deadline) {
        const current=await chrome.tabs.get(tab.id);
        source=new URL(current?.url || current?.pendingUrl || url).pathname;
        if(current?.status==='complete') {
          const result=await sendToTab(tab.id,{type:'recommendations-discover',target:query===null?Math.min(360,Math.max(60,Number(target) || 60)):60},{frameId:0},30000).catch(()=>null);
          if(result?.data?.reason)reason=result.data.reason;
          if(result?.data?.stats)stats=result.data.stats;
          if(/\/(?:login|signin)(?:\/|$)/.test(new URL(current.url).pathname))reason='sign-in-required';
          if(result?.data?.rows?.length) {
            if(query===null) {
              const count=result.data.rows.reduce((sum,row)=>sum+row.items.length,0);
              if(service==='youtube' && !triedTv && count<Math.min(360,Math.max(60,Number(target)||60))) {
                desktop={data:result.data,tab:current};triedTv=true;url='https://www.youtube.com/tv';
                await chrome.tabs.update(tab.id,{url});continue;
              }
              if(desktop) {
                const seen=new Set(),rows=[];
                for(const row of [...desktop.data.rows,...result.data.rows]) {
                  const items=row.items.filter(item=>{if(seen.has(item.url))return false;seen.add(item.url);return true;});
                  if(items.length)rows.push({...row,items});
                }
                result.data={...result.data,rows,complete:seen.size>=Math.min(360,Math.max(60,Number(target)||60))};
              }
              return recommendationsReport(result.data,{tab:current,frameId:0},result.data.complete===true);
            }
            if(recommendationProvider(current.url)!==service)throw Error('Open the app and sign in before searching');
            return bwos('api/recommendations/preview',{service,url:current.url,rows:result.data.rows});
          }
          if(service==='youtube' && query===null && !triedTv && result?.data) {
            triedTv=true;url='https://www.youtube.com/tv';await chrome.tabs.update(tab.id,{url});continue;
          }
          if(result?.data || reason==='sign-in-required')break;
        }
        await new Promise(resolve=>setTimeout(resolve,800));
      }
      if(desktop)return recommendationsReport(desktop.data,{tab:desktop.tab,frameId:0},false);
      throw Object.assign(Error(reason==='profile-required'?'Select your profile in the app, then refresh recommendations.':reason==='sign-in-required'?'Sign in inside the BWOS app session, then refresh recommendations.':reason==='home-unavailable'?'YouTube is not showing a Home feed in this session. Open YouTube and check your sign-in and watch history.':query===null?'No recommendation cards found. Open the app, select your profile or sign in, then refresh again.':'No searchable cards were available. Open the app to check your sign-in or try another search.'),{reason,stats,source});
    } finally {await chrome.tabs.remove(tab.id).catch(()=>{});recommendationTabs.delete(tab.id); registry.release(tab.id); await save();}
  })();
  recommendationTasks.set(taskKey,task);
  try{return await task;}catch(error){
    if(query===null)await bwos('api/recommendations/status',{service,status:error.reason || 'unavailable',stats:error.stats,source:error.source}).catch(()=>{});
    throw error;
  }finally{recommendationTasks.delete(taskKey);}
}
