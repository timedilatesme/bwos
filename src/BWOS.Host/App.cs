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
        public const string Version = "1.0.0";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public readonly Config Config;
        public readonly Library Library;
        public readonly ShellManager Shell;
        public readonly GameManager Games;
        public readonly WebServer Server;
        public readonly GamepadService Gamepad;
        public readonly Catalog Catalog;
        public readonly Tracker Tracker;
        public readonly PhoneKeyboard Keyboard;
        private readonly InputBridge bridge;
        private DateTime lastTrigger = DateTime.MinValue;
        private string pendingLaunchKey;   // launched from BWOS, not yet detected as running
        private string launchedKey;        // the game BWOS launched (return to BWOS when it closes)
        private string lastRunningKey;
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

            Keyboard = new PhoneKeyboard(Shell, Config.Port + 1);
            Keyboard.Typed += delegate(string action)
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
            Server.Start();
            if (Gamepad != null) Gamepad.Start();
            Log.Write("BWOS " + Version + " listening on " + Server.BaseUrl + " (guide button " +
                (Gamepad != null && Gamepad.GuideSupported ? "supported" : "NOT supported") + ")");
        }

        public void Stop()
        {
            if (Gamepad != null) Gamepad.Stop();
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
                if (Shell.ServiceIsForeground())
                {
                    // Watching something inside BWOS: home leaves the service for the BWOS home screen.
                    Shell.GoHome();
                    Log.Write("trigger (" + source + "): left streaming service -> BWOS home");
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
                    try { return Keyboard.Open(); }
                    catch (Exception ex)
                    {
                        Log.Write("phone keyboard failed: " + ex.Message);
                        return WebServer.Err("Couldn't start the phone keyboard: " + ex.Message);
                    }
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
            d["controllers"] = Gamepad != null ? Gamepad.Describe() : new List<Dictionary<string, object>>();

            var u = Library.User;
            if (u != null)
            {
                var ud = new Dictionary<string, object>();
                ud["name"] = u.Name;
                ud["avatar"] = u.AvatarPath != null ? "/api/avatar" : null;
                d["user"] = ud;
            }

            var s = new Dictionary<string, object>();
            s["trigger"] = Config.Trigger;
            s["controllerBridge"] = Config.ControllerBridge;
            s["showTools"] = Config.ShowTools;
            s["autostart"] = Autostart;
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
                case "trigger":
                    string t = Convert.ToString(value);
                    if (t != "guide" && t != "back+start" && t != "hold-back") return WebServer.Err("bad trigger");
                    Config.Set("trigger", t);
                    if (Gamepad != null) Gamepad.TriggerMode = t;
                    return Ok();
                case "controllerBridge":
                case "showTools":
                    Config.Set(key, value is bool && (bool)value);
                    if (key == "showTools") Library.Refresh();
                    return Ok();
                case "autostart":
                    Autostart = value is bool && (bool)value;
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
