using System;
using System.Collections.Generic;
using System.Threading;

namespace Bwos
{
    internal sealed class VoiceService
    {
        private readonly Config config;
        private readonly Func<bool> foreground;
        private readonly object gate = new object();
        private readonly AutoResetEvent changed = new AutoResetEvent(false);
        private volatile bool stopped;
        private int revision, installing;
        private string status = "off", message = "Voice control is off", transcript = "";
        private string installMessage = "", installError = "";
        private bool listening;
        private bool capturing;
        private int inputLevel;
        private string signalMessage = "", feedback = "";
        private double confidence;
        private bool thinking;
        private string backend = "";
        private string inputProcessing = "";
        private DateTime overlayUntil;
        private string overlayText = "";
        public void Activate() { lock (gate) { overlayUntil = DateTime.UtcNow.AddSeconds(7); overlayText = feedback; } Notify(); }
        public void HideOverlay() { lock (gate) overlayUntil = DateTime.MinValue; Notify(); }
        public void ShowFeedback(string text) { lock (gate) feedback = text; Activate(); }
        private readonly SpeechFeedback speaker = new SpeechFeedback();
        private DateTime captureStarted, lastSignal, lastBuffer, lastMeterUpdate;
        private sealed class Pending { public VoiceCommand Command; public bool Service; public DateTime At; }
        private readonly List<Pending> commands = new List<Pending>();
        public event Action Changed;
        public event Action<VoiceCommand> Command;
        public VoiceService(Config config, Func<bool> foreground) { this.config = config; this.foreground = foreground; speaker.Changed += Notify; }
        public void Start()
        {
            speaker.Start();
            if (config.VoiceEnabled && !Available()) Install();
            var worker = new Thread(Run) { IsBackground = true, Name = "BWOS offline voice" };
            worker.Start();
        }
        public void Stop() { stopped = true; speaker.Stop(); Refresh(); }
        private bool Available() { return config.VoiceEngine == "whisper" ? WhisperAssets.Available : VoiceAssets.Available(config.VoiceLanguage); }
        public void Reply(string text) { if (config.VoiceEnabled && config.VoiceReplies) speaker.Speak(text, config.VoiceLanguage); }
        public void Refresh()
        {
            speaker.Cancel();
            lock (gate) { commands.Clear(); transcript = ""; feedback = ""; inputLevel = 0; signalMessage = ""; thinking = false; backend = ""; inputProcessing = ""; }
            Interlocked.Increment(ref revision); changed.Set();
        }
        public Dictionary<string, object> Describe() { return Describe(true); }
        public Dictionary<string, object> Describe(bool microphones)
        {
            lock (gate) return new Dictionary<string, object> {
                { "status", status }, { "message", message }, { "transcript", transcript }, { "listening", listening },
                { "available", Available() }, { "installing", installing != 0 },
                { "installMessage", installMessage }, { "installError", installError },
                { "capturing", capturing }, { "inputLevel", inputLevel }, { "signalMessage", signalMessage },
                { "inputProcessing", inputProcessing },
                { "feedback", feedback }, { "confidence", confidence },
                { "thinking", thinking }, { "speaking", speaker.Speaking }, { "reply", speaker.Reply }, { "backend", backend }, { "engine", config.VoiceEngine },
                { "overlayVisible", config.VoiceEnabled && overlayUntil != DateTime.MinValue && (DateTime.UtcNow < overlayUntil || speaker.Speaking) }, { "overlayText", overlayText },
                { "microphones", microphones ? (object)Microphone.Devices() : new object[0] }
            };
        }
        private void SetStatus(string state, string text, bool active)
        {
            bool notify;
            lock (gate) { notify = status != state || message != text || listening != active; status = state; message = text; listening = active; }
            if (notify) Notify();
        }
        private void Notify() { var h = Changed; if (h != null) h(); }
        private void ObserveInput(byte[] samples)
        {
            DateTime now = DateTime.UtcNow;
            bool notify = false;
            lock (gate)
            {
                lastBuffer = now;
                inputLevel = Microphone.Level(samples);
                if (inputLevel >= 1) lastSignal = now;
                signalMessage = (now - lastSignal).TotalSeconds < 3 ? "Microphone signal detected" :
                    (now - captureStarted).TotalSeconds > 5 ? "No microphone signal — check input, mute and Windows input volume" : "Speak to test the microphone";
                if ((now - lastMeterUpdate).TotalMilliseconds >= 200) { lastMeterUpdate = now; notify = true; }
            }
            if (notify) Notify();
        }
        public void Install()
        {
            if (Interlocked.CompareExchange(ref installing, 1, 0) != 0) return;
            lock (gate) { installMessage = "Starting download…"; installError = ""; }
            Notify();
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Action<string> progress = text => { lock (gate) installMessage = text; Notify(); };
                    if (config.VoiceEngine == "whisper") WhisperAssets.Install(progress); else VoiceAssets.Install(progress);
                }
                catch (Exception ex) { lock (gate) { installError = "Voice download failed: " + ex.Message; installMessage = ""; } }
                finally { Interlocked.Exchange(ref installing, 0); Notify(); Refresh(); }
            });
        }
        public void Queue(VoiceCommand command, bool service)
        {
            lock (gate)
            {
                commands.RemoveAll(c => (DateTime.UtcNow - c.At).TotalSeconds > 15);
                if (commands.Count >= 8) commands.RemoveAt(0);
                commands.Add(new Pending { Command = command, Service = service, At = DateTime.UtcNow });
            }
            Notify();
        }
        public object Claim(bool service)
        {
            lock (gate)
            {
                var batch = new List<Dictionary<string, object>>();
                commands.RemoveAll(c => (DateTime.UtcNow - c.At).TotalSeconds > 15);
                foreach (var c in commands) if (c.Service == service) batch.Add(c.Command.ToJson());
                commands.RemoveAll(c => c.Service == service);
                return new Dictionary<string, object> { { "commands", batch }, { "enabled", config.VoiceEnabled }, { "listening", listening }, { "message", message }, { "transcript", transcript }, { "inputLevel", inputLevel }, { "speaking", speaker.Speaking }, { "thinking", thinking }, { "reply", speaker.Reply } };
            }
        }
        private void Run()
        {
            while (!stopped)
            {
                int current = revision;
                if (!config.VoiceEnabled) { SetStatus("off", "Voice control is off", false); changed.WaitOne(); continue; }
                if (!Available()) { SetStatus("missing", installing != 0 ? "Setting up your local assistant…" : "Download local voice in Settings to get started", false); changed.WaitOne(); continue; }
                try
                {
                    SetStatus("loading", "Preparing local voice… First GPU warm-up can take a minute.", false);
                    if (config.VoiceEngine == "whisper") { RunWhisper(current); continue; }
                    using (var engine = new VoskEngine(config.VoiceLanguage))
                    using (var mic = new Microphone(config.VoiceMicrophone))
                    {
                        lock (gate) { capturing = true; captureStarted = DateTime.UtcNow; lastSignal = DateTime.MinValue; lastBuffer = captureStarted; }
                        var session = new VoiceSession();
                        bool wasFront = true;
                        while (!stopped && current == revision)
                        {
                            bool front = foreground();
                            if (config.VoiceMode == "direct" && !front && wasFront) engine.Reset();
                            wasFront = front;
                            bool active = !session.Sleeping && (config.VoiceMode == "direct" ? front : config.VoiceMode == "everywhere" || session.Active(DateTime.UtcNow));
                            SetStatus("ready", session.Sleeping ? "Paused — say Hey console to resume" : config.VoiceMode == "everywhere" ? "Listening for commands anywhere" : config.VoiceMode == "direct" ? (front ? "Listening — say a command" : "Waiting for BWOS to be on screen") : active ? "Listening for your command" : "Say Hey console", active);
                            mic.Read(samples =>
                            {
                                if (inputProcessing != mic.Processing) { lock (gate) inputProcessing = mic.Processing; engine.Reset(); }
                                ObserveInput(samples);
                                if (speaker.SuppressInput) { engine.Reset(); return; }
                                if (config.VoiceMode == "direct" && !foreground()) return;
                                string text = engine.Feed(samples);
                                if (string.IsNullOrWhiteSpace(engine.LastText)) return;
                                if (string.IsNullOrWhiteSpace(text))
                                {
                                    lock (gate) { transcript = engine.LastText; confidence = engine.LastConfidence; feedback = "Heard “" + transcript + "” — unclear; please repeat"; }
                                    Notify(); return;
                                }
                                var command = session.Accept(text, config.VoiceMode, foreground(), DateTime.UtcNow);
                                bool woke; VoiceCommands.StripWake(text, out woke);
                                if (config.VoiceMode == "wake" && !woke && !session.Active(DateTime.UtcNow)) return;
                                lock (gate)
                                {
                                    transcript = text; confidence = engine.LastConfidence;
                                    feedback = command != null ? "Heard “" + text + "”" : woke ? "Ready — say your command" :
                                        session.Sleeping ? "Paused — say Hey console to resume" :
                                        config.VoiceMode == "wake" && !session.Active(DateTime.UtcNow) ? "Heard “" + text + "” — say Hey console first" :
                                        "Heard “" + text + "” — command not recognized";
                                }
                                if (command != null || woke) Activate();
                                Notify();
                                if (command != null && command.Action != "sleepVoice")
                                {
                                    // Keep microphone polling responsive while window operations run.
                                    var h = Command;
                                    if (h != null) h(command);
                                }
                            });
                            bool stalled = false;
                            lock (gate)
                            {
                                const string stalledMessage = "No audio buffers received — reselect your microphone";
                                if ((DateTime.UtcNow - lastBuffer).TotalSeconds > 5 && signalMessage != stalledMessage) { inputLevel = 0; signalMessage = stalledMessage; stalled = true; }
                            }
                            if (stalled) Notify();
                            Thread.Sleep(20);
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (gate) { capturing = false; inputLevel = 0; }
                    Log.Write("voice: " + ex.Message);
                    SetStatus("error", ex.Message, false);
                    if (current == revision && !stopped) changed.WaitOne();
                }
                finally
                {
                    lock (gate) { capturing = false; inputLevel = 0; }
                    Notify();
                }
            }
            SetStatus("off", "Voice control is off", false);
        }
        private void RunWhisper(int current)
        {
            using (var engine = new WhisperEngine()) using (var mic = new Microphone(config.VoiceMicrophone))
            using (var available = new AutoResetEvent(false)) using (var finished = new ManualResetEvent(false))
            {
                var segmenter = new SpeechSegmenter(); var session = new VoiceSession(); var requests = new SpeechRequests();
                var decoder = new Thread(delegate()
                {
                    while (!stopped && current == revision && !finished.WaitOne(0))
                    {
                        var request = requests.Take(DateTime.UtcNow);
                        if (request == null) { available.WaitOne(100); continue; }
                        try
                        {
                            lock (gate) { thinking = config.VoiceMode != "wake" || session.Active(DateTime.UtcNow); if (thinking) feedback = "Understanding your request…"; }
                            Notify();
                            string text = engine.Transcribe(request.Audio, config.VoiceLanguage);
                            if (stopped || current != revision || finished.WaitOne(0) ||
                                (DateTime.UtcNow - request.At).TotalSeconds > 15 || (config.VoiceMode == "direct" && !foreground())) continue;
                            if (string.IsNullOrWhiteSpace(text)) { lock (gate) feedback = "Couldn't hear that clearly — try again"; continue; }
                            var command = session.Accept(text, config.VoiceMode, foreground(), DateTime.UtcNow);
                            bool woke; VoiceCommands.StripWake(text, out woke);
                            if (config.VoiceMode == "wake" && !woke && !session.Active(DateTime.UtcNow))
                            {
                                lock (gate) { transcript = text; confidence = engine.LastConfidence; feedback = "Wake phrase not recognized — try Hey console, then pause briefly"; }
                                continue;
                            }
                            lock (gate)
                            {
                                transcript = text; confidence = engine.LastConfidence;
                                feedback = command != null ? "Heard “" + text + "”" : woke ? "Ready — say your command" : "Heard “" + text + "” — command not recognized";
                            }
                            if (command != null || woke) Activate();
                            // A wake-only acknowledgement is visual. Speaking here used to
                            // mute the microphone just as the user gave their next command.
                            if (command != null)
                            {
                                if (command.Action == "sleepVoice") Reply("Voice paused.");
                                else { var handler = Command; if (handler != null) handler(command); }
                            }
                        }
                        catch (Exception ex) { if (current == revision) { lock (gate) feedback = "Recognition failed: " + ex.Message; } }
                        finally { if (current == revision) { lock (gate) thinking = false; Notify(); } }
                    }
                }) { IsBackground = true, Name = "BWOS speech decoder" };
                lock (gate) { capturing = true; captureStarted = DateTime.UtcNow; lastSignal = DateTime.MinValue; lastBuffer = captureStarted; backend = engine.Backend; }
                decoder.Start();
                try
                {
                    while (!stopped && current == revision)
                    {
                        bool front = foreground();
                        bool active = !session.Sleeping && (config.VoiceMode == "direct" ? front : config.VoiceMode == "everywhere" || session.Active(DateTime.UtcNow));
                        SetStatus("ready", speaker.Speaking ? speaker.Reply : session.Sleeping ? "Paused — say Hey console to resume" :
                            config.VoiceMode == "everywhere" ? "Listening for commands anywhere" : config.VoiceMode == "direct" ? (front ? "Listening — speak naturally" : "Waiting for BWOS to be on screen") : active ? "Listening for your command" : "Say Hey console", active && !speaker.SuppressInput);
                        mic.Read(samples =>
                        {
                            if (inputProcessing != mic.Processing) { lock (gate) inputProcessing = mic.Processing; segmenter.Reset(); requests.Clear(); }
                            ObserveInput(samples);
                            if (speaker.SuppressInput || (config.VoiceMode == "direct" && !foreground())) { segmenter.Reset(); return; }
                            segmenter.MaximumBytes = config.VoiceMode == "wake" && !session.Active(DateTime.UtcNow) ? 128000 : 384000;
                            byte[] utterance = segmenter.Feed(samples);
                            if (utterance != null) { requests.Add(utterance, DateTime.UtcNow); available.Set(); }
                        });
                        Thread.Sleep(20);
                    }
                }
                finally
                {
                    requests.Clear(); finished.Set(); available.Set();
                    // Complete inference before disposing its engine or synchronization handles.
                    decoder.Join();
                }
            }
        }
    }
}
