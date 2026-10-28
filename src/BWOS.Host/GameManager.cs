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
    /// <summary>Launches games and keeps track of the one that is running.</summary>
    internal sealed class GameManager
    {
        private readonly Library library;
        private GameEntry launched;
        private DateTime launchedAt;
        private bool sawRunning;
        private string lastRunningKey;
        private volatile bool stopped;

        /// <summary>Raised with (key or null) when the running game changes.</summary>
        public event Action<string> RunningChanged;

        public GameManager(Library library)
        {
            this.library = library;
            var t = new Thread(Watch);
            t.IsBackground = true;
            t.Name = "BWOS game watch";
            t.Start();
        }

        public void Launch(GameEntry g)
        {
            launched = g;
            launchedAt = DateTime.UtcNow;
            sawRunning = false;
            Log.Write("launch " + g.Key);
            if (!string.IsNullOrEmpty(g.LaunchUri))
            {
                Process.Start(new ProcessStartInfo(g.LaunchUri) { UseShellExecute = true });
                return;
            }
            var psi = new ProcessStartInfo(g.Exe, g.Args ?? "");
            psi.UseShellExecute = true;
            if (!string.IsNullOrEmpty(g.WorkingDir) && Directory.Exists(g.WorkingDir)) psi.WorkingDirectory = g.WorkingDir;
            Process.Start(psi);
        }

        public string RunningKey
        {
            get { return lastRunningKey; }
        }

        public void Stop() { stopped = true; }

        public bool Resume()
        {
            var g = RunningGame();
            if (g == null) return false;
            foreach (var p in GameProcesses(g))
            {
                var wins = Native.TopLevelWindows(new[] { p });
                if (wins.Count > 0)
                {
                    Native.ForceForeground(wins[0]);
                    return true;
                }
            }
            return false;
        }

        public bool Quit()
        {
            var g = RunningGame();
            if (g == null) return false;
            var pids = GameProcesses(g);
            foreach (var pid in pids)
            {
                try { Process.GetProcessById(pid).CloseMainWindow(); } catch { }
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(5000);
                foreach (var pid in pids)
                {
                    try
                    {
                        var p = Process.GetProcessById(pid);
                        if (!p.HasExited) p.Kill();
                    }
                    catch { }
                }
            });
            return true;
        }

        public GameEntry RunningGame()
        {
            return lastRunningKey == null ? null : library.Find(lastRunningKey);
        }

        private void Watch()
        {
            while (!stopped)
            {
                try
                {
                    string key = DetectRunning();
                    if (key != lastRunningKey)
                    {
                        lastRunningKey = key;
                        Log.Write("running game: " + (key ?? "none"));
                        var h = RunningChanged;
                        if (h != null) h(key);
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("watch error: " + ex.Message);
                }
                Thread.Sleep(2000);
            }
        }

        private string DetectRunning()
        {
            // Steam publishes the running app id in the registry – cheap and exact.
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                object v = k == null ? null : k.GetValue("RunningAppID");
                int id = v is int ? (int)v : 0;
                if (id != 0)
                {
                    string key = "steam:" + id;
                    if (library.Find(key) != null) return key;
                }
            }

            // Non-Steam games: only look for the one we launched, by install folder.
            var g = launched;
            if (g == null || g.Source == "steam") return null;
            bool running = GameProcesses(g).Count > 0;
            if (running) { sawRunning = true; return g.Key; }
            // Launchers (Epic) take a while before the game process appears.
            if (!sawRunning && (DateTime.UtcNow - launchedAt).TotalMinutes < 3) return null;
            if (sawRunning) launched = null;
            return null;
        }

        /// <summary>Processes whose executable lives inside the game's install folder.</summary>
        public List<int> GameProcesses(GameEntry g)
        {
            var result = new List<int>();
            string dir = g.InstallDir;
            if (string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(g.Exe)) dir = Path.GetDirectoryName(g.Exe);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return result;
            dir = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath FROM Win32_Process"))
                {
                    foreach (ManagementObject mo in s.Get())
                    {
                        string path = mo["ExecutablePath"] as string;
                        if (path != null && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                            result.Add(Convert.ToInt32(mo["ProcessId"]));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("process scan failed: " + ex.Message);
            }
            return result;
        }
    }
}
