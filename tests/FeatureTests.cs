using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;

namespace Bwos
{
    internal static class FeatureTests
    {
        private static int assertions;
        private static void Assert(bool condition, string description)
        {
            assertions++;
            if (!condition) throw new Exception("FAIL: " + description);
        }
        private static void Command(string text, string action, string value)
        {
            var c = VoiceCommands.Parse(text);
            Assert(c != null && c.Action == action && c.Value == value, text + " → " + action);
        }
        private static void TestAdapterCacheUpdate()
        {
            string profile = Path.Combine(Paths.AppDir, "adapter-cache-test-" + Guid.NewGuid().ToString("N"));
            string workers = Path.Combine(profile, @"Default\Service Worker");
            Directory.CreateDirectory(Path.Combine(workers, "Database"));
            Directory.CreateDirectory(Path.Combine(workers, "ScriptCache"));
            File.WriteAllText(Path.Combine(workers, @"Database\registry"), "old worker");
            File.WriteAllText(Path.Combine(workers, @"ScriptCache\script"), "old script");
            File.WriteAllText(Path.Combine(profile, @"Default\Cookies"), "preserve sign-ins");
            Assert(ShellManager.PrepareAdapterCache(profile, "2.3.3"), "New adapter version refreshes worker cache");
            Assert(!Directory.Exists(Path.Combine(workers, "Database")), "Old worker registry moved out of active path");
            string[] backups = Directory.GetDirectories(workers, "BWOS-backup-*");
            Assert(backups.Length == 1 && File.ReadAllText(Path.Combine(backups[0], @"Database\registry")) == "old worker", "Worker registry remains recoverable");
            Assert(File.ReadAllText(Path.Combine(backups[0], @"ScriptCache\script")) == "old script", "Worker script backup preserved");
            Assert(File.ReadAllText(Path.Combine(profile, @"Default\Cookies")) == "preserve sign-ins", "Adapter refresh preserves cookies");
            Directory.CreateDirectory(Path.Combine(workers, "Database"));
            File.WriteAllText(Path.Combine(workers, @"Database\registry"), "current worker");
            Assert(!ShellManager.PrepareAdapterCache(profile, "2.3.3"), "Same adapter version does not touch cache again");
            Assert(File.ReadAllText(Path.Combine(workers, @"Database\registry")) == "current worker", "Restart preserves current registration");
            Assert(ShellManager.PrepareAdapterCache(profile, "2.3.4"), "Next adapter update refreshes cache automatically");
            Assert(Directory.GetDirectories(workers, "BWOS-backup-*").Length == 2, "Update backups remain separate");
        }

        private static void TestScreenSharing()
        {
            var now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
            var stream = new ScreenStream(() => now);
            var empty = new Dictionary<string, object>();
            var started = stream.Start("http://192.168.1.2:47914");
            string session = Json.Str(started, "session", ""), url = Json.Str(started, "url", ""), code = url.Split('=')[1];
            Assert(code.Length == 32 && session.Length == 32 && !url.Contains(session), "Viewer link has a distinct random one-time credential");
            Assert(!Json.Bool(stream.Pair("wrong"), "ok", true), "Wrong pairing code denied");
            Assert(!Json.Bool(stream.Exchange(true, "wrong", "poll", empty), "ok", true), "Wrong host session denied");
            string viewer = Json.Str(stream.Pair(code), "token", "");
            Assert(viewer.Length == 32 && stream.ViewerAuthorized(viewer), "Viewer pairs into the current sharing session");
            Assert(!Json.Bool(stream.Pair(code), "ok", true), "Sharing link is single-use");
            Assert(!stream.ViewerAuthorized(session), "Host and viewer credentials have separate scopes");
            var offer = new Dictionary<string, object> { { "type", "offer" }, { "data", "{\"type\":\"offer\",\"sdp\":\"fixture\"}" } };
            Assert(Json.Bool(stream.Exchange(true, session, "signal", offer), "ok", false), "Host sends an offer");
            Assert(!Json.Bool(stream.Exchange(false, viewer, "signal", offer), "ok", true), "Viewer cannot forge a host offer");
            var polled = stream.Exchange(false, viewer, "poll", empty);
            Assert(Json.List(polled, "messages").Count == 1, "Viewer receives host signaling");
            Assert(Json.List(stream.Exchange(false, viewer, "poll", empty), "messages").Count == 1, "Signaling can be retried without losing messages");
            Assert(Json.List(stream.Exchange(false, viewer, "poll", new Dictionary<string, object> { { "after", 1 } }), "messages").Count == 0, "Cursor avoids replaying received messages");
            Assert(!Json.Bool(stream.Exchange(false, viewer, "stop", empty), "ok", true), "Viewer cannot use host stop action");
            Assert(Json.Bool(stream.Exchange(true, session, "stop", empty), "ok", false) && !stream.ViewerAuthorized(viewer), "Stop immediately revokes screen and control access");
            started = stream.Start("http://192.168.1.2:47914");session = Json.Str(started, "session", "");code = Json.Str(started, "url", "").Split('=')[1];
            for (int i = 0; i < 11; i++) { now = now.AddMinutes(1); stream.Exchange(true, session, "poll", empty); }
            Assert(!Json.Bool(stream.Pair(code), "ok", true), "Unpaired links expire after ten minutes while the host remains alive");
            started = stream.Start("http://192.168.1.2:47914");session = Json.Str(started, "session", "");code = Json.Str(started, "url", "").Split('=')[1];viewer = Json.Str(stream.Pair(code), "token", "");
            now = now.AddSeconds(91);
            Assert(!stream.ViewerAuthorized(viewer), "A crashed or closed sender revokes the viewer after its heartbeat expires");
            started = stream.Start("http://192.168.1.2:47914");session = Json.Str(started, "session", "");code = Json.Str(started, "url", "").Split('=')[1];viewer = Json.Str(stream.Pair(code), "token", "");
            Assert(Json.Bool(stream.Exchange(false, viewer, "leave", empty), "ok", false) && !stream.ViewerAuthorized(viewer), "Viewer disconnect terminates the session");
            started = stream.Start("http://192.168.1.2:47914");session = Json.Str(started, "session", "");
            for (int i = 0; i < 256; i++) stream.Exchange(true, session, "signal", offer);
            Assert(!Json.Bool(stream.Exchange(true, session, "signal", offer), "ok", true), "Signaling memory is bounded");
            Assert(!Json.Bool(stream.Exchange(true, session, "signal", new Dictionary<string, object> { { "type", "candidate" }, { "data", new string('x', 48001) } }), "ok", true), "Oversized signaling is denied");
            stream.Stop();
        }

