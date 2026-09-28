// Win32 / XInput interop. Kept in one place so the rest of the host stays readable.
// NOTE: the host is compiled with the C# 5 compiler that ships inside Windows
// (.NET Framework 4.8), so avoid C# 6+ syntax ($"", ?., nameof, => members).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Bwos
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XInputBatteryInformation
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    internal static class XButtons
    {
        public const ushort DpadUp = 0x0001;
        public const ushort DpadDown = 0x0002;
        public const ushort DpadLeft = 0x0004;
        public const ushort DpadRight = 0x0008;
        public const ushort Start = 0x0010;
        public const ushort Back = 0x0020;
        public const ushort LeftThumb = 0x0040;
        public const ushort RightThumb = 0x0080;
        public const ushort LeftShoulder = 0x0100;
        public const ushort RightShoulder = 0x0200;
        public const ushort Guide = 0x0400; // only reported by XInputGetStateEx (ordinal 100)
        public const ushort A = 0x1000;
        public const ushort B = 0x2000;
        public const ushort X = 0x4000;
        public const ushort Y = 0x8000;
    }

    internal static class Native
    {
        public const int SW_HIDE = 0;
        public const int SW_SHOWNORMAL = 1;
        public const int SW_MINIMIZE = 6;
        public const int SW_RESTORE = 9;
        public const int SW_SHOW = 5;

        public const uint INPUT_MOUSE = 0;
        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_WHEEL = 0x0800;
        public const uint MOUSEEVENTF_HWHEEL = 0x1000;

        public const uint WM_CLOSE = 0x0010;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", EntryPoint = "GetProcAddress")]
        public static extern IntPtr GetProcAddressByOrdinal(IntPtr hModule, IntPtr ordinal);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int dwProcessId);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        public const uint ES_DISPLAY_REQUIRED = 0x00000002;

        [DllImport("kernel32.dll")]
        public static extern uint SetThreadExecutionState(uint esFlags);

        /// <summary>Turns the displays back on if Windows switched them off (controller input doesn't count as activity).</summary>
        public static void WakeDisplays()
        {
            SetThreadExecutionState(ES_DISPLAY_REQUIRED);
        }

        public static string WindowText(IntPtr hWnd)
        {
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string WindowClass(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>Visible, titled, unowned top-level windows belonging to any of the given processes.</summary>
        public static List<IntPtr> TopLevelWindows(ICollection<int> pids)
        {
            var result = new List<IntPtr>();
            if (pids == null || pids.Count == 0) return result;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains((int)pid)) return true;
                if (!IsWindowVisible(h)) return true;
                if (GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
                if (WindowText(h).Length == 0) return true;
                result.Add(h);
                return true;
            }, IntPtr.Zero);
            return result;
        }

        public static int ProcessIdOf(IntPtr hWnd)
        {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            return (int)pid;
        }

        /// <summary>
        /// Windows only lets the process that received the last input event steal focus.
        /// A background tray app never does, so we inject an empty mouse event first
        /// (the same trick PowerToys Run uses), then attach to the foreground thread.
        /// </summary>
        public static void ForceForeground(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;
            var nudge = new INPUT[1];
            nudge[0].type = INPUT_MOUSE;
            SendInput(1, nudge, Marshal.SizeOf(typeof(INPUT)));

            if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);

            IntPtr fg = GetForegroundWindow();
            uint dummy;
            uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out dummy);
            uint me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try
            {
                BringWindowToTop(hWnd);
                ShowWindow(hWnd, SW_SHOW);
                SetForegroundWindow(hWnd);
            }
            finally
            {
                if (attached) AttachThreadInput(me, fgThread, false);
            }
        }

        public static void Key(ushort vk, bool down)
        {
            var inp = new INPUT[1];
            inp[0].type = INPUT_KEYBOARD;
            inp[0].u.ki.wVk = vk;
            uint flags = down ? 0u : KEYEVENTF_KEYUP;
            // Arrow keys & friends are "extended" keys; without the flag some apps see numpad keys.
            if ((vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E) flags |= KEYEVENTF_EXTENDEDKEY;
            inp[0].u.ki.dwFlags = flags;
            SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
        }

        public static void Tap(ushort vk)
        {
            Key(vk, true);
            Key(vk, false);
        }

        public static void Mouse(uint flags, int dx, int dy, int data)
        {
            var inp = new INPUT[1];
            inp[0].type = INPUT_MOUSE;
            inp[0].u.mi.dx = dx;
            inp[0].u.mi.dy = dy;
            inp[0].u.mi.mouseData = unchecked((uint)data);
            inp[0].u.mi.dwFlags = flags;
            SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
        }
    }
}
