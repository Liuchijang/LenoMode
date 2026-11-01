using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace LenoMode
{
    /// <summary>Nothing-style palette shared with NotiGuard: monochrome, red only as an accent.</summary>
    static class Palette
    {
        public static bool Dark;
        public static SolidColorBrush Bg, Surface, SurfaceVar, Fg, FgMuted, Outline, DotOff, Ok, Warn, Bad;
        public static readonly SolidColorBrush Red = B(0xFFD71921);   // fills only; red text uses Bad
        public static readonly SolidColorBrush White = B(0xFFFFFFFF);
        public static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas");

        static SolidColorBrush B(uint argb)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze();
            return b;
        }

        public static void Install(ResourceDictionary r)
        {
            Dark = !SystemUsesLightTheme();
            if (Dark)
            {
                Bg = B(0xFF000000); Surface = B(0xFF0C0C0C); SurfaceVar = B(0xFF161616);
                Fg = B(0xFFF2F2F2); FgMuted = B(0xFF8A8A8A); Outline = B(0xFF2B2B2B); DotOff = B(0xFF1D1D1D);
                Ok = B(0xFF00C853); Warn = B(0xFFFFAB00); Bad = B(0xFFFF4A52);
            }
            else
            {
                Bg = B(0xFFFFFFFF); Surface = B(0xFFFFFFFF); SurfaceVar = B(0xFFF4F4F4);
                Fg = B(0xFF0A0A0A); FgMuted = B(0xFF6B6B6B); Outline = B(0xFFD8D8D8); DotOff = B(0xFFE8E8E8);
                Ok = B(0xFF00873A); Warn = B(0xFF9A6700); Bad = Red;
            }
            r["Bg"] = Bg; r["Surface"] = Surface; r["SurfaceVar"] = SurfaceVar;
            r["Fg"] = Fg; r["FgMuted"] = FgMuted; r["Outline"] = Outline; r["Red"] = Red;
            r["Mono"] = Mono;
        }

        static bool SystemUsesLightTheme()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 1;
                }
            }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Dot-matrix fill — the signature Nothing/Glyph texture.</summary>
    public sealed class DotField : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            const double step = 9, r = 1.1;
            dc.PushOpacity(0.45);
            for (double y = step / 2; y < ActualHeight; y += step)
                for (double x = step / 2; x < ActualWidth; x += step)
                    dc.DrawEllipse(Palette.FgMuted, null, new Point(x, y), r, r);
            dc.Pop();
        }
    }

    /// <summary>History as columns of lit dots, newest on the right in red.</summary>
    public sealed class DotChart : FrameworkElement
    {
        const double Step = 6, R = 1.35;
        const int Capacity = 400;
        readonly List<double> values = new List<double>();
        public double Min, Max = 100;

        public void Push(double v)
        {
            values.Add(v);
            if (values.Count > Capacity) values.RemoveAt(0);
            InvalidateVisual();
        }

        public void Clear()
        {
            values.Clear();
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            int cols = (int)(ActualWidth / Step), rows = (int)(ActualHeight / Step);
            if (cols <= 0 || rows <= 0) return;
            double xOff = ActualWidth - cols * Step;
            for (int c = 0; c < cols; c++)
            {
                int idx = values.Count - cols + c;
                int lit = 0;
                if (idx >= 0 && !double.IsNaN(values[idx]) && Max > Min)
                {
                    double f = Math.Max(0, Math.Min(1, (values[idx] - Min) / (Max - Min)));
                    lit = (int)Math.Round(f * rows);
                    if (lit == 0 && values[idx] > Min) lit = 1;
                }
                Brush on = c == cols - 1 ? Palette.Red : Palette.Fg;
                for (int r = 0; r < rows; r++)
                {
                    var center = new Point(xOff + c * Step + Step / 2, ActualHeight - (r * Step + Step / 2));
                    dc.DrawEllipse(r < lit ? on : Palette.DotOff, null, center, R, R);
                }
            }
        }
    }

    /// <summary>Metric card: mono label, big mono value, one-line note, dot history.</summary>
    sealed class Tile
    {
        public readonly Border Root;
        public readonly TextBlock Value, Sub;
        public readonly DotChart Chart;

        public Tile(string labelKey, double min, double max)
        {
            var label = new TextBlock { Tag = labelKey, FontFamily = Palette.Mono, FontSize = 11, Foreground = Palette.FgMuted };
            Value = new TextBlock
            {
                Text = "—", FontFamily = Palette.Mono, FontSize = 26, FontWeight = FontWeights.Bold,
                Foreground = Palette.Fg, Margin = new Thickness(0, 6, 0, 0),
            };
            Sub = new TextBlock
            {
                FontSize = 12, Foreground = Palette.FgMuted, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 10),
            };
            Chart = new DotChart { Height = 42, Min = min, Max = max };
            var sp = new StackPanel();
            sp.Children.Add(label);
            sp.Children.Add(Value);
            sp.Children.Add(Sub);
            sp.Children.Add(Chart);
            Root = new Border
            {
                Child = sp, Background = Palette.Surface, BorderBrush = Palette.Outline,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 10, 10),
            };
        }

        public void Set(string value, string sub, double chartValue, Brush valueBrush)
        {
            Value.Text = value;
            Value.Foreground = valueBrush ?? Palette.Fg;
            Sub.Text = sub;
            Chart.Push(chartValue);
        }
    }
}
