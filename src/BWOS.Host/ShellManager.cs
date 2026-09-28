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
    /// Owns the BWOS window: a fullscreen Chromium (Edge by default) app window with its own,
    /// persistent profile. Streaming services open *inside* this same window (you never leave BWOS);
    /// the bundled "bwos-tv" extension adds controller navigation to those sites, and the
    /// profile keeps you signed in to each service.
    /// </summary>
    internal sealed class ShellManager
    {
        public const string ShellTitle = "BWOS";
        private const ushort VK_F24 = 0x87; // "go home" signal understood by the bwos-tv extension

        private readonly object gate = new object();
        private readonly string browserExe;
        private readonly string shellProfile;
        private string shellUrl;
        private List<int> shellPids = new List<int>();
        private DateTime shellPidsAt = DateTime.MinValue;

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
            if (Native.WindowText(fg) == ShellTitle) return false;
            return ShellPids(false).Contains(Native.ProcessIdOf(fg));
        }

        /// <summary>Leave the streaming site and return to the BWOS home screen (same window).</summary>
        public void GoHome()
        {
            var win = ShellWindow();
            if (win == IntPtr.Zero) return;
            Native.ForceForeground(win);
            Native.Tap(VK_F24);
            // Browser-level shortcut handled by the extension's background worker: works even when a
            // page or an embedded ad frame has swallowed the key above.
            System.Threading.Thread.Sleep(60);
            Native.Key(0x11, true); Native.Key(0x10, true);   // Ctrl + Shift
            Native.Tap(0x24);                                  // Home
            Native.Key(0x10, false); Native.Key(0x11, false);
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
            return wins.Count > 0 ? wins[0] : IntPtr.Zero;
        }

        private void StartShell()
        {
            if (browserExe == null) throw new InvalidOperationException("Microsoft Edge or Google Chrome is required.");
            Directory.CreateDirectory(shellProfile);
            KillProfile(shellProfile); // a stale instance would swallow our command-line flags
            string args = "--user-data-dir=\"" + shellProfile + "\"" +
                " --no-first-run --no-default-browser-check --disable-sync" +
                " --hide-crash-restore-bubble --disable-session-crashed-bubble" +
                // Chromium only honours the LAST --disable-features switch, so keep them in one.
                " --disable-features=Translate,msEdgeTranslate,msUndersideButton,msEdgeSidebarV2,EdgeCollections,DisableLoadExtensionCommandLineSwitch" +
                " --app=\"" + shellUrl + "\"" +
                " --start-fullscreen" +
                " --autoplay-policy=no-user-gesture-required" +
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
            bool stale = shellPids.Count == 0 || (DateTime.UtcNow - shellPidsAt).TotalSeconds > 5;
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
            psi.WorkingDirectory = Path.GetDirectoryName(browserExe);
            Log.Write("start browser: " + args.Replace(shellUrl ?? "\u0000", "<shell url>"));
            var p = Process.Start(psi);
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
                try { Process.GetProcessById(pid).Kill(); } catch { }
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
