using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace LenoMode
{
    /// <summary>Notification-area icon with an owner-drawn menu in the app's Nothing style.</summary>
    sealed class Tray : IDisposable
    {
        // Settings.TrayIcon values; the menu row cycles through them in this order.
        public const int IconApp = 0, IconGraph = 1, IconDino = 2;

        readonly WinForms.NotifyIcon icon;
        readonly WinForms.ContextMenuStrip menu;
        readonly TrayItem header, label, open, overlay, iconItem, admin, exit;
        readonly TrayItem[] modes = new TrayItem[3];
        readonly Icon appIcon;
        readonly TraySpark spark = new TraySpark();
        readonly TrayDino dino;
        Icon generated;   // last icon drawn by the graph / dino, freed once replaced
        bool drawFailed;
        string modeName = "";
        double cpu = -1;

        public event Action OpenRequested, ExitRequested, OverlayRequested, AdminRequested, MenuOpening;
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
            iconItem = Add(r, TrayItem.KindToggle, 27, delegate { CycleIcon(); });
            Separator(r);
            open = Add(r, TrayItem.KindAction, 26, delegate { Raise(OpenRequested); });
            admin = Add(r, TrayItem.KindAction, 26, delegate { Raise(AdminRequested); });
            exit = Add(r, TrayItem.KindAction, 26, delegate { Raise(ExitRequested); });
            exit.Muted = true;
            menu.Opening += delegate { Raise(MenuOpening); };

            appIcon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            icon = new WinForms.NotifyIcon
            {
                Icon = appIcon,
                ContextMenuStrip = menu,
                Visible = true,
            };
            icon.MouseClick += (s, e) => { if (e.Button == WinForms.MouseButtons.Left) Raise(OpenRequested); };
            dino = new TrayDino(Show);
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

        /// <summary>Show the "restart as admin" row; only useful while the app runs unelevated.</summary>
        public bool ShowAdmin { set { admin.Visible = value; } }

        /// <summary>Called once per sensor sample (UI thread); feeds the graph / dino icon and the tooltip.</summary>
        public void SetLoad(double cpuLoad)
        {
            cpu = cpuLoad;
            // History keeps filling in every style, so switching to the graph shows the last seconds at once.
            spark.Push(cpuLoad);
            if (Settings.TrayIcon == IconGraph) Show(spark.Draw);
            else if (Settings.TrayIcon == IconDino && !drawFailed) dino.SetLoad(cpuLoad);
            UpdateTip();
        }

        void Show(Func<Icon> draw)
        {
            if (drawFailed) return;
            try { Swap(draw()); }
            catch (Exception ex)
            {
                // GDI+ can fail here (handle quota, "generic error"). Uncaught, each frame would reach the dispatcher
                // handler and open a message box, up to 30 a second from the dino timer: log once, keep the app icon.
                drawFailed = true;
                dino.Stop();
                Program.LogError(ex);
                try { Swap(null); }
                catch (Exception) { }
            }
        }

        void CycleIcon()
        {
            Settings.TrayIcon = (Settings.TrayIcon + 1) % 3;
            Settings.Save();
            drawFailed = false;   // picking a style again is a retry
            if (Settings.TrayIcon != IconDino) dino.Stop();
            if (Settings.TrayIcon == IconApp) Swap(null);
            else if (cpu >= 0) SetLoad(cpu);
            RenderIconItem();
        }

        void Swap(Icon next)
        {
            icon.Icon = next ?? appIcon;   // the shell copies the icon here, so the previous one can go
            if (generated != null) generated.Dispose();
            generated = next;
        }

        void RenderIconItem()
        {
            iconItem.Text = L.S("tray.icon").ToUpperInvariant();
            iconItem.Right = L.S(Settings.TrayIcon == IconGraph ? "tray.i.graph" : Settings.TrayIcon == IconDino ? "tray.i.dino" : "tray.off");
            iconItem.On = Settings.TrayIcon != IconApp;
            menu.Invalidate();
        }

        void UpdateTip()
        {
            string tip = "LenoMode · " + modeName + (cpu >= 0 ? " · CPU " + cpu.ToString("0") + "%" : "");
            icon.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

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
            admin.Text = L.S("admin.relaunch");
            exit.Text = L.S("tray.exit").ToUpperInvariant();
            RenderIconItem();
            modeName = L.ModeName(current);
            UpdateTip();
        }

        public void Dispose()
        {
            dino.Stop();
            icon.Visible = false;
            icon.Dispose();
            menu.Dispose();
            if (generated != null) generated.Dispose();
        }
    }

    /// <summary>Shared bits for the drawn tray icons: taskbar ink colour and bitmap-to-icon conversion.</summary>
    static class TrayArt
    {
        public static readonly Color Red = Color.FromArgb(Palette.Red.Color.R, Palette.Red.Color.G, Palette.Red.Color.B);

        /// <summary>Near-black on a light taskbar, white on a dark one.</summary>
        public static Color Ink()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    if (k != null && Convert.ToInt32(k.GetValue("SystemUsesLightTheme", 0)) == 1) return Color.FromArgb(0x1A, 0x1A, 0x1A);
            }
            catch (Exception) { }
            return Color.White;
        }

        public static int Size { get { return WinForms.SystemInformation.SmallIconSize.Width; } }

        public static Icon ToIcon(Bitmap bmp)
        {
            IntPtr h = bmp.GetHicon();
            // Clone owns its own copy of the handle, so the GDI one can be freed straight away.
            try { using (var tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone(); }
            finally { DestroyIcon(h); }
        }

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);
    }

    /// <summary>
    /// CPU sparkline: the last 7 samples (1 s each) as dot columns inside a frame, newest in red,
    /// so the load trend is visible without opening the window. Drawn in the app's mono dot-matrix style.
    /// </summary>
    sealed class TraySpark
    {
        // Also the number of dot rows (one dot = ~14 % load). 7 rather than 8 so the dots stay 3 px with the frame
        // on the 32 px icon: 7 x 4 px pitch - 1 = 27, + 1 px gap and 1 px border per side = 31.
        const int Cols = 7;

        readonly double[] hist = new double[Cols];   // oldest first

        public void Push(double load)
        {
            Array.Copy(hist, 1, hist, 0, Cols - 1);
            hist[Cols - 1] = load;
        }

        public Icon Draw()
        {
            // Ink is re-read every sample so a taskbar light/dark switch repaints within a second.
            Color on = TrayArt.Ink();
            int size = TrayArt.Size;
            // Whole-pixel pitch keeps the dots crisp: 16 px icon -> 1 px dots, 32 px -> 3 px dots with a 1 px gap.
            int border = Math.Max(1, size / 32), pitch = Math.Max(2, (size - 4 * border) / Cols), dot = pitch - 1;
            int dots = pitch * Cols - 1, pad = 1;
            if (dots + 2 * (pad + border) > size) pad = 0;   // small icons: dots touch the frame rather than overflow
            int box = dots + 2 * (pad + border), frame = (size - box) / 2, origin = frame + border + pad;
            bool round = dot >= 3;
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var bOn = new SolidBrush(on))
                using (var bOff = new SolidBrush(Color.FromArgb(55, on)))
                using (var bRed = new SolidBrush(TrayArt.Red))
                using (var bFrame = new SolidBrush(Color.FromArgb(170, on)))
                {
                    // Four bars that skip the corner pixels, so the frame reads as slightly rounded without blur.
                    int a = frame, z = frame + box - border;
                    g.FillRectangle(bFrame, a + border, a, box - 2 * border, border);
                    g.FillRectangle(bFrame, a + border, z, box - 2 * border, border);
                    g.FillRectangle(bFrame, a, a + border, border, box - 2 * border);
                    g.FillRectangle(bFrame, z, a + border, border, box - 2 * border);
                    if (round) g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    for (int c = 0; c < Cols; c++)
                    {
                        // At least one lit dot, so an idle CPU still reads as a baseline rather than an empty icon.
                        int lit = Math.Max(1, (int)Math.Round(Math.Min(100, hist[c]) / 100 * Cols));
                        for (int row = 0; row < Cols; row++)
                        {
                            Brush b = row >= lit ? bOff : c == Cols - 1 ? bRed : bOn;
                            int x = origin + c * pitch, y = origin + (Cols - 1 - row) * pitch;
                            if (round) g.FillEllipse(b, x, y, dot, dot);
                            else g.FillRectangle(b, x, y, dot, dot);
                        }
                    }
                }
                return TrayArt.ToIcon(bmp);
            }
        }
    }

    /// <summary>
    /// Chrome offline-dino style runner (RunCat idea): a pixel T-rex whose legs and scrolling ground speed up with
    /// CPU load. Redrawn by hand in the shape of the Chrome dino (not the sprite file); the eye is the red accent ("r").
    /// </summary>
    sealed class TrayDino
    {
        const int Grid = 32;   // fills the 32 px icon (200 % DPI) at one art pixel per screen pixel
        static readonly string[] Body =
        {
            "..................############..",
            ".................##rr##########.",
            ".................##rr##########.",
            ".................##############.",
            ".................##############.",
            ".................##############.",
            ".................##############.",
            ".................#######........",
            ".................###########....",
            ".#..............#######.........",
            ".#.............########.........",
            ".##...........#########.........",
            ".###........#############.......",
            ".####......##########..##.......",
            ".#####....###########...#.......",
            ".######.#############...........",
            ".####################...........",
            "..###################...........",
            "...#################............",
            "....###############.............",
            ".....#############..............",
            ".......##########...............",
        };
        // Rows 22-27: back leg lifted, then front leg lifted.
        static readonly string[][] Legs =
        {
            new[]
            {
                "........####..###...............",
                "........##.....##...............",
                "........###....##...............",
                "...............##...............",
                "...............##...............",
                "...............####.............",
            },
            new[]
            {
                "........####..###...............",
                "........##.....##...............",
                "........##.....###..............",
                "........##......................",
                "........##......................",
                "........####....................",
            },
        };
        // The ground line sits right under the feet, loose pebbles one row lower; both scroll left so the dino runs right.
        const string Ground = "##############################.######################...############.#####################";
        const string Pebbles = ".....#...........##.............#..........#..........##........#..........#.......#.....";
        // Faint scenery behind the dino, so the run reads as motion and not just twitching legs: cacti stand on the
        // ground and pass at ground speed, a cloud drifts at a quarter of it (parallax).
        // Tall enough to clear the tail: the body hides rows 9-27 left of column 19, so a short cactus barely shows.
        static readonly string[] Cactus =
        {
            "...#...", "..###..", "#.###..", "#.###.#", "#.###.#", "#####.#", "..#####", "..###..", "..###..", "..###..",
        };
        static readonly string[] Shrub = { "..#..", "#.#..", "#.#.#", "###.#", "..###", "..#..", "..#.." };
        static readonly string[] Cloud = { "...####...", ".##....##.", "#........#", "##########" };
        static readonly string[] Cacti = Strip(64, new[] { Cactus, Shrub }, new[] { 6, 38 });
        static readonly string[] Clouds = Strip(48, new[] { Cloud }, new[] { 4 });
        const int CloudTop = 2, CloudSlowdown = 4;

        readonly Action<Func<Icon>> show;
        readonly DispatcherTimer timer = new DispatcherTimer();
        Color ink = Color.White;
        int frame;

        public TrayDino(Action<Func<Icon>> show)
        {
            this.show = show;
            timer.Tick += delegate { frame++; show(Draw); };
        }

        public void SetLoad(double load)
        {
            ink = TrayArt.Ink();   // once per sample, not per frame: the frame rate can reach 30/s
            // RunCat's curve: 500 ms per frame when idle, down to ~33 ms at 75 % load and above.
            double steps = Math.Max(1, Math.Min(15, load / 5));
            timer.Interval = TimeSpan.FromMilliseconds(500 / steps);
            if (!timer.IsEnabled) { timer.Start(); show(Draw); }
        }

        public void Stop() { timer.Stop(); }

        Icon Draw()
        {
            // Whole-pixel cells; below 32 px (under 200 % DPI) the art is drawn at 32 and scaled down.
            int size = TrayArt.Size, cell = Math.Max(1, size / Grid), canvas = Math.Max(size, Grid * cell);
            int origin = (canvas - Grid * cell) / 2;
            // The ground moves every frame, the legs every other one: a 30/s stride would just flicker.
            string[] legs = Legs[(frame / 2) % 2];
            int groundRow = Body.Length + legs.Length;
            using (var art = new Bitmap(canvas, canvas, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(art))
                using (var b = new SolidBrush(ink))
                using (var far = new SolidBrush(Color.FromArgb(90, ink)))
                using (var near = new SolidBrush(Color.FromArgb(160, ink)))
                using (var red = new SolidBrush(TrayArt.Red))
                {
                    // Scenery first: the dino's opaque pixels then cover it, so it passes behind.
                    for (int y = 0; y < Clouds.Length; y++)
                        Paint(g, Scroll(Clouds[y], frame / CloudSlowdown), CloudTop + y, far, red, origin, cell);
                    for (int y = 0; y < Cacti.Length; y++)
                        Paint(g, Scroll(Cacti[y], frame), groundRow - Cacti.Length + y, near, red, origin, cell);
                    for (int y = 0; y < Grid; y++)
                    {
                        string row = y < Body.Length ? Body[y]
                            : y < groundRow ? legs[y - Body.Length]
                            : y == groundRow ? Scroll(Ground, frame) : y == groundRow + 2 ? Scroll(Pebbles, frame) : null;
                        if (row != null) Paint(g, row, y, b, red, origin, cell);
                    }
                }
                if (canvas == size) return TrayArt.ToIcon(art);
                using (var small = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(art, 0, 0, size, size);
                    }
                    return TrayArt.ToIcon(small);
                }
            }
        }

        static void Paint(Graphics g, string row, int y, Brush ink, Brush red, int origin, int cell)
        {
            for (int x = 0; x < Grid; x++)
                if (row[x] != '.') g.FillRectangle(row[x] == 'r' ? red : ink, origin + x * cell, origin + y * cell, cell, cell);
        }

        static string Scroll(string strip, int offset)
        {
            int start = offset % strip.Length;
            return (strip + strip).Substring(start, Grid);
        }

        /// <summary>Lays sprites into a looping strip at the given columns, bottom-aligned so they share a baseline.</summary>
        static string[] Strip(int length, string[][] sprites, int[] cols)
        {
            int height = 0;
            foreach (var s in sprites) height = Math.Max(height, s.Length);
            var rows = new char[height][];
            for (int y = 0; y < height; y++) rows[y] = new string('.', length).ToCharArray();
            for (int i = 0; i < sprites.Length; i++)
            {
                string[] s = sprites[i];
                int top = height - s.Length;
                for (int y = 0; y < s.Length; y++)
                    for (int x = 0; x < s[y].Length; x++)
                        if (s[y][x] != '.') rows[top + y][cols[i] + x] = '#';
            }
            var result = new string[height];
            for (int y = 0; y < height; y++) result[y] = new string(rows[y]);
            return result;
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
        Font mono, monoBold, monoSmall, title;

        public TrayRenderer()
        {
            // The process is DPI aware, so WinForms pixel sizes are not scaled for us.
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            bg = C(Palette.Bg); hover = C(Palette.SurfaceVar); fg = C(Palette.Fg); muted = C(Palette.FgMuted);
            outline = C(Palette.Outline); red = C(Palette.Red);
            // Same packaged-font trap as Palette.PickMono: use Cascadia only if WPF could read every face, and the
            // tray is built before the window, so a GDI+ failure here must not end the app at startup.
            bool cascadia = Palette.Mono.Source.StartsWith("Cascadia", StringComparison.Ordinal) && HasFont("Cascadia Mono");
            try { MakeFonts(new FontFamily(cascadia ? "Cascadia Mono" : "Consolas")); }
            catch (Exception ex)
            {
                Program.LogError(ex);
                MakeFonts(FontFamily.GenericMonospace);
            }
        }

        void MakeFonts(FontFamily family)
        {
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
            try { foreach (var f in FontFamily.Families) if (f.Name == name) return true; }
            catch (Exception) { }
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