        private static void TestScreenHttp()
        {
            // Exercise the actual request parser on loopback. No keys or real
            // screen data: only session pairing and signaling fixtures.
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                var remote = new PhoneRemote(null, port);
                var started = remote.Screen.Start("http://127.0.0.1:" + port);
                string code = Json.Str(started, "url", "").Split('=')[1];
                Func<string, string, string, string, string> request = (path, body, host, origin) =>
                {
                    Exception serverError = null;
                    var serving = new System.Threading.Thread(() => {
                        using (var incoming = listener.AcceptTcpClient())
                            try { typeof(PhoneRemote).GetMethod("Serve", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(remote, new object[] { incoming }); }
                            catch (Exception ex) { serverError = ex; }
                    }) { IsBackground = true };
                    serving.Start();
                    using (var client = new System.Net.Sockets.TcpClient())
                    {
                        client.ReceiveTimeout = 3000; client.Connect(System.Net.IPAddress.Loopback, port);
                        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body);
                        byte[] header = System.Text.Encoding.ASCII.GetBytes("POST " + path + " HTTP/1.1\r\nHost: " + host + "\r\n" + (origin == null ? "" : "Origin: " + origin + "\r\n") + "Content-Length: " + bytes.Length + "\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n");
                        var stream = client.GetStream(); stream.Write(header, 0, header.Length); stream.Write(bytes, 0, bytes.Length);
                        string response = new StreamReader(stream, System.Text.Encoding.UTF8).ReadToEnd();
                        Assert(serving.Join(3000) && serverError == null, "Remote HTTP request completed without parser failure");
                        return response;
                    }
                };
                string address = "127.0.0.1:" + port;
                string pair = Json.Serialize(new Dictionary<string, object> { { "code", code }, { "name", "हिंदी 🎵" } });
                Assert(request("/v/pair", pair, address, "http://" + address).StartsWith("HTTP/1.1 200"), "UTF-8 pairing body respects byte Content-Length and does not hang");
                Assert(request("/v/pair", pair, "evil.example:" + port, null).StartsWith("HTTP/1.1 403"), "LAN listener rejects DNS rebinding hosts");
                Assert(request("/v/pair", pair, address, "https://evil.example").StartsWith("HTTP/1.1 403"), "LAN listener rejects cross-origin requests");
                Assert(request("/v/key", "{\"action\":\"home\"}", address, null).StartsWith("HTTP/1.1 403"), "Unpaired viewer cannot send input");
                remote.Screen.Stop();
            }
            finally { listener.Stop(); }
        }

