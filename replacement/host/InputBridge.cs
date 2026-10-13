using System;

namespace Bwos
{
    /// <summary>
    /// Streaming websites don't understand controllers. While the BWOS window shows a streaming site (not the BWOS home screen), the
    /// bridge turns controller input into real keyboard + mouse input; the bwos-tv extension turns those keys into on-page navigation:
    ///   D-pad / left stick ... arrow keys (auto-repeat)     A ......... Enter (click if the cursor was just moved)
    ///   right stick ......... mouse cursor                  B ......... Escape
    ///   RT / LT ............. volume up / down*             X ......... Space (play / pause)
    ///   R3 .................. left click                    Y ......... F (fullscreen toggle on most players)
    ///   LB / RB ............. Shift+Tab / Tab               View/Back . browser back (Alt+Left)
    ///   Start (Menu) ........ F23 → "type on your phone" QR code
    /// Nothing is injected while a game or the BWOS interface itself is focused.
    /// (*) Trigger volume also works on the BWOS home screen – anywhere the BWOS window is in front.
    /// </summary>
    internal sealed class InputBridge
    {
        private const ushort VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_LEFT = 0x25, VK_UP = 0x26,
            VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_TAB = 0x09, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_F = 0x46, VK_F23 = 0x86;

        public Action<string> VolumeAction;
        private void Volume(bool up) { if(VolumeAction != null) VolumeAction(up?"volup":"voldown"); }
        private readonly ShellManager shell;
        private readonly Func<bool> enabled;
        private readonly int[] heldDir = new int[4];
        private readonly DateTime[] nextRepeat = new DateTime[4];
        private const ushort VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;
        private readonly int[] volDir = new int[4];
        private readonly DateTime[] volNext = new DateTime[4];
        private bool shellFront;
        private DateTime cursorMovedAt = DateTime.MinValue;
        private double carryX, carryY;
        private DateTime lastFrame = DateTime.UtcNow;
        private bool active;

        public InputBridge(ShellManager shell, Func<bool> enabled)
        {
            this.shell = shell;
            this.enabled = enabled;
        }

        public void OnState(int slot, XInputState st, ushort prev)
        {
            // Check focus at most every ~100 ms; GetForegroundWindow is cheap, pid lookup is cached.
            if ((DateTime.UtcNow - lastFrame).TotalMilliseconds > 100)
            {
                shellFront = shell.ShellIsForeground();
                active = shellFront && enabled() && shell.ServiceIsForeground();
                lastFrame = DateTime.UtcNow;
            }

            var gp = st.Gamepad;
            ushort now = gp.Buttons;

            // ----- triggers = system volume, whenever the BWOS window is in front (never in games)
            int vol = !shellFront ? 0 : gp.RightTrigger > 120 ? 1 : gp.LeftTrigger > 120 ? -1 : 0;
            if (vol != volDir[slot])
            {
                volDir[slot] = vol;
                if (vol != 0) { Volume(vol > 0); volNext[slot] = DateTime.UtcNow.AddMilliseconds(380); }
            }
            else if (vol != 0 && DateTime.UtcNow >= volNext[slot])
            {
                Volume(vol > 0);
                volNext[slot] = DateTime.UtcNow.AddMilliseconds(110);
            }

            if (!active)
            {
                heldDir[slot] = 0;
                return;
            }

            // ----- directions (d-pad or left stick) with TV-style auto-repeat
            int dir = 0;
            if ((now & XButtons.DpadUp) != 0 || gp.ThumbLY > 20000) dir = VK_UP;
            else if ((now & XButtons.DpadDown) != 0 || gp.ThumbLY < -20000) dir = VK_DOWN;
            else if ((now & XButtons.DpadLeft) != 0 || gp.ThumbLX < -20000) dir = VK_LEFT;
            else if ((now & XButtons.DpadRight) != 0 || gp.ThumbLX > 20000) dir = VK_RIGHT;
            if (dir != heldDir[slot])
            {
                heldDir[slot] = dir;
                if (dir != 0)
                {
                    Native.Tap((ushort)dir);
                    nextRepeat[slot] = DateTime.UtcNow.AddMilliseconds(400);
                }
            }
            else if (dir != 0 && DateTime.UtcNow >= nextRepeat[slot])
            {
                Native.Tap((ushort)dir);
                nextRepeat[slot] = DateTime.UtcNow.AddMilliseconds(90);
            }

            // ----- buttons
            if (GamepadService.Pressed(now, prev, XButtons.A))
            {
                if ((DateTime.UtcNow - cursorMovedAt).TotalSeconds < 2.5) Click(Native.MOUSEEVENTF_LEFTDOWN, Native.MOUSEEVENTF_LEFTUP);
                else Native.Tap(VK_RETURN);
            }
            if (GamepadService.Pressed(now, prev, XButtons.B)) Native.Tap(VK_ESCAPE);
            if (GamepadService.Pressed(now, prev, XButtons.X)) Native.Tap(VK_SPACE);
            if (GamepadService.Pressed(now, prev, XButtons.Y)) Native.Tap(VK_F);
            if (GamepadService.Pressed(now, prev, XButtons.Start)) Native.Tap(VK_F23); // phone keyboard (bwos-tv shows the QR code)
            if (GamepadService.Pressed(now, prev, XButtons.RightShoulder)) Native.Tap(VK_TAB);
            if (GamepadService.Pressed(now, prev, XButtons.LeftShoulder))
            {
                Native.Key(VK_SHIFT, true); Native.Tap(VK_TAB); Native.Key(VK_SHIFT, false);
            }
            if (GamepadService.Pressed(now, prev, XButtons.Back))
            {
                Native.Key(VK_MENU, true); Native.Tap(VK_LEFT); Native.Key(VK_MENU, false);
            }
            if (GamepadService.Pressed(now, prev, XButtons.RightThumb)) Click(Native.MOUSEEVENTF_LEFTDOWN, Native.MOUSEEVENTF_LEFTUP);


            // ----- right stick = mouse (quadratic response curve, sub-pixel carry)
            double sx = Stick(gp.ThumbRX), sy = -Stick(gp.ThumbRY);
            if (sx != 0 || sy != 0)
            {
                double speed = 22.0; // px per poll at full tilt (~120 polls/s)
                carryX += sx * Math.Abs(sx) * speed;
                carryY += sy * Math.Abs(sy) * speed;
                int mx = (int)carryX, my = (int)carryY;
                carryX -= mx; carryY -= my;
                if (mx != 0 || my != 0)
                {
                    Native.Mouse(Native.MOUSEEVENTF_MOVE, mx, my, 0);
                    cursorMovedAt = DateTime.UtcNow;
                }
            }
        }

        private static double Stick(short v)
        {
            const double dead = 7849;
            double a = Math.Abs((double)v);
            if (a < dead) return 0;
            return Math.Sign(v) * Math.Min(1.0, (a - dead) / (32767 - dead));
        }

        private static void Click(uint down, uint up)
        {
            Native.Mouse(down, 0, 0, 0);
            Native.Mouse(up, 0, 0, 0);
        }
    }
}
