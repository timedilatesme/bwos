using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Bwos
{
    /// <summary>
    /// Your phone as a remote and keyboard for BWOS. Scan the QR code shown on the TV: the page that
    /// opens has an Apple TV-style touch surface (swipe to move, tap to select), Back / Home / Play and
    /// volume, and a keyboard for sign-ins and searches. Served on the home network by a tiny HTTP server.
    ///
    /// Safety:
    /// - Pairing needs the random one-time code from the QR on the TV (valid 15 minutes).
    /// - A paired phone keeps a random token (only its SHA-256 is stored, in data\phones.json), so it
    ///   works again without scanning, also after BWOS restarts. Settings can forget all phones.
    /// - Keys and text only go to the BWOS window, and only while it's in front. Home and volume act
    ///   like the controller's and work anywhere (Home brings BWOS back, as on a console).
    /// </summary>
    internal sealed class PhoneRemote
    {
        private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);
        private const int MaxPhones = 8;

        private readonly ShellManager shell;
        private readonly int port;
        private readonly object gate = new object();
        private readonly string store = Path.Combine(Paths.DataDir, "phones.json");
        private List<Dictionary<string, object>> phones;
        private TcpListener listener;
        private bool stopped;
        private string code;
        private DateTime codeIssued;
        internal readonly ScreenStream Screen = new ScreenStream();

        internal Dictionary<string, object> StartScreen()
        {
            lock (gate)
            {
                string ip = LanAddress();
                if (ip == null) return WebServer.Err("Connect this PC to your local network first.");
                EnsureListening();
                return Screen.Start("http://" + ip + ":" + port);
            }
        }

        public event Action<string> Typed;   // "text", "enter", … (the UI shows a hint)
        public event Action HomePressed;     // the phone's Home button
        public event Action Paired;
        public Func<string, string> MediaControl;
        internal static string PlaybackAction(string action)
        {
            return action == "play" ? "toggle" : action == "seekback" ? "seekBack" : action == "seekforward" ? "seekForward" : null;
        }

        public PhoneRemote(ShellManager shell, int port)
        {
            this.shell = shell;
            this.port = port;
            phones = LoadPhones();
        }

        public int PairedCount { get { lock (gate) return phones.Count; } }

        public void Stop()
        {
            lock (gate)
            {
                stopped = true;
                Screen.Stop();
                if (listener != null) listener.Stop();
                listener = null;
            }
        }

        /// <summary>At start-up: paired phones should work without scanning again.</summary>
        public void StartIfPaired()
        {
            lock (gate) if (phones.Count > 0) { try { EnsureListening(); } catch (Exception ex) { Log.Write("phone remote: " + ex.Message); } }
        }

        /// <summary>Shows the QR code on the TV: a fresh pairing code and the address for the phone.</summary>
        public Dictionary<string, object> Open()
        {
            lock (gate)
            {
                EnsureListening();
                if (code == null || DateTime.UtcNow - codeIssued > CodeLifetime) { code = NewCode(); codeIssued = DateTime.UtcNow; }
                string ip = LanAddress();
                var d = new Dictionary<string, object>();
                d["ok"] = ip != null;
                d["url"] = ip == null ? null : "http://" + ip + ":" + port + "/k/" + code;
                d["code"] = code;
                d["error"] = ip == null ? "This PC doesn't seem to be on a network your phone can reach." : null;
                return d;
            }
        }

        public void ForgetPhones()
        {
            lock (gate) { phones.Clear(); SavePhones(); }
            Log.Write("phone remote: forgot all paired phones");
        }

        private void EnsureListening()
        {
            if (stopped) throw new ObjectDisposedException("Phone remote");
            if (listener != null) return;
            // All interfaces, so a phone on the same Wi-Fi can connect. Windows may ask once to allow BWOS.
            var pendingListener = new TcpListener(IPAddress.Any, port);
            try { pendingListener.Start(); }
            catch { pendingListener.Stop(); throw; }
            listener = pendingListener; // A failed bind must remain retryable.
            var t = new Thread(delegate() { AcceptLoop(pendingListener); });
            t.IsBackground = true;
            t.Name = "BWOS phone remote";
            t.Start();
            Log.Write("phone remote listening on port " + port);
        }

        private void AcceptLoop(TcpListener source)
        {
            while (true)
            {
                TcpClient client;
                try { client = source.AcceptTcpClient(); }
                catch { return; }
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { Serve(client); }
                    catch { Log.Write("phone remote: request failed"); } // Request bodies can contain pairing credentials.
                    finally { client.Close(); }
                });
            }
        }

        // ------------------------------------------------------------------ minimal HTTP

        private void Serve(TcpClient client)
        {
            client.ReceiveTimeout = 5000;
            var stream = client.GetStream();
            // HTTP Content-Length counts bytes, not UTF-8 characters. Reading
            // characters made Hindi keyboard requests wait until they timed out.
            var headerBytes = new List<byte>(); int next;
            while ((next = stream.ReadByte()) >= 0)
            {
                headerBytes.Add((byte)next);
                int n = headerBytes.Count;
                if (n > 16384) { Respond(stream, 413, "text/plain", "Request too large"); return; }
                if (n >= 4 && headerBytes[n-4] == 13 && headerBytes[n-3] == 10 && headerBytes[n-2] == 13 && headerBytes[n-1] == 10) break;
            }
            var reader = new StringReader(Encoding.ASCII.GetString(headerBytes.ToArray()));
            string requestLine = reader.ReadLine();
            if (string.IsNullOrEmpty(requestLine)) return;
            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return;
            string method = parts[0], path = parts[1].Split('?')[0];
            int length = 0;
            bool invalidLength = false, chunked = false;
            string token = null, line, host = "", origin = null;
            while (!string.IsNullOrEmpty(line = reader.ReadLine()))
            {
                int i = line.IndexOf(':');
                if (i <= 0) continue;
                string name = line.Substring(0, i).Trim(), value = line.Substring(i + 1).Trim();
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) { int parsed; if (!int.TryParse(value, out parsed) || length != 0) invalidLength = true; else length = parsed; }
                else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) chunked = true;
                else if (name.Equals("X-BWOS-Remote", StringComparison.OrdinalIgnoreCase)) token = value;
                else if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) host = value;
                else if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) origin = value;
            }
            IPAddress addressed;
            string hostAddress = host.EndsWith(":" + port, StringComparison.Ordinal) ? host.Substring(0, host.Length - (":" + port).Length) : "";
            bool localHost = IPAddress.TryParse(hostAddress, out addressed) && (IPAddress.IsLoopback(addressed) || NetworkInterface.GetAllNetworkInterfaces().Any(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(addressed))));
            bool forbidden = !localHost || (origin != null && origin != "http://" + host);
            if (invalidLength || chunked || length < 0 || length >= 64 * 1024) { Respond(stream, 413, "text/plain", "Invalid request size"); return; }
            string body = "";
            if (length > 0 && length < 64 * 1024)
            {
                var buf = new byte[length];
                int read = 0;
                while (read < length) { int n = stream.Read(buf, read, length - read); if (n <= 0) break; read += n; }
                if (read != length) return;
                body = Encoding.UTF8.GetString(buf);
            }
            // Consume a bounded body before closing a rejected request. Closing
            // with unread data sent a TCP reset and hid the useful 403 response.
            if (forbidden) { Json403(stream, "Use the BWOS local-network address."); return; }

            if (method == "GET" && path == "/view")
            {
                Respond(stream, 200, "text/html; charset=utf-8", File.ReadAllText(Path.Combine(Paths.AppDir, "ui", "remote-viewer.html"))); return;
            }
            if (method == "GET" && (path == "/viewer.js" || path == "/rtc.js"))
            {
                Respond(stream, 200, "text/javascript; charset=utf-8", File.ReadAllText(Path.Combine(Paths.UiDir, "js", path == "/viewer.js" ? "screen-viewer.js" : "screen-peer.js"))); return;
            }

            // The page: /r (paired phones, bookmarkable) or /k/<code> (just scanned).
            if (method == "GET" && (path == "/" || path == "/r" || path.StartsWith("/k/")))
            {
                Respond(stream, 200, "text/html; charset=utf-8", Page());
                return;
            }
            if (method == "GET" && path == "/manifest.json")
            {
                Respond(stream, 200, "application/manifest+json", "{\"name\":\"BWOS Remote\",\"short_name\":\"BWOS\",\"start_url\":\"/r\",\"display\":\"standalone\",\"background_color\":\"#0b0c12\",\"theme_color\":\"#0b0c12\"}");
                return;
            }
            if (method != "POST") { Respond(stream, 404, "text/plain", "Not found"); return; }

            var req = Json.ParseObject(body);
            if (path.StartsWith("/v/", StringComparison.Ordinal))
            {
                Dictionary<string, object> result;
                if (path == "/v/pair") result = Screen.Pair(Json.Str(req, "code", ""));
                else if (!Screen.ViewerAuthorized(token)) result = WebServer.Err("Sharing has stopped or this device is not paired.");
                else if (path == "/v/key" || path == "/v/type")
                {
                    string inputError = path == "/v/key" ? Press(Json.Str(req, "action", "")) : Type(Json.Str(req, "text", ""), Json.Str(req, "action", ""));
                    result = inputError == null ? new Dictionary<string, object> { { "ok", true } } : WebServer.Err(inputError);
                }
                else result = Screen.Exchange(false, token, path.Substring(3), req);
                RespondJson(stream, Json.Bool(result, "ok", false) ? 200 : 403, result); return;
            }
            if (path == "/r/pair")
            {
                string t = Pair(Json.Str(req, "code", ""), Json.Str(req, "name", "Phone"));
                if (t == null) { Json403(stream, "This code has expired. Open Phone remote on the TV and scan the new QR code."); return; }
                RespondJson(stream, 200, new Dictionary<string, object> { { "ok", true }, { "token", t } });
                return;
            }
            if (!Authorized(token)) { RespondJson(stream, 401, new Dictionary<string, object> { { "ok", false }, { "error", "Not paired. Scan the QR code on the TV." } }); return; }

            string error;
            if (path == "/r/key") error = Press(Json.Str(req, "action", ""));
            else if (path == "/r/type") error = Type(Json.Str(req, "text", ""), Json.Str(req, "action", ""));
            else { Respond(stream, 404, "text/plain", "Not found"); return; }
            RespondJson(stream, error == null ? 200 : 409, new Dictionary<string, object> { { "ok", error == null }, { "error", error } });
        }

        private static void Json403(Stream s, string message)
        {
            RespondJson(s, 403, new Dictionary<string, object> { { "ok", false }, { "error", message } });
        }

        private static void RespondJson(Stream s, int status, Dictionary<string, object> d)
        {
            Respond(s, status, "application/json", Json.Serialize(d));
        }

        private static void Respond(Stream s, int status, string type, string body)
        {
            byte[] b = Encoding.UTF8.GetBytes(body);
            string head = "HTTP/1.1 " + status + " " + (status == 200 ? "OK" : "Error") + "\r\nContent-Type: " + type +
                "\r\nContent-Length: " + b.Length + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(head);
            s.Write(h, 0, h.Length);
            s.Write(b, 0, b.Length);
        }

        // ------------------------------------------------------------------ pairing

        /// <summary>Returns the new phone's token, or null if the code isn't valid.</summary>
        private string Pair(string offered, string name)
        {
            string token;
            lock (gate)
            {
                if (code == null || offered != code || DateTime.UtcNow - codeIssued > CodeLifetime) return null;
                code = null; // one phone per QR code
                token = RandomToken();
                var entry = new Dictionary<string, object>();
                entry["hash"] = Hash(token);
                entry["name"] = name.Length > 40 ? name.Substring(0, 40) : name;
                entry["paired"] = DateTime.UtcNow.ToString("o");
                phones.Add(entry);
                while (phones.Count > MaxPhones) phones.RemoveAt(0);
                SavePhones();
                Log.Write("phone remote: paired " + entry["name"]);
            }
            var h = Paired;
            if (h != null) h();
            return token;
        }

        private static string RandomToken()
        {
            var bytes = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private bool Authorized(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length > 64) return false;
            string h = Hash(token);
            lock (gate) return phones.Any(p => Json.Str(p, "hash", "") == h);
        }

        private static string Hash(string token)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(token))).Replace("-", "").ToLowerInvariant();
        }

        private List<Dictionary<string, object>> LoadPhones()
        {
            try
            {
                if (File.Exists(store))
                {
                    var o = Json.ParseObject("{\"p\":" + File.ReadAllText(store) + "}");
                    return Json.List(o, "p");
                }
            }
            catch (Exception ex) { Log.Write("phone remote: couldn't read phones.json: " + ex.Message); }
            return new List<Dictionary<string, object>>();
        }

        private void SavePhones()
        {
            try { File.WriteAllText(store, Json.Serialize(phones), new UTF8Encoding(false)); }
            catch (Exception ex) { Log.Write("phone remote: couldn't save phones.json: " + ex.Message); }
        }

        // ------------------------------------------------------------------ remote buttons

        private const ushort VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_LEFT = 0x25, VK_UP = 0x26,
            VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_F = 0x46, VK_O = 0x4F, VK_X = 0x58, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;

        /// <summary>Returns null on success, or a message for the phone.</summary>
        internal string Press(string action)
        {
            switch (action)
            {
                case "home":
                    var h = HomePressed;
                    if (h != null) ThreadPool.QueueUserWorkItem(delegate { try { h(); } catch { } });
                    return null;
                case "volup": Native.Tap(VK_VOLUME_UP); return null;
                case "voldown": Native.Tap(VK_VOLUME_DOWN); return null;
            }
            if (!shell.ShellIsForeground()) return "BWOS isn't on the TV right now. Press Home.";
            string mediaAction = PlaybackAction(action);
            if (mediaAction != null && MediaControl != null) return MediaControl(mediaAction);
            // The same keys the controller sends: BWOS's own screens, or the streaming app inside BWOS.
            bool app = shell.ServiceIsForeground();
            switch (action)
            {
                case "up": Native.Tap(VK_UP); break;
                case "down": Native.Tap(VK_DOWN); break;
                case "left": Native.Tap(VK_LEFT); break;
                case "right": Native.Tap(VK_RIGHT); break;
                case "ok": Native.Tap(VK_RETURN); break;
                case "back": Native.Tap(VK_ESCAPE); break;
                case "play": Native.Tap(app ? VK_SPACE : (ushort)0x50); break; // semantic P key on BWOS, not focus-player X
                case "options": Native.Tap(app ? VK_F : VK_O); break;        // full screen in apps, Options in BWOS
                default: return "Unknown button";
            }
            return null;
        }

        /// <summary>Returns null on success, or a message for the phone.</summary>
        internal string Type(string text, string action)
        {
            if (!shell.ShellIsForeground()) return "BWOS isn't on the TV right now.";
            if (text.Length > 0)
            {
                foreach (char c in text.Replace("\r\n", "\n"))
                {
                    if (c == '\n') Native.Tap(VK_RETURN);
                    else Unicode(c);
                }
            }
            switch (action)
            {
                case "enter": Native.Tap(VK_RETURN); break;
                case "tab": Native.Tap(0x09); break;
                case "backspace": Native.Tap(0x08); break;
                case "clear":
                    Native.Key(0x11, true); Native.Tap(0x41); Native.Key(0x11, false); // Ctrl+A
                    Native.Tap(0x08);
                    break;
            }
            var h = Typed;
            if (h != null) h(text.Length > 0 ? "text" : action);
            return null;
        }

        private static void Unicode(char c)
        {
            var inp = new INPUT[2];
            inp[0].type = Native.INPUT_KEYBOARD;
            inp[0].u.ki.wScan = c;
            inp[0].u.ki.dwFlags = 0x0004; // KEYEVENTF_UNICODE
            inp[1] = inp[0];
            inp[1].u.ki.dwFlags = 0x0004 | Native.KEYEVENTF_KEYUP;
            Native.SendInput(2, inp, Marshal.SizeOf(typeof(INPUT)));
        }

        // ------------------------------------------------------------------ helpers

        private static string NewCode()
        {
            var bytes = new byte[8];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return new string(bytes.Select(b => Alphabet[b % Alphabet.Length]).ToArray());
        }

        /// <summary>The PC's address on the home network (the interface that has a default gateway).</summary>
        private static string LanAddress()
        {
            try
            {
                var candidates = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .Select(n => n.GetIPProperties())
                    .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                    .SelectMany(p => p.UnicastAddresses)
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                    .Select(a => a.Address.ToString())
                    .ToList();
                return candidates.FirstOrDefault(a => a.StartsWith("192.168.") || a.StartsWith("10.") || a.StartsWith("172.")) ?? candidates.FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>The page shown on the phone (self-contained, no external resources).</summary>
        private static string Page()
        {
            return PageHtml;
        }

        private const string PageHtml = @"<!doctype html>
<html lang=en><head><meta charset=utf-8>
<meta name=viewport content='width=device-width,initial-scale=1,viewport-fit=cover,user-scalable=no'>
<meta name=theme-color content='#0b0c12'><meta name=apple-mobile-web-app-capable content=yes>
<meta name=apple-mobile-web-app-status-bar-style content=black-translucent><meta name=apple-mobile-web-app-title content=BWOS>
<link rel=manifest href=/manifest.json><title>BWOS Remote</title>
<style>
:root{color-scheme:dark}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent;-webkit-user-select:none;user-select:none}
html,body{height:100%}
body{margin:0;font:16px/1.4 -apple-system,system-ui,'Segoe UI',sans-serif;color:#f4f5f8;overscroll-behavior:none;
 background:radial-gradient(120% 60% at 15% 0%,#3a2f86 0%,transparent 60%),radial-gradient(100% 55% at 100% 100%,#0d5566 0%,transparent 60%),#0b0c12}
main{max-width:520px;margin:0 auto;min-height:100%;display:flex;flex-direction:column;padding:calc(env(safe-area-inset-top) + 14px) 18px calc(env(safe-area-inset-bottom) + 16px)}
header{display:flex;align-items:center;justify-content:space-between;margin-bottom:16px}
.brand{display:flex;align-items:center;gap:9px;font-weight:700;letter-spacing:.14em;font-size:14px}
.brand i{width:9px;height:9px;border-radius:50%;background:#56d49a;box-shadow:0 0 10px #56d49a}
.brand i.off{background:#ff7a85;box-shadow:0 0 10px #ff7a85}
.seg{display:flex;padding:3px;border-radius:99px;background:rgba(255,255,255,.08);box-shadow:inset 0 0 0 1px rgba(255,255,255,.08)}
.seg button{border:0;background:none;color:#b9bdc9;font:600 14px system-ui;padding:8px 14px;border-radius:99px}
.seg button.on{background:rgba(255,255,255,.92);color:#0b0c12}
.glass{background:rgba(255,255,255,.07);box-shadow:inset 0 1px 0 rgba(255,255,255,.16),inset 0 0 0 1px rgba(255,255,255,.07);backdrop-filter:blur(30px) saturate(170%);-webkit-backdrop-filter:blur(30px) saturate(170%)}
section{display:flex;flex-direction:column;flex:1}
section[hidden]{display:none}
.pad{position:relative;flex:1;min-height:300px;max-height:460px;border-radius:30px;touch-action:none;overflow:hidden}
.pad .hint{position:absolute;left:0;right:0;bottom:16px;text-align:center;font-size:13px;color:rgba(255,255,255,.38);pointer-events:none}
.pad .dot{position:absolute;width:74px;height:74px;margin:-37px 0 0 -37px;border-radius:50%;pointer-events:none;opacity:0;
 background:radial-gradient(circle,rgba(255,255,255,.45),rgba(255,255,255,0) 70%);transition:opacity .25s}
.pad .dot.on{opacity:1;transition:none}
.pad .edge{position:absolute;color:rgba(255,255,255,.22);font-size:18px;pointer-events:none}
.row{display:grid;grid-template-columns:repeat(3,1fr);gap:12px;margin-top:14px}
button.k{display:grid;place-items:center;gap:4px;border:0;color:#f4f5f8;font:600 12px system-ui;border-radius:22px;padding:14px 6px;
 background:rgba(255,255,255,.09);box-shadow:inset 0 1px 0 rgba(255,255,255,.14),inset 0 0 0 1px rgba(255,255,255,.06);transition:transform .12s,background .12s}
button.k svg{width:26px;height:26px}
button.k:active{transform:scale(.95);background:rgba(255,255,255,.2)}
button.k.home{background:rgba(255,255,255,.92);color:#0b0c12}
.small button.k{padding:11px 6px;border-radius:18px}
textarea{width:100%;min-height:130px;padding:14px;border:0;outline:0;resize:vertical;color:inherit;font:inherit;font-size:18px;background:transparent;border-radius:18px;-webkit-user-select:text;user-select:text}
label{display:flex;gap:10px;align-items:center;margin:12px 2px 0;color:#c9ccd6;font-size:14px}
.krow{display:grid;grid-template-columns:repeat(4,1fr);gap:8px;margin-top:12px}
.krow button{font:600 14px system-ui;color:#f4f5f8;border:0;border-radius:14px;padding:13px 6px;background:rgba(255,255,255,.1);box-shadow:inset 0 1px 0 rgba(255,255,255,.15)}
.krow button:active{transform:scale(.97);background:rgba(255,255,255,.2)}
.krow .primary{grid-column:span 4;background:#fff;color:#0b0c12;font-size:17px;padding:16px}
#status{min-height:22px;margin-top:12px;font-size:14px;color:#8ee0b5;text-align:center}
#status.err{color:#ff8a95}
#gate{flex:1;display:grid;place-items:center;text-align:center;color:#c9ccd6}
#gate[hidden]{display:none}
#gate h2{color:#fff;margin:0 0 8px}
</style></head><body><main>
<header><div class=brand><i id=live></i>BWOS</div>
 <div class=seg id=seg><button data-tab=remote class=on>Remote</button><button data-tab=keys>Keyboard</button></div></header>

<section id=remote>
 <div id=pad class='pad glass'>
  <span class=edge style='top:14px;left:50%;transform:translateX(-50%)'>&#8963;</span>
  <span class=edge style='bottom:40px;left:50%;transform:translateX(-50%)'>&#8964;</span>
  <span class=edge style='left:16px;top:50%;transform:translateY(-50%)'>&#8249;</span>
  <span class=edge style='right:16px;top:50%;transform:translateY(-50%)'>&#8250;</span>
  <i class=dot id=dot></i><span class=hint>Swipe to move &middot; tap to select</span>
 </div>
 <div class=row>
  <button class=k data-k=back><svg viewBox='0 0 24 24' fill=none stroke=currentColor stroke-width=2 stroke-linecap=round stroke-linejoin=round><path d='M15 5l-7 7 7 7'/></svg>Back</button>
  <button class='k home' data-k=home><svg viewBox='0 0 24 24' fill=none stroke=currentColor stroke-width=2 stroke-linecap=round stroke-linejoin=round><path d='M4 11l8-7 8 7v8a1 1 0 0 1-1 1h-4v-6H9v6H5a1 1 0 0 1-1-1z'/></svg>Home</button>
  <button class=k data-k=play><svg viewBox='0 0 24 24' fill=currentColor><path d='M5 5.5v13l8-6.5z'/><rect x='15' y='5.5' width='2.2' height='13' rx='1'/><rect x='19' y='5.5' width='2.2' height='13' rx='1'/></svg>Play / pause</button>
 </div>
 <div class='row small'>
  <button class=k data-k=seekback>&minus;10 s</button>
  <button class=k data-k=seekforward>+10 s</button>
 </div>
 <div class='row small'>
  <button class=k data-k=options>Options</button>
  <button class=k data-k=voldown data-repeat>Vol &minus;</button>
  <button class=k data-k=volup data-repeat>Vol +</button>
 </div>
</section>

<section id=keys hidden>
 <div class=glass style='border-radius:18px'><textarea id=t autocomplete=off autocapitalize=off spellcheck=false placeholder='Email, password, search&hellip;'></textarea></div>
 <label><input type=checkbox id=hide> Hide what I type (passwords)</label>
 <label><input type=checkbox id=enter> Press Enter after sending</label>
 <div class=krow>
  <button class=primary id=send>Send to TV</button>
  <button data-a=backspace>&#9003; Delete</button><button data-a=clear>Clear</button><button data-a=tab>Next box</button><button data-a=enter>Enter</button>
 </div>
 <p style='color:#8a8fa0;font-size:13px;text-align:center'>Select a text box on the TV first.</p>
</section>

<div id=gate hidden><div><h2>Scan to connect</h2><p>On the TV, open <b>Settings &rarr; Phone remote</b><br>(or press <b>Menu</b> in an app) and scan the QR code.</p></div></div>
<div id=status></div>
</main>
<script>
const $=s=>document.querySelector(s), st=$('#status'), live=$('#live');
let token=null; try{token=localStorage.getItem('bwos.remote')}catch(e){}
const deviceName=()=>{const u=navigator.userAgent;return /iPad/.test(u)?'iPad':/iPhone/.test(u)?'iPhone':/Android/.test(u)?'Android phone':/Mac/.test(u)?'Mac':'Phone'};
let msgTimer=0;
function say(text,bad){st.className=bad?'err':'';st.textContent=text;live.classList.toggle('off',!!bad);clearTimeout(msgTimer);if(!bad)msgTimer=setTimeout(()=>st.textContent='',1400)}
function locked(){$('#remote').hidden=true;$('#keys').hidden=true;$('#seg').style.visibility='hidden';$('#gate').hidden=false}
async function pair(){
 const m=location.pathname.match(/^\/k\/([A-Z0-9]+)/); if(!m)return;
 try{const r=await fetch('/r/pair',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({code:m[1],name:deviceName()})});
  const j=await r.json().catch(()=>({}));
  if(r.ok&&j.token){token=j.token;try{localStorage.setItem('bwos.remote',token)}catch(e){};say('Connected')}
  else if(!token)say(j.error||'Couldn’t connect.',true)}
 catch(e){say('Couldn’t reach the PC. Same Wi-Fi?',true)}
 history.replaceState(null,'','/r')}
let chain=Promise.resolve();
function send(path,body){
 chain=chain.then(()=>fetch('/r/'+path,{method:'POST',headers:{'Content-Type':'application/json','X-BWOS-Remote':token||''},body:JSON.stringify(body)})
  .then(async r=>{if(r.ok){live.classList.remove('off');if(st.className)say('');return true}
   const j=await r.json().catch(()=>({}));if(r.status===401){try{localStorage.removeItem('bwos.remote')}catch(e){};token=null;locked()}
   say(j.error||'Something went wrong.',true);return false})
  .catch(()=>{say('Couldn’t reach the PC. Same Wi-Fi?',true);return false}));
 return chain}
const buzz=()=>{try{navigator.vibrate&&navigator.vibrate(8)}catch(e){}};
const key=k=>{buzz();send('key',{action:k})};

// Touch surface: swipe = move (one step per 44 px, so a long swipe scrolls), tap = select,
// tap near an edge = one step that way.
const pad=$('#pad'),dot=$('#dot');let sx,sy,lx,ly,t0=null,moved=false;
function glow(x,y){const r=pad.getBoundingClientRect();dot.style.left=(x-r.left)+'px';dot.style.top=(y-r.top)+'px';dot.classList.add('on');clearTimeout(glow.t);glow.t=setTimeout(()=>dot.classList.remove('on'),120)}
pad.addEventListener('pointerdown',e=>{pad.setPointerCapture(e.pointerId);sx=lx=e.clientX;sy=ly=e.clientY;t0=Date.now();moved=false;glow(e.clientX,e.clientY)});
pad.addEventListener('pointermove',e=>{if(t0===null)return;const dx=e.clientX-lx,dy=e.clientY-ly,step=44;
 if(Math.abs(dx)>=step||Math.abs(dy)>=step){moved=true;key(Math.abs(dx)>Math.abs(dy)?(dx>0?'right':'left'):(dy>0?'down':'up'));lx=e.clientX;ly=e.clientY;glow(e.clientX,e.clientY)}});
pad.addEventListener('pointerup',e=>{if(t0===null)return;const dt=Date.now()-t0,dist=Math.hypot(e.clientX-sx,e.clientY-sy);t0=null;
 if(moved||dist>16||dt>600)return;
 const r=pad.getBoundingClientRect(),x=(e.clientX-r.left)/r.width,y=(e.clientY-r.top)/r.height,E=.2;
 let k='ok';if(y<E&&x>E&&x<1-E)k='up';else if(y>1-E&&x>E&&x<1-E)k='down';else if(x<E)k='left';else if(x>1-E)k='right';
 key(k)});
pad.addEventListener('pointercancel',()=>{t0=null});

document.querySelectorAll('[data-k]').forEach(b=>{let rep=0;
 const stop=()=>{clearInterval(rep);rep=0};
 b.addEventListener('pointerdown',e=>{e.preventDefault();key(b.dataset.k);if(b.hasAttribute('data-repeat')){stop();rep=setInterval(()=>key(b.dataset.k),160)}});
 b.addEventListener('pointerup',stop);b.addEventListener('pointerleave',stop);b.addEventListener('pointercancel',stop)});

document.querySelectorAll('#seg button').forEach(b=>b.onclick=()=>{
 document.querySelectorAll('#seg button').forEach(x=>x.classList.toggle('on',x===b));
 $('#remote').hidden=b.dataset.tab!=='remote';$('#keys').hidden=b.dataset.tab!=='keys';
 if(b.dataset.tab==='keys')setTimeout(()=>$('#t').focus(),50)});

const t=$('#t');
$('#hide').onchange=e=>{t.style.webkitTextSecurity=e.target.checked?'disc':'none'};
$('#send').onclick=async()=>{if(!t.value)return;
 if(await send('type',{text:t.value,action:$('#enter').checked?'enter':''})){t.value='';say('Sent ✓')}t.focus()};
document.querySelectorAll('[data-a]').forEach(b=>b.onclick=()=>{send('type',{action:b.dataset.a});t.focus()});
t.addEventListener('keydown',e=>{if(e.key==='Enter'&&!e.shiftKey){e.preventDefault();$('#send').click()}});

(async()=>{await pair();if(!token)locked()})();
</script></body></html>";
    }
}
