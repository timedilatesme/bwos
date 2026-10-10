import {h,icon} from './util.js';
import {recommendationPage} from './media-hub.js';
import {artworkImage} from './artwork.js';
import {focus} from './focus.js';

export function serviceSwitcher(state, selected='all') {
  const apps=[{id:'all',name:'All services'},...(state.watch || []).filter(app=>!app.hidden && (app.section!=='music' || app.id===selected))];
  return h('nav.service-switcher',{'aria-label':'Recommendation services'},...apps.map(app=>
    h('button.service-chip'+(app.id==='all'?'':'.service-chip--logo')+(app.id===selected?'.active':''),{type:'button','data-focus':true,'aria-pressed':String(app.id===selected),
      'aria-label':app.name,title:app.name,dataset:{kind:'recommendService',id:app.id,key:'recommend:service:'+app.id}},
      app.id==='all'?icon('tiles','service-symbol'):h('img.service-logo',{src:app.iconUrl || '/logos/'+app.id+'.svg',alt:'',draggable:'false',onerror:event=>{event.target.parentElement.classList.remove('service-chip--logo');event.target.replaceWith(icon('play','service-symbol'));}}))));
}

function videoCard(video) {
  const card=h('button.media-video',{type:'button','data-focus':true,'aria-label':'Play '+video.title,
    dataset:{kind:'hubVideo',id:video.id,key:'video:'+video.id}},
    h('div.media-video-art',null,artworkImage(video,{landscape:true}),h('span.video-play',{'aria-hidden':'true'},'▶')),
    h('span.media-video-title',null,video.title),h('span.media-video-sub',null,video.author || (video.saved?'Saved video':video.watched?'Recently watched':'YouTube')));
  return card;
}

export function recommendationContents(state, options, factories) {
  const page=recommendationPage(state,options.service,options.query,options.type);
  const root=h('div.recommendations',{dataset:{service:options.service || 'all'}});
  if(!page.app) { root.append(h('div.empty',null,page.empty)); return root; }
  const feature=page.featured;
  if(feature && page.app.id!=='youtube' && !options.search) {
    const video=page.featuredKind==='video';
    const hero=h('section.recommendation-feature'+(video?'.recommendation-feature--video':''),{'aria-label':'Featured in '+page.app.name});
    const image=artworkImage(feature,{landscape:true,className:'feature-art',loading:'eager'});
    if(image)hero.append(image);
    const meta=video?[feature.author,feature.saved?'Saved to your library':feature.watched?'Recently watched':'From YouTube'].filter(Boolean):
      [feature.year,feature.type==='show'?'Series':feature.type==='movie'?'Movie':null,...(feature.genres || []).slice(0,2)].filter(Boolean);
    const action=h('button.btn.feature-button',{type:'button','data-focus':true,'aria-label':(video?'Play ':'Watch ')+feature.title,
      dataset:{kind:video?'hubVideo':'title',id:feature.id,key:'recommend:feature:'+feature.id}},video?'▶  Play':'▶  Watch');
    hero.append(h('div.feature-copy',null,h('p.feature-eyebrow',null,video?'YouTube':page.app.name==='All services'?'Featured':page.app.name),
      h('h2',null,feature.title),h('p.feature-meta',null,meta.join(' · ')),
      !video && feature.description?h('p.feature-description',null,feature.description):null,action));
    root.append(hero);
  }
  const info=h('details.recommendation-info');
  const summary=h('summary',{'data-focus':true,dataset:{kind:'recommendInfo',key:'recommend:info:'+page.app.id}},options.search?'Search results from your apps':page.rows.some(row=>row.service)?'From your apps':'Catalogue & watch history');
  summary.addEventListener('click',event=>{event.stopPropagation();focus.set(summary);});
  info.append(summary,h('p.recommendation-source',null,options.search?'Results read from your app pages. Sign-in and availability remain with the service.':page.source));
  const feed=state.recommendations?.[options.service];
  if(feed?.updated && !options.search) {
    const age=Math.max(0,Math.floor((Date.now()/1000-Number(feed.updated))/60));
    info.append(h('p.recommendation-sync-time',null,age>=1440?'Last sync is over a day old. Sync again to show current app shelves.':
      'App shelves updated '+(age<1?'just now':age<60?age+' minutes ago':Math.floor(age/60)+' hours ago')+'. Availability and recommendations can change.'));
  }
  root.append(info);
  const shelves=h('div.recommendation-shelves');
  for(const shelf of page.rows) {
    const preview=page.app.id==='all' && shelf.kind==='video' && shelf.items.length>8;
    const cards=(preview?shelf.items.slice(0,8):shelf.items).map(item=>shelf.kind==='video'?videoCard(item):shelf.kind==='resume'?factories.resume(item):factories.title(item,page.app.id));
    const row=shelf.kind==='video'?h('section.row.recommendation-video-section',{dataset:{row:'recommend:'+page.app.id+':'+shelf.id}},h('div.discovery-row-head',null,h('h3.row-title',null,shelf.title),preview?h('button.btn',{'data-focus':true,dataset:{kind:'recommendService',id:'youtube',key:'recommend:all:youtube'}},'View all'):null),h('div.media-video-grid',null,cards)):factories.row('recommend:'+page.app.id+':'+shelf.id,shelf.title,cards);
    if(row) shelves.append(row);
  }
  if(!page.rows.length) shelves.append(h('div.recommendation-empty',null,h('h3',null,options.query?'Nothing found yet':'Make yourself at home'),h('p',null,page.empty)));
  root.append(shelves);
  return root;
}
