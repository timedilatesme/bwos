// Provider-independent browsing; no account scraping or private playback APIs.
const normalized = value => String(value || '').normalize('NFKC').toLocaleLowerCase().trim();
export function compactRecommendationRows(rows,service='all') {
  const groups=new Map(),out=[];
  for(const row of rows) {
    if(row.kind==='resume' || row.items.length>2){out.push(row);continue;}
    const key=(row.service || (service==='all'?'catalogue':service))+':'+row.kind;
    let group=groups.get(key);
    if(!group){group={...row,id:'discover:'+key,title:row.service==='youtube'?'Recommended videos':row.service==='prime'?'Discover on Prime Video':row.service==='netflix'?'Discover on Netflix':'Explore more',items:[]};groups.set(key,group);out.push(group);}
    for(const item of row.items)if(!group.items.some(previous=>previous.id===item.id))group.items.push(item);
  }
  // Keep a meaningful provider heading when a single short shelf was all it supplied.
  for(const group of groups.values())if(group.items.length<=2){
    const destination=out.find(row=>row!==group && row.kind===group.kind && row.items.length>2 && (service!=='all' || (group.service && group.service===row.service)));
    if(destination){for(const item of group.items)if(!destination.items.some(previous=>previous.id===item.id))destination.items.push(item);out.splice(out.indexOf(group),1);continue;}
    const original=rows.find(row=>row.items.some(item=>item.id===group.items[0]?.id));if(original)group.title=original.title;
  }
  return out;
}
export function youtubeId(input) {
  if (/^[a-zA-Z0-9_-]{11}$/.test(input || '')) return input;
  try {
    const url = new URL(input);
    if (url.protocol !== 'https:' || url.username || url.password || url.port) return null;
    let id;
    if (url.hostname === 'youtu.be') id = url.pathname.slice(1);
    else if (['youtube.com','www.youtube.com','m.youtube.com'].includes(url.hostname)) {
      id = url.searchParams.get('v') || new URLSearchParams(url.hash.split('?')[1] || '').get('v');
      if (!id) id = /^\/(?:shorts|live|embed)\/([^/]+)\/?$/.exec(url.pathname)?.[1];
    }
    return /^[a-zA-Z0-9_-]{11}$/.test(id || '') ? id : null;
  } catch { return null; }
}
export function youtubeTvUrl(input) {
  const id=youtubeId(input);
  if(!id)return null;
  const watch=new URLSearchParams({v:id});
  try {
    const url=new URL(input),query=url.pathname.startsWith('/tv')?new URLSearchParams(url.hash.split('?')[1] || ''):url.searchParams;
    const list=query.get('list'),index=query.get('index');
    if(/^[a-zA-Z0-9_-]{1,128}$/.test(list || ''))watch.set('list',list);
    if(list && /^\d{1,6}$/.test(index || ''))watch.set('index',index);
  }catch{ /* A plain video ID needs no extra parameters. */ }
  return 'https://www.youtube.com/tv#/watch?'+watch;
}
// Keep the TV layout by default; Standard remains an explicit playback fallback.
// Phone pairing is handled separately by the host.
export function youtubeUrl(input, mode='tv') {
  const tv=youtubeTvUrl(input);
  if(tv) {
    if(mode!=='web')return tv;
    const query=new URLSearchParams(tv.split('?')[1]);
    try {
      const url=new URL(input),source=url.pathname.startsWith('/tv')?new URLSearchParams(url.hash.split('?')[1] || ''):url.searchParams;
      const time=source.get('t') || source.get('start');
      if(/^\d{1,7}(?:s)?$/.test(time || ''))query.set('t',time);
    }catch{}
    return 'https://www.youtube.com/watch?'+query;
  }
  try {
    const url=new URL(input);
    if(url.protocol==='https:' && !url.username && !url.password && !url.port && ['youtube.com','www.youtube.com','m.youtube.com'].includes(url.hostname) && ['/', '/tv', '/tv/'].includes(url.pathname) && !url.search && !url.hash)
      return mode==='web'?'https://www.youtube.com/':'https://www.youtube.com/tv';
  }catch{}
  return null;
}
export function hubVideos(state) {
  const byId = new Map();
  for (const item of [...(state.videos || []), ...(state.upNext || []).filter(item=>item.app==='youtube'),...syncedRecommendationRows(state,'youtube').flatMap(row=>row.items || [])]) {
    const id = youtubeId(item.url || item.id);
    if (!id || byId.has(id)) continue;
    byId.set(id, { ...item, id, title:item.title || 'YouTube video', url:`https://www.youtube.com/watch?v=${id}`, artwork:item.artwork || `https://i.ytimg.com/vi/${id}/hqdefault.jpg`, saved:(state.videos || []).some(v=>youtubeId(v.url || v.id)===id),watched:(state.upNext || []).some(v=>v.app==='youtube' && youtubeId(v.url || v.id)===id) });
  }
  return [...byId.values()];
}
export function hubCatalogue(state, service = 'all', query = '') {
  const visible = new Set((state.watch || []).filter(app=>!app.hidden).map(app=>app.id));
  const titles = new Map();
  for (const row of [...syncedRecommendationRows(state,service),...(state.forYou || [])]) for (const item of row.items || []) {
    const offers = (item.offers || []).filter(offer=>visible.has(offer.app) && (service==='all' || offer.app===service));
    if (!offers.length) continue;
    const previous = titles.get(item.id);
    if (previous) for (const offer of offers) { if (!previous.offers.some(o=>o.app===offer.app && o.url===offer.url)) previous.offers.push(offer); }
    else titles.set(item.id, { ...item, offers:[...offers] });
  }
  const term = normalized(query);
  return [...titles.values()].filter(item=>!term || normalized([item.title,...(item.genres || [])].join(' ')).includes(term));
}

