using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using Microsoft.Win32;

namespace Bwos
{
    /// <summary>
    /// Owns the BWOS window: a fullscreen Chromium (Edge by default) browser window with its own,
    /// persistent profile. Streaming services open *inside* this same window (you never leave BWOS);
    /// the bundled "bwos-tv" extension adds controller navigation to those sites, and the
    /// profile keeps you signed in to each service.
    /// </summary>
    internal sealed class ShellManager
    {
        public const string ShellTitle = "BWOS";
        private const ushort VK_F24 = 0x87; // Handled by remote.js in streaming pages and embedded frames.

        private readonly object gate = new object();
        private readonly string browserExe;
        private readonly string shellProfile;
        private string shellUrl;
        private List<int> shellPids = new List<int>();
        private DateTime shellPidsAt = DateTime.MinValue;
        private readonly object contextGate = new object();
        private string pageKind = "";
        private DateTime contextAt, integrationAt;
        public object IntegrationStatus() { lock (contextGate) return new Dictionary<string, object> { { "connected", (DateTime.UtcNow - integrationAt).TotalSeconds < 5 }, { "page", pageKind } }; }
        public void IntegrationPing() { lock (contextGate) integrationAt = DateTime.UtcNow; }
        public void ReportContext(string kind, bool front)
        {
            lock (contextGate) integrationAt = DateTime.UtcNow;
            if (!front || !ShellIsForeground()) return;
            lock (contextGate) { pageKind = kind; contextAt = DateTime.UtcNow; }
        }
        public bool YouTubeIsForeground() { lock (contextGate) return ShellIsForeground() && pageKind == "youtube" && (DateTime.UtcNow - contextAt).TotalSeconds < 4; }

        public ShellManager(Config config)
        {
            browserExe = FindBrowser(config.Browser);
            shellProfile = Path.Combine(Paths.DataDir, @"profiles\shell");
            Log.Write("browser: " + (browserExe ?? "NOT FOUND"));
        }

        public string BrowserExe { get { return browserExe; } }

        /// <summary>
        /// A BWOS window left over from a previous run of BWOS.exe was started with that run's flags
        /// and extension version; close it so the next "show" opens a fresh, up-to-date window.
        /// </summary>
        public void CloseStaleShell()
        {
            var pids = FindProfilePids(shellProfile);
            if (pids.Count == 0) return;
            Log.Write("closing BWOS window left over from a previous run");
            KillProfile(shellProfile);
            shellPids = new List<int>();
        }

