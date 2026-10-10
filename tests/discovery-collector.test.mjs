import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

// Model a virtualized provider: every scroll removes old cards and inserts the
// next batch. Discovery must accumulate cards instead of losing earlier pages.
let offset=0,scrolls=0,message;
const grid={hidden:false,getClientRects:()=>[{}],contains:()=>false,matches:selector=>selector==='ytd-rich-grid-renderer',querySelector:()=>null,
  querySelectorAll:selector=>selector==='a[href],[data-video-id]'?Array.from({length:20},(_,i)=>link(offset+i)):[]};
function link(id) {
  const card={querySelectorAll:()=>[],querySelector:()=>null};
  return {hidden:false,getClientRects:()=>[{}],getAttribute:name=>name==='href'?'/watch?v='+String(id).padStart(11,'0'):name==='title'?'Video '+id:null,
    closest:selector=>selector.startsWith('ytd-rich-item-renderer')?card:null};
}
const document={readyState:'complete',body:{scrollHeight:5000},documentElement:{scrollHeight:5000},querySelectorAll:()=>[grid],querySelector:()=>grid};
const window={scrollTo(){scrolls++;offset+=20;}};window.top=window;
const context=vm.createContext({window,document,location:{hostname:'www.youtube.com',pathname:'/',href:'https://www.youtube.com/',search:'',hash:''},innerHeight:720,
  URL,URLSearchParams,console,getComputedStyle:()=>({backgroundImage:'none'}),MutationObserver:class{observe(){}},
  setTimeout:callback=>{queueMicrotask(callback);return 1;},clearTimeout(){},
  chrome:{runtime:{onMessage:{addListener:callback=>{message=callback;}},sendMessage:(_message,reply)=>reply({ok:true})}}});
vm.runInContext(fs.readFileSync(new URL('../extensions/bwos-tv/recommendations.js',import.meta.url),'utf8'),context);
assert.equal(scrolls,0,'Ordinary service pages are never automatically scrolled');
const discovered=await new Promise(resolve=>message({type:'recommendations-discover',target:60},{},resolve));
assert.equal(discovered.data.rows.flatMap(row=>row.items).length,60,'Discovery retains earlier virtualized pages while filling the requested feed');
assert.equal(scrolls,2,'Discovery stops once it has enough cards');
const ids=new Set(discovered.data.rows.flatMap(row=>row.items).map(item=>item.url));
assert.equal(ids.size,60,'Recommendations across scroll pages are deduplicated');
assert.equal(discovered.data.complete,true,'Only a filled request is reported as complete');
await new Promise(resolve=>message({type:'recommendations-discover',target:10000},{},resolve));
assert.ok(scrolls<=30,'Discovery has bounded work even when a provider keeps loading forever');
console.log('PASS: bounded multi-page discovery, virtualized card retention, canonical deduplication and no scrolling of live services');

// A delayed continuation must not be mistaken for a two-card finished feed.
offset=0;scrolls=0;
grid.querySelectorAll=selector=>selector==='a[href],[data-video-id]'?Array.from({length:scrolls<7?2:60},(_,i)=>link(i)):[];
window.scrollTo=()=>{scrolls++;};
const delayed=await new Promise(resolve=>message({type:'recommendations-discover',target:60},{},resolve));
assert.equal(delayed.data.rows.flatMap(row=>row.items).length,60,'Slow hydration is given time to expand a sparse initial batch');
grid.querySelectorAll=selector=>selector==='a[href],[data-video-id]'?[link(0),link(1)]:[];
const sparse=await new Promise(resolve=>message({type:'recommendations-discover',target:60},{},resolve));
assert.equal(sparse.data.complete,false,'A truly sparse feed remains explicitly partial');
assert.equal(sparse.data.captured,2,'Captured count describes actual provider content');
const nested={clientHeight:720,scrollHeight:5000,scrollTop:0,getClientRects:()=>[{}],scrollTo({top}){this.scrollTop=top;offset+=20;}};
document.querySelectorAll=selector=>selector.startsWith('main,')?[nested]:[grid];
context.getComputedStyle=node=>({backgroundImage:'none',overflowY:node===nested?'auto':'visible'});
offset=0;
grid.querySelectorAll=selector=>selector==='a[href],[data-video-id]'?Array.from({length:20},(_,i)=>link(offset+i)):[];
const innerScroll=await new Promise(resolve=>message({type:'recommendations-discover',target:60},{},resolve));
assert.equal(innerScroll.data.rows.flatMap(row=>row.items).length,60,'Discovery pages the actual inner scrolling container');
assert.ok(nested.scrollTop>0,'Inner scroll advances instead of scrolling a fixed outer document');

// Only the muted discovery tab receives bounded carousel actions.
let carouselClicks=0;
const next={getClientRects:()=>[{}],getBoundingClientRect:()=>({top:100,bottom:150}),click(){carouselClicks++;}};
const shelf={...grid,querySelector:selector=>selector.startsWith('.handleNext')?next:null,
  querySelectorAll:selector=>selector==='a[href],.title-card[data-title-id],.title-card[data-video-id]'?Array.from({length:12},(_,i)=>({
    ...link(i),getAttribute:name=>name==='href'?'/browse?jbv='+((carouselClicks*12)+i):name==='title'?'Film '+i:null})):[]};
context.location={hostname:'www.netflix.com',pathname:'/browse',href:'https://www.netflix.com/browse',search:'',hash:''};
document.querySelectorAll=()=>[shelf];
const carousel=await new Promise(resolve=>message({type:'recommendations-discover',target:60},{},resolve));
assert.equal(carousel.data.rows.flatMap(row=>row.items).length,60,'Virtualized Netflix carousel pages accumulate into the feed');
assert.equal(carouselClicks,4,'Carousel paging is bounded and stops once the requested feed is filled');