export function syncedRecommendationRows(state,service='all',now=Date.now()/1000) {
  const visible=new Set((state.watch || []).filter(app=>!app.hidden).map(app=>app.id));
  return Object.entries(state.recommendations || {}).filter(([id,feed])=>visible.has(id) && (service==='all' || id===service) &&
    Number.isFinite(Number(feed.updated)) && now-Number(feed.updated)>=0 && now-Number(feed.updated)<86400)
    .flatMap(([id,feed])=>(feed.rows || []).map(row=>({...row,id:'synced:'+id+':'+row.id,service:id,updated:feed.updated})));
}
export function filterVideos(videos, query='') {
  const term = normalized(query);
  return videos.filter(video=>!term || normalized(video.title+' '+(video.author || '')).includes(term));
}

/** Actual BWOS data, grouped into TV shelves. This is not an account-feed mirror. */
export function recommendationPage(state, service='all', query='', type='all') {
  const apps=(state.watch || []).filter(app=>!app.hidden);
  const visible=new Set(apps.map(app=>app.id));
  const app=service==='all'?{id:'all',name:'All services',color:'#8b7bff'}:apps.find(app=>app.id===service);
  if(!app) return {app:null,rows:[],featured:null,source:'',empty:'This service is hidden or unavailable.'};
  const term=normalized(query);
  const matches=item=>!term || normalized([item.title,item.author,...(item.genres || [])].filter(Boolean).join(' ')).includes(term);
  const history=(state.upNext || []).filter(item=>visible.has(item.app) && (service==='all' || item.app===service) && matches(item));
  const continuing=history.filter(item=>Number(item.position)>0 && (!Number(item.duration) || Number(item.position)/Number(item.duration)<.95));
  const rows=[];
  if(continuing.length) rows.push({id:'continue',title:'Continue watching',kind:'resume',items:continuing});
  if(service==='youtube') {
    const synced=syncedRecommendationRows(state,'youtube');
    const videos=filterVideos(hubVideos(state),query);
    const saved=videos.filter(video=>video.saved), recent=videos.filter(video=>!video.saved && history.some(item=>youtubeId(item.url)===video.id));
    if(type==='all') for(const shelf of synced) {
      const items=(shelf.items || []).map(item=>videos.find(video=>video.id===youtubeId(item.url))).filter(Boolean);
      if(items.length) rows.push({...shelf,kind:'video',items});
    }
    const shown=new Set(rows.filter(row=>row.kind==='video').flatMap(row=>row.items.map(video=>video.id)));
    const savedItems=type==='all'?saved.filter(video=>!shown.has(video.id)):saved;
    const recentItems=type==='all'?recent.filter(video=>!shown.has(video.id)):recent;
    if(type!=='recent' && savedItems.length) rows.push({id:'saved',title:'Your saved videos',kind:'video',items:savedItems});
    if(type!=='saved' && recentItems.length) rows.push({id:'recent',title:'Recently watched',kind:'video',items:recentItems});
    if(type==='saved' || type==='recent') rows.splice(0,rows.length,...rows.filter(row=>row.kind!=='resume'));
    return {app,rows:compactRecommendationRows(rows,service),featured:query?null:videos.find(video=>type==='all' || (type==='saved'?video.saved:!video.saved)),featuredKind:'video',
      source:synced.length?'Synced from your YouTube page · Saved videos and BWOS watch history.':'Saved videos and your BWOS watch history · Sync your YouTube page to bring its rendered recommendations here.',
      empty:query?'No saved or recent videos match your search.':'Save a video link to start your YouTube home page. Videos watched through BWOS will appear here too.'};
  }
  const catalogue=hubCatalogue(state,service,query).filter(item=>type==='all' || item.type===type);
  const byId=new Map(catalogue.map(item=>[item.id,item]));
  const synced=syncedRecommendationRows(state,service);
  const hasCatalogue=hubCatalogue(state,service).length>0;
  const shelfSignatures=new Set();
  for(const original of [...synced,...(state.forYou || [])]) {
    const seen=new Set();
    const items=(original.items || []).map(item=>byId.get(item.id)).filter(item=>item && !seen.has(item.id) && seen.add(item.id));
    if(items.length) {
      const signature=items.map(item=>item.id).sort().join('|');
      if(shelfSignatures.has(signature))continue;
      shelfSignatures.add(signature);
      const video=original.service==='youtube';
      rows.push({id:'catalogue:'+original.id,service:original.service,title:original.service && service==='all'?(apps.find(app=>app.id===original.service)?.name+' · '+original.title):original.title || 'From the catalogue',kind:video?'video':'title',
        items:video?items.map(item=>({...item,id:youtubeId(item.url),artwork:item.artwork || `https://i.ytimg.com/vi/${youtubeId(item.url)}/hqdefault.jpg`})):items});
    }
  }
  // Services without a discovery catalogue still expose the user's actual history.
  const recent=history.filter(item=>!continuing.includes(item));
  if(recent.length) rows.push({id:'recent',title:app.section==='music'?'Recently played':'Recently watched',kind:'resume',items:recent});
  const feature=query?null:catalogue.find(item=>item.backdrop || item.poster || item.artwork) || catalogue[0];
  const featureVideo=feature?.offers?.every(offer=>offer.app==='youtube') && youtubeId(feature.url);
  return {app,rows:compactRecommendationRows(rows,service),featured:featureVideo?{...feature,id:youtubeId(feature.url)}:feature,featuredKind:featureVideo?'video':'title',
    source:synced.length?'Shelves synced from your app pages, alongside BWOS catalogue picks and watch history.':hasCatalogue?'BWOS picks · Availability catalogue and your BWOS watch history. Sync a supported app to add its own shelves.':
      'Your BWOS watch history · This service’s personalized recommendations are available in Open app.',
    empty:query?'No titles match in the current BWOS catalogue.':app.section==='music'?'Start listening in the app. Your recently played items will appear here.':'No catalogue titles are available yet. Refresh the catalogue or explore the service in Open app.'};
}
