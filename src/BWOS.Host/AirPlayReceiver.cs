using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Bwos
{
    /// <summary>
    /// AirPlay screen mirroring from an iPhone, iPad or Mac, using UxPlay (open source, GPL-3.0),
    /// which BWOS runs in the background as a separate program. BWOS only starts it and looks after
    /// its window: when a device starts mirroring, the video is placed borderless over the screen
    /// BWOS is on (the TV / projector) and brought to the front; when mirroring stops, BWOS returns.
    ///
    /// UxPlay is found at data\tools\uxplay\uxplay.exe (next to an MSYS2 UCRT64 runtime in
    /// data\tools\msys64), or at the "airplayPath" set in config.json.
    /// </summary>
    internal sealed class AirPlayReceiver
    {
        private readonly ShellManager shell;
        private readonly Func<bool> enabled;
        private readonly string name;
        private readonly object gate = new object();
        private Process proc;
        private IntPtr videoWindow;
        private bool shellWasInFront;
        private volatile bool stopped;
        private int failures;

        public AirPlayReceiver(ShellManager shell, Func<bool> enabled, string name)
        {
            this.shell = shell;
            this.enabled = enabled;
            this.name = name;
        }

        public string ExePath { get; set; }
        public bool Installed { get { return ExePath != null && File.Exists(ExePath); } }
        public bool Running { get { lock (gate) return proc != null && !proc.HasExited; } }
        public bool Mirroring { get { return videoWindow != IntPtr.Zero; } }

        /// <summary>Default install location, or null.</summary>
        public static string FindExe(string configured)
        {
            foreach (var p in new[] { configured, Path.Combine(Paths.DataDir, @"tools\uxplay\uxplay.exe") })
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            return null;
        }

        public void Start()
        {
            var t = new Thread(Supervise);
            t.IsBackground = true;
            t.Name = "BWOS AirPlay";
            t.Start();
        }

        public void Stop()
        {
            stopped = true;
            Kill();
        }

        /// <summary>Settings changed: (re)start or stop UxPlay on the next check.</summary>
        public void Refresh() { if (!enabled()) Kill(); }

        // ------------------------------------------------------------------ process

        private void Supervise()
        {
            KillStale();
            while (!stopped)
            {
                try
                {
                    if (enabled() && Installed && !Running) Launch();
                    if (Running) WatchWindow();
                }
                catch (Exception ex) { Log.Write("airplay: " + ex.Message); }
                Thread.Sleep(Mirroring ? 400 : 700);
            }
        }

        private void Launch()
        {
            if (failures > 0)
            {
                // Crashed or refused to start: back off (5 s, 10 s … up to 2 min).
                Thread.Sleep(Math.Min(120, 5 * (1 << Math.Min(failures - 1, 5))) * 1000);
                if (stopped || !enabled()) return;
            }
            string exeDir = Path.GetDirectoryName(ExePath);
            string runtime = Path.GetFullPath(Path.Combine(exeDir, @"..\msys64\ucrt64\bin"));
            var psi = new ProcessStartInfo(ExePath)
            {
                // -nh: no "@hostname" suffix · -p: fixed ports (TCP 7000/7001/7100, UDP 6000/6001/7011) so the
                // firewall rule stays valid · -reset 10: drop a device that went silent for 10 s · -nohold: a new
                // device takes over from the current one.
                Arguments = "-n \"" + name.Replace("\"", "") + "\" -nh -p -reset 10 -nohold -vs d3d11videosink -as wasapisink",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = exeDir
            };
            if (Directory.Exists(runtime)) psi.EnvironmentVariables["PATH"] = runtime + ";" + psi.EnvironmentVariables["PATH"];
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) => OnOutput(e.Data);
            p.ErrorDataReceived += (s, e) => OnOutput(e.Data);
            DateTime started = DateTime.UtcNow;
            p.Exited += delegate
            {
                bool quick = (DateTime.UtcNow - started).TotalSeconds < 20;
                failures = quick ? failures + 1 : 0;
                if (!stopped) Log.Write("airplay: UxPlay exited (code " + SafeExitCode(p) + ")" + (quick ? ", will retry" : ""));
            };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            lock (gate) proc = p;
            Log.Write("airplay: ready as \"" + name + "\" (UxPlay pid " + p.Id + ")");
        }

        private static string SafeExitCode(Process p)
        {
            try { return p.ExitCode.ToString(); } catch { return "?"; }
        }

        private void OnOutput(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            // Keep the log useful: connections and errors only.
            string l = line.ToLowerInvariant();
            if (l.Contains("connect") || l.Contains("error") || l.Contains("fail") || l.Contains("warning"))
                Log.Write("uxplay: " + (line.Length > 200 ? line.Substring(0, 200) : line));
        }

        private void Kill()
        {
            Process p;
            lock (gate) { p = proc; proc = null; }
            videoWindow = IntPtr.Zero;
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(); } catch { }
            try { p.Dispose(); } catch { }
        }

        /// <summary>A UxPlay left over from a previous BWOS run (e.g. after a crash) holds the ports.</summary>
        private void KillStale()
        {
            if (ExePath == null) return;
            foreach (var p in Process.GetProcessesByName("uxplay"))
            {
                try
                {
                    if (string.Equals(p.MainModule.FileName, ExePath, StringComparison.OrdinalIgnoreCase)) { p.Kill(); Log.Write("airplay: closed UxPlay left over from a previous run"); }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        // ------------------------------------------------------------------ the video window

        /// <summary>UxPlay opens its video window when a device starts mirroring and closes it when it stops.</summary>
        private void WatchWindow()
        {
            int pid;
            lock (gate) { if (proc == null) return; pid = proc.Id; }
            IntPtr win = FindVideoWindow(pid);
            if (win != IntPtr.Zero && videoWindow == IntPtr.Zero)
            {
                videoWindow = win;
                shellWasInFront = shell.ShellIsForeground();
                Native.WakeDisplays();
                Present(win);
                Log.Write("airplay: mirroring started");
            }
            else if (win == IntPtr.Zero && videoWindow != IntPtr.Zero)
            {
                videoWindow = IntPtr.Zero;
                Log.Write("airplay: mirroring stopped");
                if (shellWasInFront) { try { shell.ShowShell(); } catch { } }   // back to where you were
            }
        }

        private static IntPtr FindVideoWindow(int pid)
        {
            IntPtr found = IntPtr.Zero;
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (Native.IsWindowVisible(h) && Native.ProcessIdOf(h) == pid) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>Borderless over the whole screen BWOS is on, in front.</summary>
        private void Present(IntPtr win)
        {
            IntPtr reference = shell.Window != IntPtr.Zero ? shell.Window : win;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(MonitorFromWindow(reference, MONITOR_DEFAULTTONEAREST), ref mi)) return;
            var r = mi.rcMonitor;
            long style = GetWindowLongPtr(win, GWL_STYLE).ToInt64();
            style = (style & ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX)) | WS_POPUP;
            SetWindowLongPtr(win, GWL_STYLE, new IntPtr(style));
            SetWindowPos(win, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_FRAMECHANGED | SWP_SHOWWINDOW);
            Native.ForceForeground(win);
        }

        /// <summary>The controller's home button during mirroring: back to BWOS (mirroring keeps going behind it).</summary>
        public bool IsVideoForeground()
        {
            return videoWindow != IntPtr.Zero && Native.GetForegroundWindow() == videoWindow;
        }

        // ------------------------------------------------------------------ Win32

        private const int GWL_STYLE = -16;
        private const long WS_POPUP = 0x80000000L, WS_CAPTION = 0x00C00000L, WS_THICKFRAME = 0x00040000L,
            WS_SYSMENU = 0x00080000L, WS_MINIMIZEBOX = 0x00020000L, WS_MAXIMIZEBOX = 0x00010000L;
        private const uint SWP_FRAMECHANGED = 0x0020, SWP_SHOWWINDOW = 0x0040, MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
