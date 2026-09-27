// BWOS sound: soft synthesized interface sounds + generative ambient music for the BWOS screens.
// Everything is generated with the Web Audio API (no audio files) and kept very quiet.

let ctx = null;
let master = null;
const prefs = { ui: true, ambient: true };
try {
  prefs.ui = localStorage.getItem('bwos.sounds') !== 'off';
  prefs.ambient = localStorage.getItem('bwos.ambient') !== 'off';
} catch {}

function audio() {
  if (!ctx) {
    try {
      ctx = new AudioContext();
      master = ctx.createGain();
      master.gain.value = 0.9;
      master.connect(ctx.destination);
    } catch { return null; }
  }
  if (ctx.state === 'suspended') ctx.resume();
  return ctx;
}

/** One soft note: sine body + a faint octave "glass" partial, short attack, exponential decay. */
function note(freq, start, dur, gain, { type = 'sine', glass = 0.25, slideTo = null } = {}) {
  const a = audio();
  if (!a) return;
  const t0 = a.currentTime + start;
  const out = a.createGain();
  out.gain.setValueAtTime(0.0001, t0);
  out.gain.exponentialRampToValueAtTime(gain, t0 + 0.012);
  out.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
  const lp = a.createBiquadFilter();
  lp.type = 'lowpass';
  lp.frequency.value = 5200;
  out.connect(lp).connect(master);
  for (const [mult, level, kind] of [[1, 1, type], [2, glass, 'sine']]) {
    if (!level) continue;
    const o = a.createOscillator();
    const g = a.createGain();
    o.type = kind;
    o.frequency.setValueAtTime(freq * mult, t0);
    if (slideTo) o.frequency.exponentialRampToValueAtTime(slideTo * mult, t0 + dur);
    g.gain.value = level;
    o.connect(g).connect(out);
    o.start(t0);
    o.stop(t0 + dur + 0.05);
  }
}

// ------------------------------------------------------------------ ambient music
// Generative, so it never repeats: soft bell-like notes (a sine plus a quiet, faster-fading
// octave partial – think music box / electric piano) are placed at random moments, walking
// gently through a pentatonic set that fits the current harmony. A faint airy chord sits
// underneath and everything goes through a long synthetic reverb. Every 30–60 s the harmony
// drifts to a new "mood", and the note density slowly wanders between sparse and gentle.
// Nothing is below ~200 Hz (a high-pass removes any rumble) and there are no filter sweeps.

const midi = (n) => 440 * Math.pow(2, (n - 69) / 12);
const MOODS = [ // pad voicing + melody notes (MIDI numbers); every note is consonant with its pad
  { pad: [60, 64, 67, 71], notes: [67, 69, 71, 74, 76, 79, 81, 83] }, // Cmaj7   · G A B D E
  { pad: [57, 60, 64, 67], notes: [64, 67, 69, 72, 74, 76, 79, 81] }, // Am7     · A minor pentatonic
  { pad: [57, 60, 65, 67], notes: [65, 67, 69, 72, 74, 77, 79, 81] }, // Fadd9   · F major pentatonic
  { pad: [55, 59, 62, 67], notes: [62, 64, 67, 69, 71, 74, 76, 79] }, // G       · G major pentatonic
  { pad: [55, 59, 62, 64], notes: [64, 67, 69, 71, 74, 76, 79, 83] }, // Em7     · E minor pentatonic
  { pad: [57, 60, 62, 64], notes: [62, 64, 69, 72, 74, 76, 81, 84] }  // Dm(add9)· D E A C
];
const AMBIENT_LEVEL = 0.55;
const STEPS = [-2, -1, -1, 1, 1, 2, 3, -3]; // melodic random walk: mostly small steps
let amb = null;

function makeReverb(a, seconds) {
  const len = Math.floor(a.sampleRate * seconds);
  const buf = a.createBuffer(2, len, a.sampleRate);
  for (let ch = 0; ch < 2; ch++) {
    const d = buf.getChannelData(ch);
    for (let i = 0; i < len; i++) d[i] = (Math.random() * 2 - 1) * Math.pow(1 - i / len, 2.8);
  }
  const conv = a.createConvolver();
  conv.buffer = buf; // normalised by default
  return conv;
}

function startAmbient() {
  if (!prefs.ambient || amb) return;
  const a = audio();
  if (!a) return;

  const input = a.createGain();                 // every voice connects here
  const highpass = a.createBiquadFilter();
  highpass.type = 'highpass';
  highpass.frequency.value = 180;
  const soften = a.createBiquadFilter();
  soften.type = 'lowpass';
  soften.frequency.value = 3400;
  soften.Q.value = 0.3;
  const dry = a.createGain();
  dry.gain.value = 0.5;
  const wet = a.createGain();
  wet.gain.value = 0.85;
  const reverb = makeReverb(a, 4.5);
  const bus = a.createGain();
  bus.gain.value = 0.0001;
  input.connect(highpass).connect(soften);
  soften.connect(dry).connect(bus);
  soften.connect(reverb).connect(wet).connect(bus);
  bus.connect(master);

  const s = { a, bus, input, noteTimer: 0, driftTimer: 0, pad: [], mood: Math.floor(Math.random() * MOODS.length), density: 0.45, last: null, stopped: false };
  amb = s;
  setMood(s, s.mood, true);
  nextNote(s);
  const drift = () => {
    if (s.stopped) return;
    let m;
    do { m = Math.floor(Math.random() * MOODS.length); } while (m === s.mood);
    s.mood = m;
    setMood(s, m, false);
    s.driftTimer = setTimeout(drift, 30000 + Math.random() * 30000);
  };
  s.driftTimer = setTimeout(drift, 30000 + Math.random() * 30000);
  bus.gain.setTargetAtTime(AMBIENT_LEVEL, a.currentTime, 3); // slow fade in
}

