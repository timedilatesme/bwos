using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Bwos
{
    // User-selected YouTube links. Uses the public oEmbed endpoint for metadata,
    // never player extraction, signed stream URLs, or private account endpoints.
    internal sealed class VideoLibrary
    {
        private readonly object gate = new object();
        private readonly string file;
        private readonly Func<string, Dictionary<string, object>> metadata;
        private List<Dictionary<string, object>> videos = new List<Dictionary<string, object>>();
        public VideoLibrary(string file = null, Func<string, Dictionary<string, object>> metadata = null)
        {
            this.file = file ?? Path.Combine(Paths.DataDir, "videos.json");
            this.metadata = metadata ?? Metadata;
            try { if (File.Exists(this.file)) videos = Json.List(Json.ParseObject(File.ReadAllText(this.file, Encoding.UTF8)), "videos").Where(v => VideoId(Json.Str(v, "id", "")) != null).Take(100).ToList(); }
            catch { Log.Write("video library: could not read saved links"); }
        }
        public static string VideoId(string input)
        {
            input = (input ?? "").Trim();
            if (Regex.IsMatch(input, @"^[\w-]{11}$", RegexOptions.ECMAScript)) return input;
            Uri uri;
            if (input.Length > 2000 || !Uri.TryCreate(input, UriKind.Absolute, out uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort) return null;
            string host = uri.Host.ToLowerInvariant(), candidate = "";
            if (host == "youtu.be") candidate = uri.AbsolutePath.Trim('/');
            else if (host == "youtube.com" || host == "www.youtube.com" || host == "m.youtube.com")
            {
                if (uri.AbsolutePath == "/watch" || uri.AbsolutePath == "/tv" || uri.AbsolutePath == "/tv/")
                {
                    var match = Regex.Match(uri.Query + uri.Fragment, @"[?&]v=([^&#]+)");
                    if (match.Success) { try { candidate = Uri.UnescapeDataString(match.Groups[1].Value); } catch { return null; } }
                }
                else { var match = Regex.Match(uri.AbsolutePath, @"^/(?:shorts|embed|live)/([^/]+)/?$"); if (match.Success) candidate = match.Groups[1].Value; }
            }
            return Regex.IsMatch(candidate, @"^[\w-]{11}$", RegexOptions.ECMAScript) ? candidate : null;
        }
        private static Dictionary<string, object> Metadata(string id)
        {
            var request = (HttpWebRequest)WebRequest.Create("https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=" + id));
            request.Timeout = 8000; request.ReadWriteTimeout = 8000; request.AllowAutoRedirect = false;
            using (var response = request.GetResponse()) using (var stream = response.GetResponseStream()) using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                var buffer = new char[65537]; int length = 0, count;
                while (length < buffer.Length && (count = reader.Read(buffer, length, buffer.Length - length)) > 0) length += count;
                if (length > 65536) throw new IOException("Metadata too large");
                return Json.ParseObject(new string(buffer, 0, length));
            }
        }
        public object Describe() { lock (gate) return videos.Select(v => new Dictionary<string, object>(v)).ToArray(); }
        public object Add(string input)
        {
            string id = VideoId(input);
            if (id == null) return WebServer.Err("Enter a valid HTTPS YouTube video link.");
            Dictionary<string, object> details = null;
            try { details = metadata(id); } catch { /* A saved link still works when metadata is unavailable. */ }
            var video = new Dictionary<string, object> { { "id", id }, { "title", Clip(Json.Str(details ?? new Dictionary<string, object>(), "title", "YouTube video"), 180) },
                { "author", Clip(Json.Str(details ?? new Dictionary<string, object>(), "author_name", ""), 120) }, { "url", "https://www.youtube.com/watch?v=" + id },
                { "artwork", "https://i.ytimg.com/vi/" + id + "/hqdefault.jpg" } };
            lock (gate)
            {
                var next = videos.Where(v => Json.Str(v, "id", "") != id).ToList(); next.Insert(0, video);
                next = next.Take(100).ToList();
                try { Save(next); videos = next; } catch { return WebServer.Err("Could not save this video. Check available disk space."); }
            }
            return new Dictionary<string, object> { { "ok", true }, { "video", video }, { "metadataAvailable", details != null } };
        }
        public object Remove(string id)
        {
            lock (gate) { var next = videos.Where(v => Json.Str(v, "id", "") != id).ToList(); try { Save(next); videos = next; } catch { return WebServer.Err("Could not update saved videos."); } }
            return new Dictionary<string, object> { { "ok", true } };
        }
        private static string Clip(string text, int size) { return text.Substring(0, Math.Min(text.Length, size)); }
        private void Save(List<Dictionary<string, object>> next)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temp = file + ".tmp"; File.WriteAllText(temp, Json.Serialize(new Dictionary<string, object> { { "videos", next } }), Encoding.UTF8);
            if (File.Exists(file)) File.Replace(temp, file, null); else File.Move(temp, file);
        }
    }
}
