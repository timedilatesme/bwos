using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Bwos
{
    // A no-activation desktop companion. It never takes focus from a game or other app.
    internal sealed class PlaybackWindow : Form
    {
        private readonly App app;
        private readonly Timer timer = new Timer();
        private readonly ToolTip tips = new ToolTip();
        private readonly Label title = new Label(), caption = new Label();
        private readonly Button toggle, next, collapse;
        private Dictionary<string, object> player;
        private string selectedId;
        private bool compact, moved;
        private Point drag, start;
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000080; return cp; } }
        public PlaybackWindow(App app)
        {
            this.app = app; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            BackColor = Color.FromArgb(26, 31, 45); ForeColor = Color.FromArgb(231, 242, 252);
            Font = new Font("Segoe UI", 10); Size = new Size(420, 82); DoubleBuffered = true;
            caption.SetBounds(19, 13, 225, 19); caption.ForeColor = Color.FromArgb(139, 213, 202); caption.Font = new Font("Segoe UI", 8);
            title.SetBounds(19, 34, 220, 28); title.AutoEllipsis = true;
            Controls.Add(caption); Controls.Add(title);
            toggle = MakeButton("Ⅱ", "Play or pause", 248, delegate { ControlPlayer(FailedPlayer() ? "retry" : Json.Bool(player, "paused", false) ? "resume" : "pause"); });
            MakeButton("↗", "Reopen player", 290, delegate { ControlPlayer("focus"); });
            MakeButton("×", "Stop and close player", 332, delegate { ControlPlayer("stop"); });
            next = MakeButton("›", "Next player", 375, delegate {
                var items = (app.Playback.Describe() as Dictionary<string, object>[]).OrderBy(p => Json.Str(p, "id", "")).ToArray();
                if (items.Length > 0) { int index = Array.FindIndex(items, p => Json.Str(p, "id", "") == selectedId); selectedId = Json.Str(items[(index + 1) % items.Length], "id", ""); }
                UpdatePlayer();
            });
            collapse = MakeButton("−", "Collapse or expand", 375, delegate { compact = !compact; LayoutCloud(); });
            collapse.Top = 2; collapse.Height = 23; next.Top = 38;
            MouseDown += BeginDrag; MouseMove += DragCloud; caption.MouseDown += BeginDrag; caption.MouseMove += DragCloud;
            title.MouseDown += BeginDrag; title.MouseMove += DragCloud;
            timer.Interval = 1000; timer.Tick += delegate { UpdatePlayer(); }; timer.Start();
            LayoutCloud();
        }
        private Button MakeButton(string text, string hint, int x, EventHandler click)
        {
            var button = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(39, 46, 63), ForeColor = ForeColor, TabStop = false };
            button.FlatAppearance.BorderSize = 0; button.SetBounds(x, 26, 36, 32); button.Click += click;
            Controls.Add(button); tips.SetToolTip(button, hint); return button;
        }
        private void ControlPlayer(string action) { if (player != null) app.Playback.Control(Json.Str(player, "id", ""), action); }
        private bool FailedPlayer() { return player != null && Json.Str(player, "error", "") != "" && !Json.Bool(player, "canControl", true); }
        private void BeginDrag(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { drag = Cursor.Position; start = Location; } }
        private void DragCloud(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            moved = true; var position = Cursor.Position; Location = new Point(start.X + position.X - drag.X, start.Y + position.Y - drag.Y);
        }
        private void LayoutCloud()
        {
            foreach (Control control in Controls) control.Visible = !compact;
            collapse.Visible = true; collapse.Text = compact ? "♫" : "−";
            collapse.Left = compact ? 6 : 375; collapse.Top = compact ? 7 : 2; collapse.Height = compact ? 34 : 23;
            Size = compact ? new Size(48, 48) : new Size(420, 82);
            using (var path = new GraphicsPath())
            {
                int radius = 30; path.AddArc(0, 0, radius, radius, 180, 90); path.AddArc(Width - radius, 0, radius, radius, 270, 90);
                path.AddArc(Width - radius, Height - radius, radius, radius, 0, 90); path.AddArc(0, Height - radius, radius, radius, 90, 90); path.CloseFigure();
                var previous = Region; Region = new Region(path); if (previous != null) previous.Dispose();
            }
            if (!moved) { var area = Screen.PrimaryScreen.WorkingArea; Location = new Point(area.Left + 24, area.Bottom - Height - 24); }
        }
        private void UpdatePlayer()
        {
            var items = (app.Playback.Describe() as Dictionary<string, object>[]).Where(p => !Json.Bool(p, "local", false)).ToArray();
            if (items == null || items.Length == 0 || app.Shell.ShellIsForeground()) { if (Visible) Hide(); return; }
            player = items.FirstOrDefault(p => Json.Str(p, "id", "") == selectedId) ?? items[0];
            selectedId = Json.Str(player, "id", "");
            int index = Array.FindIndex(items.OrderBy(p => Json.Str(p, "id", "")).ToArray(), p => Json.Str(p, "id", "") == selectedId);
            title.Text = Json.Str(player, "title", "Media");
            caption.Text = FailedPlayer() ? "PLAYBACK FAILED · RETRY ↻" : Json.Bool(player, "paused", false) ? "PAUSED" : "NOW PLAYING";
            if (items.Length > 1) caption.Text += "  ·  " + (index + 1) + "/" + items.Length;
            toggle.Text = FailedPlayer() ? "↻" : Json.Bool(player, "paused", false) ? "▶" : "Ⅱ";
            toggle.Enabled = FailedPlayer() || Json.Bool(player, "canControl", true);
            tips.SetToolTip(toggle, FailedPlayer() ? "Retry this player" : "Play or pause");
            next.Enabled = items.Length > 1;
            if (!Visible) Show();
        }
        protected override void Dispose(bool disposing) { if (disposing) { timer.Dispose(); tips.Dispose(); Font.Dispose(); caption.Font.Dispose(); } base.Dispose(disposing); }
    }
}
