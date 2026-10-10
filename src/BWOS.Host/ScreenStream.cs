using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Bwos
{
    // Session-only signaling. Media goes directly between browsers, never through
    // the HTTP server. Screen access is distinct from persistent phone pairing.
    internal sealed class ScreenStream
    {
        private readonly object gate = new object();
        private string id, codeHash, viewerHash;
        private DateTime issued, touched;
        private readonly List<Dictionary<string, object>> messages = new List<Dictionary<string, object>>();
        private int sequence;
        private readonly Func<DateTime> clock;
        internal ScreenStream(Func<DateTime> clock = null) { this.clock = clock ?? (() => DateTime.UtcNow); }
        internal static string Secret()
        {
            var bytes = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        private static string Hash(string value)
        {
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? "")));
        }
        private bool Live(DateTime now)
        {
            if (id != null && (now - issued > TimeSpan.FromHours(2) || now - touched > TimeSpan.FromSeconds(90))) Stop();
            return id != null;
        }
        internal Dictionary<string, object> Start(string address)
        {
            lock (gate)
            {
                Stop();
                id = Secret(); string code = Secret(); codeHash = Hash(code);
                issued = touched = clock();
                return new Dictionary<string, object> { { "ok", true }, { "session", id }, { "url", address + "/view#code=" + code } };
            }
        }
        internal void Stop() { lock (gate) { id = codeHash = viewerHash = null; messages.Clear(); sequence = 0; } }
        internal Dictionary<string, object> Pair(string code)
        {
            lock (gate)
            {
                if (!Live(clock()) || code == null || code.Length > 64 || codeHash == null || clock() - issued > TimeSpan.FromMinutes(10) || Hash(code) != codeHash)
                    return WebServer.Err("This sharing link has expired or was used. Start a new session in BWOS.");
                codeHash = null;
                string token = Secret(); viewerHash = Hash(token);
                return new Dictionary<string, object> { { "ok", true }, { "token", token } };
            }
        }
        internal bool ViewerAuthorized(string token)
        {
            lock (gate) return Live(clock()) && !string.IsNullOrEmpty(token) && token.Length <= 64 && viewerHash == Hash(token);
        }
        internal Dictionary<string, object> Exchange(bool host, string credential, string action, Dictionary<string, object> request)
        {
            lock (gate)
            {
                if (!Live(clock()) || (host ? credential != id : !ViewerAuthorized(credential))) return WebServer.Err("Sharing has stopped. Start a new session in BWOS.");
                if (host) touched = clock();
                if (action == "leave" && !host) { Stop(); return new Dictionary<string, object> { { "ok", true } }; }
                if (action == "stop") { if (!host) return WebServer.Err("Only BWOS can stop sharing."); Stop(); return new Dictionary<string, object> { { "ok", true } }; }
                if (action == "signal")
                {
                    string type = Json.Str(request, "type", ""), data = Json.Str(request, "data", "");
                    if ((type != "candidate" && type != (host ? "offer" : "answer")) || data.Length > 48000 || string.IsNullOrEmpty(data)) return WebServer.Err("Invalid stream message.");
                    if (messages.Count >= 256) return WebServer.Err("Stream message limit reached. Start a new session.");
                    messages.Add(new Dictionary<string, object> { { "sequence", ++sequence }, { "host", host }, { "type", type }, { "data", data } });
                    return new Dictionary<string, object> { { "ok", true } };
                }
                if (action != "poll") return WebServer.Err("Unknown stream action.");
                int after = Math.Max(0, Json.Int(request, "after", 0));
                var result = new List<Dictionary<string, object>>();
                foreach (var message in messages) if ((bool)message["host"] != host && (int)message["sequence"] > after) result.Add(new Dictionary<string, object>(message));
                return new Dictionary<string, object> { { "ok", true }, { "joined", viewerHash != null }, { "cursor", sequence }, { "messages", result } };
            }
        }
    }
}
