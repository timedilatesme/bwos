using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace Bwos
{
    internal sealed class GameEntry
    {
        public string Key;          // "steam:620", "epic:Sugar", "gog:1207658924", "custom:retroarch"
        public string Source;       // steam | epic | gog | custom
        public string Id;
        public string Name;
        public string InstallDir;
        public string LaunchUri;    // protocol URI, or null when Exe is used
        public string Exe;
        public string Args;
        public string WorkingDir;
        public long LastPlayed;     // unix seconds
        public long PlaytimeMinutes;
        public long SizeBytes;
        public bool Updating;
        public bool IsTool;
        public string SteamArtId;   // Steam appid whose store art represents this game
        public readonly Dictionary<string, string> LocalArt = new Dictionary<string, string>();

        public Dictionary<string, object> ToJson(string runningKey)
        {
            var d = new Dictionary<string, object>();
            d["key"] = Key;
            d["source"] = Source;
            d["id"] = Id;
            d["name"] = Name;
            d["lastPlayed"] = LastPlayed;
            d["playtime"] = PlaytimeMinutes;
            d["size"] = SizeBytes;
            d["updating"] = Updating;
            d["tool"] = IsTool;
            d["running"] = Key == runningKey;
            var art = new Dictionary<string, object>();
            bool hasArt = SteamArtId != null || LocalArt.Count > 0;
            foreach (var kind in Library.ArtKinds)
                art[kind] = hasArt ? "/api/art/" + Uri.EscapeDataString(Key) + "/" + kind : null;
            d["art"] = art;
            return d;
        }
    }

    internal sealed class SteamUser
    {
        public string SteamId64;
        public string AccountId;
        public string Name;
        public string AvatarPath;
    }

    /// <summary>Discovers installed games from Steam, Epic, GOG and config.json.</summary>
    internal sealed class Library
    {
        public static readonly string[] ArtKinds = { "portrait", "hero", "logo", "header" };

        // Steam "apps" that are really tools/runtimes. Hidden unless showTools is on.
        private static readonly HashSet<string> SteamToolIds = new HashSet<string> {
            "228980", "250820", "1070560", "1391110", "1628350", "1493710", "1826330", "961940",
            "1054830", "1113280", "1245040", "1420170", "1580130", "1887720", "2180100", "2348590",
            "431960" /* Wallpaper Engine is an app, not a game, but people like it—kept out by default */
        };
        private static readonly Regex ToolName = new Regex(
            @"Redistributable|Steam Linux Runtime|^Proton|Steamworks|SteamVR|Dedicated Server|\bSDK\b|Soundtrack",
            RegexOptions.IgnoreCase);

        private readonly Config config;
        private readonly object gate = new object();
        private List<GameEntry> games = new List<GameEntry>();
        private Dictionary<string, string> steamArtMap; // normalized name -> steam appid ("" = no match)
        private string steamPath;
        public SteamUser User;
        public event Action Changed;

        public Library(Config config)
        {
            this.config = config;
            LoadArtMap();
        }

        public string SteamPath { get { return steamPath; } }

        public List<GameEntry> Games
        {
            get { lock (gate) return new List<GameEntry>(games); }
        }

        public GameEntry Find(string key)
        {
            lock (gate) return games.FirstOrDefault(g => g.Key == key);
        }

        public void Refresh()
        {
            var list = new List<GameEntry>();
            try { list.AddRange(ScanSteam()); } catch (Exception ex) { Log.Write("steam scan failed: " + ex); }
            try { list.AddRange(ScanEpic()); } catch (Exception ex) { Log.Write("epic scan failed: " + ex); }
            try { list.AddRange(ScanGog()); } catch (Exception ex) { Log.Write("gog scan failed: " + ex); }
            try { list.AddRange(ScanUbisoft()); } catch (Exception ex) { Log.Write("ubisoft scan failed: " + ex); }
            try { list.AddRange(ScanCustom()); } catch (Exception ex) { Log.Write("custom scan failed: " + ex); }

            var overrides = config.ArtOverrides;
            foreach (var g in list)
            {
                string artId;
                if (overrides.TryGetValue(g.Key, out artId) && artId.Length > 0) g.SteamArtId = artId;
            }

            // The same game can be installed through two launchers; keep the first (Steam wins).
            var seen = new HashSet<string>();
            var unique = new List<GameEntry>();
            foreach (var g in list)
                if (seen.Add(g.Key)) unique.Add(g);

            lock (gate) games = unique;
            Log.Write("library: " + unique.Count + " entries");
            ThreadPool.QueueUserWorkItem(delegate { try { ResolveMissingArt(); } catch (Exception ex) { Log.Write("art resolve failed: " + ex.Message); } });
        }

        // ---------------------------------------------------------------- Steam

        private IEnumerable<GameEntry> ScanSteam()
        {
            var result = new List<GameEntry>();
            steamPath = null;
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                if (k != null) steamPath = k.GetValue("SteamPath") as string;
            }
            if (string.IsNullOrEmpty(steamPath))
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                {
                    if (k != null) steamPath = k.GetValue("InstallPath") as string;
                }
            }
            if (string.IsNullOrEmpty(steamPath) || !Directory.Exists(steamPath)) return result;
            steamPath = Path.GetFullPath(steamPath.Replace('/', '\\'));

            User = ReadSteamUser();
            Dictionary<string, object> playApps = ReadLocalConfigApps(User);

            var libraries = new List<string> { steamPath };
            string lf = Path.Combine(steamPath, @"steamapps\libraryfolders.vdf");
            if (File.Exists(lf))
            {
                var root = Vdf.Child(Vdf.ParseFile(lf), "libraryfolders");
                if (root != null)
                {
                    foreach (var kv in root)
                    {
                        var entry = kv.Value as Dictionary<string, object>;
                        string p = entry != null ? Vdf.Str(entry, "path") : kv.Value as string; // old format: "1" "D:\\Lib"
                        if (string.IsNullOrEmpty(p)) continue;
                        if (!libraries.Any(x => string.Equals(x.TrimEnd('\\'), p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                            libraries.Add(p);
                    }
                }
            }

            bool showTools = config.ShowTools;
            foreach (var lib in libraries)
            {
                string apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue; // external drive not connected, etc.
                string[] manifests;
                try { manifests = Directory.GetFiles(apps, "appmanifest_*.acf"); }
                catch { continue; }

                foreach (var file in manifests)
                {
                    try
                    {
                        var st = Vdf.Child(Vdf.ParseFile(file), "AppState");
                        if (st == null) continue;
                        string id = Vdf.Str(st, "appid");
                        string name = Vdf.Str(st, "name");
                        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;

                        var g = new GameEntry();
                        g.Source = "steam";
                        g.Id = id;
                        g.Key = "steam:" + id;
                        g.Name = name;
                        g.InstallDir = Path.Combine(apps, "common", Vdf.Str(st, "installdir") ?? "");
                        g.LaunchUri = "steam://rungameid/" + id;
                        g.SizeBytes = Vdf.Long(st, "SizeOnDisk");
                        g.LastPlayed = Vdf.Long(st, "LastPlayed");
                        long flags = Vdf.Long(st, "StateFlags");
                        g.Updating = flags != 4;
                        g.IsTool = SteamToolIds.Contains(id) || ToolName.IsMatch(name);
                        g.SteamArtId = id;

                        var pa = playApps != null ? Vdf.Child(playApps, id) : null;
                        if (pa != null)
                        {
                            g.PlaytimeMinutes = Vdf.Long(pa, "Playtime");
                            long lp = Vdf.Long(pa, "LastPlayed");
                            if (lp > g.LastPlayed) g.LastPlayed = lp;
                        }

                        FindSteamArt(id, g.LocalArt);
                        if (g.IsTool && !showTools) continue;
                        result.Add(g);
                    }
                    catch (Exception ex)
                    {
                        Log.Write("bad manifest " + file + ": " + ex.Message);
                    }
                }
            }
            return result;
        }

        private SteamUser ReadSteamUser()
        {
            string file = Path.Combine(steamPath, @"config\loginusers.vdf");
            if (!File.Exists(file)) return null;
            var users = Vdf.Child(Vdf.ParseFile(file), "users");
            if (users == null) return null;
            string best = null;
            Dictionary<string, object> bestData = null;
            long bestTime = -1;
            foreach (var kv in users)
            {
                var d = kv.Value as Dictionary<string, object>;
                if (d == null) continue;
                long ts = Vdf.Long(d, "Timestamp");
                if (Vdf.Str(d, "MostRecent") == "1") ts = long.MaxValue;
                if (ts > bestTime) { bestTime = ts; best = kv.Key; bestData = d; }
            }
            if (best == null) return null;
            var u = new SteamUser();
            u.SteamId64 = best;
            u.Name = Vdf.Str(bestData, "PersonaName") ?? Vdf.Str(bestData, "AccountName");
            ulong sid;
            if (ulong.TryParse(best, out sid)) u.AccountId = (sid & 0xFFFFFFFF).ToString();
            string avatar = Path.Combine(steamPath, @"config\avatarcache\" + best + ".png");
            if (File.Exists(avatar)) u.AvatarPath = avatar;
            return u;
        }

        private Dictionary<string, object> ReadLocalConfigApps(SteamUser user)
        {
            if (user == null || user.AccountId == null) return null;
            string file = Path.Combine(steamPath, @"userdata\" + user.AccountId + @"\config\localconfig.vdf");
            if (!File.Exists(file)) return null;
            var root = Vdf.ParseFile(file);
            return Vdf.Child(root, "UserLocalConfigStore", "Software", "Valve", "Steam", "apps")
                ?? Vdf.Child(root, "UserLocalConfigStore", "Software", "Valve", "steam", "apps");
        }

        private void FindSteamArt(string appId, Dictionary<string, string> art)
        {
            string dir = Path.Combine(steamPath, @"appcache\librarycache\" + appId);
            if (!Directory.Exists(dir)) return;
            // Newer Steam clients store each image in a hash-named sub-folder; older ones keep them flat.
            string[] files;
            try { files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories); }
            catch { return; }
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files) byName[Path.GetFileName(f)] = f;
            string v;
            if (byName.TryGetValue("library_600x900_2x.jpg", out v) || byName.TryGetValue("library_600x900.jpg", out v) || byName.TryGetValue("library_capsule.jpg", out v)) art["portrait"] = v;
            if (byName.TryGetValue("library_hero.jpg", out v)) art["hero"] = v;
            if (byName.TryGetValue("logo.png", out v) || byName.TryGetValue("logo_2x.png", out v)) art["logo"] = v;
            if (byName.TryGetValue("library_header_2x.jpg", out v) || byName.TryGetValue("header.jpg", out v) || byName.TryGetValue("library_header.jpg", out v)) art["header"] = v;
        }

        public static string SteamCdn(string appId, string kind)
        {
            string file;
            switch (kind)
            {
                case "portrait": file = "library_600x900_2x.jpg"; break;
                case "hero": file = "library_hero.jpg"; break;
                case "logo": file = "logo.png"; break;
                default: file = "header.jpg"; break;
            }
            return "https://shared.steamstatic.com/store_item_assets/steam/apps/" + appId + "/" + file;
        }

        // ---------------------------------------------------------------- Epic

        private IEnumerable<GameEntry> ScanEpic()
        {
            var result = new List<GameEntry>();
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Epic\EpicGamesLauncher\Data\Manifests");
            if (!Directory.Exists(dir)) return result;
            foreach (var file in Directory.GetFiles(dir, "*.item"))
            {
                try
                {
                    var m = Json.ParseObject(File.ReadAllText(file, Encoding.UTF8));
                    string app = Json.Str(m, "AppName", null);
                    string main = Json.Str(m, "MainGameAppName", "");
                    if (app == null) continue;
                    if (main.Length > 0 && app != main) continue; // DLC
                    var cats = Json.Strings(m, "AppCategories");
                    if (cats.Count > 0 && !cats.Contains("games")) continue;
                    string install = Json.Str(m, "InstallLocation", "");
                    if (install.Length > 0 && !Directory.Exists(install)) continue;

                    var g = new GameEntry();
                    g.Source = "epic";
                    g.Id = app;
                    g.Key = "epic:" + app;
                    g.Name = CleanName(Json.Str(m, "DisplayName", app));
                    g.InstallDir = install;
                    g.LaunchUri = "com.epicgames.launcher://apps/" +
                        Uri.EscapeDataString(Json.Str(m, "CatalogNamespace", "")) + "%3A" +
                        Uri.EscapeDataString(Json.Str(m, "CatalogItemId", "")) + "%3A" +
                        Uri.EscapeDataString(app) + "?action=launch&silent=true";
                    long size;
                    long.TryParse(Json.Str(m, "InstallSize", "0"), out size);
                    g.SizeBytes = size;
                    result.Add(g);
                }
                catch (Exception ex)
                {
                    Log.Write("bad epic manifest " + file + ": " + ex.Message);
                }
            }
            return result;
        }

        // ---------------------------------------------------------------- GOG

        private IEnumerable<GameEntry> ScanGog()
        {
            var result = new List<GameEntry>();
            using (var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games"))
            {
                if (root == null) return result;
                foreach (var id in root.GetSubKeyNames())
                {
                    using (var k = root.OpenSubKey(id))
                    {
                        if (k == null) continue;
                        string name = k.GetValue("gameName") as string;
                        string exe = k.GetValue("exe") as string;
                        string path = k.GetValue("path") as string;
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(exe) || !File.Exists(exe)) continue;
                        if ((k.GetValue("dependsOn") as string ?? "").Length > 0) continue; // DLC
                        var g = new GameEntry();
                        g.Source = "gog";
                        g.Id = id;
                        g.Key = "gog:" + id;
                        g.Name = CleanName(name);
                        g.InstallDir = path;
                        g.Exe = exe;
                        g.Args = k.GetValue("launchParam") as string;
                        g.WorkingDir = (k.GetValue("workingDir") as string) ?? path;
                        result.Add(g);
                    }
                }
            }
            return result;
        }

        // ---------------------------------------------------------------- Ubisoft Connect

        private IEnumerable<GameEntry> ScanUbisoft()
        {
            var result = new List<GameEntry>();
            using (var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs"))
            {
                if (root == null) return result;
                foreach (var id in root.GetSubKeyNames())
                {
                    string dir;
                    using (var k = root.OpenSubKey(id)) dir = k == null ? null : k.GetValue("InstallDir") as string;
                    if (string.IsNullOrEmpty(dir)) continue;
                    dir = dir.Replace('/', '\\').TrimEnd('\\');
                    if (!Directory.Exists(dir)) continue;

                    string name = null;
                    using (var u = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Uplay Install " + id))
                        if (u != null) name = u.GetValue("DisplayName") as string;
                    // Ubisoft often registers a code name ("AFOP"); the game exe carries the real title.
                    if (string.IsNullOrEmpty(name) || !name.Contains(" "))
                    {
                        string product = ProductNameFromExe(dir);
                        if (!string.IsNullOrEmpty(product)) name = product;
                    }
                    if (string.IsNullOrEmpty(name)) name = Path.GetFileName(dir);

                    var g = new GameEntry();
                    g.Source = "ubisoft";
                    g.Id = id;
                    g.Key = "ubisoft:" + id;
                    g.Name = CleanName(name);
                    g.InstallDir = dir;
                    g.LaunchUri = "uplay://launch/" + id + "/0";
                    result.Add(g);
                }
            }
            return result;
        }

        /// <summary>ProductName of the largest .exe in the folder (the game binary, not a crash reporter).</summary>
        private static string ProductNameFromExe(string dir)
        {
            try
            {
                var exe = new DirectoryInfo(dir).GetFiles("*.exe")
                    .OrderByDescending(f => f.Length)
                    .FirstOrDefault();
                if (exe == null) return null;
                var v = FileVersionInfo.GetVersionInfo(exe.FullName);
                return string.IsNullOrWhiteSpace(v.ProductName) ? v.FileDescription : v.ProductName;
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- Custom (config.json)

        private IEnumerable<GameEntry> ScanCustom()
        {
            var result = new List<GameEntry>();
            foreach (var c in config.CustomGames)
            {
                string name = Json.Str(c, "name", null);
                string exe = Json.Str(c, "exe", null);
                string uri = Json.Str(c, "uri", null);
                if (name == null || (exe == null && uri == null)) continue;
                var g = new GameEntry();
                g.Source = "custom";
                g.Id = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
                g.Key = "custom:" + g.Id;
                g.Name = name;
                g.Exe = exe;
                g.LaunchUri = uri;
                g.Args = Json.Str(c, "args", null);
                g.WorkingDir = Json.Str(c, "workingDir", exe != null ? Path.GetDirectoryName(exe) : null);
                g.InstallDir = Json.Str(c, "installDir", g.WorkingDir);
                string steamArt = Json.Str(c, "steamAppId", null);
                if (steamArt != null) g.SteamArtId = steamArt;


                object artObj;
                if (c.TryGetValue("art", out artObj) && artObj is Dictionary<string, object>)
                    foreach (var kv in (Dictionary<string, object>)artObj)
                        if (kv.Value != null) g.LocalArt[kv.Key] = Convert.ToString(kv.Value);
                result.Add(g);
            }
            return result;
        }

        // ---------------------------------------------------------------- Art for non-Steam games

        /// <summary>
        /// Epic/GOG don't keep nice 10-foot artwork on disk, but most of those games are also
        /// on Steam. Look the title up in the public Steam store search once and reuse its art.
        /// </summary>
        private void ResolveMissingArt()
        {
            bool changed = false;
            foreach (var g in Games)
            {
                if (g.SteamArtId != null || g.LocalArt.Count > 0) continue;
                string norm = Normalize(g.Name);
                string appId;
                lock (gate)
                {
                    if (!steamArtMap.TryGetValue(norm, out appId)) appId = null;
                }
                if (appId == null)
                {
                    appId = LookupSteamAppId(g.Name);
                    if (appId == null) continue; // offline / error: try again next scan
                    lock (gate) steamArtMap[norm] = appId;
                    SaveArtMap();
                }
                if (appId.Length > 0)
                {
                    g.SteamArtId = appId;
                    changed = true;
                }
            }
            var c = Changed;
            if (changed && c != null) c();
        }

        private static string LookupSteamAppId(string name)
        {
            try
            {
                using (var wc = new WebClient())
                {
                    wc.Encoding = Encoding.UTF8;
                    wc.Headers[HttpRequestHeader.UserAgent] = "BWOS/1.0";
                    string url = "https://store.steampowered.com/api/storesearch/?cc=us&l=english&term=" + Uri.EscapeDataString(name);
                    var res = Json.ParseObject(wc.DownloadString(url));
                    string want = Normalize(name);
                    foreach (var item in Json.List(res, "items"))
                    {
                        string n = Normalize(Json.Str(item, "name", ""));
                        if (n == want || n.StartsWith(want) || want.StartsWith(n))
                            return Json.Str(item, "id", null);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("steam art lookup failed for " + name + ": " + ex.Message);
                return null; // don't cache failures caused by being offline
            }
            return "";
        }

        private void LoadArtMap()
        {
            steamArtMap = new Dictionary<string, string>();
            try
            {
                string f = Path.Combine(Paths.DataDir, "artmap.json");
                if (!File.Exists(f)) return;
                foreach (var kv in Json.ParseObject(File.ReadAllText(f)))
                    steamArtMap[kv.Key] = Convert.ToString(kv.Value);
            }
            catch { }
        }

        private void SaveArtMap()
        {
            try
            {
                string text;
                lock (gate) text = Json.Serialize(steamArtMap);
                File.WriteAllText(Path.Combine(Paths.DataDir, "artmap.json"), Json.Pretty(text));
            }
            catch { }
        }

        // ---------------------------------------------------------------- helpers

        public static string CleanName(string s)
        {
            if (s == null) return s;
            return Regex.Replace(s, "[\u00AE\u2122\u00A9]", "").Trim();
        }

        private static string Normalize(string s)
        {
            return Regex.Replace(CleanName(s ?? "").ToLowerInvariant(), "[^a-z0-9]+", "");
        }
    }
}
