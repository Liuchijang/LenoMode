using System;
using System.Drawing;
using System.Reflection;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace LenoMode
{
    /// <summary>Notification-area icon with an owner-drawn menu in the app's Nothing style.</summary>
    sealed class Tray : IDisposable
    {
        readonly WinForms.NotifyIcon icon;
        readonly WinForms.ContextMenuStrip menu;
        readonly TrayItem header, label, open, overlay, exit;
        readonly TrayItem[] modes = new TrayItem[3];

        public event Action OpenRequested, ExitRequested, OverlayRequested;
        public event Action<ThermalMode> ModeRequested;

        public Tray()
        {
            var r = new TrayRenderer();
            menu = new WinForms.ContextMenuStrip
            {
                Renderer = r, ShowImageMargin = false, ShowCheckMargin = false,
                Padding = new WinForms.Padding(0, r.Px(4), 0, r.Px(4)),
                // ToolStripDropDownMenu sizes its width from item text metrics; owner-drawn rows need a floor.
                MinimumSize = new Size(r.Px(220), 0),
            };
            header = Add(r, TrayItem.KindHeader, 46, null);
            header.Enabled = false;
            Separator(r);
            label = Add(r, TrayItem.KindLabel, 22, null);
            label.Enabled = false;
            for (int i = 0; i < 3; i++)
            {
                var m = (ThermalMode)i;
                modes[i] = Add(r, TrayItem.KindMode, 27, delegate { if (ModeRequested != null) ModeRequested(m); });
                modes[i].Right = "CTRL+ALT+" + (i + 1);
            }
            Separator(r);
            overlay = Add(r, TrayItem.KindToggle, 27, delegate { Raise(OverlayRequested); });
            Separator(r);
            open = Add(r, TrayItem.KindAction, 26, delegate { Raise(OpenRequested); });
            exit = Add(r, TrayItem.KindAction, 26, delegate { Raise(ExitRequested); });
            exit.Muted = true;

            icon = new WinForms.NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location),
                ContextMenuStrip = menu,
                Visible = true,
            };
            icon.MouseClick += (s, e) => { if (e.Button == WinForms.MouseButtons.Left) Raise(OpenRequested); };
        }

        TrayItem Add(TrayRenderer r, int kind, int height, EventHandler click)
        {
            var it = new TrayItem { Kind = kind, AutoSize = false, Size = new Size(r.Px(220), r.Px(height)) };
            if (click != null) it.Click += click;
            menu.Items.Add(it);
            return it;
        }

        void Separator(TrayRenderer r)
        {
            menu.Items.Add(new TraySeparator { AutoSize = false, Size = new Size(r.Px(220), r.Px(7)) });
        }

        static void Raise(Action a) { if (a != null) a(); }

        public void Update(ThermalMode current, bool overlayOn)
        {
            header.Text = "LENOMODE";
            header.Sub = L.F("tray.running", L.ModeName(current));
            label.Text = L.S("sec.thermal");
            for (int i = 0; i < 3; i++)
            {
                modes[i].Text = L.ModeName((ThermalMode)i);
                modes[i].On = (int)current == i;
            }
            overlay.Text = L.S("tray.overlay").ToUpperInvariant();
            overlay.Right = L.S(overlayOn ? "tray.on" : "tray.off");
            overlay.On = overlayOn;
            open.Text = L.S("tray.open").ToUpperInvariant();
            exit.Text = L.S("tray.exit").ToUpperInvariant();
            menu.Invalidate();
            string tip = "LenoMode · " + L.ModeName(current);
            icon.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
            menu.Dispose();
        }
    }

    sealed class TrayItem : WinForms.ToolStripMenuItem
    {
        public const int KindHeader = 0, KindLabel = 1, KindMode = 2, KindToggle = 3, KindAction = 4;
        public int Kind;
        public string Sub, Right;
        public bool On, Muted;

        // The drop-down sizes itself from preferred sizes, not from Size, so fixed rows must report it here.
        public override Size GetPreferredSize(Size constrainingSize) { return Size; }
    }

    sealed class TraySeparator : WinForms.ToolStripSeparator
    {
        public override Size GetPreferredSize(Size constrainingSize) { return Size; }
    }

    /// <summary>Draws every menu item itself: monochrome, mono labels, red only for the active dot / toggle.</summary>
    sealed class TrayRenderer : WinForms.ToolStripRenderer
    {
        readonly float scale;
        readonly Color bg, hover, fg, muted, outline, red;
        readonly Font mono, monoBold, monoSmall, title;

        public TrayRenderer()
        {
            // The process is DPI aware, so WinForms pixel sizes are not scaled for us.
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            bg = C(Palette.Bg); hover = C(Palette.SurfaceVar); fg = C(Palette.Fg); muted = C(Palette.FgMuted);
            outline = C(Palette.Outline); red = C(Palette.Red);
            string family = HasFont("Cascadia Mono") ? "Cascadia Mono" : "Consolas";
            mono = new Font(family, 8.5f);
            monoBold = new Font(family, 8.5f, FontStyle.Bold);
            monoSmall = new Font(family, 7.25f);
            title = new Font(family, 10f, FontStyle.Bold);
        }

        public int Px(float v) { return (int)Math.Round(v * scale); }

        static Color C(System.Windows.Media.SolidColorBrush b)
        {
            return Color.FromArgb(b.Color.A, b.Color.R, b.Color.G, b.Color.B);
        }

        static bool HasFont(string name)
        {
            foreach (var f in FontFamily.Families) if (f.Name == name) return true;
            return false;
        }

        protected override void OnRenderToolStripBackground(WinForms.ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(WinForms.ToolStripRenderEventArgs e)
        {
            var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using (var p = new Pen(outline)) e.Graphics.DrawRectangle(p, r);
        }

        protected override void OnRenderSeparator(WinForms.ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var p = new Pen(outline)) e.Graphics.DrawLine(p, Px(14), y, e.Item.Width - Px(14), y);
        }

        protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e) { }
        protected override void OnRenderItemCheck(WinForms.ToolStripItemImageRenderEventArgs e) { }
        protected override void OnRenderArrow(WinForms.ToolStripArrowRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
        {
            var it = e.Item as TrayItem;
            if (it == null) return;
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int w = it.Width, h = it.Height, x = Px(12);

            if (it.Selected && it.Enabled)
            {
                using (var b = new SolidBrush(hover)) g.FillRectangle(b, Px(4), 0, w - Px(8), h);
                using (var b = new SolidBrush(red)) g.FillRectangle(b, Px(4), Px(5), Px(2), h - Px(10));
            }

            switch (it.Kind)
            {
                case TrayItem.KindHeader:
                    // Dot-matrix strip, title, red marker, current mode.
                    using (var b = new SolidBrush(Color.FromArgb(110, muted)))
                        for (float dx = x; dx < w - x; dx += Px(6)) g.FillEllipse(b, dx, Px(6), Px(1.6f), Px(1.6f));
                    Text(g, it.Text, title, fg, new Rectangle(x, Px(10), w - 2 * x, Px(18)), false);
                    using (var b = new SolidBrush(red)) g.FillEllipse(b, w - x - Px(7), Px(16), Px(7), Px(7));
                    Text(g, it.Sub, monoSmall, muted, new Rectangle(x, Px(28), w - 2 * x, Px(14)), false);
                    break;
                case TrayItem.KindLabel:
                    using (var b = new SolidBrush(red)) g.FillRectangle(b, x, h / 2 - Px(3), Px(6), Px(6));
                    Text(g, it.Text, monoSmall, muted, new Rectangle(x + Px(12), 0, w - 2 * x, h), false);
                    break;
                case TrayItem.KindMode:
                    int d = Px(7), cy = h / 2;
                    if (it.On) using (var b = new SolidBrush(red)) g.FillEllipse(b, x, cy - d / 2, d, d);
                    else using (var p = new Pen(muted, Math.Max(1, scale))) g.DrawEllipse(p, x, cy - d / 2, d, d);
                    Text(g, it.Text, it.On ? monoBold : mono, it.On ? fg : muted, new Rectangle(x + Px(15), 0, w - x - Px(15), h), false);
                    Text(g, it.Right, monoSmall, muted, new Rectangle(x, 0, w - 2 * x, h), true);
                    break;
                case TrayItem.KindToggle:
                    Text(g, it.Text, mono, fg, new Rectangle(x, 0, w - 2 * x, h), false);
                    var size = WinForms.TextRenderer.MeasureText(g, it.Right ?? "", monoSmall);
                    var pill = new Rectangle(w - x - size.Width - Px(10), h / 2 - Px(8), size.Width + Px(10), Px(16));
                    if (it.On) using (var b = new SolidBrush(red)) g.FillRectangle(b, pill);
                    else using (var p = new Pen(muted)) g.DrawRectangle(p, pill);
                    WinForms.TextRenderer.DrawText(g, it.Right, monoSmall, pill, it.On ? Color.White : muted,
                        WinForms.TextFormatFlags.HorizontalCenter | WinForms.TextFormatFlags.VerticalCenter | WinForms.TextFormatFlags.NoPrefix);
                    break;
                default:
                    Text(g, it.Text, it.Muted ? mono : monoBold, it.Muted ? muted : fg, new Rectangle(x, 0, w - 2 * x, h), false);
                    break;
            }
        }

        static void Text(Graphics g, string s, Font f, Color c, Rectangle r, bool right)
        {
            if (string.IsNullOrEmpty(s)) return;
            var flags = WinForms.TextFormatFlags.VerticalCenter | WinForms.TextFormatFlags.NoPrefix | WinForms.TextFormatFlags.EndEllipsis
                | (right ? WinForms.TextFormatFlags.Right : WinForms.TextFormatFlags.Left);
            WinForms.TextRenderer.DrawText(g, s, f, r, c, flags);
        }
    }

    /// <summary>Start with Windows through the per-user Run key (no Task Scheduler, no admin).</summary>
    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "LenoMode";

        static string Command { get { return "\"" + Assembly.GetExecutingAssembly().Location + "\" --tray"; } }

        public static bool Enabled
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                        return k != null && string.Equals(k.GetValue(Name) as string, Command, StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception) { return false; }
            }
            set
            {
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                    {
                        if (value) k.SetValue(Name, Command);
                        else k.DeleteValue(Name, false);
                    }
                }
                catch (Exception ex) { Program.LogError(ex); }
            }
        }
    }
}
