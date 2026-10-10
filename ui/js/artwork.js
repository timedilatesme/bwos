import {h} from './util.js';
import {youtubeId} from './media-hub.js';

export function artworkSources(item, landscape=false) {
  const fields=landscape?['backdrop','artwork','poster']:['poster','artwork','backdrop'];
  const sources=fields.map(field=>item?.[field]).filter(value=>typeof value==='string' && value.trim());
  const id=youtubeId(item?.url || item?.id);
  if(id)sources.push(`https://i.ytimg.com/vi/${id}/hqdefault.jpg`);
  return [...new Set(sources)];
}

/** Try known artwork alternatives once, then leave a readable title fallback. */
export function artworkImage(item, {landscape=false,className='',loading='lazy',missing=()=>{}}={}) {
  const sources=artworkSources(item,landscape);
  if(!sources.length){missing();return null;}
  const image=h('img'+(className?'.'+className:''),{alt:'',decoding:'async',loading,draggable:'false'});
  let index=0;
  const next=()=>{image.classList.remove('ready');if(++index<sources.length)image.src=sources[index];else{image.remove();missing();}};
  image.onerror=next;
  image.onload=()=>{if(image.naturalWidth<=1)next();else image.classList.add('ready');};
  image.src=sources[0];
  return image;
}
