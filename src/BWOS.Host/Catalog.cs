using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace Bwos
{
    /// <summary>
    /// "For You" recommendations. Title data comes from JustWatch's public catalogue (which service
    /// carries what, in your country). BWOS starts with a few clean rows limited to the apps you keep
    /// in Apps, then grows "Because you watched …" rows from what the tracker has seen you watch.
    /// Reality TV is filtered out everywhere.
    /// </summary>
    internal sealed class Catalog
    {
        private const string Endpoint = "https://apis.justwatch.com/graphql";
        private static readonly string[] ExcludedGenres = { "rly" }; // reality TV
        private static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

        private readonly Config config;
        private readonly object gate = new object();
        private List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        private DateTime builtAt = DateTime.MinValue;
        private int building;

        public Func<List<Dictionary<string, object>>> History; // recently watched titles (from Tracker)
        public event Action Changed;

        public Catalog(Config config)
        {
            this.config = config;
            LoadCache();
            var timer = new Thread(delegate()
            {
                while (true)
                {
                    try { if (DateTime.UtcNow - builtAt > MaxAge) Rebuild(); } catch (Exception ex) { Log.Write("catalog: " + ex.Message); }
                    Thread.Sleep(TimeSpan.FromMinutes(20));
                }
            });
            timer.IsBackground = true;
            timer.Name = "BWOS catalog";
            timer.Start();
        }

        public List<Dictionary<string, object>> Rows
        {
            get { lock (gate) return new List<Dictionary<string, object>>(rows); }
        }

        /// <summary>Rebuild soon (e.g. after you watched something new). Non-blocking.</summary>
        public void RebuildSoon()
        {
            ThreadPool.QueueUserWorkItem(delegate { try { Thread.Sleep(1500); Rebuild(); } catch (Exception ex) { Log.Write("catalog: " + ex.Message); } });
        }

        public void Rebuild()
        {
            if (Interlocked.Exchange(ref building, 1) == 1) return;
            try
            {
                string country = config.Country;
                var result = new List<Dictionary<string, object>>();
                // Apps can have their own catalogue country (e.g. an Indian Prime Video account used
                // from the US): query each country with its own apps and merge the rows.
                var groups = VisibleApps().Where(a => Codes(a).Any())
                    .GroupBy(a => AppCountry(a, country)).ToList();
                if (groups.Count == 0) { Store(result, country); return; }

                // Grows with you: "Because you watched …" rows come first once there is history.
                var history = History != null ? History() : new List<Dictionary<string, object>>();
                var seenTitles = new HashSet<string>();
                foreach (var h in history.Take(3))
                {
                    string title = Json.Str(h, "title", null);
                    if (title == null || !seenTitles.Add(title.ToLowerInvariant())) continue;
                    var similar = Merge(groups.Select(g => Similar(g.Key, title, g.ToList())));
                    if (similar.Count >= 4) result.Add(Row("because:" + title, "Because you watched " + title, similar));
                }

                var mon = "[FLATRATE, FREE, ADS]";
                string noReality = ", excludeGenres: " + Q(ExcludedGenres);
                AddMergedRow(result, groups, "series", "Series worth your time", (paid, all) =>
                    "{ objectTypes: [SHOW], packages: " + Q(paid.Count > 0 ? paid : all) + ", monetizationTypes: " + mon + noReality + " }");
                AddMergedRow(result, groups, "movies", "Movies for tonight", (paid, all) =>
                    "{ objectTypes: [MOVIE], packages: " + Q(paid.Count > 0 ? paid : all) + ", monetizationTypes: " + mon + noReality + " }");
                AddMergedRow(result, groups, "indian", "Indian cinema", (paid, all) =>
                    "{ objectTypes: [MOVIE], productionCountries: [\"IN\"], excludeProductionCountries: [\"US\", \"GB\"], packages: " + Q(all) + ", monetizationTypes: " + mon + noReality + " }");
                // Einthusan isn't in JustWatch: read its own lists directly.
                var ein = VisibleApps().FirstOrDefault(a => Json.Str(a, "id", "") == "einthusan");
                if (ein != null)
                {
                    foreach (var lang in Json.Str(ein, "langs", "hindi").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().ToLowerInvariant()).Take(3))
                    {
                        string label = char.ToUpper(lang[0]) + lang.Substring(1);
                        var popular = Einthusan(ein, lang, "find=Popularity&ptype=view&tp=l30d", 2);
                        if (popular.Count >= 4) result.Add(Row("ein-pop-" + lang, "Popular on Einthusan · " + label, popular));
                        Thread.Sleep(2000); // Einthusan rate-limits quick bursts
                        var recent = Einthusan(ein, lang, "find=Recent", 2);
                        if (recent.Count >= 4) result.Add(Row("ein-new-" + lang, "New on Einthusan · " + label, recent));
                    }
                }

                AddMergedRow(result, groups, "free", "Free to stream", (paid, all) =>
                {
                    var free = all.Except(paid).ToList();
                    return free.Count == 0 ? null : "{ packages: " + Q(free) + ", monetizationTypes: [FREE, ADS]" + noReality + " }";
                });

                Store(result, country);
                Log.Write("catalog: " + result.Count + " rows (" + string.Join(", ", groups.Select(g => g.Key + ": " + string.Join("/", g.Select(a => Json.Str(a, "id", ""))))) + ")");
            }
            finally
            {
                Interlocked.Exchange(ref building, 0);
            }
        }

        /// <summary>Best JustWatch match for a title you watched (for artwork on Up Next).</summary>
        public Dictionary<string, object> Match(string title)
        {
            try
            {
                string q = "query M($country: Country!, $language: Language!) { popularTitles(country: $country, first: 1, filter: { searchQuery: " +
                    Json.Serialize(title) + " }) { edges { node { " + NodeFields + " } } } }";
                var node = Nodes(Query(q, config.Country), "popularTitles").FirstOrDefault();
                return node == null ? null : ToTitle(node, VisibleApps(), false);
            }
            catch (Exception ex)
            {
                Log.Write("catalog match failed for " + title + ": " + ex.Message);
                return null;
            }
        }

        // ------------------------------------------------------------------ internals

        private const string NodeFields =
            "id objectType " +
            "content(country: $country, language: $language) { title fullPath originalReleaseYear shortDescription posterUrl backdrops { backdropUrl } genres { shortName } scoring { imdbScore } } " +
            "offers(country: $country, platform: WEB) { monetizationType standardWebURL package { shortName clearName } }";

        private List<Dictionary<string, object>> VisibleApps()
        {
            var hidden = new HashSet<string>(config.Hidden);
            return config.Watch.Where(w => !hidden.Contains("watch:" + Json.Str(w, "id", ""))).ToList();
        }

        private static IEnumerable<string> Codes(Dictionary<string, object> app)
        {
            return Json.Str(app, "jw", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim());
        }

        private static string Q(IEnumerable<string> items)
        {
            return "[" + string.Join(", ", items.Select(i => "\"" + i.Replace("\"", "") + "\"")) + "]";
        }

        /// <summary>Films from an Einthusan list page (6 per page), in the same shape as JustWatch titles.</summary>
        private static List<Dictionary<string, object>> Einthusan(Dictionary<string, object> app, string lang, string query, int pages)
        {
            var result = new List<Dictionary<string, object>>();
            var seen = new HashSet<string>();
            for (int page = 1; page <= pages; page++)
            {
                if (page > 1) Thread.Sleep(1500);
                string html;
                try
                {
                    using (var wc = new WatchArt.DecompressingClient()) // Einthusan gzips regardless of Accept-Encoding
                    {
                        wc.Encoding = Encoding.UTF8;
                        wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36";
                        html = wc.DownloadString("https://einthusan.tv/movie/results/?" + query + "&lang=" + Uri.EscapeDataString(lang) + "&page=" + page);
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("einthusan list failed: " + ex.Message);
                    break;
                }
                int before = result.Count;
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html,
                    "<div class=\"block1\"><a[^>]*href=\"/movie/watch/([^/\"]+)/\\?lang=(\\w+)\"[^>]*>\\s*<img src=\"([^\"]+)\""))
                {
                    string id = m.Groups[1].Value;
                    if (!seen.Add(id)) continue;
                    int end = html.IndexOf("<div class=\"block1\">", m.Index + 20, StringComparison.Ordinal);
                    string block = html.Substring(m.Index, (end > 0 ? end : html.Length) - m.Index);
                    string title = WebUtility.HtmlDecode(Grab(block, "<h3>([^<]+)</h3>"));
                    if (string.IsNullOrEmpty(title)) continue;
                    int year;
                    int.TryParse(Grab(block, "<p>(\\d{4})<span>"), out year);
                    string img = m.Groups[3].Value;
                    if (img.StartsWith("//")) img = "https:" + img;

                    var offer = new Dictionary<string, object>();
                    offer["app"] = "einthusan";
                    offer["name"] = Json.Str(app, "name", "Einthusan");
                    offer["url"] = "https://einthusan.tv/movie/watch/" + id + "/?lang=" + m.Groups[2].Value;
                    offer["free"] = true;
                    var t = new Dictionary<string, object>();
                    t["id"] = "ein:" + id;
                    t["type"] = "movie";
                    t["title"] = title;
                    t["year"] = year;
                    t["description"] = WebUtility.HtmlDecode(Grab(block, "<p class=\"synopsis\">([^<]+)</p>") ?? "");
                    t["genres"] = new List<string> { char.ToUpper(lang[0]) + lang.Substring(1) };
                    t["imdb"] = null;
                    t["poster"] = img;
                    t["backdrop"] = null;
                    t["offers"] = new List<Dictionary<string, object>> { offer };
                    result.Add(t);
                }
                if (result.Count == before)
                {
                    if (page == 1) Log.Write("einthusan: no films parsed from " + query + " (" + lang + "), " + html.Length + " bytes, title: " +
                        Grab(html, "<title>([^<]*)</title>"));
                    break; // no more pages
                }
            }
            return result;
        }

        private static string Grab(string text, string pattern)
        {
            var m = System.Text.RegularExpressions.Regex.Match(text, pattern);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        private static string AppCountry(Dictionary<string, object> app, string fallback)
        {
            string c = Json.Str(app, "country", "");
            return c.Length == 2 ? c.ToUpperInvariant() : fallback;
        }

        /// <summary>Runs one row per country group (with that group's apps) and merges the results.</summary>
        private void AddMergedRow(List<Dictionary<string, object>> rowsOut, List<IGrouping<string, Dictionary<string, object>>> groups,
            string id, string title, Func<List<string>, List<string>, string> filterFor)
        {
            var parts = new List<List<Dictionary<string, object>>>();
            foreach (var g in groups)
            {
                var apps = g.ToList();
                var paid = apps.Where(a => Json.Str(a, "section", "") == "stream").SelectMany(Codes).Distinct().ToList();
                var all = apps.SelectMany(Codes).Distinct().ToList();
                string filter = filterFor(paid, all);
                if (filter == null) continue;
                try
                {
                    string q = "query R($country: Country!, $language: Language!) { popularTitles(country: $country, first: 24, filter: " + filter +
                        ") { edges { node { " + NodeFields + " } } } }";
                    parts.Add(Nodes(Query(q, g.Key), "popularTitles").Select(n => ToTitle(n, apps, true)).Where(t => t != null).ToList());
                }
                catch (Exception ex)
                {
                    Log.Write("catalog row " + id + " (" + g.Key + ") failed: " + ex.Message);
                }
            }
            var items = Merge(parts);
            if (items.Count >= 4) rowsOut.Add(Row(id, title, items));
        }

        /// <summary>Interleaves lists (so every service is represented) and merges duplicate titles' offers.</summary>
        private static List<Dictionary<string, object>> Merge(IEnumerable<List<Dictionary<string, object>>> lists)
        {
            var sources = lists.Where(l => l != null && l.Count > 0).ToList();
            var byId = new Dictionary<string, Dictionary<string, object>>();
            var order = new List<Dictionary<string, object>>();
            for (int i = 0; sources.Any(s => i < s.Count); i++)
            {
                foreach (var s in sources)
                {
                    if (i >= s.Count) continue;
                    var t = s[i];
                    string key = Json.Str(t, "id", "");
                    Dictionary<string, object> existing;
                    if (byId.TryGetValue(key, out existing))
                    {
                        var offers = (List<Dictionary<string, object>>)existing["offers"];
                        foreach (var o in (List<Dictionary<string, object>>)t["offers"])
                            if (!offers.Any(x => Json.Str(x, "app", "") == Json.Str(o, "app", ""))) offers.Add(o);
                        continue;
                    }
                    byId[key] = t;
                    order.Add(t);
                }
            }
            return order.Take(30).ToList();
        }

        private List<Dictionary<string, object>> Similar(string country, string title, List<Dictionary<string, object>> apps)
        {
            try
            {
                string q = "query S($country: Country!, $language: Language!) { popularTitles(country: $country, first: 1, filter: { searchQuery: " +
                    Json.Serialize(title) + " }) { edges { node { id similarTitlesV2(country: $country, first: 40) { edges { node { " + NodeFields + " } } } } } } }";
                var root = Nodes(Query(q, country), "popularTitles").FirstOrDefault();
                if (root == null) return new List<Dictionary<string, object>>();
                object sim;
                var simObj = root.TryGetValue("similarTitlesV2", out sim) ? sim as Dictionary<string, object> : null;
                return Json.List(simObj, "edges")
                    .Select(e => { object n; return e.TryGetValue("node", out n) ? n as Dictionary<string, object> : null; })
                    .Where(n => n != null)
                    .Select(n => ToTitle(n, apps, true))
                    .Where(t => t != null)
                    .Take(24)
                    .ToList();
            }
            catch (Exception ex)
            {
                Log.Write("catalog similar failed for " + title + ": " + ex.Message);
                return new List<Dictionary<string, object>>();
            }
        }

        /// <summary>Flattens a JustWatch node; returns null when none of your apps can play it (if required).</summary>
        private static Dictionary<string, object> ToTitle(Dictionary<string, object> node, List<Dictionary<string, object>> apps, bool requirePlayable)
        {
            object c;
            var content = node.TryGetValue("content", out c) ? c as Dictionary<string, object> : null;
            if (content == null) return null;
            var genres = Json.List(content, "genres").Select(g => Json.Str(g, "shortName", "")).ToList();
            if (genres.Contains("rly")) return null;

            var offers = new List<Dictionary<string, object>>();
            var usedApps = new HashSet<string>();
            foreach (var o in Json.List(node, "offers"))
            {
                string type = Json.Str(o, "monetizationType", "");
                if (type != "FLATRATE" && type != "FREE" && type != "ADS") continue;
                object p;
                var pkg = o.TryGetValue("package", out p) ? p as Dictionary<string, object> : null;
                string code = Json.Str(pkg, "shortName", "");
                var app = apps.FirstOrDefault(a => Codes(a).Contains(code));
                if (app == null) continue;
                string appId = Json.Str(app, "id", "");
                if (!usedApps.Add(appId)) continue;
                var offer = new Dictionary<string, object>();
                offer["app"] = appId;
                offer["name"] = Json.Str(app, "name", "");
                offer["url"] = Json.Str(o, "standardWebURL", Json.Str(app, "url", ""));
                offer["free"] = type != "FLATRATE";
                offers.Add(offer);
            }
            if (requirePlayable && offers.Count == 0) return null;

            var t = new Dictionary<string, object>();
            t["id"] = Json.Str(node, "id", "");
            t["type"] = Json.Str(node, "objectType", "") == "SHOW" ? "show" : "movie";
            t["title"] = Json.Str(content, "title", "");
            t["year"] = Json.Int(content, "originalReleaseYear", 0);
            t["description"] = Json.Str(content, "shortDescription", "");
            t["genres"] = genres.Select(GenreName).Where(g => g != null).Take(3).ToList();
            object sc;
            var scoring = content.TryGetValue("scoring", out sc) ? sc as Dictionary<string, object> : null;
            object imdb;
            t["imdb"] = scoring != null && scoring.TryGetValue("imdbScore", out imdb) && imdb != null ? imdb : null;
            t["poster"] = Image(Json.Str(content, "posterUrl", null), "s592");
            var backdrop = Json.List(content, "backdrops").FirstOrDefault();
            t["backdrop"] = backdrop != null ? Image(Json.Str(backdrop, "backdropUrl", null), "s1920") : null;
            t["offers"] = offers;
            return t;
        }

        private static string Image(string path, string profile)
        {
            if (string.IsNullOrEmpty(path)) return null;
            return "https://images.justwatch.com" + path.Replace("{profile}", profile).Replace("{format}", "webp");
        }

        private static string GenreName(string code)
        {
            switch (code)
            {
                case "act": return "Action"; case "ani": return "Animation"; case "cmy": return "Comedy";
                case "crm": return "Crime"; case "doc": return "Documentary"; case "drm": return "Drama";
                case "fnt": return "Fantasy"; case "hst": return "History"; case "hrr": return "Horror";
                case "fml": return "Family"; case "msc": return "Music"; case "trl": return "Thriller";
                case "rma": return "Romance"; case "scf": return "Sci-Fi"; case "spt": return "Sport";
                case "war": return "War"; case "wsn": return "Western"; case "eur": return "Made in Europe";
                default: return null;
            }
        }

        private static Dictionary<string, object> Row(string id, string title, List<Dictionary<string, object>> items)
        {
            var r = new Dictionary<string, object>();
            r["id"] = id;
            r["title"] = title;
            r["items"] = items;
            return r;
        }

        private static IEnumerable<Dictionary<string, object>> Nodes(Dictionary<string, object> data, string field)
        {
            object f;
            var obj = data != null && data.TryGetValue(field, out f) ? f as Dictionary<string, object> : null;
            foreach (var e in Json.List(obj, "edges"))
            {
                object n;
                var node = e.TryGetValue("node", out n) ? n as Dictionary<string, object> : null;
                if (node != null) yield return node;
            }
        }

        private static Dictionary<string, object> Query(string query, string country)
        {
            var body = new Dictionary<string, object>();
            body["query"] = query;
            var vars = new Dictionary<string, object>();
            vars["country"] = country;
            vars["language"] = "en";
            body["variables"] = vars;
            using (var wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                wc.Headers[HttpRequestHeader.UserAgent] = "BWOS/1.0";
                var res = Json.ParseObject(wc.UploadString(Endpoint, Json.Serialize(body)));
                object errors;
                if (res.TryGetValue("errors", out errors) && errors != null)
                    throw new InvalidOperationException(Json.Serialize(errors));
                object d;
                return res.TryGetValue("data", out d) ? d as Dictionary<string, object> : null;
            }
        }

        private void Store(List<Dictionary<string, object>> result, string country)
        {
            lock (gate)
            {
                rows = result;
                builtAt = DateTime.UtcNow;
            }
            try
            {
                var cache = new Dictionary<string, object>();
                cache["builtAt"] = builtAt.ToString("o");
                cache["country"] = country;
                cache["rows"] = result;
                File.WriteAllText(Path.Combine(Paths.DataDir, "catalog.json"), Json.Serialize(cache), new UTF8Encoding(false));
            }
            catch { }
            var h = Changed;
            if (h != null) h();
        }

        private void LoadCache()
        {
            try
            {
                string f = Path.Combine(Paths.DataDir, "catalog.json");
                if (!File.Exists(f)) return;
                var cache = Json.ParseObject(File.ReadAllText(f, Encoding.UTF8));
                if (Json.Str(cache, "country", "") != config.Country) return;
                rows = Json.List(cache, "rows");
                DateTime t;
                if (DateTime.TryParse(Json.Str(cache, "builtAt", ""), null, System.Globalization.DateTimeStyles.RoundtripKind, out t)) builtAt = t;
            }
            catch { }
        }
    }
}
