using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Bwos
{
    internal static class Program
    {
        private const string MutexName = "BWOS.SingleInstance";
        private const string ShowEventName = "BWOS.ShowShell";

        [STAThread]
        private static void Main(string[] args)
        {
            bool background = Array.IndexOf(args, "--background") >= 0;
            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    // Already running (e.g. from autostart): ask that instance to show the UI.
                    if (!background)
                    {
                        EventWaitHandle ev;
                        if (EventWaitHandle.TryOpenExisting(ShowEventName, out ev)) using (ev) ev.Set();
                    }
                    return;
                }

                // Exes built by the in-box compiler run in legacy TLS mode; allow modern TLS for HTTPS lookups.
                System.Net.ServicePointManager.SecurityProtocol |=
                    System.Net.SecurityProtocolType.Tls12 | (System.Net.SecurityProtocolType)12288 /* TLS 1.3 */;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Log.Write("FATAL: " + e.ExceptionObject);
                };
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    Log.Write("UI thread error: " + e.Exception);
                };

                App app;
                try
                {
                    app = new App();
                    app.Start();
                    // --dev: print the UI address (with its session token) to the log for browser debugging.
                    app.Dev = Array.IndexOf(args, "--dev") >= 0;
                    if (app.Dev) Log.Write("DEV UI: " + app.Server.BaseUrl + "#t=" + app.Server.Token);
                }
                catch (Exception ex)
                {
                    Log.Write("startup failed: " + ex);
                    MessageBox.Show("BWOS could not start:\n\n" + ex.Message, "BWOS", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                using (var tray = new TrayContext(app))
                using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                using (var stopEvent = new ManualResetEvent(false))
                {
                    var waiter = new Thread(delegate()
                    {
                        while (WaitHandle.WaitAny(new WaitHandle[] { stopEvent, showEvent }) == 1)
                        {
                            try { app.Shell.ShowShell(); } catch (Exception ex) { Log.Write("show failed: " + ex.Message); }
                        }
                    });
                    waiter.IsBackground = true;
                    waiter.Start();

                    if (!background)
                        ThreadPool.QueueUserWorkItem(delegate
                        {
                            try { app.Shell.ShowShell(); } catch (Exception ex) { Log.Write("show failed: " + ex.Message); }
                        });

                    try { Application.Run(tray); }
                    finally { stopEvent.Set(); waiter.Join(); app.Stop(); }
                }
            }
        }
    }

    /// <summary>Notification-area icon. BWOS has no other desktop window.</summary>
    internal sealed class TrayContext : ApplicationContext
    {
        private readonly App app;
        private readonly NotifyIcon icon;
        private readonly HidWatcher hid;
        private readonly ToolStripMenuItem autostartItem;
        private readonly Control uiThread = new Control(); // marshals calls from worker threads
        private readonly PowerModeChangedEventHandler powerChanged;
        private readonly SessionSwitchEventHandler sessionSwitched;
        private readonly PlaybackWindow playback;
        private readonly AssistantWindow assistant;

        public TrayContext(App app)
        {
            this.app = app;
            playback = new PlaybackWindow(app);
            assistant = new AssistantWindow(app.Voice);
            uiThread.CreateControl();
            IntPtr force = uiThread.Handle; // make sure the handle exists on this (UI) thread
            app.RequestExit = delegate { uiThread.BeginInvoke(new Action(ExitThread)); };

            hid = new HidWatcher();
            hid.HomePressed += delegate { ThreadPool.QueueUserWorkItem(delegate { app.OnTrigger("PS/Switch home"); }); };
            hid.HotkeyPressed += delegate { ThreadPool.QueueUserWorkItem(delegate { app.OnTrigger("Ctrl+Alt+Home"); }); };

            // Waking the PC (or signing in right after) lands in BWOS.
            powerChanged = delegate(object s, PowerModeChangedEventArgs e)
            {
                if (e.Mode == PowerModes.Resume) ThreadPool.QueueUserWorkItem(delegate { app.OnResumed(); });
            };
            sessionSwitched = delegate(object s, SessionSwitchEventArgs e)
            {
                if (e.Reason == SessionSwitchReason.SessionUnlock) ThreadPool.QueueUserWorkItem(delegate { app.OnUnlocked(); });
            };
            SystemEvents.PowerModeChanged += powerChanged;
            SystemEvents.SessionSwitch += sessionSwitched;

            var menu = new ContextMenuStrip();
            var open = new ToolStripMenuItem("Open BWOS", null, delegate { BackgroundWork.Queue("open shell", delegate { app.Shell.ShowShell(); }); });
            open.Font = new Font(open.Font, FontStyle.Bold);
            menu.Items.Add(open);
            menu.Items.Add(new ToolStripSeparator());
            autostartItem = new ToolStripMenuItem("Start with Windows", null, delegate
            {
                app.Autostart = !app.Autostart;
                autostartItem.Checked = app.Autostart;
            });
            menu.Items.Add(autostartItem);
            menu.Items.Add(new ToolStripMenuItem("Rescan game library", null, delegate { BackgroundWork.Queue("library refresh", delegate { app.Library.Refresh(); }); }));
            menu.Items.Add(new ToolStripMenuItem("Open settings folder", null, delegate { Process.Start("explorer.exe", "\"" + Paths.DataDir + "\""); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit BWOS", null, delegate
            {
                try { app.Shell.CloseShell(); } catch { }
                ExitThread();
            }));
            menu.Opening += delegate { autostartItem.Checked = app.Autostart; };

            icon = new NotifyIcon();
            try { icon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { icon.Icon = SystemIcons.Application; }
            icon.Text = "BWOS – press the controller's home button";
            icon.ContextMenuStrip = menu;
            icon.DoubleClick += delegate { BackgroundWork.Queue("open shell", delegate { app.Shell.ShowShell(); }); };
            icon.Visible = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.PowerModeChanged -= powerChanged;
                SystemEvents.SessionSwitch -= sessionSwitched;
                icon.Visible = false;
                icon.Dispose();
                hid.Dispose();
                uiThread.Dispose();
                playback.Dispose();
                assistant.Dispose();
            }
            base.Dispose(disposing);
        }
    }

}
