// The isolated extension bridge owns tabs; Home remains a live document.
export function openServiceTab(url) { return requestService('bwos-open-service',{url}); }
export function syncServiceRecommendations(service,target=60) { return requestService('bwos-sync-recommendations',{service,target},70000); }
export function searchServiceRecommendations(service,query) { return requestService('bwos-search-recommendations',{service,query},70000); }
function requestService(type,payload,deadline=8000) {
  return new Promise((resolve, reject) => {
    const requestId = crypto.randomUUID();
    const finish = (result) => { clearTimeout(timeout); removeEventListener('message', receive); result?.ok ? resolve(result) : reject(new Error(result?.error || 'Could not open this app')); };
    const receive = event => {
      if (event.source === window && event.origin === location.origin && event.data?.type === 'bwos-open-result' && event.data.requestId === requestId) finish(event.data.result);
    };
    const timeout = setTimeout(() => finish({ error: 'Browser adapter unavailable. Restart BWOS and try again.' }), deadline);
    addEventListener('message', receive);
    window.postMessage({ type, requestId, ...payload }, location.origin);
  });
}
