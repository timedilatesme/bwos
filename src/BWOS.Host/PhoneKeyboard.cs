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
    /// "Type on your phone": scan the QR code shown on the TV, type on the phone, and the text is
    /// typed into the BWOS window (sign-in forms, search boxes). Served on the home network by a tiny
    /// HTTP server that only starts the first time the keyboard is used.
    /// Safety: every session has a random one-time code (in the QR URL), it expires after 15 minutes
    /// of inactivity, and text is only ever typed while the BWOS window is in front.
    /// </summary>
    internal sealed class PhoneKeyboard
    {
        private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

        private readonly ShellManager shell;
        private readonly int port;
        private readonly object gate = new object();
        private TcpListener listener;
        private string code;
        private DateTime lastUse;

        public event Action<string> Typed; // action name, for the UI ("text", "enter", …)

        public PhoneKeyboard(ShellManager shell, int port)
        {
            this.shell = shell;
            this.port = port;
        }

        /// <summary>Starts (or refreshes) a session and returns what the TV should show.</summary>
        public Dictionary<string, object> Open()
        {
            lock (gate)
            {
                EnsureListening();
                if (code == null || DateTime.UtcNow - lastUse > Lifetime) code = NewCode();
                lastUse = DateTime.UtcNow;
                string ip = LanAddress();
                var d = new Dictionary<string, object>();
                d["ok"] = ip != null;
                d["url"] = ip == null ? null : "http://" + ip + ":" + port + "/k/" + code;
                d["code"] = code;
                d["error"] = ip == null ? "This PC doesn't seem to be on a network your phone can reach." : null;
                return d;
            }
        }

        private void EnsureListening()
        {
            if (listener != null) return;
            // All interfaces, so a phone on the same Wi-Fi can connect. Windows may ask once to allow
            // BWOS on private networks.
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            var t = new Thread(AcceptLoop);
            t.IsBackground = true;
            t.Name = "BWOS phone keyboard";
            t.Start();
            Log.Write("phone keyboard listening on port " + port);
        }

        private void AcceptLoop()
        {
            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { return; }
                ThreadPool.QueueUserWorkItem(delegate { try { Serve(client); } catch (Exception ex) { Log.Write("phone keyboard: " + ex.Message); } finally { client.Close(); } });
            }
        }

        // ------------------------------------------------------------------ minimal HTTP

        private void Serve(TcpClient client)
        {
            client.ReceiveTimeout = 5000;
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, true);
            string requestLine = reader.ReadLine();
            if (string.IsNullOrEmpty(requestLine)) return;
            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return;
            string method = parts[0], path = parts[1];
            int length = 0;
            string line;
            while (!string.IsNullOrEmpty(line = reader.ReadLine()))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line.Substring(15).Trim(), out length);
            }
            string body = "";
            if (length > 0 && length < 64 * 1024)
            {
                var buf = new char[length];
                int read = 0;
                while (read < length) { int n = reader.Read(buf, read, length - read); if (n <= 0) break; read += n; }
                body = new string(buf, 0, read);
            }

            var seg = path.Split(new[] { '/', '?' }, StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length < 2 || seg[0] != "k") { Respond(stream, 404, "text/plain", "Not found"); return; }
            bool valid;
            lock (gate) valid = code != null && seg[1] == code && DateTime.UtcNow - lastUse < Lifetime;
            if (!valid)
            {
                Respond(stream, 403, "text/html; charset=utf-8", Page(null));
                return;
            }
            lock (gate) lastUse = DateTime.UtcNow;

            if (method == "GET") { Respond(stream, 200, "text/html; charset=utf-8", Page(seg[1])); return; }
            if (method == "POST" && seg.Length >= 3 && seg[2] == "type")
            {
                var req = Json.ParseObject(body);
                string result = Type(Json.Str(req, "text", ""), Json.Str(req, "action", ""));
                Respond(stream, result == null ? 200 : 409, "application/json", Json.Serialize(new Dictionary<string, object> { { "ok", result == null }, { "error", result } }));
                return;
            }
            Respond(stream, 404, "text/plain", "Not found");
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

        // ------------------------------------------------------------------ typing

        /// <summary>Returns null on success, or a message for the phone.</summary>
        private string Type(string text, string action)
        {
            if (!shell.ShellIsForeground()) return "BWOS isn't on the TV screen right now.";
            if (text.Length > 0)
            {
                foreach (char c in text.Replace("\r\n", "\n"))
                {
                    if (c == '\n') Native.Tap(0x0D);
                    else Unicode(c);
                }
            }
            switch (action)
            {
                case "enter": Native.Tap(0x0D); break;
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
        private static string Page(string sessionCode)
        {
            if (sessionCode == null)
                return "<!doctype html><meta name=viewport content='width=device-width,initial-scale=1'><title>BWOS keyboard</title>" +
                    "<body style='font:17px system-ui;background:#0b0c12;color:#eee;display:grid;place-items:center;height:90vh;text-align:center'>" +
                    "<div><h2>This code has expired</h2><p>Press <b>Menu</b> on the controller and scan the new code on the TV.</p></div>";
            return @"<!doctype html>
<html lang=en><head><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1,viewport-fit=cover'>
<meta name=theme-color content='#0b0c12'><title>BWOS keyboard</title>
<style>
:root{color-scheme:dark}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}
body{margin:0;min-height:100vh;font:16px/1.4 -apple-system,system-ui,'Segoe UI',sans-serif;color:#f4f5f8;
 background:radial-gradient(120% 60% at 20% 0%,#3b2f8a 0%,transparent 60%),radial-gradient(100% 60% at 100% 100%,#0e5a6e 0%,transparent 60%),#0b0c12}
main{max-width:560px;margin:0 auto;padding:22px 18px 30px}
h1{font-size:21px;margin:4px 0 2px}p.sub{margin:0 0 16px;color:#a9adbb;font-size:14px}
.glass{background:rgba(255,255,255,.08);border-radius:18px;box-shadow:inset 0 1px 0 rgba(255,255,255,.18),inset 0 0 0 1px rgba(255,255,255,.07);backdrop-filter:blur(30px) saturate(170%)}
textarea{width:100%;min-height:120px;padding:14px;border:0;outline:0;resize:vertical;color:inherit;font:inherit;font-size:18px;background:transparent}
.row{display:grid;grid-template-columns:repeat(4,1fr);gap:8px;margin-top:10px}
button{font:600 15px system-ui;color:#f4f5f8;border:0;border-radius:14px;padding:14px 8px;background:rgba(255,255,255,.1);box-shadow:inset 0 1px 0 rgba(255,255,255,.15)}
button:active{transform:scale(.97);background:rgba(255,255,255,.2)}
button.primary{grid-column:span 4;background:#fff;color:#0b0c12;font-size:17px;padding:16px}
label{display:flex;gap:10px;align-items:center;margin:14px 2px 0;color:#c9ccd6;font-size:14px}
#status{margin-top:14px;min-height:20px;font-size:14px;color:#8ee0b5;text-align:center}
#status.err{color:#ff8a95}
</style></head><body><main>
<h1>Type on the TV</h1><p class=sub>Select a text box on the TV with <b>A</b>, then type here.</p>
<div class=glass><textarea id=t autofocus autocomplete=off autocapitalize=off spellcheck=false placeholder='Email, password, search…'></textarea></div>
<label><input type=checkbox id=hide> Hide what I type (passwords)</label>
<label><input type=checkbox id=enter> Press Enter after sending</label>
<div class=row>
 <button class=primary id=send>Send to TV</button>
 <button data-a=backspace>&#9003; Delete</button><button data-a=clear>Clear box</button><button data-a=tab>Next box</button><button data-a=enter>Enter &#9166;</button>
</div>
<div id=status></div>
</main>
<script>
const code='" + sessionCode + @"',t=document.getElementById('t'),st=document.getElementById('status');
document.getElementById('hide').onchange=e=>{t.style.webkitTextSecurity=e.target.checked?'disc':'none'};
async function go(body){
  try{const r=await fetch('/k/'+code+'/type',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
   const j=await r.json().catch(()=>({}));
   if(r.ok){st.className='';st.textContent='Sent ✓';setTimeout(()=>st.textContent='',1500);return true}
   st.className='err';st.textContent=j.error||(r.status==403?'Code expired – scan the new one on the TV.':'Couldn\'t reach BWOS.');
  }catch(e){st.className='err';st.textContent='Couldn\'t reach the PC. Same Wi-Fi?'}
  return false}
document.getElementById('send').onclick=async()=>{if(!t.value)return;
  if(await go({text:t.value,action:document.getElementById('enter').checked?'enter':''}))t.value='';t.focus()};
document.querySelectorAll('[data-a]').forEach(b=>b.onclick=()=>{go({action:b.dataset.a});t.focus()});
t.addEventListener('keydown',e=>{if(e.key==='Enter'&&!e.shiftKey){e.preventDefault();document.getElementById('send').click()}});
</script></body></html>";
        }
    }
}
