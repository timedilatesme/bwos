import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
const source=fs.readFileSync(new URL('../ui/js/api.js',import.meta.url),'utf8').replaceAll('export const','const').replaceAll('export function','function');
const key='11111111111111111111111111111111';
const storage=new Map(), requests=[];
function load(href) {
  const location=new URL(href);
  const context=vm.createContext({location,URL,URLSearchParams,AbortController,setTimeout,clearTimeout,
    sessionStorage:{setItem:(k,v)=>storage.set(k,v),getItem:k=>storage.get(k)},
    history:{state:null,replaceState:(_state,_title,url)=>{location.href=String(url);}},
    fetch:async(path,options)=>{requests.push({path,options});return {ok:true,json:async()=>({ok:true})};}});
  vm.runInContext(source,context); return {context,location};
}
const first=load('http://127.0.0.1:47913/#t='+key+'&view=watch');
assert.equal(first.location.hash,'#view=watch','Third-party players cannot inherit the host token in their embedding URL');
assert.equal(storage.get('bwos.token'),key,'Authentication survives removal from the address');
await vm.runInContext('api.state()',first.context);
assert.equal(requests.at(-1).options.headers['X-BWOS-Token'],key);
const reload=load(first.location.href);
await vm.runInContext('api.localPlaybackReport({url:"https://www.youtube.com/watch?v=abcdefghijk"})',reload.context);
assert.equal(requests.at(-1).options.headers['X-BWOS-Token'],key,'A reload authenticates from session storage');
assert.equal(vm.runInContext('standalone',reload.context),false);
console.log('PASS: URL token cleanup, retained fragment, authenticated playback and reload');
