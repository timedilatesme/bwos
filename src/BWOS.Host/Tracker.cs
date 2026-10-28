using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Bwos
{
    /// <summary>
    /// Remembers what you watch inside BWOS, across every service. The bwos-tv extension reports
    /// the title, episode, artwork and position of whatever is playing; entries are keyed by
    /// *show* (not by app or episode URL), so "Up Next" follows the show wherever you watched it.
    /// Stored in data\watching.json.
    /// </summary>
    internal sealed class Tracker
    {
        private const int MaxEntries = 60;
        private readonly Config config;
        private readonly Catalog catalog;
        private readonly object gate = new object();
        private List<Dictionary<string, object>> entries = new List<Dictionary<string, object>>();
        private DateTime lastSave = DateTime.MinValue;

        public event Action Changed;

        private static string FilePath { get { return Path.Combine(Paths.DataDir, "watching.json"); } }

        public Tracker(Config config, Catalog catalog)
        {
            this.config = config;
            this.catalog = catalog;
            try
            {
                if (File.Exists(FilePath))
                    entries = Json.List(Json.ParseObject(File.ReadAllText(FilePath, Encoding.UTF8)), "entries");
            }
            catch (Exception ex) { Log.Write("watching.json unreadable: " + ex.Message); }
        }

        /// <summary>Something is playing (called every ~15 s and on pause/end by the extension).</summary>
        public void Report(Dictionary<string, object> r)
        {
            string title = Clean(Json.Str(r, "title", ""));
            string url = Json.Str(r, "url", "");
            double position = Num(r, "position"), duration = Num(r, "duration");
            if (title.Length == 0 || url.Length == 0) return;
            if (duration > 0 && duration < 120) return; // trailers, previews, ads

            string app = AppFor(url);
            string key = (app ?? HostOf(url)) + "|" + title.ToLowerInvariant();
            bool isNew = false;
            Dictionary<string, object> e;
            lock (gate)
            {
                e = entries.FirstOrDefault(x => Json.Str(x, "key", "") == key);
                if (e == null)
                {
                    e = new Dictionary<string, object>();
                    e["key"] = key;
                    e["id"] = Guid.NewGuid().ToString("N").Substring(0, 12);
                    e["title"] = title;
                    e["first"] = Now();
                    entries.Insert(0, e);
                    isNew = true;
                }
                else
                {
                    entries.Remove(e);
                    entries.Insert(0, e);
                }
                e["app"] = app;
                e["url"] = url;
                e["subtitle"] = Clean(Json.Str(r, "subtitle", ""));
                string art = Json.Str(r, "artwork", "");
                if (art.StartsWith("https://")) e["artwork"] = art;
                e["position"] = Math.Round(position);
                e["duration"] = Math.Round(duration);
                e["updated"] = Now();
                e["dismissed"] = false;
                while (entries.Count > MaxEntries) entries.RemoveAt(entries.Count - 1);
            }

            if (isNew)
            {
                Log.Write("now watching: " + title + " on " + (app ?? HostOf(url)));
                // Look the show up once for proper artwork and to seed "Because you watched".
                BackgroundWork.Queue("watch history lookup", delegate
                {
                    var match = catalog.Match(title);
                    if (match != null)
                    {
                        lock (gate)
                        {
                            e["jwId"] = match["id"];
                            e["type"] = match["type"];
                            if (match["backdrop"] != null) e["backdrop"] = match["backdrop"];
                            if (match["poster"] != null) e["poster"] = match["poster"];
                        }
                        Save(true);
                    }
                    catalog.RebuildSoon();
                });
            }
            Save(isNew);
        }

        /// <summary>Saved position for a URL, used to resume sites that don't remember it themselves.</summary>
        public double PositionFor(string url)
        {
            lock (gate)
            {
                var e = entries.FirstOrDefault(x => Json.Str(x, "url", "") == url);
                return e == null ? 0 : Num(e, "position");
            }
        }

        public void Dismiss(string id)
        {
            lock (gate)
            {
                var e = entries.FirstOrDefault(x => Json.Str(x, "id", "") == id);
                if (e != null) e["dismissed"] = true;
            }
            Save(true);
        }

        /// <summary>Shows and movies you're part-way through, newest first.</summary>
        public List<Dictionary<string, object>> UpNext()
        {
            lock (gate)
            {
                return entries.Where(e =>
                {
                    if (Json.Bool(e, "dismissed", false)) return false;
                    double pos = Num(e, "position"), dur = Num(e, "duration");
                    bool finished = dur > 0 && pos / dur > 0.93;
                    // A finished movie is done; a finished episode means the next one is up.
                    return !(finished && Json.Str(e, "type", "") == "movie");
                }).Take(20).Select(e => new Dictionary<string, object>(e)).ToList();
            }
        }

        /// <summary>Recently watched titles, for recommendations.</summary>
        public List<Dictionary<string, object>> History()
        {
            lock (gate) return entries.Take(10).Select(e => new Dictionary<string, object>(e)).ToList();
        }

        private void Save(bool force)
        {
            try
            {
                lock (gate)
                {
                    if (force || (DateTime.UtcNow - lastSave).TotalSeconds >= 30)
                    {
                        var root = new Dictionary<string, object>();
                        root["entries"] = entries;
                        AtomicFile.WriteText(FilePath, Json.Pretty(Json.Serialize(root)));
                        lastSave = DateTime.UtcNow;
                    }
                }
            }
            catch (Exception ex) { Log.Write("watching.json save failed: " + ex.Message); }
            Notify();
        }

        private void Notify()
        {
            var h = Changed;
            if (h != null) h();
        }

        /// <summary>The Apps entry whose site this URL belongs to (e.g. amazon.com/gp/video → Prime Video).</summary>
        private string AppFor(string url)
        {
            string host = HostOf(url);
            foreach (var w in config.Watch)
            {
                var hosts = new List<string> { HostOf(Json.Str(w, "url", "")) };
                hosts.AddRange(Json.Str(w, "hosts", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
                if (hosts.Any(h => h.Length > 0 && (host == h || host.EndsWith("." + h)))) return Json.Str(w, "id", null);
            }
            return null;
        }

        private static string HostOf(string url)
        {
            try
            {
                string h = new Uri(url).Host.ToLowerInvariant();
                return h.StartsWith("www.") ? h.Substring(4) : h;
            }
            catch { return ""; }
        }

        private static string Clean(string s)
        {
            s = (s ?? "").Trim();
            foreach (var suffix in new[] { " | Netflix", " - Netflix", " | Prime Video", " - Prime Video", " - YouTube", " | Tubi", " - Einthusan" })
                if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - suffix.Length).Trim();
            // "Baby Do Die Do (2026) Hindi in HD" → "Baby Do Die Do"
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*\((19|20)\d{2}\)\s.*$", "").Trim();
            s = System.Text.RegularExpressions.Regex.Replace(s, @"^Watch\s+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return s.Length > 140 ? s.Substring(0, 140) : s;
        }

        private static double Num(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return 0;
            try
            {
                double value = Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
                return double.IsNaN(value) || double.IsInfinity(value) ? 0 : Math.Max(0, Math.Min(31536000, value));
            }
            catch { return 0; }
        }

        private static long Now()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }
    }
}
