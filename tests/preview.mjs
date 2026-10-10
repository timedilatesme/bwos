// Local deterministic UI fixture: no microphone, game launches, Bluetooth changes or service logins.
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
const root = path.resolve(new URL('../ui', import.meta.url).pathname.replace(/^\/([A-Z]:)/i, '$1'));
const projectRoot=path.dirname(root);
let commands = [];
let streamSession;
const eventClients=new Set();
const broadcast=event=>{for(const client of eventClients)client.write('event: '+event+'\ndata: {}\n\n');};
const playback = [{ id: '42', url: 'https://www.youtube.com/watch?v=fixture', title: 'Evening music — Hindi favourites', background: true, paused: false }];
const voice = { available: true, status: 'ready', message: 'Listening — say a command', listening: true, inputLevel: 8, feedback: 'Heard “Open YouTube”', backend: 'NVIDIA GPU', capturing: true, microphones: [{ id: 0, name: 'USB microphone' }] };
const settings = { voiceEnabled: true, voiceEngine: 'whisper', voiceReplies: true, voiceLanguage: 'auto', voiceMode: 'direct', voiceMicrophone: -1, trigger: 'guide', guideSupported: true, browser: 'msedge', blankAfter: 0, standbyAfter: 0, country: 'US' };
const state = { version: '1.1.0', games: [{ key: 'custom:demo', name: 'Rocket League', source: 'custom', art: {}, playtime: 0 }], watch: [{ id: 'youtube', name: 'YouTube', section: 'stream', url: '/youtube-fixture.html', color: '#ff0033' }], settings, controllers: [{ slot: 0, battery: 'wired' }], forYou: [], upNext: [], running: null, user: { name: 'Player' }, voice,
  devices: { bluetooth: [{ id: 'sample', name: 'Living room speaker', connected: true, type: 'Audio', status: 'Connected' }], audio: [{ id: 'audio:sample', name: 'Living room speaker', connected: true, canControl: true }], error: '' } };
state.watch=[{id:'youtube',name:'YouTube',section:'stream',url:'https://www.youtube.com/tv',color:'#ff0033'},
  {id:'netflix',name:'Netflix',section:'stream',url:'https://www.netflix.com/browse',color:'#e50914'},
  {id:'prime',name:'Prime Video',section:'stream',url:'https://www.primevideo.com',color:'#00a8e1'}];
const film={id:'fixture-film',title:'Fixture Film',type:'movie',year:2024,genres:['Adventure'],poster:'/tests/missing-poster.jpg',backdrop:'/tests/art-fixture.svg',description:'A sample film for verifying BWOS title selection.',offers:[{app:'netflix',name:'Netflix',url:'https://www.netflix.com/title/fixture'}]};
state.forYou=[{id:'featured',title:'Sample catalogue',items:[film,{id:'fixture-series',title:'Fixture Series',type:'show',year:2025,genres:['Drama'],offers:[{app:'netflix',name:'Netflix',url:'https://www.netflix.com/title/series'}]}]},
  {id:'prime-featured',title:'Another service',items:[{...film,offers:[{app:'prime',name:'Prime Video',url:'https://www.primevideo.com/detail/fixture'}]}]}];
state.videos=[{id:'M7lc1UVf-VE',title:'YouTube Developers Live: Embedded Web Player Customization',author:'Google for Developers',url:'https://www.youtube.com/watch?v=M7lc1UVf-VE',artwork:'https://i.ytimg.com/vi/M7lc1UVf-VE/hqdefault.jpg'}];
state.videos.unshift({id:'jNQXAC9IVRw',title:'Me at the zoo',author:'Public playback test',url:'https://www.youtube.com/watch?v=jNQXAC9IVRw'});
state.recommendations={youtube:{updated:Date.now()/1000,rows:[{id:'fixture-home',title:'From YouTube',items:state.videos.map(video=>({...video,id:'feed:youtube:'+video.id,offers:[{app:'youtube',name:'YouTube',url:video.url}]}))}]},
  netflix:{updated:Date.now()/1000,rows:[{id:'imported',title:'Picked for you',items:Array.from({length:6},(_,i)=>({id:'feed:netflix:'+i,title:'Imported film '+(i+1),artwork:'/tests/art-fixture.svg',backdrop:'/tests/art-fixture.svg',offers:[{app:'netflix',name:'Netflix',url:'https://www.netflix.com/title/'+(100+i)}]}))}]}};
