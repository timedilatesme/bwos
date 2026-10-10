// The assistant's visual state follows the host, including real microphone activity.
export function assistantPhase(voice = {}) {
  if (voice.installing) return 'thinking';
  if (voice.status === 'error' || voice.status === 'missing') return 'error';
  if (voice.speaking) return 'speaking';
  if (voice.thinking || voice.status === 'loading' || voice.status === 'connecting') return 'thinking';
  if (!voice.listening) return 'paused';
  return Number(voice.inputLevel) >= 2 ? 'listening' : 'ready';
}

export function updateAssistant(voice = {}, enabled = false) {
  const el = document.getElementById('assistant');
  if (!el) return;
  el.hidden = !enabled;
  const phase = assistantPhase(voice);
  el.dataset.state = phase;
  el.style.setProperty('--voice-energy', String(Math.max(0, Math.min(1, Number(voice.inputLevel || 0) / 30))));
  const labels = { ready: 'Ready when you are', listening: 'Listening', thinking: 'Working on it', speaking: 'Speaking', paused: 'Voice paused', error: 'Voice needs attention' };
  document.getElementById('assistant-label').textContent = labels[phase];
}
