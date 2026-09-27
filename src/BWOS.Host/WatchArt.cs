using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Bwos
{
    /// <summary>
    /// Official app icons for the streaming tiles. Fetched once from each service's own web-app
    /// manifest (the same icon it uses when installed as an app), falling back to its
    /// apple-touch-icon and finally a high-resolution favicon. Cached in data\art\.
    /// A "icon" URL on a watch entry in config.json overrides the lookup.
    /// </summary>
    internal static class WatchArt
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<string> Failed = new HashSet<string>();
        private const string BrowserUa =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0";

        private static string Dir
        {
            get { string d = Path.Combine(Paths.DataDir, "art"); Directory.CreateDirectory(d); return d; }
        }

        /// <summary>Local path of the icon for a watch entry, downloading it on first use. Null if none could be found.</summary>
        public static string Icon(Dictionary<string, object> app)
        {
            string id = Regex.Replace(Json.Str(app, "id", "x"), "[^a-zA-Z0-9_-]", "");
            string cached = Directory.GetFiles(Dir, "watch-" + id + ".*").FirstOrDefault();
            if (cached != null) return cached;

            lock (Gate)
            {
                if (Failed.Contains(id)) return null;
                cached = Directory.GetFiles(Dir, "watch-" + id + ".*").FirstOrDefault();
                if (cached != null) return cached;
                try
                {
                    var urls = new List<string>();
                    string overrideUrl = Json.Str(app, "icon", null);
                    if (overrideUrl != null) urls.Add(overrideUrl);
                    urls.AddRange(Discover(Json.Str(app, "url", "")));

                    // Take the first icon that is at least 128 px; otherwise the largest one seen.
                    byte[] best = null; string bestUrl = null; int bestPx = -1;
                    foreach (var url in urls.Distinct())
                    {
                        byte[] bytes;
                        try { bytes = Download(url); } catch { continue; }
                        if (ExtensionOf(bytes) == null) continue;
                        int px = PixelSize(bytes);
                        if (px > bestPx) { best = bytes; bestUrl = url; bestPx = px; }
                        if (px >= 128) break;
                    }
                    if (best != null)
                    {
                        string file = Path.Combine(Dir, "watch-" + id + ExtensionOf(best));
                        File.WriteAllBytes(file, best);
                        Log.Write("icon for " + id + " (" + (bestPx == int.MaxValue ? "svg" : bestPx + "px") + "): " + bestUrl);
                        return file;
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("icon lookup failed for " + id + ": " + ex.Message);
                }
                Failed.Add(id); // don't retry every request this session
                return null;
            }
        }

        /// <summary>Candidate icon URLs, best first.</summary>
        private static List<string> Discover(string pageUrl)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(pageUrl)) return result;
            var appUri = new Uri(pageUrl);
            // The service's home page carries the manifest; app pages (/tv, /browse) often don't.
            var baseUri = new Uri(appUri.Scheme + "://" + appUri.Host + "/");
            string html = null;
            foreach (var page in new[] { baseUri.ToString(), pageUrl })
            {
                try { html = DownloadString(page); if (html.IndexOf("<link", StringComparison.OrdinalIgnoreCase) >= 0) break; }
                catch (Exception ex) { Log.Write("icon page fetch failed " + page + ": " + ex.Message); }
            }

            if (html != null)
            {
                // 1) Web app manifest → largest icon
                string manifestHref = LinkHref(html, "manifest");
                if (manifestHref != null)
                {
                    try
                    {
                        var manifestUri = new Uri(baseUri, WebUtility.HtmlDecode(manifestHref));
                        var manifest = Json.ParseObject(DownloadString(manifestUri.ToString()).TrimStart('\uFEFF'));
                        var icons = new List<KeyValuePair<int, string>>();
                        foreach (var icon in Json.List(manifest, "icons"))
                        {
                            string src = Json.Str(icon, "src", null);
                            if (src == null) continue;
                            string purpose = Json.Str(icon, "purpose", "any");
                            if (purpose.Contains("monochrome")) continue;
                            int size = 0;
                            foreach (Match m in Regex.Matches(Json.Str(icon, "sizes", ""), @"(\d+)x\d+"))
                                size = Math.Max(size, int.Parse(m.Groups[1].Value));
                            if (purpose.Contains("maskable") && !purpose.Contains("any")) size -= 1; // prefer "any"
                            icons.Add(new KeyValuePair<int, string>(size, new Uri(manifestUri, src).ToString()));
                        }
                        result.AddRange(icons.OrderByDescending(i => i.Key).Select(i => i.Value));
                    }
                    catch (Exception ex) { Log.Write("manifest read failed " + manifestHref + ": " + ex.Message); }
                }
                // 2) apple-touch-icon (usually 180×180)
                foreach (var rel in new[] { "apple-touch-icon", "apple-touch-icon-precomposed" })
                {
                    string touch = LinkHref(html, rel);
                    if (touch != null) result.Add(new Uri(baseUri, WebUtility.HtmlDecode(touch)).ToString());
                }
            }
            // 3) Well-known locations, then a high-resolution favicon service
            result.Add(new Uri(baseUri, "/apple-touch-icon.png").ToString());
            result.Add("https://t1.gstatic.com/faviconV2?client=SOCIAL&type=FAVICON&fallback_opts=TYPE,SIZE,URL&size=256&url=" +
                Uri.EscapeDataString(baseUri.Scheme + "://" + baseUri.Host));
            return result;
        }

        /// <summary>Width in pixels (PNG/JPEG/ICO); SVG counts as infinitely large.</summary>
        private static int PixelSize(byte[] b)
        {
            string ext = ExtensionOf(b);
            if (ext == ".svg") return int.MaxValue;
            if (ext == ".png" && b.Length > 24) return (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
            if (ext == ".ico")
            {
                int count = b[4] | (b[5] << 8), max = 0;
                for (int i = 0; i < count && 6 + i * 16 < b.Length; i++) max = Math.Max(max, b[6 + i * 16] == 0 ? 256 : b[6 + i * 16]);
                return max;
            }
            if (ext == ".jpg")
            {
                for (int i = 2; i + 9 < b.Length;)
                {
                    if (b[i] != 0xFF) { i++; continue; }
                    byte marker = b[i + 1];
                    int len = (b[i + 2] << 8) | b[i + 3];
                    if (marker >= 0xC0 && marker <= 0xC3) return (b[i + 7] << 8) | b[i + 8];
                    i += 2 + len;
                }
            }
            return 0;
        }

        private static string LinkHref(string html, string rel)
        {
            foreach (Match m in Regex.Matches(html, @"<link\b[^>]*>", RegexOptions.IgnoreCase))
            {
                string tag = m.Value;
                var relM = Regex.Match(tag, @"\brel\s*=\s*[""']?([^""'>]+)", RegexOptions.IgnoreCase);
                if (!relM.Success) continue;
                var rels = relM.Groups[1].Value.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (!rels.Contains(rel)) continue;
                var href = Regex.Match(tag, @"\bhref\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (href.Success) return href.Groups[1].Value;
            }
            return null;
        }

        /// <summary>WebClient that transparently un-gzips (many CDNs compress JSON and HTML regardless).</summary>
        internal sealed class DecompressingClient : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                var r = base.GetWebRequest(address);
                var h = r as HttpWebRequest;
                if (h != null) { h.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate; h.Timeout = 12000; }
                return r;
            }
        }

        private static WebClient Client()
        {
            var wc = new DecompressingClient();
            wc.Headers[HttpRequestHeader.UserAgent] = BrowserUa;
            wc.Headers[HttpRequestHeader.AcceptLanguage] = "en-US,en;q=0.8";
            wc.Encoding = Encoding.UTF8;
            return wc;
        }

        private static string DownloadString(string url) { using (var wc = Client()) return wc.DownloadString(url); }
        private static byte[] Download(string url) { using (var wc = Client()) return wc.DownloadData(url); }

        private static string ExtensionOf(byte[] b)
        {
            if (b == null || b.Length < 12) return null;
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return ".png";
            if (b[0] == 0xFF && b[1] == 0xD8) return ".jpg";
            if (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 && b[8] == 0x57 && b[9] == 0x45) return ".webp";
            if (b[0] == 0 && b[1] == 0 && b[2] == 1 && b[3] == 0) return ".ico";
            string head = Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 256)).TrimStart();
            if (head.StartsWith("<svg") || (head.StartsWith("<?xml") && head.Contains("<svg"))) return ".svg";
            return null;
        }
    }
}
