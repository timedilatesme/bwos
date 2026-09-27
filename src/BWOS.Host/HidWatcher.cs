using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Bwos
{
    /// <summary>
    /// Background "home button" detection for controllers that are not XInput devices:
    /// PlayStation (DualShock 4 / DualSense) and Nintendo (Switch Pro / Joy-Con), read via
    /// Raw Input with RIDEV_INPUTSINK so it works while games are focused.
    /// Also owns the global keyboard hotkey (Ctrl+Alt+Home) as a fallback for any device.
    /// </summary>
    internal sealed class HidWatcher : NativeWindow, IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort UsagePage;
            public ushort Usage;
            public uint Flags;
            public IntPtr Target;
        }

        [StructLayout(LayoutKind.Explicit, Size = 32)]
        private struct RID_DEVICE_INFO
        {
            [FieldOffset(0)] public uint cbSize;
            [FieldOffset(4)] public uint dwType;
            [FieldOffset(8)] public uint VendorId;
            [FieldOffset(12)] public uint ProductId;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

        [DllImport("user32.dll")]
        private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

        [DllImport("user32.dll")]
        private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, ref RID_DEVICE_INFO data, ref uint size);

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_INPUT = 0x00FF;
        private const int WM_HOTKEY = 0x0312;
        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RID_INPUT = 0x10000003;
        private const uint RIDI_DEVICEINFO = 0x2000000b;
        private const uint RIM_TYPEHID = 2;
        private const int HotkeyId = 0xB05;

        private const uint Sony = 0x054C;
        private const uint Nintendo = 0x057E;

        private readonly Dictionary<IntPtr, uint[]> devices = new Dictionary<IntPtr, uint[]>();
        private readonly Dictionary<IntPtr, bool> homeWasDown = new Dictionary<IntPtr, bool>();
        private readonly int headerSize = IntPtr.Size == 8 ? 24 : 16;

        public event Action HomePressed;
        public event Action HotkeyPressed;

        public HidWatcher()
        {
            CreateHandle(new CreateParams { Caption = "BWOS HID sink" });

            var reg = new RAWINPUTDEVICE[2];
            reg[0].UsagePage = 0x01; reg[0].Usage = 0x05; reg[0].Flags = RIDEV_INPUTSINK; reg[0].Target = Handle; // gamepad
            reg[1].UsagePage = 0x01; reg[1].Usage = 0x04; reg[1].Flags = RIDEV_INPUTSINK; reg[1].Target = Handle; // joystick
            if (!RegisterRawInputDevices(reg, 2, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
                Log.Write("RegisterRawInputDevices failed: " + Marshal.GetLastWin32Error());

            // Ctrl + Alt + Home  (MOD_ALT=1, MOD_CONTROL=2, MOD_NOREPEAT=0x4000)
            if (!RegisterHotKey(Handle, HotkeyId, 0x1 | 0x2 | 0x4000, 0x24))
                Log.Write("Ctrl+Alt+Home hotkey is already taken by another app");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT)
            {
                try { HandleInput(m.LParam); }
                catch (Exception ex) { Log.Write("raw input error: " + ex.Message); }
            }
            else if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                var h = HotkeyPressed;
                if (h != null) h();
            }
            base.WndProc(ref m);
        }

        private void HandleInput(IntPtr hRaw)
        {
            uint size = 0;
            GetRawInputData(hRaw, RID_INPUT, IntPtr.Zero, ref size, (uint)headerSize);
            if (size == 0 || size > 4096) return;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(hRaw, RID_INPUT, buf, ref size, (uint)headerSize) != size) return;
                uint type = (uint)Marshal.ReadInt32(buf, 0);
                if (type != RIM_TYPEHID) return;
                IntPtr device = Marshal.ReadIntPtr(buf, 8);

                uint[] id = Identify(device);
                if (id == null || (id[0] != Sony && id[0] != Nintendo)) return;

                int sizeHid = Marshal.ReadInt32(buf, headerSize);
                int count = Marshal.ReadInt32(buf, headerSize + 4);
                if (sizeHid <= 0 || count <= 0) return;
                var report = new byte[sizeHid];
                // Only the most recent report matters for button state.
                Marshal.Copy(IntPtr.Add(buf, headerSize + 8 + sizeHid * (count - 1)), report, 0, sizeHid);

                bool down = IsHomeDown(id[0], id[1], report);
                bool was;
                homeWasDown.TryGetValue(device, out was);
                homeWasDown[device] = down;
                if (down && !was)
                {
                    var h = HomePressed;
                    if (h != null) h();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        private uint[] Identify(IntPtr device)
        {
            uint[] id;
            if (devices.TryGetValue(device, out id)) return id;
            var info = new RID_DEVICE_INFO();
            info.cbSize = 32;
            uint size = 32;
            if (GetRawInputDeviceInfo(device, RIDI_DEVICEINFO, ref info, ref size) == unchecked((uint)-1)) return null;
            id = new[] { info.VendorId, info.ProductId };
            devices[device] = id;
            return id;
        }

        /// <summary>Report layouts from the public DS4/DualSense/Switch reverse-engineering docs.</summary>
        private static bool IsHomeDown(uint vendor, uint product, byte[] r)
        {
            if (r.Length < 3) return false;
            byte reportId = r[0];
            if (vendor == Sony)
            {
                bool dualSense = product == 0x0CE6 || product == 0x0DF2;
                if (dualSense)
                {
                    if (reportId == 0x01 && r.Length >= 64) return (r[10] & 0x01) != 0; // USB
                    if (reportId == 0x01 && r.Length > 7) return (r[7] & 0x01) != 0;    // Bluetooth, simple
                    if (reportId == 0x31 && r.Length > 11) return (r[11] & 0x01) != 0;  // Bluetooth, full
                    return false;
                }
                if (reportId == 0x01 && r.Length > 7) return (r[7] & 0x01) != 0;        // DS4 USB / BT simple
                if (reportId == 0x11 && r.Length > 9) return (r[9] & 0x01) != 0;        // DS4 BT full
                return false;
            }
            if (vendor == Nintendo)
            {
                if (reportId == 0x3F && r.Length > 2) return (r[2] & 0x10) != 0;        // simple HID mode
                if ((reportId == 0x30 || reportId == 0x21) && r.Length > 4) return (r[4] & 0x10) != 0; // full mode
            }
            return false;
        }

        public void Dispose()
        {
            UnregisterHotKey(Handle, HotkeyId);
            DestroyHandle();
        }
    }
}
