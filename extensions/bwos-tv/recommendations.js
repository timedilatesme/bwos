// Read rendered discovery cards in the existing BWOS browser profile. No cookies,
// credentials, private endpoints, response interception or account identifiers.
(() => {
  'use strict';
  const provider = hostname => /^(www\.)?youtube\.com$/.test(hostname)?'youtube':/^(www\.)?netflix\.com$/.test(hostname)?'netflix':/^(www\.)?primevideo\.com$/.test(hostname)?'prime':null;
  const text = element => String(element?.textContent || '').replace(/\s+/g,' ').trim().slice(0,200);
  function imageUrl(value,base) {
    try {const url=new URL(value,base);return url.protocol==='https:' && !url.username && !url.password && !url.port?url.href:'';}catch{return '';}
  }
  function cardArtwork(card,base) {
    const images=[...card.querySelectorAll('img')].sort((a,b)=>{
      const score=image=>(/boxart|thumbnail/i.test(image.className)?100000:0)+(image.clientWidth || Number(image.getAttribute('width')) || 0)*(image.clientHeight || Number(image.getAttribute('height')) || 0);
      return score(b)-score(a);
    });
    for(const image of images) {
      const sets=[image.getAttribute('srcset'),image.getAttribute('data-srcset')].filter(Boolean).flatMap(value=>value.split(',').map(part=>part.trim().split(/\s+/))).sort((a,b)=>(parseFloat(b[1]) || 0)-(parseFloat(a[1]) || 0)).map(parts=>parts[0]);
      for(const source of [image.currentSrc,image.getAttribute('data-src'),image.getAttribute('data-lazy-src'),...sets,image.getAttribute('src')]) {
        if(!source || /(?:transparent|spacer|placeholder)(?:[_.\/]|$)/i.test(source))continue;
        if(image.complete && image.naturalWidth===1 && (source===image.currentSrc || source===image.getAttribute('src')))continue;
        const url=imageUrl(source,base);if(url)return {url,alt:image.getAttribute('alt') || ''};
      }
    }
    for(const node of [card,...card.querySelectorAll('[style*="background"],.boxart-image,.boxart-container')].slice(0,15)) {
      const value=getComputedStyle(node).backgroundImage;
      const match=/url\(["']?([^"')]+)["']?\)/.exec(value || '');
      const url=match && imageUrl(match[1],base);if(url)return {url,alt:''};
    }
    return {url:'',alt:''};
  }
  function titleLink(value, base, service) {
    try {
      const url=new URL(value,base);
      if(url.protocol!=='https:' || url.username || url.password || url.port || provider(url.hostname)!==service) return null;
      if(service==='youtube') {
        const id=url.searchParams.get('v') || new URLSearchParams(url.hash.split('?')[1] || '').get('v');
        return /^[\w-]{11}$/.test(id || '')?'https://www.youtube.com/watch?v='+id:null;
      }
      if(service==='netflix') {
        const id=/^\/(?:title|watch)\/(\d+)\/?$/.exec(url.pathname)?.[1] ||
          (/^\/(?:browse|search)\/?$/.test(url.pathname)?url.searchParams.get('jbv'):null);
        return /^\d+$/.test(id || '')?'https://www.netflix.com/title/'+id:null;
      }
      if(!/\/(?:detail|dp)\/[A-Za-z0-9]+(?:\/|$)/.test(url.pathname)) return null;
      url.search='';url.hash='';return url.href;
    } catch { return null; }
  }
  function collect(document, location) {
    const service=provider(location.hostname);
    if(!service || /\/(?:watch|embed|login|signin)(?:\/|$)/.test(location.pathname) || location.search.includes('v=') || /watch\?/.test(location.hash)) return null;
    const selector=service==='youtube'?'ytd-rich-grid-renderer,ytd-item-section-renderer,ytd-rich-shelf-renderer,ytd-shelf-renderer,ytlr-shelf-renderer':service==='netflix'?'.lolomoRow,.rowContainer':'[data-testid*="carousel"],[data-testid*="collection"],[data-automation-id*="carousel"],section';
    const containers=[...document.querySelectorAll(selector)].filter(element=>!element.hidden && element.getClientRects().length);
    if(!containers.length) containers.push(document.querySelector('ytd-rich-grid-renderer,main') || document.body);
    const rows=[],seenRows=new Set(),seenPage=new Set();let total=0;
    const stats={anchors:0,validLinks:0,missingTitles:0,shelves:0};
    // The main grid is included even when YouTube also exposes small topic shelves.
    // Prefer outer collections; nested Prime card/collection containers are not shelves.
    const shelves=containers.filter(container=>!containers.some(other=>other!==container && other.contains(container)));
    stats.shelves=shelves.length;
    for(const container of shelves.slice(0,40)) {
      const heading=service==='youtube' && container.matches('ytd-rich-grid-renderer')?'Recommended for you':service==='youtube' && location.pathname==='/results'?'Search results':text(container.querySelector('.row-header-title,.rowTitle,h2,h3,#title')) || (service==='youtube'?(location.pathname.startsWith('/tv')?'From YouTube TV':'From your YouTube home'):'From the app');
      const items=[],seen=new Set();
      const candidates=service==='netflix'?'a[href],.title-card[data-title-id],.title-card[data-video-id]':service==='youtube'?'a[href],[data-video-id]':'a[href]';
      for(const link of [...container.querySelectorAll(candidates)].slice(0,1800)) {
        stats.anchors++;
        if(total>=360 || items.length>=360) break;
        const cardId=service==='netflix'?(link.getAttribute('data-title-id') || link.getAttribute('data-video-id')):null;
        const videoId=service==='youtube'?link.getAttribute('data-video-id'):null;
        const url=titleLink(videoId && /^[\w-]{11}$/.test(videoId)?'/watch?v='+videoId:cardId && /^\d+$/.test(cardId)?'/title/'+cardId:link.getAttribute('href'),location.href,service);
        if(url)stats.validLinks++;
        if(!url || seen.has(url) || seenPage.has(url) || link.hidden || !link.getClientRects().length) continue;
        if(service==='youtube' && link.closest('ytd-ad-slot-renderer,ytd-promoted-video-renderer,ytd-display-ad-renderer,.ytLockupViewModelCollectionStack2'))continue;
        const card=link.closest('ytd-rich-item-renderer,ytd-video-renderer,yt-lockup-view-model,yt-lockup-view-model-wiz,.ytLockupViewModelHost,ytlr-tile-renderer,ytlr-compact-video-renderer,.title-card,.slider-item,[data-testid*="card"],article') || link;
        const art=cardArtwork(card,location.href);
        const title=link.getAttribute('title') || text(card.querySelector('#video-title,#video-title-link,.yt-lockup-metadata-view-model__title,.yt-lockup-metadata-view-model-wiz__title,.ytLockupMetadataViewModelTitle,.fallback-text,[data-testid*="title"]')) || art.alt || link.getAttribute('aria-label') || text(link);
        if(!title?.trim()){stats.missingTitles++;continue;}
        const artwork=art.url || (service==='youtube'?'https://i.ytimg.com/vi/'+new URL(url).searchParams.get('v')+'/hqdefault.jpg':'');
        seen.add(url);seenPage.add(url);items.push({title:title.trim().slice(0,200),url,artwork,author:text(card.querySelector('#channel-name,yt-content-metadata-view-model a[href^="/@"],.ytContentMetadataViewModelMetadata a[href^="/@"]'))});total++;
      }
      const signature=items.map(item=>item.url).join('|');
      if(items.length && !seenRows.has(signature)) {seenRows.add(signature);rows.push({title:heading,items});}
      if(rows.length>=16 || total>=360) break;
    }
    const reason=service==='netflix' && document.querySelector('.profiles-gate-container,.profile-selection')?'profile-required':
      service==='youtube' && document.querySelector('ytd-feed-nudge-renderer')?'home-unavailable':'no-cards';
    return {service,url:location.href,rows,reason,stats};
  }
  // Also exposes the pure DOM collector to our local fixture; extension worlds are isolated.
  globalThis.BWOSRecommendationCollector={collect,titleLink,provider};
  if(typeof chrome==='undefined' || !chrome.runtime || window.top!==window || !provider(location.hostname)) return;
  let timer,last='',discoveryTask,discoveryDepth=0;
  const wait=ms=>new Promise(resolve=>setTimeout(resolve,ms));
  function advanceDiscovery(carousels) {
    // Netflix virtualizes horizontal shelves separately from the page scroll.
    // Only advance its next-page controls, never title links or playback controls.
    let advanced=false;
    if(provider(location.hostname)==='netflix')for(const row of [...document.querySelectorAll('.lolomoRow,.rowContainer')].slice(0,40)) {
      const next=row.querySelector('.handleNext:not(.inactive):not([aria-disabled="true"]):not([disabled])');
      if(!next || !next.getClientRects().length)continue;
      const rect=next.getBoundingClientRect();if(rect.bottom<=0 || rect.top>=innerHeight)continue;
      const attempts=carousels.get(next) || 0;if(attempts>=4)continue;
      carousels.set(next,attempts+1);next.click();advanced=true;
    }
    if(advanced)return;
    const pageRoot=document.scrollingElement || document.documentElement;
    const nested=[...document.querySelectorAll('main,[role="main"],ytd-app,ytd-page-manager,ytd-browse,ytlr-app,ytlr-browse,#primary')]
      .filter(node=>node.getClientRects().length && /^(auto|scroll)$/.test(getComputedStyle(node).overflowY) && node.scrollHeight>node.clientHeight+100);
    const root=nested.sort((a,b)=>b.clientHeight-a.clientHeight)[0] || pageRoot;
    const height=root===pageRoot?innerHeight:root.clientHeight;
    const step=Math.max(400,height*.85);
    // Walking the page avoids skipping rows that hydrate only near the viewport.
    const top=Math.min((root.scrollTop || 0)+step,Math.max(0,root.scrollHeight-height));
    if(root===pageRoot)window.scrollTo({top,behavior:'instant'});else root.scrollTo({top,behavior:'instant'});
  }
  async function discover(target=60) {
    // Only called for BWOS's muted temporary discovery tab. The user's live
    // service/player tab is never scrolled or navigated by this operation.
    const rows=new Map(),seen=new Set(),carousels=new Map();
    let data,quiet=0;
    for(let pass=0;pass<28;pass++) {
      data=collect(document,location);if(!data)return null;
      const before=seen.size;
      for(const row of data.rows) {
        const items=rows.get(row.title) || [];
        for(const item of row.items)if(!seen.has(item.url)){seen.add(item.url);items.push(item);}
        rows.set(row.title,items);
      }
      if(seen.size>=target || seen.size>=360)break;
      quiet=seen.size===before?quiet+1:0;
      // Small initial batches are not a finished feed. Give hydration/loading
      // at least six seconds, and tolerate slow continuation requests.
      if(pass>=10 && quiet>=8 && seen.size)break;
      advanceDiscovery(carousels);
      await wait(600);
    }
    discoveryDepth=Math.max(discoveryDepth,seen.size);
    return data?{...data,complete:seen.size>=target,captured:seen.size,rows:[...rows].filter(([,items])=>items.length).slice(0,16).map(([title,items])=>({title,items}))}:null;
  }
  function report() {
    const data=collect(document,location);if(!data?.rows.length) return;
    const signature=JSON.stringify(data.rows);if(signature===last)return;
    chrome.runtime.sendMessage({type:'recommendations-report',data},result=>{if(!chrome.runtime.lastError && result?.ok)last=signature;});
  }
  chrome.runtime.onMessage.addListener((message,sender,reply)=>{
    if(message?.type==='recommendations-snapshot') {reply({data:collect(document,location)});return false;}
    if(message?.type==='recommendations-discover') {
      if(!discoveryTask)discoveryTask=discover(Math.min(360,Math.max(60,Number(message.target) || discoveryDepth+60))).finally(()=>{discoveryTask=null;});
      discoveryTask.then(data=>reply({data}),()=>reply({data:null}));return true;
    }
  });
  function start() {
    report();
    new MutationObserver(()=>{clearTimeout(timer);timer=setTimeout(report,1500);}).observe(document.body,{childList:true,subtree:true,attributes:true,attributeFilter:['src','srcset','data-src','data-srcset','style','title','aria-label']});
  }
  if(document.readyState==='loading')addEventListener('DOMContentLoaded',start,{once:true});else start();
})();
