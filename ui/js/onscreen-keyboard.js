import {h} from './util.js';

// Uses the same focus/actions as the rest of the TV interface. Text remains local
// until Search is selected; a hardware/phone keyboard can edit the field too.
export function searchKeyboard(value, submit, close) {
  const field=h('input.field.keyboard-preview',{type:'search',maxlength:200,autocomplete:'off',spellcheck:'false','aria-label':'Search text','data-focus':true,dataset:{kind:'btn',action:'search'}});
  field.value=value;
  const handlers={search:()=>submit(field.value),cancel:close};
  const key=(action,label,run)=>{
    handlers[action]=run;
    return h('button.btn.keyboard-key',{type:'button','data-focus':true,dataset:{kind:'btn',action,stay:'1',key:'keyboard:'+action}},label);
  };
  let shifted=false;
  const characters=[];
  const grid=h('div.keyboard-grid',{'aria-label':'On-screen keyboard'});
  for(const letter of '1234567890qwertyuiopasdfghjkl-zxcvbnm.,') {
    const button=key('letter:'+letter,letter,()=>{field.value=(field.value+(shifted?letter.toUpperCase():letter)).slice(0,200);});
    characters.push([letter,button]);grid.append(button);
  }
  const actions=h('div.keyboard-actions',null,
    key('shift','⇧',()=>{shifted=!shifted;for(const [letter,button] of characters)button.textContent=shifted?letter.toUpperCase():letter;}),
    key('space','Space',()=>{field.value=(field.value+' ').slice(0,200);}),
    key('delete','⌫',()=>{field.value=Array.from(field.value).slice(0,-1).join('');}),
    key('clear','Clear',()=>{field.value='';}),
    h('button.btn',{type:'button','data-focus':true,dataset:{kind:'btn',action:'search'}},'Search'),
    h('button.btn',{type:'button','data-focus':true,dataset:{kind:'btn',action:'cancel'}},'Cancel'));
  const panel=h('div.modal-panel.modal-keyboard',null,h('h2.modal-title',null,'Search'),field,grid,actions,
    h('p.modal-text',null,'Use the arrows and Select, or type with your keyboard or phone.'));
  const typing=event=>{
    if(!panel.isConnected || event.target.matches('input,textarea') || event.ctrlKey || event.metaKey || event.altKey)return;
    if(Array.from(event.key).length===1){field.value=(field.value+event.key).slice(0,200);}
    else if(event.key==='Backspace')field.value=Array.from(field.value).slice(0,-1).join('');
    else return;
    field.focus({preventScroll:true}); // Phone Send/Enter now submits the text field.
    event.preventDefault();event.stopImmediatePropagation();
  };
  document.addEventListener('keydown',typing,true);
  return {panel,handlers,first:grid.firstElementChild,dispose:()=>document.removeEventListener('keydown',typing,true)};
}
