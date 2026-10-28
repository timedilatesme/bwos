using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Bwos
{
    /// <summary>
    /// Polls XInput in the background (works while games are fullscreen and while the
    /// BWOS window is hidden) and raises <see cref="Triggered"/> when the configured
    /// "open BWOS" button is pressed. Also feeds raw state to the input bridge.
    /// </summary>
    internal sealed class GamepadService
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint GetStateFn(uint index, out XInputState state);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint GetBatteryFn(uint index, byte devType, out XInputBatteryInformation info);

        private readonly GetStateFn getState;
        private readonly GetBatteryFn getBattery;
        private readonly bool guideSupported;
        private readonly ushort[] previous = new ushort[4];
        private readonly bool[] connected = new bool[4];
        private readonly DateTime[] nextProbe = new DateTime[4];
        private readonly DateTime[] backHeldSince = new DateTime[4];
        private readonly bool[] backHoldFired = new bool[4];
        private DateTime lastTrigger = DateTime.MinValue;
        private Thread thread;
        private volatile bool running;

        public event Action Triggered;
        public event Action<int, bool> ConnectionChanged;
        /// <summary>Called every poll with (slot, state, previousButtons).</summary>
        public Action<int, XInputState, ushort> StateSink;

        /// <summary>"guide", "back+start" or "hold-back".</summary>
        public string TriggerMode = "guide";

        public bool GuideSupported { get { return guideSupported; } }

        public GamepadService()
        {
            IntPtr dll = Native.LoadLibrary("xinput1_4.dll");
            if (dll == IntPtr.Zero) dll = Native.LoadLibrary("xinput1_3.dll");
            if (dll == IntPtr.Zero) dll = Native.LoadLibrary("xinput9_1_0.dll");
            if (dll == IntPtr.Zero) throw new InvalidOperationException("XInput is not available on this system.");

            // Ordinal 100 = XInputGetStateEx: identical to XInputGetState but includes the Guide bit.
            IntPtr ex = Native.GetProcAddressByOrdinal(dll, new IntPtr(100));
            IntPtr fn = ex != IntPtr.Zero ? ex : Native.GetProcAddress(dll, "XInputGetState");
            guideSupported = ex != IntPtr.Zero;
            getState = (GetStateFn)Marshal.GetDelegateForFunctionPointer(fn, typeof(GetStateFn));

            IntPtr bat = Native.GetProcAddress(dll, "XInputGetBatteryInformation");
            if (bat != IntPtr.Zero)
                getBattery = (GetBatteryFn)Marshal.GetDelegateForFunctionPointer(bat, typeof(GetBatteryFn));
        }

        public void Start()
        {
            if (running) return;
            running = true;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "BWOS gamepad poll";
            thread.Priority = ThreadPriority.AboveNormal; // keep polling smooth under game load
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            if (thread != null && thread != Thread.CurrentThread) thread.Join(500);
        }

        public List<Dictionary<string, object>> Describe()
        {
            var list = new List<Dictionary<string, object>>();
            for (int i = 0; i < 4; i++)
            {
                if (!connected[i]) continue;
                var d = new Dictionary<string, object>();
                d["slot"] = i;
                string battery = "unknown";
                if (getBattery != null)
                {
                    XInputBatteryInformation info;
                    if (getBattery((uint)i, 0, out info) == 0)
                    {
                        // BatteryType: 1 = wired, 2 = alkaline, 3 = NiMH; Level: 0 empty .. 3 full
                        if (info.BatteryType == 1) battery = "wired";
                        else if (info.BatteryType == 0) battery = "disconnected";
                        else battery = new[] { "empty", "low", "medium", "full" }[Math.Min((int)info.BatteryLevel, 3)];
                    }
                }
                d["battery"] = battery;
                list.Add(d);
            }
            return list;
        }

        private void Loop()
        {
            while (running)
            {
                bool any = false;
                for (int i = 0; i < 4; i++)
                {
                    // Polling empty slots is expensive in XInput; probe them only every ~1s.
                    if (!ProbeDue(connected[i], DateTime.UtcNow, ref nextProbe[i])) continue;

                    XInputState st;
                    bool ok = getState((uint)i, out st) == 0;
                    if (ok != connected[i])
                    {
                        connected[i] = ok;
                        previous[i] = 0;
                        backHeldSince[i] = DateTime.MinValue;
                        backHoldFired[i] = false;
                        var cc = ConnectionChanged;
                        int slot = i; // C# 5 for-loop variables are shared by closures
                        if (cc != null) BackgroundWork.Queue("controller connection", delegate { cc(slot, ok); });
                    }
                    if (!ok) continue;
                    any = true;
                    ushort now = st.Gamepad.Buttons;
                    ushort prev = previous[i];
                    CheckTrigger(i, now, prev);
                    var sink = StateSink;
                    if (sink != null)
                    {
                        try { sink(i, st, prev); }
                        catch (Exception ex) { Log.Write("bridge error: " + ex.Message); }
                    }
                    previous[i] = now;
                }
                Thread.Sleep(any ? 8 : 50);
            }
        }

        private void CheckTrigger(int slot, ushort now, ushort prev)
        {
            string mode = TriggerMode;
            if (mode == "guide" && !guideSupported) mode = "back+start";
            bool fire = false;

            if (mode == "guide")
            {
                fire = Pressed(now, prev, XButtons.Guide);
            }
            else if (mode == "back+start")
            {
                ushort combo = XButtons.Back | XButtons.Start;
                fire = (now & combo) == combo && (prev & combo) != combo;
            }
            else if (mode == "hold-back")
            {
                bool backOnly = (now & ~XButtons.Guide & 0xFFFF) == XButtons.Back;
                if (backOnly)
                {
                    if (backHeldSince[slot] == DateTime.MinValue) { backHeldSince[slot] = DateTime.UtcNow; backHoldFired[slot] = false; }
                    else if (!backHoldFired[slot] && (DateTime.UtcNow - backHeldSince[slot]).TotalMilliseconds > 700)
                    {
                        backHoldFired[slot] = true;
                        fire = true;
                    }
                }
                else { backHeldSince[slot] = DateTime.MinValue; backHoldFired[slot] = false; }
            }

            if (!fire) return;
            if ((DateTime.UtcNow - lastTrigger).TotalMilliseconds < 700) return;
            lastTrigger = DateTime.UtcNow;
            var t = Triggered;
            if (t != null) BackgroundWork.Queue("controller home", delegate { t(); });
        }

        internal static bool ProbeDue(bool isConnected, DateTime now, ref DateTime next)
        {
            if (isConnected) return true;
            if (now < next) return false;
            next = now.AddSeconds(1);
            return true;
        }

        public static bool Pressed(ushort now, ushort prev, ushort mask)
        {
            return (now & mask) != 0 && (prev & mask) == 0;
        }
    }
}
