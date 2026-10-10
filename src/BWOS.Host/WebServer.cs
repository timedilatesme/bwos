using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Bwos
{
    internal sealed class Request
    {
        public HttpListenerContext Context;
        public string Method;
        public string[] Segments; // unescaped path segments, e.g. ["api","art","steam:620","hero"]
        public Dictionary<string, object> Body;
    }

    /// <summary>
    /// Tiny localhost-only HTTP server: serves the ui/ folder, the JSON API and a
    /// Server-Sent-Events stream. Every mutating call must carry the per-session token,
    /// so random websites open in other browsers cannot drive it.
    /// </summary>
    internal sealed class WebServer
    {
        private readonly HttpListener listener = new HttpListener();
        private readonly int port;
        private readonly List<HttpListenerResponse> streams = new List<HttpListenerResponse>();
        private readonly object streamGate = new object();
        private readonly List<WebSocket> playbackSockets = new List<WebSocket>();
        private readonly object socketGate = new object();
        public int PlaybackClients { get { lock (socketGate) return playbackSockets.Count; } }
        public readonly string Token;

        public Func<Request, object> ApiHandler;

        public WebServer(int port)
        {
            this.port = port;
            Token = LoadOrCreateToken();
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
        }

        public string BaseUrl { get { return "http://127.0.0.1:" + port + "/"; } }

        /// <summary>
        /// The token survives host restarts (it lives in the data folder), so a BWOS window that is
        /// already open keeps working after an update or crash of BWOS.exe.
        /// </summary>
        private static string LoadOrCreateToken()
        {
            string file = Path.Combine(Paths.DataDir, "session.key");
            try
            {
                if (File.Exists(file))
                {
                    string existing = File.ReadAllText(file).Trim();
                    if (existing.Length == 32) return existing;
                }
            }
            catch { }
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            string token = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            try { File.WriteAllText(file, token); } catch { }
            return token;
        }

        public void Start()
        {
            listener.Start();
            var t = new Thread(delegate()
            {
                while (listener.IsListening)
                {
                    try
                    {
                        var ctx = listener.GetContext();
                        ThreadPool.QueueUserWorkItem(delegate { Handle(ctx); });
                    }
                    catch (Exception ex)
                    {
                        if (listener.IsListening) Log.Write("listener: " + ex.Message);
                    }
                }
            });
            t.IsBackground = true;
            t.Name = "BWOS http";
            t.Start();

            var hb = new Thread(delegate()
            {
                while (true)
                {
                    Thread.Sleep(20000);
                    Broadcast("ping", null);
                }
            });
            hb.IsBackground = true;
            hb.Start();
        }

        public void Stop()
        {
            lock (socketGate) { foreach (var socket in playbackSockets) socket.Abort(); playbackSockets.Clear(); }
            try { listener.Stop(); } catch { }
        }

        public void Broadcast(string evt, object data)
        {
            if (evt == "playback" || evt == "ping")
            {
                byte[] notification = Encoding.UTF8.GetBytes(evt);
                lock (socketGate)
                    for (int i = playbackSockets.Count - 1; i >= 0; i--)
                    {
                        try { if (!playbackSockets[i].SendAsync(new ArraySegment<byte>(notification), WebSocketMessageType.Text, true, CancellationToken.None).Wait(1000)) throw new IOException("Playback connection stalled"); }
                        catch { playbackSockets[i].Abort(); playbackSockets.RemoveAt(i); }
                    }
            }
            string payload = "event: " + evt + "\ndata: " + (data == null ? "{}" : Json.Serialize(data)) + "\n\n";
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            lock (streamGate)
            {
                for (int i = streams.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        streams[i].OutputStream.Write(bytes, 0, bytes.Length);
                        streams[i].OutputStream.Flush();
                    }
                    catch
                    {
                        try { streams[i].Abort(); } catch { }
                        streams.RemoveAt(i);
                    }
                }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            var res = ctx.Response;
            try
            {
                // DNS-rebinding guard: only accept requests addressed to our loopback origin.
                string host = ctx.Request.Headers["Host"] ?? "";
                if (host != "127.0.0.1:" + port && host != "localhost:" + port)
                {
                    Send(res, 403, "text/plain", Encoding.UTF8.GetBytes("forbidden"));
                    return;
                }

                string raw = ctx.Request.RawUrl ?? "/";
                int q = raw.IndexOf('?');
                string path = q >= 0 ? raw.Substring(0, q) : raw;
                var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++) parts[i] = Uri.UnescapeDataString(parts[i]);

                if (parts.Length > 0 && parts[0] == "api")
                {
                    HandleApi(ctx, parts);
                    return;
                }
                ServeStatic(res, parts);
            }
            catch (Exception ex)
            {
                Log.Write("http error " + ctx.Request.RawUrl + ": " + ex);
                try { SendJson(res, 500, Err(ex.Message)); } catch { }
            }
        }

        private void HandleApi(HttpListenerContext ctx, string[] parts)
        {
            var res = ctx.Response;
            string method = ctx.Request.HttpMethod;
            if (method == "GET" && parts.Length == 2 && parts[1] == "playback-events" && ctx.Request.IsWebSocketRequest)
            {
                if (ctx.Request.QueryString["t"] != Token) { Send(res, 401, "text/plain", new byte[0]); return; }
                ConnectPlayback(ctx); return;
            }

            // Images and the event stream are loaded by <img>/EventSource which can't set headers.
            bool open = method == "GET" && parts.Length > 1 && (parts[1] == "art" || parts[1] == "avatar");
            if (method == "GET" && parts.Length > 1 && parts[1] == "events")
            {
                string t = ctx.Request.QueryString["t"];
                if (t != Token) { Send(res, 401, "text/plain", new byte[0]); return; }
                res.ContentType = "text/event-stream";
                res.Headers["Cache-Control"] = "no-cache";
                res.SendChunked = true;
                byte[] hello = Encoding.UTF8.GetBytes("retry: 1500\nevent: hello\ndata: {}\n\n");
                res.OutputStream.Write(hello, 0, hello.Length);
                res.OutputStream.Flush();
                lock (streamGate) streams.Add(res);
                return; // keep open
            }
            if (!open && ctx.Request.Headers["X-BWOS-Token"] != Token)
            {
                SendJson(res, 401, Err("bad token"));
                return;
            }

            var req = new Request();
            req.Context = ctx;
            req.Method = method;
            req.Segments = parts;
            if (method == "POST")
            {
                using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    req.Body = Json.ParseObject(r.ReadToEnd());
            }
            var result = ApiHandler(req);
            if (result is FileResult)
            {
                var f = (FileResult)result;
                if (f.Redirect != null)
                {
                    res.StatusCode = 302;
                    res.RedirectLocation = f.Redirect;
                    res.Headers["Cache-Control"] = "public, max-age=86400";
                    res.Close();
                    return;
                }
                if (f.Path == null || !File.Exists(f.Path)) { Send(res, 404, "text/plain", new byte[0]); return; }
                res.Headers["Cache-Control"] = "public, max-age=86400";
                Send(res, 200, MimeOf(f.Path), File.ReadAllBytes(f.Path));
                return;
            }
            if (result == null) { SendJson(res, 404, Err("not found")); return; }
            SendJson(res, 200, result);
        }
        private async void ConnectPlayback(HttpListenerContext context)
        {
            WebSocket socket = null;
            try
            {
                var connected = await context.AcceptWebSocketAsync(null); socket = connected.WebSocket;
                lock (socketGate) { if (playbackSockets.Count >= 4) { socket.Abort(); return; } playbackSockets.Add(socket); }
                var buffer = new byte[128];
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch { }
            finally { if (socket != null) { lock (socketGate) playbackSockets.Remove(socket); socket.Dispose(); } }
        }

        private void ServeStatic(HttpListenerResponse res, string[] parts)
        {
            string rel = parts.Length == 0 ? "index.html" : string.Join("\\", parts);
            string root = Path.GetFullPath(Paths.UiDir) + "\\";
            string full = Path.GetFullPath(Path.Combine(root, rel));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                Send(res, 404, "text/plain", Encoding.UTF8.GetBytes("not found"));
                return;
            }
            res.Headers["Cache-Control"] = "no-cache";
            Send(res, 200, MimeOf(full), File.ReadAllBytes(full));
        }

        public static Dictionary<string, object> Err(string message)
        {
            var d = new Dictionary<string, object>();
            d["ok"] = false;
            d["error"] = message;
            return d;
        }

        private static void SendJson(HttpListenerResponse res, int status, object o)
        {
            res.Headers["Cache-Control"] = "no-store";
            Send(res, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Json.Serialize(o)));
        }

        private static void Send(HttpListenerResponse res, int status, string type, byte[] body)
        {
            res.StatusCode = status;
            res.ContentType = type;
            res.ContentLength64 = body.Length;
            res.OutputStream.Write(body, 0, body.Length);
            res.Close();
        }

        private static string MimeOf(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".js": return "application/javascript; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".webp": return "image/webp";
                case ".ico": return "image/x-icon";
                case ".gif": return "image/gif";
                case ".woff2": return "font/woff2";
                default: return "application/octet-stream";
            }
        }
    }

    internal sealed class FileResult
    {
        public string Path;
        public string Redirect;
    }
}
