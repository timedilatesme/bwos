using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Bwos
{
    // Live playback is separate from the persisted "continue watching" history.
    internal sealed class PlaybackService
    {
        private sealed class Player { public Dictionary<string, object> Data; public DateTime Seen, Order; }
        private readonly object gate = new object();
        private readonly Dictionary<string, Player> players = new Dictionary<string, Player>();
        private readonly List<Dictionary<string, object>> commands = new List<Dictionary<string, object>>();
        private readonly List<Dictionary<string, object>> diagnostics = new List<Dictionary<string, object>>();
        public event Action Changed;
        private void Notify() { var changed = Changed; if (changed != null) changed(); }
        public void Report(Dictionary<string, object> data)
        {
            string id = Json.Str(data, "id", ""), url = Json.Str(data, "url", "");
            if (!Regex.IsMatch(id, @"^\d{1,12}$") || !url.StartsWith("https://") || (id == "0" && !Json.Bool(data, "local", false))) return;
            lock (gate)
            {
                Player previous;
                DateTime order = players.TryGetValue(id, out previous) && !(Json.Bool(previous.Data, "paused", false) && !Json.Bool(data, "paused", false)) && !(Json.Bool(previous.Data, "background", false) && !Json.Bool(data, "background", false)) ? previous.Order : DateTime.UtcNow;
                if (Json.Bool(data, "ended", false)) players.Remove(id);
                else players[id] = new Player { Data = new Dictionary<string, object> {
                    { "id", id }, { "url", url.Substring(0, Math.Min(url.Length, 2000)) },
                    { "title", Clip(Json.Str(data, "title", "Media"), 180) }, { "subtitle", Clip(Json.Str(data, "subtitle", ""), 120) },
                    { "paused", Json.Bool(data, "paused", false) }, { "background", Json.Bool(data, "background", false) },
                    { "local", id == "0" && Json.Bool(data, "local", false) },
                    { "position", Seconds(data, "position") }, { "duration", Seconds(data, "duration") },
                    { "artwork", Clip(Json.Str(data, "artwork", ""), 2000) }, { "error", Clip(Json.Str(data, "error", ""), 180) }, { "canControl", Json.Bool(data, "canControl", true) }
                }, Seen = DateTime.UtcNow, Order = order };
            }
            Notify();
        }
        private static string Clip(string text, int length) { return text.Substring(0, Math.Min(text.Length, length)); }
        private static double Seconds(Dictionary<string, object> data, string key)
        {
            object value; double seconds;
            try { seconds = data.TryGetValue(key, out value) ? Convert.ToDouble(value) : 0; }
            catch { return 0; }
            return double.IsNaN(seconds) || double.IsInfinity(seconds) ? 0 : Math.Max(0, Math.Min(31536000, seconds));
        }
        private void Prune() { foreach (var key in players.Where(p => (DateTime.UtcNow - p.Value.Seen).TotalMinutes > 3).Select(p => p.Key).ToArray()) players.Remove(key); }
        public object Describe()
        {
            lock (gate) { Prune(); return players.Values.OrderByDescending(p => p.Order).Select(p => new Dictionary<string, object>(p.Data)).ToArray(); }
        }
        public object Control(string id, string action)
        {
            return Control(id, action, "");
        }
        public object Control(string id, string action, string value)
        {
            if (!new[] { "pause", "resume", "toggle", "focus", "stop", "retry", "restart", "seekForward", "seekBack", "nextMedia", "previousMedia", "subtitlesOn", "subtitlesOff" }.Contains(action)) return WebServer.Err("Unknown playback action");
            if (action == "seekForward" || action == "seekBack") { int seconds; if (!int.TryParse(value, out seconds) || seconds < 1 || seconds > 100) return WebServer.Err("Seek needs 1–100 seconds"); }
            lock (gate)
            {
                Prune(); if (!players.ContainsKey(id)) return WebServer.Err("Player is no longer available");
                if (action == "focus" || action == "resume") players[id].Order = DateTime.UtcNow;
                if (commands.Count >= 16) commands.RemoveAt(0);
                commands.Add(new Dictionary<string, object> { { "id", id }, { "action", action }, { "value", value } });
            }
            Notify(); return new Dictionary<string, object> { { "ok", true } };
        }
        public object Claim(bool local = false)
        {
            lock (gate) { var result = commands.Where(c => (Json.Str(c, "id", "") == "0") == local).ToArray(); commands.RemoveAll(c => (Json.Str(c, "id", "") == "0") == local); return new Dictionary<string, object> { { "commands", result } }; }
        }
        public object Diagnostics() { lock (gate) return diagnostics.Select(d => new Dictionary<string, object>(d)).ToArray(); }
        public void Diagnostic(Dictionary<string, object> data)
        {
            string id = Json.Str(data, "id", ""), service = Json.Str(data, "service", ""), category = Json.Str(data, "category", ""), code = Json.Str(data, "code", "");
            if (!Regex.IsMatch(id, @"^\d{1,12}$") || service != "youtube" || !new[] { "player", "media-network", "player-network" }.Contains(category) ||
                !Regex.IsMatch(code, @"^(MEDIA_[1-4]|TV_PLAYER_ERROR|HTTP_[45]\d{2}|net::ERR_[A-Z_]{1,60})$")) return;
            lock (gate)
            {
                var last = diagnostics.LastOrDefault();
                DateTime at;
                if (last != null && Json.Str(last, "id", "") == id && Json.Str(last, "category", "") == category && Json.Str(last, "code", "") == code &&
                    DateTime.TryParse(Json.Str(last, "at", ""), out at) && (DateTime.UtcNow - at.ToUniversalTime()).TotalSeconds < 10) return;
                if (diagnostics.Count >= 30) diagnostics.RemoveAt(0);
                diagnostics.Add(new Dictionary<string, object> { { "id", id }, { "service", service }, { "category", category }, { "code", code }, { "at", DateTime.UtcNow.ToString("o") } });
            }
            Log.Write("playback diagnostic: " + service + " " + category + " " + code);
        }
        public void Error(string id, string error)
        {
            lock (gate) { Player player; if (players.TryGetValue(id, out player)) player.Data["error"] = Clip(error, 180); }
            Notify();
        }
    }
}