state.recommendations.prime={updated:Date.now()/1000,rows:Array.from({length:10},(_,i)=>({id:'short'+i,title:'Prime row '+i,items:[{id:'feed:prime:'+i,title:'Sample Prime film '+(i+1),artwork:'/tests/art-fixture.svg',offers:[{app:'prime',name:'Prime Video',url:'https://www.primevideo.com/detail/SAMPLE'+i}]}]}))};
const sampleVideos=Array.from({length:24},(_,i)=>({...state.recommendations.youtube.rows[0].items[i%2],id:'feed:youtube:sample'+i,url:'https://www.youtube.com/watch?v='+String(i).padStart(11,'0'),title:'Sample video '+(i+1),artwork:'/tests/art-fixture.svg',author:'Preview channel',offers:[{app:'youtube',name:'YouTube',url:'https://www.youtube.com/watch?v='+String(i).padStart(11,'0')}]}));
state.recommendations.youtube.rows[0].items.push(...sampleVideos);
http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1:47920');
  const json = data => { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify(data)); };
  if(url.pathname==='/test/bridge.js') {
    res.setHeader('Content-Type','text/javascript');
    return res.end(`addEventListener('message',async event=>{if(event.source!==window || event.origin!==location.origin)return;const {type,requestId,service,query,url}=event.data || {};if(!['bwos-open-service','bwos-sync-recommendations','bwos-search-recommendations'].includes(type))return;const result=await fetch('/test/bridge',{method:'POST',body:JSON.stringify({type,service,query,url})}).then(response=>response.json());window.postMessage({type:'bwos-open-result',requestId,result},location.origin);});`);
  }
  if (url.pathname === '/api/events') { res.writeHead(200, { 'Content-Type': 'text/event-stream' }); res.write('event: hello\ndata: {}\n\n');eventClients.add(res);res.on('close',()=>eventClients.delete(res)); return; }
  let body = ''; for await (const chunk of req) body += chunk;
  if(url.pathname==='/api/stream/start') {streamSession={id:'sample-session',code:'sample-only-code',token:null,messages:[],keys:[],cursor:0};return json({ok:true,session:streamSession.id,url:'http://127.0.0.1:47920/view#code='+streamSession.code});}
  if(url.pathname==='/v/pair') {const request=JSON.parse(body);if(!streamSession || request.code!==streamSession.code || streamSession.token){res.statusCode=403;return json({ok:false,error:'Link expired or used'});}streamSession.token='sample-viewer-token';return json({ok:true,token:streamSession.token});}
  if(url.pathname.startsWith('/api/stream/') || url.pathname.startsWith('/v/')) {
    const host=url.pathname.startsWith('/api/stream/'),request=JSON.parse(body || '{}'),action=url.pathname.split('/').at(-1);
    if(!streamSession || (host?request.session!==streamSession.id:req.headers['x-bwos-remote']!==streamSession.token)){res.statusCode=403;return json({ok:false,error:'Sharing stopped'});}
    if(action==='stop' || action==='leave'){streamSession=null;return json({ok:true});}
    if(action==='signal'){streamSession.messages.push({...request,host,sequence:++streamSession.cursor});return json({ok:true});}
    if(action==='poll')return json({ok:true,joined:!!streamSession.token,cursor:streamSession.cursor,messages:streamSession.messages.filter(message=>message.host!==host && message.sequence>(request.after || 0))});
    if(action==='key'){streamSession.keys.push(request.action);return json({ok:true});}
    return json({ok:true});
  }
  if(url.pathname==='/test/stream')return json({active:!!streamSession,keys:streamSession?.keys || []});
  if(url.pathname==='/test/bridge') {const request=JSON.parse(body);if(request.type==='bwos-open-service')return json({ok:true,url:request.url});const count=state.recommendations[request.service]?.rows?.reduce((n,row)=>n+row.items.length,0) || 0;if(request.type==='bwos-search-recommendations')return json({ok:true,count,rows:state.recommendations[request.service]?.rows || []});return json({ok:true,count});}
  if (url.pathname === '/api/state') return json({ ...state, version: '1.8.0', playback });
  if (url.pathname === '/api/playback') return json(playback);
  if (url.pathname === '/api/playback/control') { const { id, action } = JSON.parse(body); const player = playback.find(p => p.id === id); if (player) { if (action === 'stop') playback.splice(playback.indexOf(player), 1); else if (action === 'pause' || action === 'resume') player.paused = action === 'pause'; }broadcast('playback'); return json({ ok: true }); }
  if (url.pathname === '/test/voice') { Object.assign(voice, JSON.parse(body)); return json({ ok: true }); }
  if (url.pathname === '/api/voice') return json(voice);
  if (url.pathname === '/api/voice/claim') { const batch = commands; commands = []; return json({ commands: batch }); }
  if (url.pathname === '/test/command') { commands.push(JSON.parse(body)); return json({ ok: true }); }
  if (url.pathname === '/api/settings') { const { key, value } = JSON.parse(body); settings[key] = value; return json({ ok: true }); }
  if (url.pathname.startsWith('/api/')) return json({ ok: true });
  const base=['/tests/','/ui/','/extensions/'].some(prefix=>url.pathname.startsWith(prefix)) ? projectRoot : root;
  const aliases={'/view':'/remote-viewer.html','/viewer.js':'/js/screen-viewer.js','/rtc.js':'/js/screen-peer.js'};
  const file = path.resolve(base, '.' + decodeURIComponent(aliases[url.pathname] || (url.pathname === '/' ? '/index.html' : url.pathname)));
  if (!file.startsWith(base + path.sep) || !fs.existsSync(file)) { res.writeHead(404); return res.end(); }
  const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.png': 'image/png', '.svg':'image/svg+xml' };
  res.setHeader('Content-Type', types[path.extname(file)] || 'application/octet-stream');
  if(url.pathname==='/')return res.end(fs.readFileSync(file,'utf8').replace('</body>','<script src="/test/bridge.js"></script></body>'));
  res.end(fs.readFileSync(file));
}).listen(47920, '127.0.0.1', () => console.log('UI fixture: http://127.0.0.1:47920/#t=11111111111111111111111111111111'));
