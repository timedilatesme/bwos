using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Bwos
{
    // Stores only rendered shelf titles, public title links and artwork. No account
    // identifiers, cookies, credentials or provider API responses enter this cache.
    internal sealed class RecommendationFeeds
    {
        private readonly string file;
        private readonly object gate = new object();
        private Dictionary<string, object> feeds = new Dictionary<string, object>();
        public event Action Changed;
        public RecommendationFeeds() : this(Path.Combine(Paths.DataDir, "recommendations.json")) { }
        internal RecommendationFeeds(string file)
        {
            this.file = file;
            try { if (File.Exists(file)) feeds = Json.ParseObject(File.ReadAllText(file, Encoding.UTF8)); } catch { }
        }
        public Dictionary<string, object> Describe() { lock (gate) return Json.ParseObject(Json.Serialize(feeds)); }
        internal static string Provider(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort) return null;
            string host = uri.Host;
            if (host == "www.youtube.com" || host == "youtube.com") return "youtube";
            if (host == "www.netflix.com" || host == "netflix.com") return "netflix";
            if (host == "www.primevideo.com" || host == "primevideo.com") return "prime";
            return null;
        }
        private static string Clean(string value, int max) { return new string((value ?? "").Where(c => !char.IsControl(c)).Take(max).ToArray()).Trim(); }
        private static string TitleUrl(string value, string service)
        {
            if (value.Length > 2000 || Provider(value) != service) return null;
            if (service == "youtube") { string id = VideoLibrary.VideoId(value); return id == null ? null : "https://www.youtube.com/watch?v=" + id; }
            var uri = new Uri(value);
            string pattern = service == "netflix" ? @"^/(?:title|watch)/\d+/?$" : @"/(?:detail|dp)/[A-Za-z0-9]+(?:/|$)";
            if (!System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, pattern)) return null;
            return uri.GetLeftPart(UriPartial.Path);
        }
        private static string Artwork(string value, string service)
        {
            Uri uri;
            if (value.Length > 8192 || !Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort) return "";
            string[] domains = service == "youtube" ? new[] { "ytimg.com", "ggpht.com" } : service == "netflix" ? new[] { "nflximg.net", "nflxso.net", "nflxext.com" } : new[] { "media-amazon.com", "ssl-images-amazon.com", "images-amazon.com" };
            // Provider CDNs use query parameters for image sizes and signed delivery.
            // Removing them can turn a valid imported image into a 403 or wrong rendition.
            return domains.Any(domain => uri.Host == domain || uri.Host.EndsWith("." + domain, StringComparison.Ordinal)) ? new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri : "";
        }
        public object Report(Dictionary<string, object> data) { return ReadCards(data, true); }
        public object Preview(Dictionary<string, object> data) { return ReadCards(data, false); }
        public object Status(Dictionary<string, object> data)
        {
            string service = Json.Str(data, "service", "");
            if (!new[] { "youtube", "netflix", "prime" }.Contains(service)) return WebServer.Err("Unknown recommendation source");
            lock (gate)
            {
                Dictionary<string, object> feed;
                if (!feeds.ContainsKey(service) || (feed = feeds[service] as Dictionary<string, object>) == null)
                    feeds[service] = feed = new Dictionary<string, object> { { "rows", new object[0] }, { "updated", 0 } };
                string status = Json.Str(data, "status", "unavailable");
                feed["status"] = new[] { "profile-required", "sign-in-required", "home-unavailable", "no-cards" }.Contains(status) ? status : "unavailable";
                feed["attempted"] = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                SetDiagnostics(feed, data, Json.Str(data, "source", "other"));
            }
            var handler = Changed; if (handler != null) handler();
            return new Dictionary<string, object> { { "ok", true } };
        }
        private object ReadCards(Dictionary<string, object> data, bool persist)
        {
            string service = Json.Str(data, "service", "");
            if (Provider(Json.Str(data, "url", "")) != service || !new[] { "youtube", "netflix", "prime" }.Contains(service)) return WebServer.Err("Unknown recommendation source");
            var rows = new List<Dictionary<string, object>>(); int total = 0;
            foreach (var input in Json.List(data, "rows").Take(16))
            {
                var items = new List<Dictionary<string, object>>(); var seen = new HashSet<string>();
                foreach (var item in Json.List(input, "items").Take(360))
                {
                    if (total >= 360) break;
                    string url = TitleUrl(Json.Str(item, "url", ""), service), title = Clean(Json.Str(item, "title", ""), 200);
                    if (url == null || title.Length == 0 || !seen.Add(url)) continue;
                    string id; using (var hash = SHA256.Create()) id = "feed:" + service + ":" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-", "").Substring(0, 20);
                    string art = Artwork(Json.Str(item, "artwork", ""), service);
                    items.Add(new Dictionary<string, object> { { "id", id }, { "title", title }, { "url", url }, { "artwork", art }, { "backdrop", art }, { "app", service },
                        { "author", Clean(Json.Str(item, "author", ""), 100) }, { "offers", new[] { new Dictionary<string, object> { { "app", service }, { "name", service == "prime" ? "Prime Video" : service == "netflix" ? "Netflix" : "YouTube" }, { "url", url } } } } });
                    total++;
                }
                if (items.Count > 0) rows.Add(new Dictionary<string, object> { { "id", "page:" + rows.Count }, { "title", Clean(Json.Str(input, "title", "From the app"), 120) }, { "items", items } });
            }
            // Empty/profile-picker pages cannot erase a valid cached feed.
            if (rows.Count == 0) return WebServer.Err("No recommendation cards found. Open the app, select a profile if needed, and try again.");
            if (!persist) return new Dictionary<string, object> { { "ok", true }, { "count", total }, { "rows", rows } };
            int captured = total;
            bool complete = Json.Bool(data, "complete", false);
            lock (gate)
            {
                var previous = feeds.ContainsKey(service) ? feeds[service] as Dictionary<string, object> : null;
                long previousUpdated;
                long.TryParse(Json.Str(previous, "updated", "0"), out previousUpdated);
                // A live page may initially render only a handful of lazy cards.
                // Keep already loaded recommendations until a complete discovery
                // snapshot explicitly replaces them, within the same 24-hour cache.
                int previousCount = Json.List(previous, "rows").Sum(row => Json.List(row, "items").Count);
                // A sparse result cannot declare itself complete and discard a
                // fuller recent feed, including reports from older adapters.
                complete = complete && (total >= 48 || total >= previousCount);
                if (!complete && previous != null &&
                    (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds - previousUpdated < 86400)
                {
                    var seen = new HashSet<string>(rows.SelectMany(row => Json.List(row, "items")).Select(item => Json.Str(item, "url", "")));
                    foreach (var oldRow in Json.List(previous, "rows"))
                    {
                        var oldItems = Json.List(oldRow, "items").Where(item => seen.Add(Json.Str(item, "url", ""))).Take(Math.Max(0, 360 - total)).ToList();
                        if (oldItems.Count == 0) continue;
                        var matching = rows.FirstOrDefault(row => Json.Str(row, "title", "") == Json.Str(oldRow, "title", ""));
                        if (matching != null) ((List<Dictionary<string, object>>)matching["items"]).AddRange(oldItems);
                        else if (rows.Count < 16) rows.Add(new Dictionary<string, object> { { "id", "page:" + rows.Count }, { "title", Json.Str(oldRow, "title", "From the app") }, { "items", oldItems } });
                        else continue;
                        total += oldItems.Count;
                    }
                }
                feeds[service] = new Dictionary<string, object> { { "updated", (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds }, { "rows", rows },
                    { "collector", Json.Int(data, "collector", 0) }, { "captured", captured }, { "partial", !complete } };
                var stored = feeds[service] as Dictionary<string, object>;
                SetDiagnostics(stored, data, new Uri(Json.Str(data, "url", "")).AbsolutePath);
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                string temp = file + ".tmp"; File.WriteAllText(temp, Json.Serialize(feeds), Encoding.UTF8);
                if (File.Exists(file)) File.Replace(temp, file, null); else File.Move(temp, file);
            }
            var handler = Changed; if (handler != null) handler();
            return new Dictionary<string, object> { { "ok", true }, { "count", total }, { "captured", captured }, { "partial", !complete } };
        }
        private static void SetDiagnostics(Dictionary<string, object> feed, Dictionary<string, object> data, string path)
        {
            feed["source"] = new[] { "/", "/browse", "/results", "/search", "/tv", "/login", "/signin" }.Contains(path) ? path : "other";
            var stats = data.ContainsKey("stats") ? data["stats"] as Dictionary<string, object> : null;
            feed["stats"] = new[] { "anchors", "validLinks", "missingTitles", "shelves" }.ToDictionary(key => key, key => (object)Math.Max(0, Math.Min(100000, Json.Int(stats, key, 0))));
        }
    }
}