        public static void Main(string[] args)
        {
            if (args.Contains("--microphone")) { TestMicrophone(); return; }
            if (args.Contains("--stream-http")) { TestScreenHttp(); Console.WriteLine("PASS: " + assertions + " streaming HTTP assertions"); return; }
            if (args.Contains("--volume-read")) { Console.WriteLine("Windows volume: " + AudioVolume.Apply("read", "")); return; }
            if (args.Contains("--websocket")) { TestPlaybackSocket(); Console.WriteLine("PASS: " + assertions + " playback WebSocket assertions"); return; }
            if (args.Contains("--whisper-startup")) { using (var engine = new WhisperEngine()) Console.WriteLine(engine.StartupLog); return; }
            if (args.Contains("--whisper")) { TestWhisper(args.Contains("--cpu")); Console.WriteLine("PASS: " + assertions + " Whisper assertions"); return; }
            if (args.Contains("--install"))
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                VoiceAssets.Install(Console.WriteLine);
                Console.WriteLine("Assets: " + VoiceAssets.Root);
                return;
            }
            TestAdapterCacheUpdate();
            TestScreenSharing();
            Assert(PhoneRemote.PlaybackAction("play") == "toggle", "Phone Play/Pause addresses playback rather than focus");
            Assert(PhoneRemote.PlaybackAction("seekback") == "seekBack" && PhoneRemote.PlaybackAction("seekforward") == "seekForward", "Phone seek buttons use explicit media commands");
            Assert(PhoneRemote.PlaybackAction("home") == null, "Phone media actions preserve Home routing");
            string feedDir = Path.Combine(Paths.AppDir, "recommendation-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var feeds = new RecommendationFeeds(Path.Combine(feedDir, "feeds.json"));
                var report = Json.ParseObject("{\"service\":\"netflix\",\"url\":\"https://www.netflix.com/browse\",\"rows\":[{\"title\":\"Picked for you\",\"items\":[{\"title\":\"A film\",\"url\":\"https://www.netflix.com/title/123?tracking=private\",\"artwork\":\"https://evil.test/tracker\"},{\"title\":\"Wrong provider\",\"url\":\"https://www.primevideo.com/detail/ABC\"},{\"title\":\"Login\",\"url\":\"https://www.netflix.com/login\"}]}]}");
                feeds.Report(report);
                var snapshot = feeds.Describe();
                var feed = snapshot["netflix"] as Dictionary<string, object>;
                var items = Json.List(Json.List(feed, "rows")[0], "items");
                Assert(items.Count == 1, "Imported feed excludes account pages and other providers");
                Assert(Json.Str(items[0], "url", "") == "https://www.netflix.com/title/123", "Imported links discard tracking queries");
                Assert(Json.Str(items[0], "artwork", "") == "", "Only provider artwork domains enter the cache");
                snapshot.Clear(); Assert(feeds.Describe().Count == 1, "Snapshot cannot mutate stored feeds");
                Assert(new RecommendationFeeds(Path.Combine(feedDir, "feeds.json")).Describe().Count == 1, "Recommendations survive restart");
                report["rows"] = new object[0]; feeds.Report(report);
                Assert(feeds.Describe().Count == 1, "Empty profile picker cannot erase a prior snapshot");
                Assert(RecommendationFeeds.Provider("https://netflix.com.evil.test/") == null && RecommendationFeeds.Provider("https://secret@netflix.com/") == null, "Recommendation sources reject deceptive domains and credentials");
                var signed = Json.ParseObject("{\"service\":\"netflix\",\"url\":\"https://www.netflix.com/browse\",\"rows\":[{\"items\":[{\"title\":\"Image test\",\"url\":\"https://www.netflix.com/title/123\",\"artwork\":\"https://occ.nflxso.net/art.jpg?token=sample&width=640#fragment\"}]}]}");
                feeds.Report(signed);
                var signedItems = Json.List(Json.List(feeds.Describe()["netflix"] as Dictionary<string, object>, "rows")[0], "items");
                Assert(Json.Str(signedItems[0], "artwork", "") == "https://occ.nflxso.net/art.jpg?token=sample&width=640", "CDN delivery and resize queries survive while fragments are removed");
                var search = Json.ParseObject("{\"service\":\"netflix\",\"url\":\"https://www.netflix.com/search?q=film\",\"rows\":[{\"title\":\"Search\",\"items\":[{\"title\":\"Search result\",\"url\":\"https://www.netflix.com/title/999\"}]}]}");
                string beforeSearch = Json.Serialize(feeds.Describe());
                var preview = feeds.Preview(search) as Dictionary<string, object>;
                Assert(Json.List(preview, "rows").Count == 1, "Search previews validate and return provider cards");
                Assert(Json.Serialize(feeds.Describe()) == beforeSearch, "Search results never replace personalized Home recommendations");
                feeds.Report(search);
                var expandedRows = Json.List(feeds.Describe()["netflix"] as Dictionary<string, object>, "rows");
                Assert(expandedRows.Sum(row => Json.List(row, "items").Count) == 2, "Sparse lazy reports preserve previously discovered cards");
                search["complete"] = true; feeds.Report(search);
                Assert(Json.List(feeds.Describe()["netflix"] as Dictionary<string, object>, "rows").Sum(row => Json.List(row, "items").Count) == 2, "Sparse complete reports cannot discard a fuller recent snapshot");
                Assert(Json.Int(feeds.Describe()["netflix"] as Dictionary<string, object>, "captured", 0) == 1, "Feed diagnostics distinguish newly captured cards from retained cards");
                string beforeFailure = Json.Serialize(Json.List(feeds.Describe()["netflix"] as Dictionary<string, object>, "rows"));
                feeds.Status(Json.ParseObject("{\"service\":\"netflix\",\"status\":\"profile-required\"}"));
                Assert(Json.Str(feeds.Describe()["netflix"] as Dictionary<string, object>, "status", "") == "profile-required", "Unavailable feed status identifies profile selection without account details");
                Assert(Json.Serialize(Json.List(feeds.Describe()["netflix"] as Dictionary<string, object>, "rows")) == beforeFailure, "Failed discovery status preserves the collected feed");
                var many = Enumerable.Range(1, 400).Select(i => new Dictionary<string, object> { { "title", "Film " + i }, { "url", "https://www.netflix.com/title/" + i } }).ToList();
                search["rows"] = new[] { new Dictionary<string, object> { { "title", "Large discovery" }, { "items", many } } };
                var large = feeds.Preview(search) as Dictionary<string, object>;
                Assert(Json.List(Json.List(large, "rows")[0], "items").Count == 360, "Large discovery shelves retain more than 30 cards within a bounded total");
            }
            finally { if (Directory.Exists(feedDir)) Directory.Delete(feedDir, true); }
            Command("Open YouTube", "open", "youtube");
            Command("Open YouTube now", "open", "youtube");
            Command("Search for Arijit Singh songs", "search", "arijit singh songs");
            Command("Can you please search for guitar lessons", "search", "guitar lessons");
            Command("गाने खोजो", "search", "गाने");
            Command("असिस्टेंट दिखाओ", "showAssistant", "");
            Command("Please open Netflix", "open", "netflix");
            Command("Open Netflix now please", "open", "netflix");
            Command("Search YouTube for music now", "youtubeSearch", "music now");
            Assert(Microphone.Level(new byte[3200]) == 0, "Silent PCM has no microphone activity");
            Assert(Microphone.Level(new byte[] { 0, 64, 0, 192 }) == 100, "Positive and negative PCM report activity");
            Assert(Microphone.Level(new byte[] { 64, 0, 192, 255 }) == 1, "Quiet microphone activity is measurable");
            Command("Launch Rocket League", "open", "rocket league");
            Command(" Hey console, open YouTube ", "open", "youtube");
            Command("Search YouTube for C# tutorials", "youtubeSearch", "c# tutorials");
            Command("यूट्यूब खोलो", "open", "यूट्यूब");
            Command("यूट्यूब पर संगीत खोजो", "youtubeSearch", "संगीत");
            Command("यूट्यूब पर खोजो हिंदी गाने", "youtubeSearch", "हिंदी गाने");
            Command("नंबर तीन चुनो", "selectNumber", "3");
            Command("Select five", "selectNumber", "5");
            Command("Select twelve", "selectNumber", "12");
            Command("Select ninety nine", "selectNumber", "99");
            Command("Select number twenty-one", "selectNumber", "21");
            Command("नंबर बारह चुनो", "selectNumber", "12");
            Command("नंबर एक दो चुनो", "selectNumber", "12");
            Command("नंबर १२", "selectNumber", "12");
            Command("नंबर पाँच", "selectNumber", "5");
            Command("आवाज़ कम करो", "voldown", "");
            Command("Hey console,volume up", "volup", "");
            Command("Please go volume up now", "volup", "");
            Command("Could you make it quieter please", "voldown", "");
            Command("Set volume to fifty percent", "volumeSet", "50");
            Command("Volume one hundred", "volumeSet", "100");
            Command("आवाज पचास प्रतिशत करो", "volumeSet", "50");
            Command("Increase the volume by ten percent", "volumeChange", "10");
            Command("Lower volume by twenty", "volumeChange", "-20");
            Command("Unmute", "unmute", "");
            Command("Pause the music please", "pause", "");
            Command("Stop music", "stopPlayback", "");
            Command("Show now playing", "playerControls", "");
            Command("Reopen player", "reopenPlayer", "");
            Command("Skip forward twenty seconds", "seekForward", "20");
            Command("Rewind ten seconds", "seekBack", "10");
            Command("Restart song", "restartMedia", "");
            Command("Next video", "nextMedia", "");
            Command("Subtitles off", "subtitlesOff", "");
            Command("Show keyboard", "keyboard", "");
            Command("Reload page", "reloadPage", "");
            Assert(VoiceCommands.Parse("Set volume to 101 percent") == null, "Invalid volume is ignored");
            Assert(VoiceCommands.Parse("Skip forward -5 seconds") == null, "Negative seek is ignored");
            var defaultConfig = Json.ParseObject((string)typeof(Config).GetMethod("DefaultJson", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, null));
            Assert(Json.Str(defaultConfig, "voiceMode", "") == "wake", "New installs default to wake mode");
            Command("रोक दो", "pause", "");
            Command("जारी रखो", "resume", "");
            Command("गेम जारी रखो", "resumeGame", "");
            Command("Stop listening", "sleepVoice", "");
            Command("घर चलो", "home", "");
            Command("डिवाइस दिखाओ", "open", "डिवाइस");
            Assert(VoiceCommands.Parse("shutdown") == null, "No power action from speech");
            Assert(VoiceCommands.Parse("delete everything") == null, "Unknown command ignored");
            Assert(VoiceCommands.Parse("select 100") == null, "Number out of range");
            Assert(VoiceCommands.Parse("select -1") == null, "Negative number rejected");
            Assert(VoiceCommands.Parse("select -") == null, "Malformed number rejected");
            Assert(VoiceCommands.Parse("") == null, "Empty transcript");
            var time = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
            Command("Hey, console. Open YouTube", "open", "youtube");
            Command("Hello, console! Volume up", "volup", "");
            Command("Hey counsel, open YouTube", "open", "youtube");
            Command("Hey con sole, volume up", "volup", "");
            Command("हेय कंसोल घर चलो", "home", "");
            Command("हे, कंसोल। घर चलो", "home", "");
            bool woke;
            VoiceCommands.StripWake("Hey, console.", out woke);
            Assert(woke, "Punctuated wake-only dictation arms listening");
            foreach (string ordinary in new[] { "the console is open", "hey consoleation open youtube", "console open youtube", "hey constantly", "I said hey console" })
            {
                VoiceCommands.StripWake(ordinary, out woke);
                Assert(!woke, "Ordinary speech is not a wake phrase: " + ordinary);
            }
            var requests = new SpeechRequests();
            requests.Add(new byte[] { 1 }, time); requests.Add(new byte[] { 2 }, time.AddSeconds(1));
            Assert(requests.Take(time.AddSeconds(2)).Audio[0] == 1 && requests.Take(time.AddSeconds(2)).Audio[0] == 2, "Wake and follow-up survive busy inference in capture order");
            requests.Add(new byte[] { 1 }, time);
            Assert(requests.Take(time.AddSeconds(9)) == null, "Stale speech cannot execute later");
            for (byte i = 0; i < 4; i++) requests.Add(new byte[] { i }, time);
            Assert(requests.Take(time).Audio[0] == 1, "Speech backlog is bounded");
            requests.Clear(); Assert(requests.Take(time) == null, "Settings changes clear pending speech");
            var session = new VoiceSession();
            Assert(session.Accept("open youtube", "direct", true, time) != null, "Direct foreground");
            Assert(session.Accept("open youtube", "direct", false, time) == null, "Direct background blocked");
            session = new VoiceSession();
            Assert(session.Accept("open youtube", "wake", true, time) == null, "Wake required");
            Assert(session.Accept("hey console", "wake", false, time) == null && session.Active(time), "Wake arms session");
            Assert(session.Accept("open youtube", "wake", false, time.AddSeconds(2)) != null, "Wake background command");
            Assert(session.Accept("open youtube", "wake", true, time.AddSeconds(15)) == null, "Wake timeout");
            Assert(session.Accept("hey console open games", "wake", false, time.AddSeconds(16)) != null, "One-sentence wake");
            Assert(session.Accept("stop listening", "wake", true, time.AddSeconds(17)) != null && session.Sleeping, "Pause listening");
            Assert(session.Accept("home", "wake", true, time.AddSeconds(18)) == null, "Paused commands ignored");
            Assert(session.Accept("हेलो कंसोल यूट्यूब खोलो", "wake", true, time.AddSeconds(19)) != null, "Hindi wake resumes");
            session = new VoiceSession();
            Assert(session.Accept("open youtube", "everywhere", false, time) != null, "Commands anywhere works outside BWOS");
            Assert(session.Accept("today the sky is blue", "everywhere", false, time) == null, "Unrelated speech ignored everywhere");
            Assert(session.Accept("stop listening", "everywhere", false, time) != null && session.Sleeping, "Pause also applies everywhere");
            Assert(session.Accept("open youtube", "everywhere", false, time) == null, "Paused background commands ignored");
            Assert(session.Accept("Hey assistant", "everywhere", false, time) == null && !session.Sleeping, "Hey assistant wakes the paused assistant");
            Assert(session.Accept("search for Hindi music", "everywhere", false, time) != null, "Search follows wake-up");
            var voice = new VoiceService(new Config(), () => true);
            using (var overlay = new AssistantWindow(voice))
            using (var picture = new System.Drawing.Bitmap(390, 90))
            using (var graphics = System.Drawing.Graphics.FromImage(picture))
            {
                graphics.Clear(System.Drawing.Color.FromArgb(19, 25, 42));
                overlay.PaintState(graphics, Json.ParseObject("{\"speaking\":true,\"listening\":true,\"reply\":\"Searching for Hindi songs.\"}"), 1);
                picture.Save(Path.Combine(Paths.AppDir, "assistant-overlay.png"));
            }
            voice.Queue(VoiceCommands.Parse("open youtube"), false);
            Assert(Json.List(voice.Claim(true) as Dictionary<string, object>, "commands").Count == 0, "Service cannot steal pending home command during navigation");
            Assert(Json.List(voice.Claim(false) as Dictionary<string, object>, "commands").Count == 1, "Home receives pending command once");
            Assert(Json.List(voice.Claim(false) as Dictionary<string, object>, "commands").Count == 0, "Consumed command is not repeated");
            var playback = new PlaybackService();
            Command("browse youtube", "browseService", "youtube");
            Command("open netflix app", "openServiceApp", "netflix");
            Command("यूट्यूब ब्राउज़ करो", "browseService", "यूट्यूब");
            Assert(VideoLibrary.VideoId("https://www.youtube.com/tv#/watch?v=zTSXiF_cxG8") == "zTSXiF_cxG8", "TV watch links normalize for the embedded player");
            Assert(VideoLibrary.VideoId("https://youtu.be/zTSXiF_cxG8?t=2") == "zTSXiF_cxG8", "Short YouTube links normalize");
            foreach (string url in new[] { "https://youtube.com.evil.test/watch?v=zTSXiF_cxG8", "http://www.youtube.com/watch?v=zTSXiF_cxG8", "https://token@www.youtube.com/watch?v=zTSXiF_cxG8", "https://www.youtube.com:8080/watch?v=zTSXiF_cxG8" }) Assert(VideoLibrary.VideoId(url) == null, "Unsafe link rejected");
            string videoDir = Path.Combine(Paths.AppDir, "video-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                int calls = 0;
                var videos = new VideoLibrary(Path.Combine(videoDir, "videos.json"), delegate(string id) { calls++; return new Dictionary<string, object> { { "title", "Sample video" }, { "author_name", "Sample channel" } }; });
                videos.Add("https://youtu.be/zTSXiF_cxG8"); videos.Add("https://www.youtube.com/watch?v=zTSXiF_cxG8");
                Assert((videos.Describe() as Dictionary<string, object>[]).Length == 1, "Saved video deduplicates canonical URLs");
                videos.Add("https://evil.test/watch?v=zTSXiF_cxG8"); Assert(calls == 2, "Rejected URLs never trigger metadata requests");
                var loaded = new VideoLibrary(Path.Combine(videoDir, "videos.json"));
                Assert((loaded.Describe() as Dictionary<string, object>[]).Length == 1, "Saved video survives app restart");
                loaded.Remove("zTSXiF_cxG8"); Assert((loaded.Describe() as Dictionary<string, object>[]).Length == 0, "Remove updates saved library");
            }
            finally { if (Directory.Exists(videoDir)) Directory.Delete(videoDir, true); }
            var localPlayback = new PlaybackService();
            localPlayback.Report(Json.ParseObject("{\"id\":\"0\",\"local\":true,\"url\":\"https://www.youtube.com/watch?v=zTSXiF_cxG8\"}"));
            localPlayback.Control("0", "pause");
            Assert(Json.List(localPlayback.Claim() as Dictionary<string, object>, "commands").Count == 0, "Browser worker cannot consume native player commands");
            Assert(Json.List(localPlayback.Claim(true) as Dictionary<string, object>, "commands").Count == 1, "Native player receives its own command exactly once");
            playback.Report(Json.ParseObject("{\"id\":\"42\",\"url\":\"https://www.youtube.com/watch?v=test\",\"title\":\"Music\",\"background\":true}"));
            Assert((playback.Describe() as Dictionary<string, object>[]).Length == 1, "Live background player recorded");
            playback.Report(new Dictionary<string, object> { { "id", "42" }, { "url", "https://www.youtube.com/watch?v=test" }, { "position", 12.5 }, { "duration", 90.25 } });
            var player = (playback.Describe() as Dictionary<string, object>[])[0];
            Assert((double)player["position"] == 12.5 && (double)player["duration"] == 90.25, "Playback timeline preserves fractional seconds");
            playback.Report(new Dictionary<string, object> { { "id", "42" }, { "url", "https://www.youtube.com/watch?v=test" }, { "position", -1 }, { "duration", double.PositiveInfinity } });
            player = (playback.Describe() as Dictionary<string, object>[])[0];
            Assert((double)player["position"] == 0 && (double)player["duration"] == 0, "Invalid and live-stream timing is normalized");
            Assert(Json.Bool(playback.Control("42", "pause") as Dictionary<string, object>, "ok", false), "Known player accepts pause");
            Assert(Json.List(playback.Claim() as Dictionary<string, object>, "commands").Count == 1, "Playback commands delivered once");
            Assert(Json.List(playback.Claim() as Dictionary<string, object>, "commands").Count == 0, "Playback command consumed");
            Assert(!Json.Bool(playback.Control("42", "delete") as Dictionary<string, object>, "ok", true), "Arbitrary playback action rejected");
            Assert(!Json.Bool(playback.Control("99", "stop") as Dictionary<string, object>, "ok", true), "Unknown player rejected");
            Assert(Json.Bool(playback.Control("42", "retry") as Dictionary<string, object>, "ok", false), "Retry queues only the known player");
            playback.Diagnostic(Json.ParseObject("{\"id\":\"42\",\"service\":\"youtube\",\"category\":\"media-network\",\"code\":\"HTTP_403\",\"url\":\"signed-secret\"}"));
            var diagnostic = (playback.Diagnostics() as Dictionary<string, object>[])[0];
            Assert(Json.Str(diagnostic, "code", "") == "HTTP_403" && !diagnostic.ContainsKey("url"), "Diagnostics retain status codes without signed URLs");
            playback.Diagnostic(Json.ParseObject("{\"id\":\"42\",\"service\":\"youtube\",\"category\":\"media-network\",\"code\":\"HTTP_403\"}"));
            playback.Diagnostic(Json.ParseObject("{\"id\":\"42\",\"service\":\"youtube\",\"category\":\"player\",\"code\":\"private-data\\nHTTP_403\"}"));
            Assert((playback.Diagnostics() as Dictionary<string, object>[]).Length == 1, "Repeated diagnostics and arbitrary text are rejected");
            for (int i = 0; i < 40; i++) playback.Diagnostic(new Dictionary<string, object> { { "id", i.ToString() }, { "service", "youtube" }, { "category", "player" }, { "code", "MEDIA_2" } });
            Assert((playback.Diagnostics() as Dictionary<string, object>[]).Length == 30, "Diagnostics history is bounded");
            playback.Report(Json.ParseObject("{\"id\":\"42\",\"url\":\"https://www.youtube.com/\",\"ended\":true}"));
            Assert((playback.Describe() as Dictionary<string, object>[]).Length == 0, "Ended player removed");
            var segmenter = new SpeechSegmenter();
            for (int i = 0; i < 30; i++) Assert(segmenter.Feed(new byte[3200]) == null, "Silence never opens a speech segment");
            var spoken = Enumerable.Repeat((byte)40, 3200).ToArray();
            for (int i = 0; i < 5; i++) segmenter.Feed(spoken);
            byte[] utterance = null;
            for (int i = 0; i < 8; i++) utterance = segmenter.Feed(new byte[3200]) ?? utterance;
            Assert(utterance != null && utterance.Length > spoken.Length * 5, "Speech segment keeps pre-roll and finishes after a pause");
            var shortBlocks = new SpeechSegmenter();
            for (int i = 0; i < 50; i++) shortBlocks.Feed(new byte[640]);
            for (int i = 0; i < 25; i++) shortBlocks.Feed(Enumerable.Repeat((byte)40, 640).ToArray());
            byte[] shortUtterance = null;
            for (int i = 0; i < 40; i++) shortUtterance = shortBlocks.Feed(new byte[640]) ?? shortUtterance;
            Assert(shortUtterance != null && shortUtterance.Length >= 49000, "AEC's smaller audio blocks still keep 400 ms of pre-roll");
            var wakeChunks = new SpeechSegmenter { MaximumBytes = 128000 };
            byte[] wakeChunk = null;
            for (int i = 0; i < 40; i++) wakeChunk = wakeChunks.Feed(spoken) ?? wakeChunk;
            Assert(wakeChunk != null && wakeChunk.Length <= 128000, "Continuous music cannot delay wake decoding for twelve seconds");
            wakeChunks.MaximumBytes = 32000;
            byte[] overlapping = null;
            for (int i = 0; i < 6; i++) overlapping = wakeChunks.Feed(spoken) ?? overlapping;
            Assert(overlapping != null && overlapping.Length == 32000, "Wake chunks retain audio across boundaries");
            Assert(SpeechSegmenter.Wav(spoken).Length == spoken.Length + 44, "In-memory WAV has a complete header");
            if (args.Contains("--models") || args.Contains("--audio"))
            {
                Assert(VoiceAssets.Available("en") && VoiceAssets.Available("hi"), "Both offline models installed");
                using (var hindi = new VoskEngine("hi")) Assert(hindi.Feed(new byte[3200]) == null, "Hindi model loads and silence produces no action");
                using (var english = new VoskEngine("en")) Assert(english.Feed(new byte[3200]) == null, "English silence produces no action");
                Assert(VoiceAssets.Available("en-in"), "Indian English model installed");
                using (var indian = new VoskEngine("en-in")) Assert(indian.Feed(new byte[3200]) == null, "Indian English model loads and silence produces no action");
            }
            if (args.Contains("--devices"))
            {
                var devices = new Devices(); devices.Refresh();
                var json = Json.Serialize(devices.Describe());
                Console.WriteLine("Device inventory: " + json);
                Assert(json.Contains("\"error\":\"\""), "Native Bluetooth and audio inventory succeeds");
                Assert(!Json.Bool(devices.Connect("untrusted-id", true) as Dictionary<string, object>, "ok", true), "Unknown device rejected without changing hardware");
            }
            if (args.Contains("--audio")) TestAudio();
            Console.WriteLine("PASS: " + assertions + " assertions");
        }
        private static void TestMicrophone()
        {
            // Exercise different consumers of the same Windows audio COM object.
            BluetoothAudio.Read(new List<Dictionary<string, object>>());
            Console.WriteLine("Windows volume: " + AudioVolume.Apply("read", ""));
            int device = -1;
            int.TryParse(Environment.GetEnvironmentVariable("BWOS_TEST_MICROPHONE") ?? "-1", out device);
            int buffers = 0, bytes = 0, peak = 0;
            using (var microphone = new Microphone(device))
            {
                DateTime until = DateTime.UtcNow.AddSeconds(6);
                while (DateTime.UtcNow < until)
                {
                    microphone.Read(samples => { buffers++; bytes += samples.Length; peak = Math.Max(peak, Microphone.Level(samples)); });
                    System.Threading.Thread.Sleep(20);
                }
                Console.WriteLine(Json.Serialize(new Dictionary<string, object> { { "processing", microphone.Processing }, { "buffers", buffers }, { "seconds", bytes / 32000.0 }, { "peak", peak } }));
            }
            Assert(buffers > 0 && bytes > 64000, "Native microphone produces PCM in a quiet room");
        }
        private static void TestAudio()
        {
            foreach (var phrase in new[] { "open youtube", "hey console open youtube", "show numbers", "volume down" })
            {
                using (var wav = new MemoryStream())
                using (var speaker = new SpeechSynthesizer())
                using (var engine = new VoskEngine("en"))
                {
                    speaker.SetOutputToAudioStream(wav, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                    speaker.Speak(phrase); speaker.SetOutputToNull();
                    byte[] pcm = wav.ToArray();
                    Assert(pcm.Length > 0, "TTS fixture contains PCM");
                    string transcript = null;
                    for (int offset = 0; offset < pcm.Length; offset += 3200)
                    {
                        byte[] block = new byte[Math.Min(3200, pcm.Length - offset)]; Array.Copy(pcm, offset, block, 0, block.Length);
                        transcript = engine.Feed(block) ?? transcript;
                    }
                    for (int i = 0; i < 20; i++) transcript = engine.Feed(new byte[3200]) ?? transcript;
                    Console.WriteLine("Audio: " + phrase + " → " + transcript);
                    Assert(VoiceCommands.Parse(transcript) != null, "Recognized synthetic spoken command");
                }
            }
        }
        private static void TestWhisper(bool cpu)
        {
            Assert(WhisperAssets.Available, "Whisper assets installed");
            var startup = System.Diagnostics.Stopwatch.StartNew();
            using (var engine = new WhisperEngine(cpu))
            {
                Console.WriteLine("Whisper startup: " + startup.Elapsed.TotalSeconds.ToString("F2") + " sec; backend " + engine.Backend);
                Console.WriteLine(engine.StartupLog);
                foreach (var phrase in new[] { "Hey console", "Hey console, open YouTube", "Open YouTube", "Please open Netflix", "Search YouTube for Hindi songs", "Show numbers", "today the sky is blue" })
                {
                    using (var wav = new MemoryStream()) using (var speaker = new SpeechSynthesizer())
                    {
                        speaker.SetOutputToAudioStream(wav, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                        speaker.Speak(phrase); speaker.SetOutputToNull();
                        var timer = System.Diagnostics.Stopwatch.StartNew();
                        string text = engine.Transcribe(wav.ToArray(), "auto");
                        Console.WriteLine(phrase + " → " + text + "; " + timer.Elapsed.TotalSeconds.ToString("F2") + " sec; confidence " + engine.LastConfidence.ToString("F2"));
                        bool woke;
                        VoiceCommands.StripWake(text, out woke);
                        Assert(phrase == "Hey console" ? woke : phrase == "today the sky is blue" ? VoiceCommands.Parse(text) == null : VoiceCommands.Parse(text) != null, "Whisper spoken wake / command / unrelated sentence");
                    }
                }
                Assert(string.IsNullOrWhiteSpace(engine.Transcribe(new byte[64000], "en")), "Silence is not a command");
            }
        }
        private static void TestPlaybackSocket()
        {
            var server = new WebServer(47921); server.Start();
            try
            {
                using (var denied = new System.Net.WebSockets.ClientWebSocket())
                {
                    bool rejected = false;
                    try { denied.ConnectAsync(new Uri("ws://127.0.0.1:47921/api/playback-events?t=wrong"), System.Threading.CancellationToken.None).GetAwaiter().GetResult(); }
                    catch { rejected = true; }
                    Assert(rejected, "Playback socket requires host token");
                }
                using (var socket = new System.Net.WebSockets.ClientWebSocket())
                {
                    socket.ConnectAsync(new Uri("ws://127.0.0.1:47921/api/playback-events?t=" + server.Token), System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                    var buffer = new byte[128];
                    var read = socket.ReceiveAsync(new ArraySegment<byte>(buffer), System.Threading.CancellationToken.None);
                    System.Threading.Thread.Sleep(100); server.Broadcast("playback", null);
                    Assert(read.Wait(3000), "Playback command wakes extension immediately");
                    Assert(System.Text.Encoding.UTF8.GetString(buffer, 0, read.Result.Count) == "playback", "Playback notification received");
                    socket.Abort();
                }
            }
            finally { server.Stop(); }
        }
    }
}
