import {h} from './util.js';
import {focus} from './focus.js';

export function settingsSections(list, opened) {
  for(const group of [...list.children]) {
    const heading=group.querySelector(':scope > h3');if(!heading)continue;
    const name=heading.textContent;
    const summary=h('summary',{'data-focus':true,'aria-label':name+' settings',dataset:{kind:'settingsSection',key:'settings:section:'+name}},name);
    summary.addEventListener('click',event=>{event.stopPropagation();focus.set(summary);});
    const section=h('details.settings-group.settings-section',null,summary,h('div.settings-section-body',null,...[...group.children].filter(child=>child!==heading)));
    section.open=opened.has(name);
    section.addEventListener('toggle',()=>{if(section.open)opened.add(name);else opened.delete(name);});
    group.replaceWith(section);
  }
  return list;
}
