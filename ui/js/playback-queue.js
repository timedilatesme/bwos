import {youtubeId} from './media-hub.js';

export function videoQueue(selected, candidates=[]) {
  const selectedId=youtubeId(selected.url || selected.id);
  if(!selectedId)throw Error('This is not a valid YouTube video.');
  const queue=[],seen=new Set();
  for(const candidate of candidates.slice(0,100)) {
    const id=youtubeId(candidate.url || candidate.id);
    if(!id || seen.has(id))continue;
    seen.add(id);
    queue.push({...candidate,...(id===selectedId?selected:{}),id,url:`https://www.youtube.com/watch?v=${id}`});
  }
  if(!seen.has(selectedId))queue.unshift({...selected,id:selectedId,url:`https://www.youtube.com/watch?v=${selectedId}`});
  return queue;
}
