using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LenoMode
{
    /// <summary>
    /// Small always-on-top, click-through stats panel for use over a game. Always dark, whatever the
    /// app theme. Not visible over exclusive-fullscreen games (borderless and windowed are fine).
    /// Fields, size, background opacity, layout (panel / one-line bar) and position are configurable.
    /// </summary>
    sealed class Overlay
    {
        // field bits, in the order of the toggle buttons (labels "ov.f.<index>")
        public const int FMode = 1, FFps = 2, FLow = 4, FGpu = 8, FCpu = 16, FTemp = 32, FPower = 64, FRam = 128, FVram = 256;
        public const int FieldCount = 9, DefaultFields = 0x7F;   // everything the overlay showed before it was configurable

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly double[] Scales = { 0.85, 1.0, 1.25 };
        static readonly byte[] Alphas = { 0x66, 0xB3, 0xE6 };   // 40 / 70 / 90 %
        static readonly Brush Text = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF2));
        static readonly Brush Muted = Frozen(Color.FromRgb(0xA8, 0xA8, 0xA8));
        static readonly Brush Amber = Frozen(Color.FromRgb(0xFF, 0xAB, 0x00));
        static readonly Brush Pink = Frozen(Color.FromRgb(0xFF, 0x4A, 0x52));
        static readonly Brush Edge = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

        readonly Window win;
        readonly Border frame;
        readonly ScaleTransform scale = new ScaleTransform(1, 1);
        readonly StackPanel panel, head, stats, bar;
        readonly TextBlock mode, fps, line1, line2, line3, line4, barText;
        readonly Ellipse dot, barDot;
        readonly WinEventProc foregroundProc;   // field so the GC keeps it alive while the hook is installed
        IntPtr handle, foregroundHook;
        int corner, fields = DefaultFields;
        bool editing, modeBusy;
        ThermalMode modeValue = ThermalMode.Unknown;
        Sample last;

        /// <summary>Position as a fraction of the free work area (0..1); negative = use the corner preset.</summary>
        public double PosX = -1, PosY = -1;

        /// <summary>Raised after the user drags the overlay in edit mode (PosX/PosY updated).</summary>
        public event Action Moved;

        public Overlay()
        {
            foregroundProc = OnForegroundChanged;
            dot = Dot();
            mode = Mono(11, Muted, FontWeights.Medium);
            head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(dot);
            head.Children.Add(mode);
            fps = Mono(22, Text, FontWeights.Bold);
            line1 = Mono(11, Muted, FontWeights.Normal);
            line2 = Mono(12, Text, FontWeights.Normal);
            line3 = Mono(12, Text, FontWeights.Normal);
            line4 = Mono(12, Text, FontWeights.Normal);
            stats = new StackPanel();
            foreach (var e in new UIElement[] { line2, line3, line4 }) stats.Children.Add(e);
            panel = new StackPanel { MinWidth = 146 };
            foreach (var e in new UIElement[] { head, fps, line1, stats }) panel.Children.Add(e);

            barDot = Dot();
            barText = Mono(12, Text, FontWeights.Normal);
            bar = new StackPanel { Orientation = Orientation.Horizontal };
            bar.Children.Add(barDot);
            bar.Children.Add(barText);

            frame = new Border
            {
                Child = panel, BorderBrush = Edge, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 9, 14, 10), LayoutTransform = scale,
            };
            win = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                Topmost = true, ShowInTaskbar = false, ShowActivated = false, Focusable = false,
                ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight,
                Title = "LenoMode overlay", Content = frame,
            };
            TextOptions.SetTextFormattingMode(win, TextFormattingMode.Display);
            win.SourceInitialized += delegate
            {
                handle = new WindowInteropHelper(win).Handle;
                ApplyExStyle();
                foregroundHook = SetWinEventHook(0x0003 /*EVENT_SYSTEM_FOREGROUND*/, 0x0003, IntPtr.Zero, foregroundProc, 0, 0, 0 /*OUTOFCONTEXT*/);
            };
            win.SizeChanged += delegate { Place(); };
            win.MouseLeftButtonDown += delegate { if (editing) Drag(); };
            Configure(DefaultFields, 1, 2, false);
        }

        public bool Visible { get { return win.IsVisible; } }

        /// <summary>
        /// Edit mode drops click-through so the overlay can be dragged; the amber border marks it.
        /// Hiding the overlay ends it, so a hidden window never keeps eating clicks.
        /// </summary>
        public bool Editing
        {
            get { return editing; }
            set
            {
                editing = value && win.IsVisible;
                frame.BorderBrush = editing ? Amber : Edge;
                frame.BorderThickness = new Thickness(editing ? 2 : 1);
                win.Cursor = editing ? Cursors.SizeAll : null;
                ApplyExStyle();
            }
        }

        public void Configure(int fields, int size, int opacity, bool asBar)
        {
            this.fields = fields == 0 ? DefaultFields : fields;
            double k = Scales[Math.Max(0, Math.Min(2, size))];
            scale.ScaleX = scale.ScaleY = k;
            frame.Background = Frozen(Color.FromArgb(Alphas[Math.Max(0, Math.Min(2, opacity))], 0, 0, 0));
            frame.Child = asBar ? (UIElement)bar : panel;
            frame.Padding = asBar ? new Thickness(10, 5, 12, 6) : new Thickness(12, 9, 14, 10);
            Render();
        }

        public void Show() { win.Show(); Place(); }
        public void Hide() { Editing = false; win.Hide(); }
        public void Close()
        {
            if (foregroundHook != IntPtr.Zero) { UnhookWinEvent(foregroundHook); foregroundHook = IntPtr.Zero; }
            win.Close();
        }

        public void SetMode(ThermalMode m, bool busy)
        {
            modeValue = m;
            modeBusy = busy;
            Render();
        }

        public void Update(Sample s)
        {
            last = s;
            if (!win.IsVisible) return;
            Render();
            BringToTop();   // fallback for z-order changes that do not change the foreground window
        }

        bool Has(int f) { return (fields & f) != 0; }

        void Render()
        {
            Sample s = last;
            string modeText = (modeBusy ? "… " : "") + L.ModeName(modeValue);
            Brush dotBrush = modeBusy ? Amber : Palette.Red;
            bool noFps = s == null || double.IsNaN(s.Fps);
            string fpsText = noFps ? "— FPS" : s.Fps.ToString("0", Inv) + " FPS";
            string lowText = s == null ? "" : noFps ? L.S("ov.nofps")
                : "1% " + s.Fps1Low.ToString("0", Inv) + " · " + s.FrameMs.ToString("0.0", Inv) + " ms";
            string gpu = null, cpu = null, temp = null, pow = null, ram = null, vram = null;
            Brush tempBrush = Text;
            if (s != null)
            {
                gpu = "GPU " + s.GpuGame.ToString("0", Inv) + "%";
                cpu = "CPU " + s.CpuGhz.ToString("0.00", Inv) + " GHz";
                temp = double.IsNaN(s.TempC) ? "—°C" : s.TempC.ToString("0", Inv) + "°C";
                tempBrush = s.TempC >= 85 ? Pink : s.TempC >= 75 ? Amber : Text;
                pow = double.IsNaN(s.PowerW) ? "AC" : s.PowerW.ToString("0.0", Inv) + " W";
                ram = "RAM " + s.RamPct.ToString("0", Inv) + "%";
                vram = "VRAM " + (s.GpuSharedGb + s.GpuDedicatedGb).ToString("0.0", Inv) + " GB";
            }

            // panel
            dot.Fill = barDot.Fill = dotBrush;
            mode.Text = modeText;
            head.Visibility = Vis(Has(FMode));
            fps.Text = fpsText;
            fps.Visibility = Vis(Has(FFps));
            fps.Margin = new Thickness(0, Has(FMode) ? 4 : 0, 0, 0);
            line1.Text = lowText;
            line1.Visibility = Vis(Has(FLow) && lowText.Length > 0);
            SetLine(line2, Join(Has(FGpu) ? gpu : null, Has(FCpu) ? cpu : null));
            SetLine(line3, Join(Has(FTemp) ? temp : null, Has(FPower) ? pow : null));
            line3.Foreground = Has(FTemp) ? tempBrush : Text;
            SetLine(line4, Join(Has(FRam) ? ram : null, Has(FVram) ? vram : null));
            bool above = Has(FMode) || Has(FFps) || line1.Visibility == Visibility.Visible;
            stats.Margin = new Thickness(0, above ? 6 : 0, 0, 0);

            // bar: one line, same order as the panel
            barDot.Visibility = Vis(Has(FMode));
            var runs = new List<Run>();
            if (Has(FMode)) runs.Add(new Run(modeText) { Foreground = Muted });
            if (Has(FFps)) runs.Add(new Run(fpsText) { FontWeight = FontWeights.Bold });
            if (Has(FLow) && lowText.Length > 0) runs.Add(new Run(lowText) { Foreground = Muted });
            if (Has(FGpu) && gpu != null) runs.Add(new Run(gpu));
            if (Has(FCpu) && cpu != null) runs.Add(new Run(cpu));
            if (Has(FTemp) && temp != null) runs.Add(new Run(temp) { Foreground = tempBrush });
            if (Has(FPower) && pow != null) runs.Add(new Run(pow));
            if (Has(FRam) && ram != null) runs.Add(new Run(ram));
            if (Has(FVram) && vram != null) runs.Add(new Run(vram));
            barText.Inlines.Clear();
            for (int i = 0; i < runs.Count; i++)
            {
                if (i > 0) barText.Inlines.Add(new Run("  │  ") { Foreground = Edge });   // not "·": the 1% low group already uses it
                barText.Inlines.Add(runs[i]);
            }
        }

        static void SetLine(TextBlock t, string text)
        {
            t.Text = text ?? "";
            t.Visibility = Vis(text != null);
        }

        static string Join(string a, string b)
        {
            if (a == null) return b;
            return b == null ? a : a + " · " + b;
        }

        static Visibility Vis(bool on) { return on ? Visibility.Visible : Visibility.Collapsed; }

        /// <summary>
        /// A borderless-fullscreen game that takes the foreground is placed above other topmost windows,
        /// so the overlay re-asserts itself the moment the foreground changes instead of waiting for a tick.
        /// </summary>
        void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time)
        {
            BringToTop();
        }

        void BringToTop()
        {
            if (!win.IsVisible || handle == IntPtr.Zero) return;
            SetWindowPos(handle, new IntPtr(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0,
                0x0001 /*NOSIZE*/ | 0x0002 /*NOMOVE*/ | 0x0010 /*NOACTIVATE*/ | 0x0040 /*SHOWWINDOW*/);
        }

        /// <summary>Stored as a fraction of the free area so the spot survives size, scale and layout changes.</summary>
        void Place()
        {
            var area = SystemParameters.WorkArea;
            const double m = 16;
            double freeW = Math.Max(0, area.Width - win.ActualWidth), freeH = Math.Max(0, area.Height - win.ActualHeight);
            if (PosX >= 0 && PosY >= 0)
            {
                win.Left = area.Left + Math.Min(1, PosX) * freeW;
                win.Top = area.Top + Math.Min(1, PosY) * freeH;
                return;
            }
            win.Left = (corner & 1) == 0 ? area.Left + m : area.Right - win.ActualWidth - m;
            win.Top = (corner & 2) == 0 ? area.Top + m : area.Bottom - win.ActualHeight - m;
        }

        public void SetPosition(int corner, double x, double y)
        {
            this.corner = corner & 3;
            PosX = x;
            PosY = y;
            if (win.IsVisible) Place();
        }

        void Drag()
        {
            try { win.DragMove(); }
            catch (InvalidOperationException) { return; }   // button already released
            var area = SystemParameters.WorkArea;
            double freeW = area.Width - win.ActualWidth, freeH = area.Height - win.ActualHeight;
            PosX = freeW > 0 ? Clamp01((win.Left - area.Left) / freeW) : 0;
            PosY = freeH > 0 ? Clamp01((win.Top - area.Top) / freeH) : 0;
            Place();
            if (Moved != null) Moved();
        }

        static double Clamp01(double v) { return v < 0 ? 0 : v > 1 ? 1 : v; }

        void ApplyExStyle()
        {
            if (handle == IntPtr.Zero) return;
            int ex = GetWindowLong(handle, -20) | 0x80 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/;
            ex = editing ? ex & ~0x20 : ex | 0x20 /*TRANSPARENT: clicks go to the game*/;
            SetWindowLong(handle, -20, ex);
        }

        delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time);

        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        static Ellipse Dot()
        {
            return new Ellipse { Width = 8, Height = 8, Fill = Palette.Red, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        }

        static TextBlock Mono(double size, Brush fg, FontWeight weight)
        {
            return new TextBlock { FontFamily = Palette.Mono, FontSize = size, Foreground = fg, FontWeight = weight };
        }

        static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
    }
}
