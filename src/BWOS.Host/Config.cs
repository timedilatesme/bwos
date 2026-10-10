using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace Bwos
{
    internal static class Paths
    {
        /// <summary>Folder that holds BWOS.exe and the ui/ folder.</summary>
        public static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

        /// <summary>
        /// Runtime data (config, logs, browser profiles). Portable by design: it lives next
        /// to the exe unless that folder is read-only, in which case %LOCALAPPDATA%\BWOS is used.
        /// </summary>
        public static readonly string DataDir = ResolveDataDir();

        public static string UiDir { get { return Path.Combine(AppDir, "ui"); } }
        public static string ConfigFile { get { return Path.Combine(DataDir, "config.json"); } }

        private static string ResolveDataDir()
        {
            string local = Path.Combine(AppDir, "data");
            try
            {
                Directory.CreateDirectory(local);
                string probe = Path.Combine(local, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return local;
            }
            catch
            {
                string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BWOS");
                Directory.CreateDirectory(fallback);
                return fallback;
            }
        }
    }

    internal static class Log
    {
        private static readonly object Gate = new object();

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    string file = Path.Combine(Paths.DataDir, "bwos.log");
                    var fi = new FileInfo(file);
                    if (fi.Exists && fi.Length > 1024 * 1024) File.Delete(file);
                    File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    internal static class Json
    {
        private static JavaScriptSerializer Make()
        {
            var s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            s.RecursionLimit = 256;
            return s;
        }

        public static string Serialize(object o) { return Make().Serialize(o); }

        public static Dictionary<string, object> ParseObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, object>();
            var o = Make().DeserializeObject(text) as Dictionary<string, object>;
            return o ?? new Dictionary<string, object>();
        }

        public static object Parse(string text) { return Make().DeserializeObject(text); }

        /// <summary>Minimal pretty-printer so config.json stays hand-editable.</summary>
        public static string Pretty(string json)
        {
            var sb = new StringBuilder();
            int indent = 0;
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\' || (i > 1 && json[i - 2] == '\\'))) inString = !inString;
                if (inString) { sb.Append(c); continue; }
                switch (c)
                {
                    case '{':
                    case '[':
                        sb.Append(c);
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) { sb.Append(json[++i]); break; }
                        sb.Append('\n').Append(' ', ++indent * 2);
                        break;
                    case '}':
                    case ']':
                        sb.Append('\n').Append(' ', --indent * 2).Append(c);
                        break;
                    case ',':
                        sb.Append(c).Append('\n').Append(' ', indent * 2);
                        break;
                    case ':':
                        sb.Append(": ");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public static string Str(Dictionary<string, object> d, string key, string fallback)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null) return Convert.ToString(v);
            return fallback;
        }

        public static bool Bool(Dictionary<string, object> d, string key, bool fallback)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v is bool) return (bool)v;
            return fallback;
        }

        public static int Int(Dictionary<string, object> d, string key, int fallback)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToInt32(v); } catch { }
            }
            return fallback;
        }

        public static List<Dictionary<string, object>> List(Dictionary<string, object> d, string key)
        {
            var result = new List<Dictionary<string, object>>();
            object v;
            if (d == null || !d.TryGetValue(key, out v)) return result;
            var arr = v as IEnumerable;
            if (arr == null || v is string) return result;
            foreach (var item in arr)
            {
                var o = item as Dictionary<string, object>;
                if (o != null) result.Add(o);
            }
            return result;
        }

        public static List<string> Strings(Dictionary<string, object> d, string key)
        {
            var result = new List<string>();
            object v;
            if (d == null || !d.TryGetValue(key, out v)) return result;
            var arr = v as IEnumerable;
            if (arr == null || v is string) return result;
            foreach (var item in arr) if (item != null) result.Add(Convert.ToString(item));
            return result;
        }
    }

    /// <summary>User settings persisted in data/config.json.</summary>
    internal sealed class Config
    {
        private readonly object gate = new object();
        private Dictionary<string, object> data;

        public Config()
        {
            Load();
        }

        public void Load()
        {
            lock (gate)
            {
                if (!File.Exists(Paths.ConfigFile))
                {
                    data = Json.ParseObject(DefaultJson());
                    Save();
                    return;
                }
                try
                {
                    data = Json.ParseObject(File.ReadAllText(Paths.ConfigFile, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    Log.Write("config.json is invalid, using defaults: " + ex.Message);
                    data = Json.ParseObject(DefaultJson());
                }
                // Fill in keys that were added in newer versions.
                var defaults = Json.ParseObject(DefaultJson());
                foreach (var kv in defaults) if (!data.ContainsKey(kv.Key)) data[kv.Key] = kv.Value;
                if (MergeNewDefaultApps(defaults)) Save();
            }
        }

        /// <summary>
        /// Apps added to BWOS in an update appear in existing setups too – once. Apps the user removed
        /// from "watch" stay removed, because every default id is remembered in "seenDefaultApps".
        /// Also fills newer fields (e.g. "jw") into default apps the user already has.
        /// </summary>
        private bool MergeNewDefaultApps(Dictionary<string, object> defaults)
        {
            var seen = Json.Strings(data, "seenDefaultApps");
            var watch = Json.List(data, "watch");

            bool changed = false;
            foreach (var app in Json.List(defaults, "watch"))
            {
                string id = Json.Str(app, "id", "");
                var mine = watch.FirstOrDefault(w => Json.Str(w, "id", "") == id);
                if (mine != null)
                {
                    foreach (var kv in app) if (!mine.ContainsKey(kv.Key)) { mine[kv.Key] = kv.Value; changed = true; }
                }
                else if (!seen.Contains(id) && id != "") { watch.Add(app); changed = true; }
                if (!seen.Contains(id)) { seen.Add(id); changed = true; }
            }
            if (changed) { data["watch"] = watch; data["seenDefaultApps"] = seen; }
            return changed;
        }

        public void Save()
        {
            lock (gate)
            {
                File.WriteAllText(Paths.ConfigFile, Json.Pretty(Json.Serialize(data)), new UTF8Encoding(false));
            }
        }

        public int Port { get { lock (gate) return Json.Int(data, "port", 47913); } }
        public string Trigger { get { lock (gate) return Json.Str(data, "trigger", "guide"); } }
        public bool ControllerBridge { get { lock (gate) return Json.Bool(data, "controllerBridge", true); } }
        public bool ShowTools { get { lock (gate) return Json.Bool(data, "showTools", false); } }
        /// <summary>Minutes without input before the BWOS screen fades to black (0 = never).</summary>
        public int BlankAfter { get { lock (gate) return Json.Int(data, "blankAfter", 10); } }
        /// <summary>Minutes without input before BWOS lets Windows switch the display off, so the projector can go to standby (0 = never).</summary>
        public int StandbyAfter { get { lock (gate) return Json.Int(data, "standbyAfter", 0); } }
        public bool OpenOnWake { get { lock (gate) return Json.Bool(data, "openOnWake", true); } }
        /// <summary>Show up in phones' YouTube cast menu (DIAL on the home network).</summary>
        public bool CastEnabled { get { lock (gate) return Json.Bool(data, "castEnabled", true); } }
        /// <summary>AirPlay screen mirroring (runs UxPlay in the background when it's installed).</summary>
        public bool AirPlayEnabled { get { lock (gate) return Json.Bool(data, "airplayEnabled", true); } }
        /// <summary>Custom uxplay.exe location ("" = data\tools\uxplay\uxplay.exe).</summary>
        public string AirPlayPath { get { lock (gate) return Json.Str(data, "airplayPath", ""); } }
        /// <summary>Name shown in the top bar ("" = your Steam profile name).</summary>
        public string UserName { get { lock (gate) return Json.Str(data, "userName", ""); } }
        public string Browser { get { lock (gate) return Json.Str(data, "browser", ""); } }
        public bool VoiceEnabled { get { lock (gate) return Json.Bool(data, "voiceEnabled", false); } }
        public string VoiceLanguage { get { lock (gate) return Json.Str(data, "voiceLanguage", "auto"); } }
        public string VoiceEngine { get { lock (gate) return Json.Str(data, "voiceEngine", "whisper"); } }
        public bool VoiceReplies { get { lock (gate) return Json.Bool(data, "voiceReplies", true); } }
        public string VoiceMode { get { lock (gate) return Json.Str(data, "voiceMode", "wake"); } }
        public int VoiceMicrophone { get { lock (gate) return Json.Int(data, "voiceMicrophone", -1); } }
        public List<string> Hidden { get { lock (gate) return Json.Strings(data, "hidden"); } }
        public List<Dictionary<string, object>> Watch { get { lock (gate) return Json.List(data, "watch"); } }
        public List<Dictionary<string, object>> CustomGames { get { lock (gate) return Json.List(data, "customGames"); } }

        /// <summary>Two-letter country for recommendations; defaults to the Windows region.</summary>
        public string Country
        {
            get
            {
                string c;
                lock (gate) c = Json.Str(data, "country", "");
                if (c.Length == 2) return c.ToUpperInvariant();
                try { return new System.Globalization.RegionInfo(System.Globalization.CultureInfo.CurrentCulture.Name).TwoLetterISORegionName; }
                catch { return "US"; }
            }
        }

        /// <summary>"artOverrides": { "epic:Sugar": "252950" } – use that Steam app's artwork for a game.</summary>
        public Dictionary<string, string> ArtOverrides
        {
            get
            {
                var result = new Dictionary<string, string>();
                lock (gate)
                {
                    object v;
                    var d = data.TryGetValue("artOverrides", out v) ? v as Dictionary<string, object> : null;
                    if (d != null) foreach (var kv in d) if (kv.Value != null) result[kv.Key] = Convert.ToString(kv.Value);
                }
                return result;
            }
        }

        public void Set(string key, object value)
        {
            lock (gate) data[key] = value;
            Save();
        }

        public void ToggleHidden(string key)
        {
            lock (gate)
            {
                var list = Json.Strings(data, "hidden");
                if (list.Contains(key)) list.Remove(key); else list.Add(key);
                data["hidden"] = list;
            }
            Save();
        }

        private static string DefaultJson()
        {
            // "section": stream = sign-in services, free = free & legal ad-supported, music.
            // "jw": JustWatch provider codes (For You); "country": per-app catalogue region; "hosts": extra site domains.
            return @"{
  ""port"": 47913,
  ""trigger"": ""guide"",
  ""controllerBridge"": true,
  ""showTools"": false,
  ""blankAfter"": 10,
  ""standbyAfter"": 0,
  ""openOnWake"": true,
  ""browser"": """",
  ""voiceEnabled"": false,
  ""voiceLanguage"": ""auto"",
  ""voiceEngine"": ""whisper"",
  ""voiceReplies"": true,
  ""voiceMode"": ""wake"",
  ""voiceMicrophone"": -1,
  ""hidden"": [""watch:hotstar"", ""watch:disney"", ""watch:crunchyroll"", ""watch:twitch"", ""watch:zee5"", ""watch:sonyliv"", ""watch:mxplayer"", ""watch:archive"", ""watch:spotify"", ""watch:ytmusic""],
  ""customGames"": [],
  ""artOverrides"": {},
  ""country"": """",
  ""watch"": [
    { ""id"": ""youtube"",    ""name"": ""YouTube"",        ""section"": ""stream"", ""url"": ""https://www.youtube.com/tv"", ""color"": ""#ff0033"", ""tagline"": ""TV interface · controller friendly"" },
    { ""id"": ""netflix"",    ""name"": ""Netflix"",        ""section"": ""stream"", ""url"": ""https://www.netflix.com/browse"", ""icon"": ""https://assets.nflxext.com/en_us/layout/ecweb/netflix-app-icon_152.jpg"", ""color"": ""#e50914"", ""tagline"": ""Series & films"", ""jw"": ""nfx,nfa"" },
    { ""id"": ""prime"",      ""name"": ""Prime Video"",    ""section"": ""stream"", ""url"": ""https://www.primevideo.com"", ""color"": ""#1a98ff"", ""tagline"": ""Originals, movies, sport"", ""jw"": ""amp,prv"", ""hosts"": ""primevideo.com,amazon.com"" },
    { ""id"": ""hotstar"",    ""name"": ""JioHotstar"",     ""section"": ""stream"", ""url"": ""https://www.hotstar.com"", ""color"": ""#1f80e0"", ""tagline"": ""Movies, shows, live cricket"", ""jw"": ""jhs"" },
    { ""id"": ""zee5"",       ""name"": ""ZEE5"",           ""section"": ""stream"", ""url"": ""https://www.zee5.com"", ""color"": ""#8230c6"", ""tagline"": ""Indian originals, movies, TV"", ""jw"": ""zee"" },
    { ""id"": ""sonyliv"",    ""name"": ""Sony LIV"",       ""section"": ""stream"", ""url"": ""https://www.sonyliv.com"", ""color"": ""#c7a44a"", ""tagline"": ""Shows, movies, live sport"", ""jw"": ""snl"" },
    { ""id"": ""disney"",     ""name"": ""Disney+"",        ""section"": ""stream"", ""url"": ""https://www.disneyplus.com"", ""color"": ""#0b3fa8"", ""tagline"": ""Disney, Pixar, Marvel, Star Wars"" },
    { ""id"": ""crunchyroll"",""name"": ""Crunchyroll"",    ""section"": ""stream"", ""url"": ""https://www.crunchyroll.com"", ""color"": ""#f47521"", ""tagline"": ""Anime"", ""jw"": ""cru"" },
    { ""id"": ""twitch"",     ""name"": ""Twitch"",         ""section"": ""stream"", ""url"": ""https://www.twitch.tv"", ""color"": ""#9146ff"", ""tagline"": ""Live streams"" },
    { ""id"": ""plex"",       ""name"": ""Plex"",           ""section"": ""free"",   ""url"": ""https://watch.plex.tv"", ""color"": ""#e5a00d"", ""tagline"": ""Free movies & live TV"", ""jw"": ""pxp,plc"" },
    { ""id"": ""pluto"",      ""name"": ""Pluto TV"",       ""section"": ""free"",   ""url"": ""https://pluto.tv"", ""color"": ""#2b2bd9"", ""tagline"": ""Free live channels"", ""jw"": ""ptv"" },
    { ""id"": ""tubi"",       ""name"": ""Tubi"",           ""section"": ""free"",   ""url"": ""https://tubitv.com"", ""color"": ""#7408ff"", ""tagline"": ""Free movies & shows"", ""jw"": ""tbv"" },
    { ""id"": ""mxplayer"",   ""name"": ""Amazon MX Player"",""section"": ""free"",  ""url"": ""https://www.mxplayer.in"", ""color"": ""#0c7cff"", ""tagline"": ""Free series & movies"", ""jw"": ""mxp"" },
    { ""id"": ""einthusan"",  ""name"": ""Einthusan"",      ""section"": ""free"",   ""url"": ""https://einthusan.tv"", ""color"": ""#d4382c"", ""tagline"": ""Indian movies in many languages"" },
    { ""id"": ""archive"",    ""name"": ""Internet Archive"",""section"": ""free"",  ""url"": ""https://archive.org/details/feature_films"", ""color"": ""#3a3a3a"", ""tagline"": ""Public-domain classics"" },
    { ""id"": ""spotify"",    ""name"": ""Spotify"",        ""section"": ""music"",  ""url"": ""https://open.spotify.com"", ""color"": ""#1db954"", ""tagline"": ""Music & podcasts"" },
    { ""id"": ""ytmusic"",    ""name"": ""YouTube Music"",  ""section"": ""music"",  ""url"": ""https://music.youtube.com"", ""color"": ""#c4302b"", ""tagline"": ""Songs, albums, mixes"" }
  ]
}";
        }
    }
}
