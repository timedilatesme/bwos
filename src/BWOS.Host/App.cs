using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Bwos
{
    /// <summary>Wires the services together and implements the HTTP API used by the UI.</summary>
    internal sealed class App
    {
        public const string Version = "1.8.0";
        /// <summary>How this PC appears in phones' cast and AirPlay menus.</summary>
        public static readonly string CastName = "BWOS (" + Environment.MachineName + ")";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public readonly Config Config;
        public readonly Library Library;
        public readonly ShellManager Shell;
        public readonly GameManager Games;
        public readonly WebServer Server;
        public readonly GamepadService Gamepad;
        public readonly Catalog Catalog;
        public readonly Tracker Tracker;
        public readonly PhoneRemote Remote;
        public readonly CastReceiver Cast;
        public readonly AirPlayReceiver AirPlay;
        public readonly VoiceService Voice;
        public readonly Devices Devices;
        public readonly PlaybackService Playback;
        public readonly VideoLibrary Videos;
        public readonly RecommendationFeeds Recommendations;
        private readonly Queue<VoiceCommand> voiceDispatch = new Queue<VoiceCommand>();
        private bool voiceDispatching;
        private string pendingCast;          // YouTube URL a phone cast, waiting for the BWOS screen to open it
        private DateTime pendingCastAt;
        private readonly InputBridge bridge;
        private DateTime lastTrigger = DateTime.MinValue;
        private string pendingLaunchKey;   // launched from BWOS, not yet detected as running
        private string launchedKey;        // the game BWOS launched (return to BWOS when it closes)
        private string lastRunningKey;
        private DateTime resumedAt = DateTime.MinValue;
        public bool Dev;

        /// <summary>Set by the tray: quits the app on the UI thread.</summary>
        public Action RequestExit;

        public App()
        {
            Config = new Config();
            Library = new Library(Config);

            Shell = new ShellManager(Config);
            Games = new GameManager(Library);
            Games.RunningChanged += OnRunningChanged;

            Server = new WebServer(Config.Port);
            Server.ApiHandler = HandleApi;
            Shell.SetShellUrl(Server.BaseUrl + "#t=" + Server.Token);
            Library.Changed += delegate { Server.Broadcast("library", null); };
            Library.Refresh();

            Catalog = new Catalog(Config);
            Tracker = new Tracker(Config, Catalog);
            Catalog.History = Tracker.History;
            Catalog.Changed += delegate { Server.Broadcast("foryou", null); };
            Tracker.Changed += delegate { Server.Broadcast("upnext", null); };

            Remote = new PhoneRemote(Shell, Config.Port + 1);
            Remote.MediaControl = delegate(string action)
            {
                var candidates = Playback.Describe() as Dictionary<string, object>[];
                var player = candidates.FirstOrDefault(p => !Json.Bool(p, "background", false)) ?? candidates.FirstOrDefault();
                if (player == null) return "Start a video in BWOS first.";
                if (!Json.Bool(player, "canControl", true)) return "Wait for the player to load, or use Retry if playback failed.";
                var result = Playback.Control(Json.Str(player, "id", ""), action, "10") as Dictionary<string, object>;
                return Json.Bool(result, "ok", false) ? null : Json.Str(result, "error", "Player could not respond.");
            };
            Devices = new Devices();
            Playback = new PlaybackService();
            Videos = new VideoLibrary();
            Recommendations = new RecommendationFeeds();
            Recommendations.Changed += delegate { Server.Broadcast("foryou", null); };
            Playback.Changed += delegate { Server.Broadcast("playback", null); };
            Devices.Changed += delegate { Server.Broadcast("devices", null); };
            Voice = new VoiceService(Config, Shell.ShellIsForeground);
            Voice.Changed += delegate { Server.Broadcast("voice", null); };
            Voice.Command += EnqueueVoice;
            Remote.HomePressed += delegate { OnTrigger("phone remote"); };
            Remote.Paired += delegate { Server.Broadcast("phone", null); };
            Cast = new CastReceiver(Config.Port + 2, () => Config.CastEnabled);
            Cast.Launch += OnCast;
            AirPlay = new AirPlayReceiver(Shell, () => Config.AirPlayEnabled, CastName);
            AirPlay.ExePath = AirPlayReceiver.FindExe(Config.AirPlayPath);
            // "Stop casting" on the phone: leave YouTube (but never another app that's on screen).
            Cast.Stop += delegate
            {
                if (Shell.ServiceIsForeground() && Native.WindowText(Native.GetForegroundWindow()).IndexOf("YouTube", StringComparison.OrdinalIgnoreCase) >= 0)
                    Shell.GoHome();
            };
            Remote.Typed += delegate(string action)
            {
                var d = new Dictionary<string, object>();
                d["action"] = action;
                Server.Broadcast("keyboard", d);
            };

            try
            {
                Gamepad = new GamepadService();
                Gamepad.TriggerMode = Config.Trigger;
                Gamepad.Triggered += delegate { OnTrigger("controller " + Gamepad.TriggerMode); };
                Gamepad.ConnectionChanged += delegate(int slot, bool on)
                {
                    var d = new Dictionary<string, object>();
                    d["slot"] = slot;
                    d["connected"] = on;
                    Log.Write("controller slot " + slot + (on ? " connected" : " disconnected"));
                    Server.Broadcast("controller", d);
                };
                bridge = new InputBridge(Shell, () => Config.ControllerBridge);
                Gamepad.StateSink = bridge.OnState;
            }
            catch (Exception ex)
            {
                Log.Write("gamepad unavailable: " + ex.Message);
            }
        }

        public void Start()
        {
            Shell.CloseStaleShell();
            Shell.RefreshAdapterCache();
            Server.Start();
            if (Gamepad != null) Gamepad.Start();
            Cast.Start();
            AirPlay.Start();
            Remote.StartIfPaired();
            Devices.Start();
            Voice.Start();
            if (!AirPlay.Installed) Log.Write("airplay: UxPlay not installed (see README → AirPlay)");
            Log.Write("BWOS " + Version + " listening on " + Server.BaseUrl + " (guide button " +
                (Gamepad != null && Gamepad.GuideSupported ? "supported" : "NOT supported") + ")");
        }

        public void Stop()
        {
            if (Gamepad != null) Gamepad.Stop();
            Cast.Shutdown();
            AirPlay.Stop();
            Devices.Stop();
            Voice.Stop();
            Server.Stop();
        }

        /// <summary>The controller's home button (or hotkey) was pressed somewhere in Windows.</summary>
        public void OnTrigger(string source)
        {
            lock (this)
            {
                if ((DateTime.UtcNow - lastTrigger).TotalMilliseconds < 700) return;
                lastTrigger = DateTime.UtcNow;
            }
            try
            {
                Native.WakeDisplays();
                if (Shell.ServiceIsForeground())
                {
                    // Watching something inside BWOS: home leaves the service for the BWOS home screen.
                    Shell.GoHome();
                    Log.Write("trigger (" + source + "): requested streaming service -> BWOS home");
                    return;
                }
                if (Shell.ShellIsForeground())
                {
                    // Pressing home inside BWOS jumps back into the running game, like a console.
                    if (Games.RunningKey != null && Games.Resume()) { Log.Write("trigger (" + source + "): resumed game"); return; }
                    Log.Write("trigger (" + source + "): already in BWOS -> home");
                    Server.Broadcast("home", null);
                    Shell.HoldShellForeground();
                    return;
                }
                bool wasRunning = Shell.ShowShell();
                Log.Write("trigger (" + source + "): " + (wasRunning ? "brought BWOS to front" : "started BWOS"));
                if (wasRunning) Server.Broadcast("focus", null);
            }
            catch (Exception ex)
            {
                Log.Write("trigger failed: " + ex);
            }
        }

        /// <summary>
        /// A phone cast to BWOS: bring the BWOS window up and have its home screen open YouTube's TV app
        /// with the phone's pairing code. The screen claims the URL once (see /api/cast/claim), whether it
        /// was already showing, had a streaming app open, or has to start first.
        /// </summary>
        private void OnCast(string url)
        {
            lock (this) { pendingCast = url; pendingCastAt = DateTime.UtcNow; }
            Native.WakeDisplays();
            if (Shell.ServiceIsForeground()) { Shell.GoHome(); return; }   // the home screen loads and claims it
            if (!Shell.ShowShell()) return;                                   // starting: claimed at boot
            Thread.Sleep(400);
            if (Shell.ServiceIsForeground()) Shell.GoHome();
            else Server.Broadcast("cast", null);
        }

        private string ClaimCast()
        {
            lock (this)
            {
                string url = pendingCast != null && (DateTime.UtcNow - pendingCastAt).TotalSeconds < 60 ? pendingCast : null;
                pendingCast = null;
                return url;
            }
        }

        /// <summary>The PC woke from sleep or hibernation.</summary>
        public void OnResumed()
        {
            resumedAt = DateTime.UtcNow;
            if (!Config.OpenOnWake) return;
            Thread.Sleep(2500); // let the displays come back first
            OpenAfterWake("wake");
        }

        /// <summary>Signed in at the lock screen. Right after a wake, that's when BWOS can actually come to the front.</summary>
        public void OnUnlocked()
        {
            if (!Config.OpenOnWake || (DateTime.UtcNow - resumedAt).TotalMinutes > 15) return;
            Thread.Sleep(800);
            OpenAfterWake("unlock after wake");
        }

        private void OpenAfterWake(string why)
        {
            if (Games.RunningKey != null) return; // a game was left running: it stays in front
            try
            {
                if (Shell.ShowShell()) Server.Broadcast("focus", null);
                Log.Write(why + ": opened BWOS");
            }
            catch (Exception ex)
            {
                Log.Write(why + ": couldn't open BWOS: " + ex.Message);
            }
        }

        private void OnRunningChanged(string key)
        {
            if (Server == null) return;
            var d = new Dictionary<string, object>();
            d["key"] = key;
            Server.Broadcast("running", d);
            string previous = lastRunningKey;
            lastRunningKey = key;
            if (key != null)
            {
                // The game is up: get the (fullscreen) interface out of its way.
                if (key == pendingLaunchKey)
                {
                    pendingLaunchKey = null;
                    launchedKey = key;
                    Thread.Sleep(1200);
                    Shell.HideShell();
                }
            }
            else if (previous != null && previous == launchedKey)
            {
                // A game started from BWOS closed: come back to the couch UI, like a console would.
                // (Games started from the desktop don't pull BWOS up.)
                launchedKey = null;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    Thread.Sleep(1500);
                    try { Shell.ShowShell(); } catch { }
                });
            }
        }

        private void EnqueueVoice(VoiceCommand command)
        {
            lock (voiceDispatch)
            {
                // Do not build an unbounded backlog while a launcher takes foreground time.
                if (voiceDispatch.Count >= 8) return;
                voiceDispatch.Enqueue(command);
                if (voiceDispatching) return;
                voiceDispatching = true;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                while (true)
                {
                    VoiceCommand next;
                    lock (voiceDispatch)
                    {
                        if (voiceDispatch.Count == 0) { voiceDispatching = false; return; }
                        next = voiceDispatch.Dequeue();
                    }
                    try { DispatchVoice(next); } catch (Exception ex) { Log.Write("voice action: " + ex.Message); }
                }
            });
        }

        private void DispatchVoice(VoiceCommand command)
        {
            if (!Config.VoiceEnabled) return;
            // Direct mode never injects into a game or another desktop app, including after a focus change.
            if (Config.VoiceMode == "direct" && !Shell.ShellIsForeground()) return;
            Voice.Activate();
            if (command.Action == "showAssistant") { Voice.Reply("I'm listening."); return; }
            if ((command.Action == "youtubeSearch" && Shell.YouTubeIsForeground()) || (command.Action == "search" && Shell.ServiceIsForeground()))
            {
                Voice.Reply("Searching for " + command.Value.Substring(0, Math.Min(140, command.Value.Length)) + ".");
                Voice.Queue(command, true); return;
            }
            if (command.Action == "resumeGame") { Games.Resume(); return; }
            if (command.Action == "home")
            {
                Voice.Reply("Going home.");
                if (Shell.ServiceIsForeground()) Shell.GoHome();
                else { if (!Shell.ShellIsForeground()) Shell.ShowShell(); Server.Broadcast("home", null); }
                return;
            }
            if (Array.IndexOf(new[] { "volup", "voldown", "mute", "unmute", "volumeSet", "volumeChange" }, command.Action) >= 0)
            {
                try { Voice.Reply(AudioVolume.Apply(command.Action, command.Value)); }
                catch (Exception ex) { Voice.ShowFeedback("Could not change volume: " + ex.Message); }
                return;
            }
            if (command.Action == "keyboard")
            {
                if (Shell.ServiceIsForeground()) Voice.Queue(command, true);
                else { if (!Shell.ShellIsForeground()) Shell.ShowShell(); Voice.Queue(command, false); }
                return;
            }
            var mediaActions = new Dictionary<string, string> { { "pause", "pause" }, { "resume", "resume" }, { "togglePlayback", "toggle" }, { "stopPlayback", "stop" }, { "reopenPlayer", "focus" }, { "restartMedia", "restart" }, { "seekForward", "seekForward" }, { "seekBack", "seekBack" }, { "nextMedia", "nextMedia" }, { "previousMedia", "previousMedia" }, { "subtitlesOn", "subtitlesOn" }, { "subtitlesOff", "subtitlesOff" } };
            string mediaAction;
            if (mediaActions.TryGetValue(command.Action, out mediaAction))
            {
                if (mediaAction == "focus" && Shell.ServiceIsForeground()) { Voice.ShowFeedback("Player is already open."); return; }
                if (Shell.ServiceIsForeground()) { command.Action = mediaAction; Voice.Queue(command, true); }
                else
                {
                    var player = (Playback.Describe() as Dictionary<string, object>[]).FirstOrDefault();
                    if (player != null)
                    {
                        if (Json.Bool(player, "local", false) && (mediaAction == "focus" || mediaAction == "resume") && !Shell.ShellIsForeground()) Shell.ShowShell();
                        Playback.Control(Json.Str(player, "id", ""), mediaAction, command.Value);
                    }
                    else if (command.Action == "resume" && Games.RunningKey != null) Games.Resume();
                    else Voice.ShowFeedback("Start media in a streaming app first.");
                }
                return;
            }
            if (command.Action == "open" || command.Action == "browseService" || command.Action == "openServiceApp" || command.Action == "search" || command.Action == "help" || command.Action == "youtubeSearch" || command.Action == "playerControls")
            {
                if (Shell.ServiceIsForeground()) Shell.GoHome();
                else if (!Shell.ShellIsForeground()) Shell.ShowShell();
            }
            if (!Shell.ShellIsForeground()) return;
            if (Shell.ServiceIsForeground() && Array.IndexOf(new[] { "up", "down", "left", "right", "accept", "back" }, command.Action) >= 0)
            {
                Remote.Press(command.Action == "accept" ? "ok" : command.Action);
                return;
            }
            bool homeCommand = command.Action == "open" || command.Action == "browseService" || command.Action == "openServiceApp" || command.Action == "search" || command.Action == "help" || command.Action == "youtubeSearch" || command.Action == "playerControls";
            Voice.Queue(command, !homeCommand && Shell.ServiceIsForeground());
        }

        // ------------------------------------------------------------------ API

        private object HandleApi(Request r)
        {
            string route = r.Segments.Length > 1 ? r.Segments[1] : "";
            string sub = r.Segments.Length > 2 ? r.Segments[2] : "";

            if (r.Method == "GET")
            {
                switch (route)
                {
                    case "state": return State();
                    case "devices": return Devices.Describe();
                    case "voice": return Voice.Describe();
                    case "playback": return sub == "diagnostics" ? Playback.Diagnostics() : Playback.Describe();
                    case "art": return Art(sub, r.Segments.Length > 3 ? r.Segments[3] : "portrait");
                    case "avatar":
                        var u = Library.User;
                        return new FileResult { Path = u == null ? null : u.AvatarPath };
                }
                return null;
            }

            var b = r.Body;
            switch (route)
            {
                case "stream":
                    if (sub == "start") return Remote.StartScreen();
                    return Remote.Screen.Exchange(true, Json.Str(b, "session", ""), sub, b);
                case "browser":
                    if (sub == "ping") { Shell.IntegrationPing(); return Ok(); }
                    if (sub == "error") { Voice.ShowFeedback(Json.Str(b, "message", "Browser integration needs attention")); return Ok(); }
                    if (sub == "context")
                    {
                        string kind = Json.Str(b, "kind", "");
                        if (kind != "home" && kind != "youtube" && kind != "service") return WebServer.Err("Unknown browser context");
                        Shell.ReportContext(kind, Json.Bool(b, "foreground", false)); return Ok();
                    }
                    break;
                case "playback":
                    if (sub == "local-report") { b["id"] = "0"; b["local"] = true; Playback.Report(b); return Ok(); }
                    if (sub == "local-claim") return Playback.Claim(true);
                    if (sub == "diagnostic") { Playback.Diagnostic(b); return Ok(); }
                    if (sub == "error") { Playback.Error(Json.Str(b, "id", ""), Json.Str(b, "error", "")); return Ok(); }
                    if (sub == "report") { Playback.Report(b); return Ok(); }
                    if (sub == "claim") return Playback.Claim();
                    if (sub == "control") return Playback.Control(Json.Str(b, "id", ""), Json.Str(b, "action", ""), Json.Str(b, "value", ""));
                    break;
                case "videos":
                    if (sub == "add") return Videos.Add(Json.Str(b, "url", ""));
                    if (sub == "remove") return Videos.Remove(Json.Str(b, "id", ""));
                    break;
                case "recommendations":
                    if (sub == "report") return Recommendations.Report(b);
                    if (sub == "preview") return Recommendations.Preview(b);
                    if (sub == "status") return Recommendations.Status(b);
                    break;
                case "devices":
                    if (sub == "refresh") { ThreadPool.QueueUserWorkItem(delegate { Devices.Refresh(); }); return Ok(); }
                    if (sub == "settings") return Bwos.Devices.OpenSettings(Json.Str(b, "kind", "bluetooth"));
                    if (sub == "connect" || sub == "disconnect") return Devices.Connect(Json.Str(b, "id", ""), sub == "connect");
                    break;
                case "voice":
                    if (sub == "install") { Voice.Install(); return Ok(); }
                    if (sub == "reply") { string reply = Json.Str(b, "text", ""); if (reply.Length > 200) return WebServer.Err("Reply too long"); Voice.Reply(reply); return Ok(); }
                    if (sub == "claim" || sub == "service")
                    {
                        bool service = Shell.ServiceIsForeground();
                        if (!Shell.ShellIsForeground() || (sub == "service" ? !service : service))
                            return new Dictionary<string, object> { { "commands", new object[0] } };
                        return Voice.Claim(sub == "service");
                    }
                    if (sub == "type")
                    {
                        string text = Json.Str(b, "text", "");
                        if (!Shell.ServiceIsForeground() || text.Length > 300) return WebServer.Err("Select a search box inside BWOS first");
                        string error = Remote.Type(text, "enter");
                        return error == null ? Ok() : WebServer.Err(error);
                    }
                    break;
                case "launch":
                {
                    var g = Library.Find(Json.Str(b, "key", ""));
                    if (g == null) return WebServer.Err("Game not found");
                    pendingLaunchKey = g.Key;
                    Games.Launch(g);
                    return Ok();
                }

                case "game":
                    if (sub == "resume") return Games.Resume() ? Ok() : WebServer.Err("Couldn't find the game window");
                    if (sub == "quit") return Games.Quit() ? Ok() : WebServer.Err("No game is running");
                    break;

                case "hide":
                    Config.ToggleHidden(Json.Str(b, "key", ""));
                    if (Json.Str(b, "key", "").StartsWith("watch:")) Catalog.RebuildSoon();
                    return Ok();
                case "progress":
                    if (sub == "lookup")
                    {
                        var d = new Dictionary<string, object>();
                        d["position"] = Tracker.PositionFor(Json.Str(b, "url", ""));
                        return d;
                    }
                    if (sub == "dismiss") { Tracker.Dismiss(Json.Str(b, "id", "")); return Ok(); }
                    Tracker.Report(b);
                    return Ok();
                case "keyboard":
                    try { return Remote.Open(); }
                    catch (Exception ex)
                    {
                        Log.Write("phone remote failed: " + ex.Message);
                        return WebServer.Err("Couldn't start the phone remote: " + ex.Message);
                    }
                case "phones":
                    if (sub != "forget") break;
                    Remote.ForgetPhones();
                    return Ok();
                case "cast":
                    if (sub != "claim") break;
                    var cd = new Dictionary<string, object>();
                    cd["url"] = ClaimCast();
                    return cd;
                case "foryou":
                    ThreadPool.QueueUserWorkItem(delegate { try { Catalog.Rebuild(); } catch (Exception ex) { Log.Write("catalog: " + ex.Message); } });
                    return Ok();
                case "log":
                    string msg = Json.Str(b, "message", "");
                    Log.Write("ui: " + (msg.Length > 300 ? msg.Substring(0, 300) : msg));
                    return Ok();
                case "refresh":
                    Config.Load();
                    Library.Refresh();
                    return Ok();
                case "settings":
                    return ApplySetting(Json.Str(b, "key", ""), b.ContainsKey("value") ? b["value"] : null);
                case "system":
                    return SystemAction(Json.Str(b, "action", ""));
            }
            return null;
        }

        private Dictionary<string, object> State()
        {
            var d = new Dictionary<string, object>();
            string running = Games.RunningKey;
            var hidden = new HashSet<string>(Config.Hidden);
            d["version"] = Version;
            d["games"] = Library.Games.Select(g =>
            {
                var j = g.ToJson(running);
                j["hidden"] = hidden.Contains(g.Key);
                return j;
            }).ToList();
            d["watch"] = Config.Watch.Select(w =>
            {
                var c = new Dictionary<string, object>(w);
                c["hidden"] = hidden.Contains("watch:" + Json.Str(w, "id", ""));
                c["iconUrl"] = "/api/art/" + Uri.EscapeDataString("watch:" + Json.Str(w, "id", "")) + "/icon";
                return c;
            }).ToList();
            d["running"] = running;
            d["forYou"] = Catalog.Rows;
            d["upNext"] = Tracker.UpNext();
            d["devices"] = Devices.Describe();
            d["voice"] = Voice.Describe();
            d["playback"] = Playback.Describe();
            d["videos"] = Videos.Describe();
            d["recommendations"] = Recommendations.Describe();
            d["browserIntegration"] = Shell.IntegrationStatus();
            d["playbackConnections"] = Server.PlaybackClients;
            d["controllers"] = Gamepad != null ? Gamepad.Describe() : new List<Dictionary<string, object>>();

            // The name shown in the top bar: your own choice, else your Steam profile name.
            var u = Library.User;
            string custom = Config.UserName;
            var ud = new Dictionary<string, object>();
            ud["name"] = custom.Length > 0 ? custom : (u != null ? u.Name : "");
            ud["steamName"] = u != null ? u.Name : null;
            ud["custom"] = custom.Length > 0;
            ud["avatar"] = u != null && u.AvatarPath != null ? "/api/avatar" : null;
            d["user"] = ud;

            var s = new Dictionary<string, object>();
            s["trigger"] = Config.Trigger;
            s["voiceEnabled"] = Config.VoiceEnabled;
            s["voiceLanguage"] = Config.VoiceLanguage;
            s["voiceMode"] = Config.VoiceMode;
            s["voiceMicrophone"] = Config.VoiceMicrophone;
            s["voiceEngine"] = Config.VoiceEngine;
            s["voiceReplies"] = Config.VoiceReplies;
            s["controllerBridge"] = Config.ControllerBridge;
            s["showTools"] = Config.ShowTools;
            s["autostart"] = Autostart;
            s["blankAfter"] = Config.BlankAfter;
            s["standbyAfter"] = Config.StandbyAfter;
            s["openOnWake"] = Config.OpenOnWake;
            s["castEnabled"] = Config.CastEnabled;
            s["castName"] = CastName;
            s["airplayEnabled"] = Config.AirPlayEnabled;
            s["airplayInstalled"] = AirPlay.Installed;
            s["phones"] = Remote.PairedCount;
            s["guideSupported"] = Gamepad != null && Gamepad.GuideSupported;
            s["dev"] = Dev;
            s["country"] = Config.Country;
            s["steamFound"] = Library.SteamPath != null;
            s["browser"] = Shell.BrowserExe != null ? Path.GetFileNameWithoutExtension(Shell.BrowserExe) : null;
            d["settings"] = s;
            return d;
        }

        private object Art(string key, string kind)
        {
            if (key.StartsWith("watch:"))
            {
                string id = key.Substring(6);
                var app = Config.Watch.FirstOrDefault(w => Json.Str(w, "id", "") == id);
                return new FileResult { Path = app == null ? null : WatchArt.Icon(app) };
            }
            var g = Library.Find(key);
            if (g == null || Array.IndexOf(Library.ArtKinds, kind) < 0) return new FileResult();
            string local;
            if (g.LocalArt.TryGetValue(kind, out local))
            {
                if (local.StartsWith("http://") || local.StartsWith("https://")) return new FileResult { Redirect = local };
                if (File.Exists(local)) return new FileResult { Path = local };
            }
            if (g.SteamArtId != null) return new FileResult { Redirect = Library.SteamCdn(g.SteamArtId, kind) };
            return new FileResult();
        }

        private object ApplySetting(string key, object value)
        {
            switch (key)
            {
                case "voiceEnabled":
                    if (!(value is bool)) return WebServer.Err("bad voice toggle");
                    Config.Set(key, value); Voice.Refresh();
                    if ((bool)value) Voice.Install(); return Ok();
                case "voiceReplies":
                    if (!(value is bool)) return WebServer.Err("bad voice replies toggle");
                    Config.Set(key, value); return Ok();
                case "voiceEngine":
                    if (Convert.ToString(value) != "whisper" && Convert.ToString(value) != "vosk") return WebServer.Err("bad voice engine");
                    Config.Set(key, value); Voice.Refresh(); if (Config.VoiceEnabled) Voice.Install(); return Ok();
                case "voiceLanguage":
                case "voiceMode":
                    string voiceValue = Convert.ToString(value);
                    if (key == "voiceLanguage" ? voiceValue != "auto" && voiceValue != "en" && voiceValue != "en-in" && voiceValue != "hi" : voiceValue != "direct" && voiceValue != "wake" && voiceValue != "everywhere") return WebServer.Err("bad voice option");
                    Config.Set(key, voiceValue); Voice.Refresh(); return Ok();
                case "voiceMicrophone":
                    int mic;
                    if (!int.TryParse(Convert.ToString(value), out mic) || (mic != -1 && !Microphone.Devices().Any(x => Json.Int(x, "id", -2) == mic))) return WebServer.Err("Microphone not found");
                    Config.Set(key, mic); Voice.Refresh(); return Ok();
                case "trigger":
                    string t = Convert.ToString(value);
                    if (t != "guide" && t != "back+start" && t != "hold-back") return WebServer.Err("bad trigger");
                    Config.Set("trigger", t);
                    if (Gamepad != null) Gamepad.TriggerMode = t;
                    return Ok();
                case "blankAfter":
                case "standbyAfter":
                    int minutes;
                    if (!int.TryParse(Convert.ToString(value), out minutes) || minutes < 0 || minutes > 24 * 60) return WebServer.Err("bad duration");
                    Config.Set(key, minutes);
                    return Ok();
                case "controllerBridge":
                case "showTools":
                case "openOnWake":
                case "castEnabled":
                case "airplayEnabled":
                    Config.Set(key, value is bool && (bool)value);
                    if (key == "showTools") Library.Refresh();
                    if (key == "airplayEnabled") AirPlay.Refresh();
                    return Ok();
                case "autostart":
                    Autostart = value is bool && (bool)value;
                    return Ok();
                case "userName":
                    string name = new string(Convert.ToString(value ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
                    if (name.Length > 24) return WebServer.Err("Keep the name to 24 characters or fewer");
                    Config.Set("userName", name);
                    return Ok();
                case "country":
                    string c = Convert.ToString(value ?? "");
                    if (c.Length != 0 && c.Length != 2) return WebServer.Err("bad country");
                    Config.Set("country", c.ToUpperInvariant());
                    Catalog.RebuildSoon();
                    return Ok();
            }
            return WebServer.Err("unknown setting");
        }

        private object SystemAction(string action)
        {
            switch (action)
            {
                case "desktop":
                    Shell.HideShell();
                    return Ok();
                case "close":
                    Shell.CloseShell();
                    return Ok();
                case "exit":
                    Shell.CloseShell();
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        Thread.Sleep(300); // let this response reach the UI first
                        var exit = RequestExit;
                        if (exit != null) exit();
                    });
                    return Ok();
                case "bigpicture":
                    Process.Start(new ProcessStartInfo("steam://open/bigpicture") { UseShellExecute = true });
                    return Ok();
                case "sleep":
                    Shell.HideShell();
                    ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(800); Application.SetSuspendState(PowerState.Suspend, false, false); });
                    return Ok();
                case "restart":
                    Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
                    return Ok();
                case "shutdown":
                    Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
                    return Ok();
            }
            return WebServer.Err("unknown action");
        }

        public bool Autostart
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    var v = k == null ? null : k.GetValue("BWOS") as string;
                    return v != null && v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("BWOS", "\"" + Application.ExecutablePath + "\" --background");
                    else k.DeleteValue("BWOS", false);
                }
            }
        }

        private static Dictionary<string, object> Ok()
        {
            var d = new Dictionary<string, object>();
            d["ok"] = true;
            return d;
        }
    }
}
