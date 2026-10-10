import assert from 'node:assert/strict';
import {recommendationPage,hubVideos,hubCatalogue,syncedRecommendationRows} from '../ui/js/media-hub.js';
const movie={id:'film',title:'One film',type:'movie',genres:['Drama'],offers:[{app:'netflix',name:'Netflix',url:'https://www.netflix.com/title/1'},{app:'prime',name:'Prime Video',url:'https://www.primevideo.com/detail/ONE'}]};
const state={watch:[{id:'youtube',name:'YouTube'},{id:'netflix',name:'Netflix'},{id:'prime',name:'Prime Video'},{id:'spotify',name:'Spotify',section:'music'},{id:'hidden',hidden:true}],
  forYou:[{id:'movies',title:'Movies for tonight',items:[movie]}],upNext:[{id:'progress',app:'netflix',title:'One film',position:100,duration:1000},{id:'completed',app:'prime',title:'Finished',position:990,duration:1000},{id:'secret',app:'hidden',position:10}],
  videos:[{id:'M7lc1UVf-VE',title:'Saved demo'}],recommendations:{netflix:{updated:Date.now()/1000,rows:[{id:'1',title:'Picked for you',items:[{id:'feed:nfx:one',title:'Account pick',offers:[{app:'netflix',url:'https://www.netflix.com/title/2'}]}]}]},
  youtube:{updated:Date.now()/1000,rows:[{id:'1',title:'From YouTube',items:[{id:'feed:yt:two',title:'Rendered video',url:'https://www.youtube.com/watch?v=abcdefghijk',offers:[{app:'youtube',url:'https://www.youtube.com/watch?v=abcdefghijk'}]}]}]}}};
const netflix=recommendationPage(state,'netflix');
assert.equal(netflix.rows[0].title,'Continue watching');
assert.ok(netflix.rows.some(row=>row.title==='Picked for you'));
assert.equal(netflix.rows.flatMap(row=>row.items).some(item=>item.app==='hidden'),false);
assert.ok(netflix.rows.filter(row=>row.kind==='title').every(row=>row.items.every(item=>item.offers.every(offer=>offer.app==='netflix'))));
assert.equal(recommendationPage(state,'prime').rows[0].title,'Movies for tonight','Completed titles do not become Continue Watching');
assert.equal(recommendationPage(state,'netflix','missing').rows.length,0,'Filtering applies to history and synced cards');
assert.equal(recommendationPage(state,'spotify').featured,undefined);
assert.match(recommendationPage(state,'spotify').empty,/listening/);
assert.equal(recommendationPage(state,'hidden').app,null);
assert.ok(recommendationPage(state,'youtube').rows.some(row=>row.title==='From YouTube'));
assert.ok(recommendationPage(state,'all').rows.some(row=>row.kind==='video' && row.items[0].id==='abcdefghijk'),'For You includes synced YouTube cards routed to the embedded player');
assert.equal(hubVideos(state).length,2,'Imported videos use the same canonical player queue');
assert.equal(recommendationPage(state,'youtube','','saved').rows.length,1);
assert.equal(recommendationPage(state,'youtube','','recent').rows.length,0,'Recommendations are not mislabeled as watch history');
assert.ok(hubCatalogue(state).some(item=>item.id==='feed:nfx:one'));
assert.equal(syncedRecommendationRows(state,'netflix',Date.now()/1000+86401).length,0,'Stale account snapshots stop masquerading as current recommendations');
assert.equal(movie.offers.length,2,'Rendering does not mutate provider offers');
const duplicateShelves={...state,forYou:[{id:'a',items:[movie]},{id:'b',items:[movie]}]};
assert.equal(recommendationPage(duplicateShelves,'prime').rows.filter(row=>row.kind==='title').length,1,'Identical shelves do not bloat a provider page');
console.log('PASS: synced shelves, source distinction, provider isolation, completed history, hidden services and stale feeds');
const primeSingles={...state,upNext:[],forYou:Array.from({length:12},(_,i)=>({id:'one'+i,title:'A tiny row '+i,items:[{...movie,id:'film'+i}]}))};
const primeRows=recommendationPage(primeSingles,'prime').rows;
assert.equal(primeRows.length,1,'Twelve single-card Prime catalogue shelves become one useful collection');
assert.equal(primeRows[0].items.length,12,'Compaction preserves every distinct title');