        public void RefreshAdapterCache()
        {
            try
            {
                string manifest = Path.Combine(Paths.AppDir, @"extensions\bwos-tv\manifest.json");
                if (!File.Exists(manifest)) return;
                string version = Json.Str(Json.ParseObject(File.ReadAllText(manifest)), "version", "");
                if (string.IsNullOrEmpty(version)) return;
                // Development builds can change scripts without changing the
                // manifest version. Chromium otherwise reuses an older worker.
                string directory = Path.GetDirectoryName(manifest);
                string source = string.Join("\n", Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Where(path => new[] { ".js", ".json", ".css" }.Contains(Path.GetExtension(path)))
                    .OrderBy(path => path, StringComparer.Ordinal).Select(path => Path.GetFileName(path) + "\n" + File.ReadAllText(path)));
                using (var hash = System.Security.Cryptography.SHA256.Create())
                    version += ":" + BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(source))).Replace("-", "");
                PrepareAdapterCache(shellProfile, version);
            }
            catch (Exception ex) { Log.Write("browser adapter cache refresh failed: " + ex.Message); }
        }

        // Chromium can retain an old unpacked extension worker across app updates.
        // Refresh its regenerable worker registry once per extension release; site
        // cookies, local storage, IndexedDB and media caches stay in the profile.
        internal static bool PrepareAdapterCache(string profile, string version)
        {
            string root = Path.GetFullPath(profile).TrimEnd(Path.DirectorySeparatorChar);
            string marker = Path.Combine(root, "bwos-adapter-version.txt");
            if (File.Exists(marker) && File.ReadAllText(marker) == version) return false;
            string workers = Path.Combine(root, @"Default\Service Worker");
            foreach (string path in new[] { root, Path.Combine(root, "Default"), workers })
                if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Cannot refresh a redirected browser cache");
            string backup = Path.Combine(workers, "BWOS-backup-" + Guid.NewGuid().ToString("N"));
            foreach (string name in new[] { "Database", "ScriptCache" })
            {
                string source = Path.GetFullPath(Path.Combine(workers, name));
                string target = Path.GetFullPath(Path.Combine(backup, name));
                if (!source.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    !target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Browser cache path outside BWOS profile");
                if (!Directory.Exists(source)) continue;
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Cannot move a redirected browser cache");
                Directory.CreateDirectory(backup);
                Directory.Move(source, target);
            }
            Directory.CreateDirectory(root);
            File.WriteAllText(marker, version);
            Log.Write("browser adapter cache prepared for " + version);
            return true;
        }

        public void SetShellUrl(string url) { shellUrl = url; }

        /// <summary>Bring the BWOS window to the front, starting it if needed. Returns false if it was just started.</summary>
        public bool ShowShell()
        {
            lock (gate)
            {
                var win = ShellWindow();
                if (win != IntPtr.Zero)
                {
                    Native.ForceForeground(win);
                    HoldForeground(win);
                    return true;
                }
                StartShell();
                return false;
            }
        }

        public bool ShellIsForeground()
        {
            var fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            // Cheap filter first (called from the controller thread, also during games).
            if (!Native.WindowClass(fg).StartsWith("Chrome_WidgetWin")) return false;
            return ShellPids(false).Contains(Native.ProcessIdOf(fg));
        }

        /// <summary>True while the BWOS window is in front showing a streaming site rather than the BWOS home screen.</summary>
        public bool ServiceIsForeground()
        {
            var fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            // Cheap filters first: this runs from the controller thread, including during games.
            if (!Native.WindowClass(fg).StartsWith("Chrome_WidgetWin")) return false;
            lock (contextGate) if ((DateTime.UtcNow - contextAt).TotalSeconds < 4 && ShellPids(false).Contains(Native.ProcessIdOf(fg))) return pageKind != "home";
            if (HomeTitle(Native.WindowText(fg))) return false;
            return ShellPids(false).Contains(Native.ProcessIdOf(fg));
        }

        /// <summary>Ask the browser adapter to return Home, retaining any active player.</summary>
        public void GoHome()
        {
            var win = ShellIsForeground() ? Native.GetForegroundWindow() : ShellWindow();
            if (win == IntPtr.Zero) return;
            if (Native.GetForegroundWindow() != win) Native.ForceForeground(win);
            // The page adapter handles F24 directly. Do not follow it with another
            // shortcut: Edge may leave that shortcut unassigned, or send it to the
            // newly active Home tab partway through the first handoff.
            Native.Tap(VK_F24);
        }

        public void HideShell()
        {
            foreach (var w in Native.TopLevelWindows(ShellPids(true))) Native.ShowWindow(w, Native.SW_MINIMIZE);
        }

        public void CloseShell()
        {
            foreach (var w in Native.TopLevelWindows(ShellPids(true))) Native.PostMessage(w, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        public void HoldShellForeground()
        {
            var win = ShellWindow();
            if (win != IntPtr.Zero) HoldForeground(win);
        }

        /// <summary>
        /// Other apps listen to the home button too (Steam's "Guide Button Focuses Steam", Game Bar) and
        /// grab focus shortly after us. Watch for a few seconds and take the foreground back if needed.
        /// </summary>
        private void HoldForeground(IntPtr win)
        {
            int reasserts = 0;
            var until = DateTime.UtcNow.AddMilliseconds(4000); // Big Picture can take ~3 s to appear
            while (DateTime.UtcNow < until)
            {
                Thread.Sleep(90);
                var fg = Native.GetForegroundWindow();
                if (fg == win || fg == IntPtr.Zero) continue;
                int pid = Native.ProcessIdOf(fg);
                if (ShellPids(false).Contains(pid)) continue;
                if (reasserts++ >= 4) break;
                string thief = "?";
                try { thief = Process.GetProcessById(pid).ProcessName + " \"" + Native.WindowText(fg) + "\""; } catch { }
                Log.Write("focus taken by " + thief + " - taking it back" +
                    (thief.StartsWith("steam", StringComparison.OrdinalIgnoreCase) ? " (turn off Steam > Settings > Controller > Guide Button Focuses Steam)" : ""));
                Native.ForceForeground(win);
            }
        }

        /// <summary>The BWOS window, or IntPtr.Zero if it isn't open.</summary>
        public IntPtr Window { get { return ShellWindow(); } }

        private IntPtr ShellWindow()
        {
            var wins = Native.TopLevelWindows(ShellPids(true));
            var home = wins.FirstOrDefault(w => HomeTitle(Native.WindowText(w)));
            if (home != IntPtr.Zero) return home;
            return wins.Count > 0 ? wins[0] : IntPtr.Zero;
        }
        private static bool HomeTitle(string title) { return title == ShellTitle || title.StartsWith(ShellTitle + " - ", StringComparison.Ordinal); }

        private void StartShell()
        {
            if (browserExe == null) throw new InvalidOperationException("Microsoft Edge or Google Chrome is required.");
            Directory.CreateDirectory(shellProfile);
            KillProfile(shellProfile); // a stale instance would swallow our command-line flags
            string args = "--user-data-dir=\"" + shellProfile + "\"" +
                " --no-first-run --no-default-browser-check --disable-sync" +
                " --enable-logging=stderr" +
                " --hide-crash-restore-bubble --disable-session-crashed-bubble" +
                // Chromium only honours the LAST --disable-features switch, so keep them in one.
                " --disable-features=Translate,msEdgeTranslate,msUndersideButton,msEdgeSidebarV2,EdgeCollections,DisableLoadExtensionCommandLineSwitch" +
                // A normal fullscreen browser window supports retained media tabs.
                // App-mode windows cannot reliably hold multiple tabs and could
                // create a second visible window when Home was requested.
                " --new-window \"" + shellUrl + "\"" +
                " --start-fullscreen" +
                " --autoplay-policy=no-user-gesture-required" +
                " --disable-backgrounding-occluded-windows" +
                " --disable-pinch --overscroll-history-navigation=0";
            string ext = Path.Combine(Paths.AppDir, @"extensions\bwos-tv");
            if (Directory.Exists(ext)) args += " --load-extension=\"" + ext + "\"";

            var p = Start(args);
            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(150);
                shellPids = FindProfilePids(shellProfile);
                if (p != null && !p.HasExited && !shellPids.Contains(p.Id)) shellPids.Add(p.Id);
                shellPidsAt = DateTime.UtcNow;
                var win = ShellWindow();
                if (win != IntPtr.Zero)
                {
                    Native.ForceForeground(win);
                    HoldForeground(win);
                    return;
                }
            }
        }

        /// <summary>Browser process ids for the shell. The input bridge calls this ~120×/s, so the WMI lookup is cached.</summary>
        private List<int> ShellPids(bool fresh)
        {
            bool stale = (DateTime.UtcNow - shellPidsAt).TotalSeconds > 5;
            if (fresh && shellPids.Any(pid => !Alive(pid))) stale = true;
            if (stale)
            {
                shellPids = FindProfilePids(shellProfile);
                shellPidsAt = DateTime.UtcNow;
            }
            return shellPids;
        }

        private Process Start(string args)
        {
            var psi = new ProcessStartInfo(browserExe, args);
            psi.UseShellExecute = false;
            psi.RedirectStandardError = true;
            psi.WorkingDirectory = Path.GetDirectoryName(browserExe);
            Log.Write("start browser: " + args.Replace(shellUrl ?? "\u0000", "<shell url>"));
            var p = Process.Start(psi);
            p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                if (e.Data == null) return;
                if (e.Data.IndexOf("Failed to load extension", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    e.Data.IndexOf("Service worker registration failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (e.Data.Contains("/background.js") && (e.Data.Contains("Uncaught") || e.Data.Contains("BWOS"))))
                    Log.Write("browser integration: " + System.Text.RegularExpressions.Regex.Replace(e.Data, "[a-f0-9]{32}", "[redacted]", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            };
            p.BeginErrorReadLine();
            try { Native.AllowSetForegroundWindow(p.Id); } catch { }
            return p;
        }

        private static bool Alive(int pid)
        {
            try { return !Process.GetProcessById(pid).HasExited; }
            catch { return false; }
        }

        /// <summary>Browser processes whose command line uses the given profile directory.</summary>
        private List<int> FindProfilePids(string profile)
        {
            var result = new List<int>();
            string exeName = Path.GetFileName(browserExe ?? "msedge.exe");
            try
            {
                using (var s = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = '" + exeName.Replace("'", "") + "'"))
                {
                    foreach (ManagementObject mo in s.Get())
                    {
                        string cmd = mo["CommandLine"] as string;
                        // Renderer/GPU child processes carry --type=; only the browser process owns windows.
                        if (cmd == null || cmd.Contains("--type=")) continue;
                        if (cmd.IndexOf(profile, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        result.Add(Convert.ToInt32(mo["ProcessId"]));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("WMI query failed: " + ex.Message);
            }
            return result;
        }

        private void KillProfile(string profile)
        {
            foreach (var pid in FindProfilePids(profile))
            {
                try { var process = Process.GetProcessById(pid); process.Kill(); process.WaitForExit(3000); } catch { }
            }
        }

        private static string FindBrowser(string preferred)
        {
            if (!string.IsNullOrEmpty(preferred) && File.Exists(preferred)) return preferred;
            foreach (var name in new[] { "msedge.exe", "chrome.exe" })
            {
                foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
                {
                    using (var k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + name))
                    {
                        string p = k == null ? null : k.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(p) && File.Exists(p.Trim('"'))) return p.Trim('"');
                    }
                }
            }
            string[] guesses = {
                @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe",
                @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe",
                @"%ProgramFiles%\Google\Chrome\Application\chrome.exe",
                @"%LocalAppData%\Google\Chrome\Application\chrome.exe"
            };
            foreach (var g in guesses)
            {
                string p = Environment.ExpandEnvironmentVariables(g);
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }
}
