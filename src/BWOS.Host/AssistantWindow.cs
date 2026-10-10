using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Bwos
{
    // One transient overlay works over Home, streaming pages and desktop apps.
    internal sealed class AssistantWindow : Form
    {
        private readonly VoiceService voice;
        private readonly Timer timer = new Timer();
        private Dictionary<string, object> state;
        private readonly Font heading = new Font("Segoe UI", 11, FontStyle.Bold), copy = new Font("Segoe UI", 9);
        private double phase;
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000080; return cp; } }
        public AssistantWindow(VoiceService voice)
        {
            this.voice = voice; FormBorderStyle = FormBorderStyle.None; TopMost = true; ShowInTaskbar = false;
            BackColor = Color.FromArgb(19, 25, 42); ForeColor = Color.FromArgb(227, 238, 250); Size = new Size(390, 90); DoubleBuffered = true;
            using (var path = new GraphicsPath())
            {
                const int d = 36;
                path.AddArc(0, 0, d, d, 180, 90); path.AddArc(Width - d, 0, d, d, 270, 90);
                path.AddArc(Width - d, Height - d, d, d, 0, 90); path.AddArc(0, Height - d, d, d, 90, 90); path.CloseFigure(); Region = new Region(path);
            }
            MouseDown += delegate(object sender, MouseEventArgs e) { if (e.X >= Width - 34) voice.HideOverlay(); };
            timer.Interval = 100;
            timer.Tick += delegate {
                state = voice.Describe(false);
                if (!Json.Bool(state, "overlayVisible", false)) { if (Visible) Hide(); return; }
                if (!Visible) { var area = Screen.PrimaryScreen.WorkingArea; Location = new Point(area.Right - Width - 24, area.Bottom - Height - 84); Show(); }
                phase += .3; Invalidate();
            };
            timer.Start();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); PaintState(e.Graphics, state ?? new Dictionary<string, object>(), phase);
        }
        internal void PaintState(Graphics graphics, Dictionary<string, object> data, double frame)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            bool speaking = Json.Bool(data, "speaking", false), thinking = Json.Bool(data, "thinking", false);
            float energy = Math.Min(1, Json.Int(data, "inputLevel", 0) / 25f);
            float radius = 21 + energy * 4 + (float)Math.Sin(frame) * (speaking ? 3 : 1);
            var blue = thinking ? Color.FromArgb(164, 145, 255) : Color.FromArgb(112, 218, 218);
            for (int i = 8; i > 0; i--) using (var halo = new SolidBrush(Color.FromArgb(12, blue))) graphics.FillEllipse(halo, 43 - radius - i, 45 - radius - i, (radius + i) * 2, (radius + i) * 2);
            using (var glow = new LinearGradientBrush(new RectangleF(20, 20, 48, 50), Color.FromArgb(145, 128, 248), blue, (float)(frame * 22)))
                graphics.FillEllipse(glow, 43 - radius, 45 - radius, radius * 2, radius * 2);
            string label = speaking ? "Speaking" : thinking ? "Working on it" : Json.Bool(data, "listening", false) ? "I'm listening" : "Voice paused";
            TextRenderer.DrawText(graphics, label, heading, new Rectangle(83, 20, 263, 24), ForeColor, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            string line = speaking ? Json.Str(data, "reply", "") : Json.Str(data, "overlayText", "Say a command");
            TextRenderer.DrawText(graphics, line, copy, new Rectangle(83, 48, 270, 23), Color.FromArgb(177, 194, 218), TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(graphics, "×", copy, new Rectangle(359, 9, 23, 24), Color.FromArgb(177, 194, 218));
        }
        protected override void Dispose(bool disposing) { if (disposing) { timer.Dispose(); heading.Dispose(); copy.Dispose(); } base.Dispose(disposing); }
    }
}
