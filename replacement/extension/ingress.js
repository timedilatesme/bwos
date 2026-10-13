// Physical keyboard/controller injection and remote actions meet at the same core.
(() => {
  if(window.top!==window)return;
  const keys={ArrowUp:'up',ArrowDown:'down',ArrowLeft:'left',ArrowRight:'right',Enter:'select',Escape:'back'};
  addEventListener('keydown',event=>{
    if(!event.isTrusted || event.target?.matches('input,textarea,[contenteditable=true]'))return;
    if(event.key==='F24'){event.preventDefault();event.stopImmediatePropagation();chrome.runtime.sendMessage({type:'home',id:crypto.randomUUID()});return;}
    if(keys[event.key]){event.preventDefault();event.stopImmediatePropagation();chrome.runtime.sendMessage({type:'core-navigation',action:keys[event.key],id:crypto.randomUUID()});}
    if(event.key===' '){event.preventDefault();event.stopImmediatePropagation();chrome.runtime.sendMessage({type:'media-intent',action:'toggle',id:crypto.randomUUID()});}
  },true);
  chrome.runtime.onMessage.addListener((msg,sender,reply)=>{
    if(msg.type!=='core-input')return;
    if(msg.kind==='text'){
      const field=document.activeElement;
      if(!field?.matches('input,textarea,[contenteditable=true]')){reply({ok:false,error:'Select a text field in the provider first'});return;}
      const existing=field.isContentEditable?field.textContent:field.value;
      const start=field.selectionStart ?? existing.length,end=field.selectionEnd ?? existing.length;
      let value=(existing.slice(0,start)+String(msg.value || '')+existing.slice(end)).slice(0,2000);
      if(msg.action==='clear')value='';
      if(msg.action==='backspace')value=start===end?Array.from(existing.slice(0,start)).slice(0,-1).join('')+existing.slice(end):existing.slice(0,start)+existing.slice(end);
      if(field.isContentEditable)field.textContent=value;
      else {const prototype=field.tagName==='TEXTAREA'?HTMLTextAreaElement.prototype:HTMLInputElement.prototype;Object.getOwnPropertyDescriptor(prototype,'value').set.call(field,value);}
      field.dispatchEvent(new Event('input',{bubbles:true}));
      if(msg.action==='enter')field.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true,cancelable:true}));
      reply({ok:true});return;
    }
    const key={up:'ArrowUp',down:'ArrowDown',left:'ArrowLeft',right:'ArrowRight',select:'Enter',back:'Escape',options:'ContextMenu'}[msg.action];
    if(!key){reply({ok:false,error:'Unsupported navigation action'});return;}
    window.dispatchEvent(new KeyboardEvent('keydown',{key,bubbles:true,cancelable:true}));reply({ok:true});
  });
})();