/** Crossfades the soft chord underneath to a new mood. */
function setMood(s, index, first) {
  const a = s.a, t = a.currentTime;
  for (const v of s.pad) {
    v.g.gain.setTargetAtTime(0.0001, t, 3);
    setTimeout(() => { try { v.o.stop(); v.lfo.stop(); } catch {} }, 16000);
  }
  s.pad = MOODS[index].pad.map((note, n) => {
    const o = a.createOscillator();
    o.type = 'sine';
    o.frequency.value = midi(note);
    o.detune.value = (Math.random() - 0.5) * 8;
    const g = a.createGain();
    g.gain.value = 0.0001;
    g.gain.setTargetAtTime(0.016 / (1 + n * 0.4), t + (first ? 0 : 1.5), 4);
    const lfo = a.createOscillator();              // each voice "breathes" at its own slow pace
    lfo.frequency.value = 0.03 + Math.random() * 0.05;
    const depth = a.createGain();
    depth.gain.value = 0.005;
    lfo.connect(depth).connect(g.gain);
    const pan = a.createStereoPanner();
    pan.pan.value = (n / 3) * 1.2 - 0.6;
    o.connect(g).connect(pan).connect(s.input);
    o.start();
    lfo.start();
    return { o, g, lfo };
  });
}

function nextNote(s) {
  if (s.stopped) return;
  s.density = Math.min(0.85, Math.max(0.12, s.density + (Math.random() - 0.5) * 0.12));
  const wait = 900 + (1 - s.density) * 3800 + Math.random() * 1400;
  s.noteTimer = setTimeout(() => {
    if (s.stopped) return;
    bell(s, false);
    if (Math.random() < 0.18) setTimeout(() => !s.stopped && bell(s, true), 160 + Math.random() * 280);
    nextNote(s);
  }, wait);
}

/** One soft bell note. `harmony` = a quieter companion note a little below the melody. */
function bell(s, harmony) {
  const a = s.a, notes = MOODS[s.mood].notes;
  let i = s.last == null ? Math.floor(Math.random() * notes.length)
    : Math.max(0, Math.min(notes.length - 1, s.last + STEPS[Math.floor(Math.random() * STEPS.length)]));
  if (harmony) i = Math.max(0, i - 2); else s.last = i;
  const f = midi(notes[i]);
  const t = a.currentTime + 0.02;
  const peak = (harmony ? 0.022 : 0.034) + Math.random() * 0.022;
  const decay = 2.8 + Math.random() * 2.6;

  const g = a.createGain();
  g.gain.setValueAtTime(0.0001, t);
  g.gain.exponentialRampToValueAtTime(peak, t + 0.03 + Math.random() * 0.05);
  g.gain.exponentialRampToValueAtTime(0.0001, t + decay);
  const pan = a.createStereoPanner();
  pan.pan.value = (Math.random() - 0.5) * 1.2;
  const body = a.createOscillator();
  body.type = 'sine';
  body.frequency.value = f;
  const shimmer = a.createOscillator();         // octave partial that fades faster → bell timbre
  shimmer.type = 'sine';
  shimmer.frequency.value = f * 2;
  const sg = a.createGain();
  sg.gain.setValueAtTime(0.16, t);
  sg.gain.exponentialRampToValueAtTime(0.001, t + decay * 0.35);
  body.connect(g);
  shimmer.connect(sg).connect(g);
  g.connect(pan).connect(s.input);
  body.start(t); shimmer.start(t);
  body.stop(t + decay + 0.1); shimmer.stop(t + decay + 0.1);
}

function stopAmbient(fast) {
  if (!amb || !ctx) return;
  const s = amb;
  amb = null;
  s.stopped = true;
  clearTimeout(s.noteTimer);
  clearTimeout(s.driftTimer);
  s.bus.gain.setTargetAtTime(0.0001, ctx.currentTime, fast ? 0.15 : 0.8);
  setTimeout(() => {
    for (const v of s.pad) { try { v.o.stop(); v.lfo.stop(); } catch {} }
    s.bus.disconnect();
  }, fast ? 800 : 4000);
}
// ------------------------------------------------------------------ public API

export const sound = {
  get enabled() { return prefs.ui; },
  set enabled(v) {
    prefs.ui = v;
    try { localStorage.setItem('bwos.sounds', v ? 'on' : 'off'); } catch {}
  },
  get ambient() { return prefs.ambient; },
  set ambient(v) {
    prefs.ambient = v;
    try { localStorage.setItem('bwos.ambient', v ? 'on' : 'off'); } catch {}
    if (v) startAmbient(); else stopAmbient();
  },
  startAmbient,
  stopAmbient,

  move() { if (prefs.ui) note(1568, 0, 0.07, 0.014, { glass: 0.35 }); },
  edge() { if (prefs.ui) note(196, 0, 0.12, 0.03, { type: 'triangle', glass: 0 }); },
  tab() { if (prefs.ui) note(1175, 0, 0.12, 0.02, { glass: 0.5 }); },
  select() { if (prefs.ui) { note(784, 0, 0.22, 0.03); note(1175, 0.06, 0.32, 0.024); } },
  back() { if (prefs.ui) note(880, 0, 0.18, 0.024, { slideTo: 587 }); },
  launch() {
    if (!prefs.ui) return;
    stopAmbient(true);
    [523.25, 659.25, 783.99, 1046.5].forEach((f, i) => note(f, i * 0.07, 0.9, 0.026, { glass: 0.4 }));
  },
  error() { if (prefs.ui) { note(233, 0, 0.16, 0.035, { type: 'triangle', glass: 0 }); note(196, 0.12, 0.22, 0.03, { type: 'triangle', glass: 0 }); } }
};
