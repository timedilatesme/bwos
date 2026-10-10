using System;
using System.Linq;
using System.Speech.Synthesis;
using System.Threading;

namespace Bwos
{
    internal sealed class SpeechFeedback
    {
        private readonly object gate = new object();
        private readonly AutoResetEvent pending = new AutoResetEvent(false);
        private string text, language;
        private volatile bool stopped;
        private DateTime quietUntil;
        private SpeechSynthesizer current;
        public volatile bool Speaking;
        public string Reply = "";
        public event Action Changed;
        public bool SuppressInput { get { lock (gate) return Speaking || DateTime.UtcNow < quietUntil; } }
        public void Start() { new Thread(Run) { IsBackground = true, Name = "BWOS spoken replies" }.Start(); }
        public void Speak(string reply, string lang)
        {
            if (string.IsNullOrWhiteSpace(reply)) return;
            lock (gate) { text = reply; language = lang; }
            pending.Set();
        }
        public void Cancel() { lock (gate) { text = null; if (current != null) current.SpeakAsyncCancelAll(); } }
        public void Stop() { stopped = true; Cancel(); pending.Set(); }
        private void Notify() { var changed = Changed; if (changed != null) changed(); }
        private void Run()
        {
            while (!stopped)
            {
                pending.WaitOne(); if (stopped) return;
                string line, lang;
                lock (gate) { line = text; lang = language; text = null; }
                if (line == null) continue;
                try
                {
                    using (var speaker = new SpeechSynthesizer())
                    {
                        var voices = speaker.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();
                        var voice = voices.FirstOrDefault(v => v.Culture.TwoLetterISOLanguageName == (lang == "hi" ? "hi" : "en"))
                            ?? voices.FirstOrDefault(v => v.Name.IndexOf("Zira", StringComparison.OrdinalIgnoreCase) >= 0)
                            ?? voices.FirstOrDefault();
                        if (voice == null) continue;
                        speaker.SelectVoice(voice.Name); speaker.Rate = 0;
                        using (var done = new ManualResetEvent(false))
                        {
                            speaker.SpeakCompleted += delegate { done.Set(); };
                            lock (gate) { if (stopped) return; current = speaker; Speaking = true; Reply = line; speaker.SpeakAsync(line); }
                            Notify(); done.WaitOne();
                            lock (gate) current = null;
                        }
                    }
                }
                catch (Exception ex) { Log.Write("voice reply unavailable: " + ex.GetType().Name); }
                finally { lock (gate) { current = null; quietUntil = DateTime.UtcNow.AddMilliseconds(700); } Speaking = false; Notify(); }
            }
        }
    }
}
