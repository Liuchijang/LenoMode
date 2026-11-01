using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LenoMode
{
    static class Program
    {
        const string MutexName = @"Local\LenoMode.Single", ShowEventName = @"Local\LenoMode.Show";

        [STAThread]
        static int Main(string[] args)
        {
            // Elevated one-shot helper: the app itself runs without admin (ITS works without it), only the
            // BIOS boot default needs admin, so that write is done by a short-lived elevated copy.
            if (args.Length == 2 && args[0] == "--set-boot")
            {
                int m;
                if (!int.TryParse(args[1], out m) || m < 0 || m > 2) return 2;
                return LenovoWmi.Apply((ThermalMode)m) == null ? 0 : 1;
            }

            bool tray = Array.IndexOf(args, "--tray") >= 0;
            bool owned;
            var single = new Mutex(true, MutexName, out owned);
            // A relaunch as admin waits for the instance it replaces to exit.
            if (!owned && Array.IndexOf(args, "--wait") >= 0)
            {
                try { owned = single.WaitOne(10000); }
                catch (AbandonedMutexException) { owned = true; }
            }
            if (!owned)
            {
                try { using (var ev = EventWaitHandle.OpenExisting(ShowEventName)) ev.Set(); }
                catch (Exception) { }
                return 0;
            }

            Its.BeginProbe();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) =>
            {
                LogError(e.Exception);
                MessageBox.Show(e.Exception.Message, "LenoMode", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            Palette.Install(app.Resources);
            Settings.Load();
            var ui = new MainUi(new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName));
            if (tray) ui.StartHidden();
            else ui.Window.Show();
            app.Run();
            GC.KeepAlive(single);
            return 0;
        }

        /// <summary>
        /// Every thermal mode the app sees or sets, so changes made by something else (BIOS defaults at boot,
        /// Lenovo Vantage / Smart Mode, other tools) can be told apart from the app's own.
        /// </summary>
        public static void LogMode(string what)
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                File.AppendAllText(System.IO.Path.Combine(Settings.Dir, "mode.log"), DateTime.Now.ToString("s") + " " + what + Environment.NewLine);
            }
            catch (Exception) { }
        }

        public static void LogError(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                File.AppendAllText(System.IO.Path.Combine(Settings.Dir, "error.log"), DateTime.Now.ToString("s") + " " + ex + Environment.NewLine);
            }
            catch (Exception) { }
        }
    }

    sealed class ModeStats
    {
        public int N, PowN, TempN, FpsN;
        public double Game, Gpu, Cpu, Ghz, Temp, Pow, Fps, Low;

        public void Add(Sample s)
        {
            N++;
            Game += s.GpuGame; Gpu += s.GpuTotal; Cpu += s.CpuLoad; Ghz += s.CpuGhz;
            if (!double.IsNaN(s.TempC)) { Temp += s.TempC; TempN++; }
            if (!double.IsNaN(s.PowerW)) { Pow += s.PowerW; PowN++; }
            if (!double.IsNaN(s.Fps)) { Fps += s.Fps; Low += s.Fps1Low; FpsN++; }
        }
    }

    sealed class ModeCard
    {
        public Button Btn;
        public Ellipse Dot;
    }

    sealed class MainUi
    {
        const int WarmupSeconds = 10;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public readonly Window Window;
        readonly FrameCounter frameCounter = new FrameCounter();
        Sensors sensors;   // built on the sampler thread: WMI + perf counter setup costs ~350 ms (measured 2026-10-31)
        readonly Overlay overlay = new Overlay();
        readonly ManualResetEvent stop = new ManualResetEvent(false);

        // thermal mode
        ThermalMode current = ThermalMode.Unknown, pending = ThermalMode.Unknown;
        bool busy;
        string modeError;
        readonly ModeCard[] cards = new ModeCard[3];
        ThermalMode bootMode = ThermalMode.Unknown;   // BIOS SystemPerformanceMode: applied at the next boot
        bool vantage;
        TextBlock bootStatus;
        Button bootBtn;

        // windows power mode
        readonly Button[] powerBtns = new Button[3];
        int powerIdx = -1;

        // live
        Tile tFps, tGame, tGpu, tCpu, tClock, tTemp, tPow, tRam, tVram;
        Sample last;
        int tick;

        // session
        bool recording;
        readonly List<Sample> log = new List<Sample>();
        readonly ModeStats[] stats = { new ModeStats(), new ModeStats(), new ModeStats() };
        readonly TextBlock[,] cells = new TextBlock[3, 9];
        readonly Ellipse[] rowDots = new Ellipse[3];
        Button recBtn, testBtn, durBtn, loadBtn, exportBtn, resetBtn;
        string sessionMsg;

        // auto test (runs on its own thread, UI state only touched on the dispatcher)
        Thread testThread;
        readonly ManualResetEvent testCancel = new ManualResetEvent(false);
        volatile int testMode = -1;
        volatile bool testRecording, testWarm;
        long testPhaseEndTicks;

        readonly Button[] langBtns = new Button[2];
        Button overlayBtn, moveBtn, autostartBtn, adminBtn;
        Button[] fieldBtns, layoutBtns, sizeBtns, opacityBtns, cornerBtns;
        Tray tray;
        bool started, exiting;
        readonly EventWaitHandle showSignal;
        bool hotkeysOk = true;
        readonly TextBlock modeStatus, footer, liveRight, testStatus, device;
        readonly Ellipse modeStatusDot;
        readonly UniformGrid modeGrid, tileGrid;

        public MainUi(EventWaitHandle showSignal)
        {
            this.showSignal = showSignal;
            using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("LenoMode.MainWindow.xaml"))
                Window = (Window)XamlReader.Load(st);

            modeStatus = (TextBlock)Window.FindName("ModeStatus");
            modeStatusDot = (Ellipse)Window.FindName("ModeStatusDot");
            footer = (TextBlock)Window.FindName("Footer");
            liveRight = (TextBlock)Window.FindName("LiveRight");
            testStatus = (TextBlock)Window.FindName("TestStatus");
            device = (TextBlock)Window.FindName("Device");
            modeGrid = (UniformGrid)Window.FindName("ModeGrid");
            tileGrid = (UniformGrid)Window.FindName("TileGrid");

            BuildLang((StackPanel)Window.FindName("LangHost"));
            BuildModes();
            BuildBoot((WrapPanel)Window.FindName("BootRow"));
            BuildPower((WrapPanel)Window.FindName("PowerRow"));
            BuildOverlay((WrapPanel)Window.FindName("OverlayRow"), (Grid)Window.FindName("OverlayGrid"));
            BuildApp((WrapPanel)Window.FindName("AppRow"));
            BuildTiles();
            BuildSession((WrapPanel)Window.FindName("SessionBar"), (Grid)Window.FindName("SessionTable"));
            BuildCollapsible();

            tray = new Tray();
            tray.OpenRequested += ShowWindow;
            tray.ExitRequested += Exit;
            tray.OverlayRequested += ToggleOverlay;
            tray.ModeRequested += SetMode;

            Window.SourceInitialized += delegate { DarkTitleBar(); RegisterHotkeys(); Start(); };
            Window.SizeChanged += delegate { Relayout(); };
            Window.Closing += (sender, e) =>
            {
                // Close hides to the tray; the hotkeys and overlay keep working. Exit is in the tray menu.
                if (exiting) return;
                e.Cancel = true;
                Window.Hide();
            };
            Application.Current.SessionEnding += delegate { Exit(); };
            ApplyLang();
        }

        // ---------- build ----------

        Button Pill(string tag, RoutedEventHandler click)
        {
            var b = new Button { Style = (Style)Window.FindResource("Pill"), Tag = tag };
            b.Click += click;
            return b;
        }

        void BuildLang(StackPanel host)
        {
            string[] codes = { "VI", "EN" };
            for (int i = 0; i < 2; i++)
            {
                bool vi = i == 0;
                var b = new Button
                {
                    Style = (Style)Window.FindResource("Pill"), Content = codes[i],
                    FontSize = 11, Padding = new Thickness(10, 3, 10, 3), MinHeight = 0, Margin = new Thickness(0, 0, 4, 0),
                };
                System.Windows.Automation.AutomationProperties.SetName(b, vi ? "Tiếng Việt" : "English");
                b.Click += delegate { L.Vi = vi; Settings.Save(); ApplyLang(); };
                langBtns[i] = b;
                host.Children.Add(b);
            }
        }

        void BuildModes()
        {
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                var dot = new Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
                var name = new TextBlock
                {
                    Tag = "mode." + i, FontFamily = Palette.Mono, FontSize = 15, FontWeight = FontWeights.Bold,
                    Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(dot);
                head.Children.Add(name);
                var bios = new TextBlock
                {
                    Text = Thermal.Names[i].ToUpperInvariant(), FontFamily = Palette.Mono, FontSize = 11,
                    Opacity = 0.6, Margin = new Thickness(0, 10, 0, 0),
                };
                var desc = new TextBlock
                {
                    Tag = "mode." + i + ".desc", FontSize = 12.5, Opacity = 0.8,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
                };
                var sp = new StackPanel();
                sp.Children.Add(head);
                sp.Children.Add(bios);
                sp.Children.Add(desc);
                var btn = new Button { Style = (Style)Window.FindResource("ModeBtn"), Content = sp };
                btn.Click += delegate { SetMode((ThermalMode)idx); };
                cards[i] = new ModeCard { Btn = btn, Dot = dot };
                modeGrid.Children.Add(btn);
            }
        }

        void BuildBoot(WrapPanel row)
        {
            bootStatus = new TextBlock { FontFamily = Palette.Mono, FontSize = 11, Foreground = Palette.FgMuted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 8) };
            bootBtn = Pill("boot.save", delegate { SaveBootDefault(); });
            bootBtn.FontSize = 11;
            bootBtn.MinHeight = 0;
            bootBtn.Padding = new Thickness(10, 3, 10, 3);
            row.Children.Add(bootStatus);
            row.Children.Add(bootBtn);
        }

        void SaveBootDefault()
        {
            ThermalMode m = current;
            if (m == ThermalMode.Unknown || busy) return;
            bootBtn.IsEnabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err;
                ThermalMode now;
                if (IsAdmin())
                {
                    err = LenovoWmi.Apply(m);
                    now = LenovoWmi.Read();
                }
                else
                {
                    err = RunElevated("--set-boot " + (int)m);
                    now = err == null ? m : ThermalMode.Unknown;
                }
                Program.LogMode("app set boot default " + Thermal.Names[(int)m] + " -> read " + (LenovoWmi.LastRaw ?? "?") + (err == null ? "" : " error: " + err));
                Ui(delegate
                {
                    bootMode = now;
                    if (err != null) modeError = err;
                    RenderModes();
                });
            });
        }

        /// <summary>Runs this exe elevated with the given arguments and waits. Null on success.</summary>
        static string RunElevated(string args)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, args) { Verb = "runas", UseShellExecute = true }))
                {
                    if (!p.WaitForExit(30000)) return "timeout";
                    return p.ExitCode == 0 ? null : "exit code " + p.ExitCode;
                }
            }
            catch (System.ComponentModel.Win32Exception ex) { return ex.Message; }
        }

        void ReadBootDefault()
        {
            ThermalMode b = LenovoWmi.Read();
            Ui(delegate { bootMode = b; RenderModes(); });
        }

        void BuildPower(WrapPanel row)
        {
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                powerBtns[i] = Pill("power." + i, delegate
                {
                    if (PowerMode.Set(idx)) powerIdx = idx;
                    RefreshPower();
                });
                row.Children.Add(powerBtns[i]);
            }
        }

        void BuildTiles()
        {
            tFps = new Tile("tile.fps", 0, 60);
            tGame = new Tile("tile.game", 0, 100);
            tGpu = new Tile("tile.gpu", 0, 100);
            tCpu = new Tile("tile.cpu", 0, 100);
            tClock = new Tile("tile.clock", 0, 5.2);
            tTemp = new Tile("tile.temp", 30, 100);
            tPow = new Tile("tile.power", 0, 70);
            tRam = new Tile("tile.ram", 0, 100);
            tVram = new Tile("tile.vram", 0, 8);
            foreach (var t in AllTiles()) tileGrid.Children.Add(t.Root);
        }

        Tile[] AllTiles() { return new[] { tFps, tGame, tGpu, tCpu, tClock, tTemp, tPow, tRam, tVram }; }

        /// <summary>Main toggle on its own, then one labelled row per setting so every choice is visible at once.</summary>
        void BuildOverlay(WrapPanel row, Grid grid)
        {
            overlayBtn = Pill(null, delegate { ToggleOverlay(); });
            row.Children.Add(overlayBtn);

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());

            var tags = new string[Overlay.FieldCount];
            for (int i = 0; i < tags.Length; i++) tags[i] = "ov.f." + i;
            fieldBtns = SettingRow(grid, "ov.row.show", tags, delegate(int i)
            {
                int f = Settings.OverlayFields ^ (1 << i);
                if (f == 0) return;   // keep at least one field, an empty overlay looks broken
                Settings.OverlayFields = f;
            });
            layoutBtns = SettingRow(grid, "ov.row.layout", new[] { "ov.layout.0", "ov.layout.1" },
                delegate(int i) { Settings.OverlayBar = i == 1; });
            sizeBtns = SettingRow(grid, "ov.row.size", new[] { "ov.size.0", "ov.size.1", "ov.size.2" },
                delegate(int i) { Settings.OverlaySize = i; });
            opacityBtns = SettingRow(grid, "ov.row.bg", new[] { "ov.bg.0", "ov.bg.1", "ov.bg.2" },
                delegate(int i) { Settings.OverlayOpacity = i; });
            cornerBtns = SettingRow(grid, "ov.row.pos", new[] { "ov.corner.0", "ov.corner.1", "ov.corner.2", "ov.corner.3" },
                delegate(int i)
                {
                    Settings.OverlayCorner = i;
                    Settings.OverlayX = Settings.OverlayY = -1;   // a corner preset replaces a dragged spot
                });
            moveBtn = SmallPill(null, delegate
            {
                if (!overlay.Visible) ToggleOverlay();
                overlay.Editing = !overlay.Editing;
                RenderOverlayControls();
            });
            moveBtn.Margin = new Thickness(8, 0, 4, 6);   // apart from the corner presets: it is an action, not a preset
            ((WrapPanel)cornerBtns[0].Parent).Children.Add(moveBtn);

            overlay.Moved += delegate
            {
                Settings.OverlayX = overlay.PosX;
                Settings.OverlayY = overlay.PosY;
                Settings.Save();
                RenderOverlayControls();
            };
            ConfigureOverlay();
        }

        delegate void Pick(int index);

        Button[] SettingRow(Grid grid, string labelTag, string[] tags, Pick pick)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Style = (Style)Window.FindResource("Mono11"), Tag = labelTag, Margin = new Thickness(0, 5, 16, 0) };
            Grid.SetRow(label, r);
            grid.Children.Add(label);
            var host = new WrapPanel();
            Grid.SetRow(host, r);
            Grid.SetColumn(host, 1);
            grid.Children.Add(host);
            var btns = new Button[tags.Length];
            for (int i = 0; i < tags.Length; i++)
            {
                int k = i;
                btns[i] = SmallPill(tags[i], delegate { pick(k); OverlaySettingsChanged(); });
                host.Children.Add(btns[i]);
            }
            return btns;
        }

        Button SmallPill(string tag, RoutedEventHandler click)
        {
            var b = Pill(tag, click);
            b.FontSize = 11;
            b.Padding = new Thickness(10, 3, 10, 3);
            b.MinHeight = 0;
            b.Margin = new Thickness(0, 0, 4, 6);
            return b;
        }

        static void Mark(Button b, bool on)
        {
            b.Background = on ? Palette.Fg : Brushes.Transparent;
            b.Foreground = on ? Palette.Bg : Palette.FgMuted;
            b.BorderBrush = on ? Palette.Fg : Palette.FgMuted;
        }

        /// <summary>
        /// Live, comparison and overlay start collapsed on every launch so the window opens on the mode controls.
        /// Collapsed live still shows the three tiles that tell whether a mode helps: FPS, CPU clock, temperature.
        /// </summary>
        void BuildCollapsible()
        {
            string[] names = { "Live", "Session", "Overlay" };
            var keyTiles = new[] { tFps, tClock, tTemp };
            for (int i = 0; i < names.Length; i++)
            {
                bool live = i == 0, closed = true;
                var head = (Button)Window.FindName(names[i] + "Head");
                var body = (FrameworkElement)Window.FindName(names[i] + "Body");
                var chevron = (TextBlock)Window.FindName(names[i] + "Chevron");
                Action apply = delegate
                {
                    chevron.Text = closed ? "▸" : "▾";
                    if (!live)
                    {
                        body.Visibility = closed ? Visibility.Collapsed : Visibility.Visible;
                        return;
                    }
                    foreach (var t in AllTiles())   // UniformGrid skips collapsed children, so the 3 fill the row
                        t.Root.Visibility = !closed || Array.IndexOf(keyTiles, t) >= 0 ? Visibility.Visible : Visibility.Collapsed;
                };
                head.Click += delegate { closed = !closed; apply(); };
                apply();
            }
        }

        void BuildApp(WrapPanel row)
        {
            autostartBtn = Pill(null, delegate
            {
                Autostart.Enabled = !Autostart.Enabled;
                RenderOverlayControls();
            });
            row.Children.Add(autostartBtn);
            adminBtn = Pill("admin.relaunch", delegate { RelaunchAsAdmin(); });
            row.Children.Add(adminBtn);
        }

        void ConfigureOverlay()
        {
            overlay.Configure(Settings.OverlayFields, Settings.OverlaySize, Settings.OverlayOpacity, Settings.OverlayBar);
            overlay.SetPosition(Settings.OverlayCorner, Settings.OverlayX, Settings.OverlayY);
        }

        void OverlaySettingsChanged()
        {
            ConfigureOverlay();
            Settings.Save();
            RenderOverlayControls();
        }

        void ToggleOverlay()
        {
            if (overlay.Visible) overlay.Hide();
            else
            {
                overlay.Show();
                overlay.SetMode(busy ? pending : current, busy);
                if (last != null) overlay.Update(last);
            }
            Settings.OverlayOn = overlay.Visible;
            Settings.Save();
            RenderOverlayControls();
        }

        void RelaunchAsAdmin()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--wait") { Verb = "runas", UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception) { return; }   // UAC declined: keep running
            Exit();
        }

        void RenderOverlayControls()
        {
            bool on = overlay.Visible;
            overlayBtn.Content = L.S(on ? "ov.on" : "ov.off");
            overlayBtn.Background = on ? Palette.Red : Brushes.Transparent;
            overlayBtn.BorderBrush = on ? Palette.Red : Palette.FgMuted;
            overlayBtn.Foreground = on ? Palette.White : Palette.Fg;
            for (int i = 0; i < fieldBtns.Length; i++) Mark(fieldBtns[i], (Settings.OverlayFields & (1 << i)) != 0);
            for (int i = 0; i < 2; i++) Mark(layoutBtns[i], Settings.OverlayBar == (i == 1));
            for (int i = 0; i < 3; i++) Mark(sizeBtns[i], Settings.OverlaySize == i);
            for (int i = 0; i < 3; i++) Mark(opacityBtns[i], Settings.OverlayOpacity == i);
            for (int i = 0; i < 4; i++) Mark(cornerBtns[i], Settings.OverlayX < 0 && Settings.OverlayCorner == i);
            bool edit = overlay.Editing, free = Settings.OverlayX >= 0;
            moveBtn.Content = L.S(edit ? "ov.done" : free ? "ov.moved" : "ov.move");
            if (edit)
            {
                moveBtn.Background = moveBtn.BorderBrush = Palette.Warn;
                moveBtn.Foreground = Palette.Bg;
            }
            else Mark(moveBtn, free);
            bool auto = Autostart.Enabled;
            autostartBtn.Content = L.S(auto ? "autostart.on" : "autostart.off");
            autostartBtn.Background = auto ? Palette.Fg : Brushes.Transparent;
            autostartBtn.Foreground = auto ? Palette.Bg : Palette.Fg;
            autostartBtn.BorderBrush = auto ? Palette.Fg : Palette.FgMuted;
            adminBtn.Visibility = IsAdmin() ? Visibility.Collapsed : Visibility.Visible;
            tray.Update(busy ? pending : current, overlay.Visible);
            var note = (TextBlock)Window.FindName("HotkeyNote");
            note.Text = (edit ? L.S("ov.movehint") + "  " : "") + (hotkeysOk ? "" : L.S("ov.hotkeyfail"));
            note.Visibility = note.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------- global hotkeys: Ctrl+Alt+1/2/3 thermal mode, Ctrl+Alt+O overlay ----------

        const int HotkeyBase = 0x4C4D;

        void RegisterHotkeys()
        {
            IntPtr h = new WindowInteropHelper(Window).Handle;
            const uint mods = 0x1 /*ALT*/ | 0x2 /*CONTROL*/ | 0x4000 /*NOREPEAT*/;
            uint[] keys = { 0x31, 0x32, 0x33, 0x4F };
            hotkeysOk = true;
            for (int i = 0; i < keys.Length; i++)
                hotkeysOk &= RegisterHotKey(h, HotkeyBase + i, mods, keys[i]);
            HwndSource.FromHwnd(h).AddHook(WndProc);
            RenderOverlayControls();
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != 0x0312) return IntPtr.Zero;   // WM_HOTKEY
            int id = wParam.ToInt32() - HotkeyBase;
            if (id >= 0 && id <= 2) SetMode((ThermalMode)id);
            else if (id == 3) ToggleOverlay();
            handled = true;
            return IntPtr.Zero;
        }

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

        void BuildSession(WrapPanel bar, Grid table)
        {
            recBtn = Pill("rec.start", delegate
            {
                recording = !recording;
                sessionMsg = recording ? "rec.on" : null;
                RenderSession();
            });
            testBtn = Pill("test.start", delegate { ToggleTest(); });
            durBtn = Pill(null, delegate
            {
                Settings.TestSeconds = Settings.TestSeconds >= 120 ? 30 : Settings.TestSeconds * 2;
                Settings.Save();
                RenderSession();
            });
            exportBtn = Pill("export", delegate { Export(true); });
            resetBtn = Pill("reset", delegate { ResetStats(); sessionMsg = null; RenderSession(); });
            loadBtn = Pill(null, delegate
            {
                Settings.TestLoad = !Settings.TestLoad;
                Settings.Save();
                RenderSession();
            });
            foreach (var b in new[] { recBtn, testBtn, durBtn, loadBtn, exportBtn, resetBtn }) bar.Children.Add(b);

            string[] heads = { "tbl.mode", "tbl.time", "tbl.fps", "tbl.game", "tbl.gpu", "tbl.cpu", "tbl.clock", "tbl.temp", "tbl.power" };
            for (int c = 0; c < heads.Length; c++)
                table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(c == 0 ? 1.6 : 1, GridUnitType.Star) });
            for (int r = 0; r < 5; r++)
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int c = 0; c < heads.Length; c++)
                Place(table, new TextBlock { Tag = heads[c], FontFamily = Palette.Mono, FontSize = 11, Foreground = Palette.FgMuted, Margin = new Thickness(0, 0, 8, 6) }, 0, c);
            Place(table, new Rectangle { Height = 1, Fill = Palette.Outline, Margin = new Thickness(0, 0, 0, 4) }, 1, 0, heads.Length);

            for (int m = 0; m < 3; m++)
            {
                var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                var name = new TextBlock { Tag = "mode." + m, FontFamily = Palette.Mono, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 8, 5) };
                sp.Children.Add(dot);
                sp.Children.Add(name);
                rowDots[m] = dot;
                Place(table, sp, m + 2, 0);
                for (int c = 1; c < heads.Length; c++)
                {
                    cells[m, c] = new TextBlock { FontFamily = Palette.Mono, FontSize = 13, Margin = new Thickness(0, 5, 8, 5), VerticalAlignment = VerticalAlignment.Center };
                    Place(table, cells[m, c], m + 2, c);
                }
            }
        }

        static void Place(Grid g, UIElement e, int row, int col, int span = 1)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            Grid.SetColumnSpan(e, span);
            g.Children.Add(e);
        }

        // ---------- lifecycle ----------

        /// <summary>Started from Windows login: create the window handle (hotkeys) without showing it.</summary>
        public void StartHidden()
        {
            new WindowInteropHelper(Window).EnsureHandle();
        }

        void ShowWindow()
        {
            Window.Show();
            if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
            Window.Activate();
        }

        void Exit()
        {
            if (exiting) return;
            exiting = true;
            Shutdown();
            tray.Dispose();
            Window.Close();
            Application.Current.Shutdown();
        }

        void Start()
        {
            if (started) return;
            started = true;
            var waiter = new Thread(delegate()
            {
                while (true)
                {
                    if (WaitHandle.WaitAny(new WaitHandle[] { showSignal, stop }) != 0) return;
                    Ui(ShowWindow);
                }
            }) { IsBackground = true, Name = "show-signal" };
            waiter.Start();
            Relayout();
            RefreshPower();
            ThreadPool.QueueUserWorkItem(delegate { ReadBootDefault(); });
            if (Settings.OverlayOn) ToggleOverlay();
            var t = new Thread(SamplerLoop) { IsBackground = true, Name = "sampler" };
            t.Start();
        }

        void SamplerLoop()
        {
            try { sensors = new Sensors(frameCounter); }
            catch (Exception ex) { Program.LogError(ex); return; }
            frameCounter.Start();
            int n = 0;
            while (true)
            {
                var t0 = DateTime.UtcNow;
                Sample s = null;
                try { s = sensors.Read(n % 2 == 0); }
                catch (Exception ex) { Program.LogError(ex); }
                n++;
                if (s != null) Ui(delegate { OnSample(s); });
                int wait = 1000 - (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                if (stop.WaitOne(Math.Max(50, wait))) return;
            }
        }

        void Shutdown()
        {
            if (testThread != null)
            {
                testCancel.Set();
                testThread.Join(15000);   // its finally block restores the original mode
            }
            stop.Set();
            frameCounter.Stop();
            overlay.Close();
            IntPtr h = new WindowInteropHelper(Window).Handle;
            for (int i = 0; i < 4; i++) UnregisterHotKey(h, HotkeyBase + i);
        }

        void Ui(Action a)
        {
            try { Window.Dispatcher.BeginInvoke(a); }
            catch (Exception) { }
        }

        void ReadDeviceInfo()
        {
            string model = Wmi("SELECT Version FROM Win32_ComputerSystemProduct", "Version");
            string bios = Wmi("SELECT SMBIOSBIOSVersion FROM Win32_BIOS", "SMBIOSBIOSVersion");
            string cpu = Wmi("SELECT Name FROM Win32_Processor", "Name");
            string gpu = Wmi("SELECT Name FROM Win32_VideoController", "Name");
            Ui(delegate
            {
                device.Text = (model ?? "").ToUpperInvariant();
                footer.Text = string.Join("  ·  ", new[] { L.S(IsAdmin() ? "footer.admin" : "footer.noadmin"), "BIOS " + bios, cpu, gpu });
            });
        }

        static bool IsAdmin()
        {
            using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        static string Wmi(string query, string prop)
        {
            try
            {
                using (var s = new ManagementObjectSearcher(query))
                using (var col = s.Get())
                    foreach (ManagementBaseObject o in col)
                        using (o) return Convert.ToString(o[prop]).Trim();
            }
            catch (Exception) { }
            return null;
        }

        void DarkTitleBar()
        {
            if (!Palette.Dark) return;
            try
            {
                int on = 1;
                IntPtr h = new WindowInteropHelper(Window).Handle;
                if (DwmSetWindowAttribute(h, 20, ref on, 4) != 0) DwmSetWindowAttribute(h, 19, ref on, 4);
            }
            catch (Exception) { }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void Relayout()
        {
            double w = Window.ActualWidth;
            tileGrid.Columns = w >= 900 ? 3 : w >= 600 ? 2 : 1;
            modeGrid.Columns = w >= 760 ? 3 : 1;
        }

        // ---------- language ----------

        void ApplyLang()
        {
            Translate(Window);
            for (int i = 0; i < 2; i++)
            {
                bool active = (i == 0) == L.Vi;
                langBtns[i].Background = active ? Palette.Red : Brushes.Transparent;
                langBtns[i].BorderBrush = active ? Palette.Red : Palette.FgMuted;
                langBtns[i].Foreground = active ? Palette.White : Palette.FgMuted;
            }
            RenderModes();
            RenderPower();
            RenderSession();
            RenderOverlayControls();
            if (last != null) RenderSample(last, false);
            ThreadPool.QueueUserWorkItem(delegate { ReadDeviceInfo(); });
        }

        static void Translate(DependencyObject node)
        {
            var tb = node as TextBlock;
            if (tb != null && tb.Tag is string) tb.Text = L.S((string)tb.Tag);
            var btn = node as Button;
            if (btn != null && btn.Tag is string) btn.Content = L.S((string)btn.Tag);
            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                var d = child as DependencyObject;
                if (d != null) Translate(d);
            }
        }

        // ---------- thermal mode ----------

        void SetMode(ThermalMode m)
        {
            if (busy || testThread != null || m == current || !Thermal.Supported) return;
            busy = true;
            pending = m;
            modeError = null;
            RenderModes();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err = Thermal.Apply(m);
                ThermalMode now = Thermal.Read();
                Program.LogMode("app set " + Thermal.Names[(int)m] + " -> read " + (Thermal.LastRaw ?? "?") + (err == null ? "" : " error: " + err));
                Ui(delegate
                {
                    busy = false;
                    modeError = err;
                    current = now;
                    RenderModes();
                });
            });
        }

        void RenderModes()
        {
            for (int i = 0; i < 3; i++)
            {
                bool active = (int)current == i;
                var c = cards[i];
                c.Btn.Background = active ? Palette.Fg : Palette.Surface;
                c.Btn.Foreground = active ? Palette.Bg : Palette.Fg;
                c.Btn.BorderBrush = active ? Palette.Fg : Palette.Outline;
                c.Dot.Fill = active ? Palette.Red : (busy && (int)pending == i ? Palette.Warn : Palette.FgMuted);
                c.Btn.Opacity = testThread != null && !active ? 0.55 : 1;
                System.Windows.Automation.AutomationProperties.SetName(c.Btn, L.ModeName((ThermalMode)i) + (active ? " ✓" : ""));
            }

            string text;
            Brush dot;
            if (!Thermal.Supported) { text = L.S(IsAdmin() ? "status.unsupported" : "status.needadmin"); dot = Palette.Bad; }
            else if (busy) { text = L.F("status.applying", L.ModeName(pending)); dot = Palette.Warn; }
            else if (modeError != null) { text = L.F("status.error", modeError); dot = Palette.Bad; }
            else if (current == ThermalMode.Unknown) { text = L.S("status.unknown"); dot = Palette.FgMuted; }
            else
            {
                text = L.F(Thermal.Live ? "status.live" : "status.verified", Thermal.Names[(int)current]);
                if (vantage) { text += "  ·  " + L.S("status.vantage"); dot = Palette.Warn; }
                else dot = Palette.Ok;
                if (testThread != null) text += "  ·  " + L.S("status.testlock");
            }
            modeStatus.Text = text;
            modeStatus.Foreground = dot == Palette.Bad ? Palette.Bad : Palette.FgMuted;
            modeStatusDot.Fill = dot;
            bool live = Thermal.Live;
            bootStatus.Visibility = bootBtn.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            bootStatus.Text = L.F("status.boot", bootMode == ThermalMode.Unknown ? "—" : Thermal.Names[(int)bootMode]);
            bootBtn.IsEnabled = live && !busy && testThread == null && current != ThermalMode.Unknown && current != bootMode;
            overlay.SetMode(busy ? pending : current, busy);
            if (overlayBtn != null) tray.Update(busy ? pending : current, overlay.Visible);
        }

        // ---------- windows power mode ----------

        void RefreshPower()
        {
            powerIdx = PowerMode.Read();
            RenderPower();
        }

        void RenderPower()
        {
            for (int i = 0; i < 3; i++)
            {
                bool active = i == powerIdx;
                powerBtns[i].Background = active ? Palette.Red : Brushes.Transparent;
                powerBtns[i].BorderBrush = active ? Palette.Red : Palette.FgMuted;
                powerBtns[i].Foreground = active ? Palette.White : Palette.Fg;
            }
        }

        // ---------- live ----------

        void OnSample(Sample s)
        {
            if (s.ModeRead && !busy && testThread == null && s.Mode != current)
            {
                Program.LogMode((current == ThermalMode.Unknown ? "startup read " : "external change from " + Thermal.Names[(int)current] + " to ")
                    + (s.Mode == ThermalMode.Unknown ? "?" : Thermal.Names[(int)s.Mode]) + " (" + (Thermal.LastRaw ?? "?") + ")");
                current = s.Mode;
                RenderModes();
            }
            else if (s.ModeRead && !Thermal.Supported) RenderModes();
            if (++tick % 5 == 0)
            {
                RefreshPower();
                bool v = Its.VantageRunning();
                if (v != vantage) { vantage = v; RenderModes(); }
            }

            bool rec = testThread != null ? testRecording : recording;
            ThermalMode attr = testThread != null ? (ThermalMode)testMode : current;
            if (rec && attr != ThermalMode.Unknown)
            {
                s.Mode = attr;
                stats[(int)attr].Add(s);
                log.Add(s);
            }
            else s.Mode = ThermalMode.Unknown;

            last = s;
            RenderSample(s, true);
            overlay.Update(s);
            RenderSession();
        }

        static string Pct(double v) { return v.ToString("0", Inv) + "%"; }

        void RenderSample(Sample s, bool push)
        {
            if (!push)
            {
                // Language change: re-render text without adding history.
                foreach (var t in AllTiles()) t.Chart.InvalidateVisual();
            }
            Action<Tile, string, string, double, Brush> set = (t, v, sub, chart, brush) =>
            {
                if (push) t.Set(v, sub, chart, brush);
                else { t.Value.Text = v; t.Sub.Text = sub; t.Value.Foreground = brush ?? Palette.Fg; }
            };

            if (double.IsNaN(s.Fps))
                set(tFps, "—", frameCounter.Error != null ? L.F("sub.fpserr", frameCounter.Error) : L.S("sub.nofps"), double.NaN, null);
            else
            {
                if (push) tFps.Chart.Max = Math.Max(tFps.Chart.Max, Math.Ceiling(s.Fps / 30) * 30);
                set(tFps, s.Fps.ToString("0", Inv), L.F("sub.fps", s.Fps1Low.ToString("0", Inv), s.FrameMs.ToString("0.0", Inv), s.FpsName), s.Fps,
                    s.Fps < 30 ? Palette.Warn : null);
            }
            set(tGame, s.GameName == null ? "—" : Pct(s.GpuGame), s.GameName ?? L.S("sub.nogame"), s.GpuGame, null);
            set(tGpu, Pct(s.GpuTotal), L.S("sub.gpu"), s.GpuTotal, null);
            set(tCpu, Pct(s.CpuLoad), L.S("sub.cpu"), s.CpuLoad, null);
            set(tClock, s.CpuGhz.ToString("0.00", Inv) + " GHz", L.F("sub.limit", s.CpuThrottle.ToString("0", Inv)), s.CpuGhz,
                s.CpuThrottle >= 5 ? Palette.Warn : null);

            double t2 = s.TempC;
            set(tTemp, double.IsNaN(t2) ? "—" : t2.ToString("0", Inv) + "°C", L.S(double.IsNaN(t2) && !IsAdmin() ? "sub.tempadmin" : "sub.temp"), t2,
                t2 >= 85 ? Palette.Bad : t2 >= 75 ? Palette.Warn : null);

            string batt = s.BatteryPct < 0 ? "—" : s.BatteryPct.ToString(Inv);
            if (double.IsNaN(s.PowerW)) set(tPow, "AC", L.F("sub.ac", batt), double.NaN, null);
            else set(tPow, s.PowerW.ToString("0.0", Inv) + " W", L.F("sub.battery", batt), s.PowerW, null);

            set(tRam, Pct(s.RamPct), L.F("sub.ram", s.RamUsedGb.ToString("0.0", Inv), s.RamTotalGb.ToString("0", Inv)), s.RamPct, null);
            if (s.RamTotalGb > 0) tVram.Chart.Max = Math.Max(4, s.RamTotalGb / 2);
            set(tVram, (s.GpuSharedGb + s.GpuDedicatedGb).ToString("0.0", Inv) + " GB",
                L.F("sub.vram", s.GpuSharedGb.ToString("0.0", Inv), s.GpuDedicatedGb.ToString("0.0", Inv)),
                s.GpuSharedGb + s.GpuDedicatedGb, null);

            liveRight.Text = s.GameName == null ? "" : L.F("live.top", s.GameName);
        }

        // ---------- session ----------

        void ResetStats()
        {
            for (int i = 0; i < 3; i++) stats[i] = new ModeStats();
            log.Clear();
        }

        void RenderSession()
        {
            bool testing = testThread != null;
            recBtn.Content = L.S(recording ? "rec.stop" : "rec.start");
            recBtn.Background = recording ? Palette.Red : Brushes.Transparent;
            recBtn.BorderBrush = recording ? Palette.Red : Palette.FgMuted;
            recBtn.Foreground = recording ? Palette.White : Palette.Fg;
            recBtn.IsEnabled = !testing;

            testBtn.Content = L.S(testing ? "test.cancel" : "test.start");
            testBtn.Background = testing ? Palette.Red : Brushes.Transparent;
            testBtn.BorderBrush = testing ? Palette.Red : Palette.FgMuted;
            testBtn.Foreground = testing ? Palette.White : Palette.Fg;
            testBtn.IsEnabled = testing || (Thermal.Supported && !busy);

            durBtn.Content = L.F("test.dur", Settings.TestSeconds);
            durBtn.IsEnabled = !testing;
            loadBtn.Content = L.S(Settings.TestLoad ? "test.load.on" : "test.load.off");
            loadBtn.Background = Settings.TestLoad ? Palette.Fg : Brushes.Transparent;
            loadBtn.Foreground = Settings.TestLoad ? Palette.Bg : Palette.Fg;
            loadBtn.BorderBrush = Settings.TestLoad ? Palette.Fg : Palette.FgMuted;
            loadBtn.IsEnabled = !testing;
            exportBtn.IsEnabled = log.Count > 0;
            resetBtn.IsEnabled = !testing && log.Count > 0;

            if (testing)
            {
                int left = Math.Max(0, (int)Math.Ceiling(TimeSpan.FromTicks(Interlocked.Read(ref testPhaseEndTicks) - DateTime.UtcNow.Ticks).TotalSeconds));
                string mode = L.ModeName((ThermalMode)testMode);
                testStatus.Text = testMode < 0 ? "" : L.F(testWarm ? "test.warm" : "test.run", mode, left);
                testStatus.Foreground = Palette.Fg;
            }
            else
            {
                testStatus.Text = sessionMsg == null ? L.F("test.hint", Settings.TestSeconds)
                    : sessionMsg.StartsWith("test.") ? L.F(sessionMsg, L.ModeName(current))
                    : sessionMsg.StartsWith("!") ? sessionMsg.Substring(1)
                    : L.S(sessionMsg);
                testStatus.Foreground = sessionMsg == null ? Palette.FgMuted : Palette.Fg;
            }

            ThermalMode attr = testing ? (ThermalMode)testMode : current;
            bool rec = testing ? testRecording : recording;
            for (int m = 0; m < 3; m++)
            {
                var st = stats[m];
                rowDots[m].Fill = rec && (int)attr == m ? Palette.Red : Palette.DotOff;
                cells[m, 1].Text = st.N == 0 ? "—" : TimeSpan.FromSeconds(st.N).ToString(@"m\:ss");
                cells[m, 2].Text = st.FpsN == 0 ? "—" : (st.Fps / st.FpsN).ToString("0", Inv) + " (" + (st.Low / st.FpsN).ToString("0", Inv) + ")";
                cells[m, 3].Text = st.N == 0 ? "—" : Pct(st.Game / st.N);
                cells[m, 4].Text = st.N == 0 ? "—" : Pct(st.Gpu / st.N);
                cells[m, 5].Text = st.N == 0 ? "—" : Pct(st.Cpu / st.N);
                cells[m, 6].Text = st.N == 0 ? "—" : (st.Ghz / st.N).ToString("0.00", Inv) + " GHz";
                cells[m, 7].Text = st.TempN == 0 ? "—" : (st.Temp / st.TempN).ToString("0.0", Inv) + "°C";
                cells[m, 8].Text = st.PowN == 0 ? "—" : (st.Pow / st.PowN).ToString("0.0", Inv) + " W";
                for (int c = 1; c < 9; c++) cells[m, c].Foreground = st.N == 0 ? Palette.FgMuted : Palette.Fg;
            }
        }

        void ToggleTest()
        {
            if (testThread != null)
            {
                testCancel.Set();
                return;
            }
            if (busy || !Thermal.Supported) return;

            ResetStats();
            recording = false;
            sessionMsg = null;
            testCancel.Reset();
            ThermalMode restore = current;
            int secs = Settings.TestSeconds;
            bool load = Settings.TestLoad;

            testThread = new Thread(delegate()
            {
                string err = null;
                bool cancelled = false;
                try
                {
                    if (load) CpuStress.Start();
                    for (int i = 0; i < 3 && err == null && !cancelled; i++)
                    {
                        int mode = i;
                        testRecording = false;
                        testWarm = true;
                        testMode = mode;
                        Ui(delegate { busy = true; pending = (ThermalMode)mode; RenderModes(); RenderSession(); });
                        err = Thermal.Apply((ThermalMode)mode);
                        ThermalMode now = Thermal.Read();
                        string e1 = err;
                        Ui(delegate { busy = false; current = now; modeError = e1; RenderModes(); });
                        if (err != null) break;

                        Interlocked.Exchange(ref testPhaseEndTicks, DateTime.UtcNow.AddSeconds(WarmupSeconds).Ticks);
                        if (testCancel.WaitOne(WarmupSeconds * 1000)) { cancelled = true; break; }

                        Interlocked.Exchange(ref testPhaseEndTicks, DateTime.UtcNow.AddSeconds(secs).Ticks);
                        testWarm = false;
                        testRecording = true;
                        cancelled = testCancel.WaitOne(secs * 1000);
                        testRecording = false;
                    }
                }
                catch (Exception ex) { err = ex.Message; Program.LogError(ex); }
                finally
                {
                    CpuStress.Stop();
                    testRecording = false;
                    string rerr = restore == ThermalMode.Unknown ? null : Thermal.Apply(restore);
                    ThermalMode now = Thermal.Read();
                    string final = err ?? rerr;
                    bool wasCancelled = cancelled;
                    Ui(delegate
                    {
                        testThread = null;
                        testMode = -1;
                        busy = false;
                        current = now;
                        modeError = final;
                        sessionMsg = final != null ? "!" + L.F("status.error", final) : wasCancelled ? "test.cancelled" : "test.done";
                        if (final == null && !wasCancelled)
                        {
                            Export(false);   // keep the result even if the app is closed without exporting
                            sessionMsg = "!" + L.F("test.done", L.ModeName(current)) + " " + sessionMsg.Substring(1);
                        }
                        RenderModes();
                        RenderSession();
                    });
                }
            }) { IsBackground = true, Name = "autotest" };
            testThread.Start();
            RenderModes();
            RenderSession();
        }

        /// <summary>Writes the session log to Documents\LenoMode; reveal opens it in Explorer.</summary>
        void Export(bool reveal)
        {
            if (log.Count == 0) return;
            try
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LenoMode");
                Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv) + ".csv");
                var sb = new StringBuilder();
                sb.AppendLine("time,thermal_mode,game,fps,fps_1pct_low,frame_ms,fps_process,game_gpu_pct,gpu_total_pct,cpu_load_pct,cpu_ghz,cpu_throttle_pct,temp_c,power_w,on_ac,ram_pct,gpu_mem_gb");
                foreach (var s in log)
                {
                    sb.Append(s.Time.ToString("yyyy-MM-dd HH:mm:ss", Inv)).Append(',')
                      .Append(s.Mode == ThermalMode.Unknown ? "" : Thermal.Names[(int)s.Mode]).Append(',')
                      .Append(s.GameName ?? "").Append(',')
                      .Append(double.IsNaN(s.Fps) ? "" : s.Fps.ToString("0.0", Inv)).Append(',')
                      .Append(double.IsNaN(s.Fps1Low) ? "" : s.Fps1Low.ToString("0.0", Inv)).Append(',')
                      .Append(double.IsNaN(s.FrameMs) ? "" : s.FrameMs.ToString("0.00", Inv)).Append(',')
                      .Append(s.FpsName ?? "").Append(',')
                      .Append(s.GpuGame.ToString("0.0", Inv)).Append(',')
                      .Append(s.GpuTotal.ToString("0.0", Inv)).Append(',')
                      .Append(s.CpuLoad.ToString("0.0", Inv)).Append(',')
                      .Append(s.CpuGhz.ToString("0.000", Inv)).Append(',')
                      .Append(s.CpuThrottle.ToString("0.0", Inv)).Append(',')
                      .Append(double.IsNaN(s.TempC) ? "" : s.TempC.ToString("0.0", Inv)).Append(',')
                      .Append(double.IsNaN(s.PowerW) ? "" : s.PowerW.ToString("0.00", Inv)).Append(',')
                      .Append(s.OnAc ? "1" : "0").Append(',')
                      .Append(s.RamPct.ToString("0.0", Inv)).Append(',')
                      .Append((s.GpuSharedGb + s.GpuDedicatedGb).ToString("0.00", Inv))
                      .AppendLine();
                }
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                sessionMsg = "!" + L.F("export.ok", path);
                if (reveal) Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch (Exception ex)
            {
                sessionMsg = "!" + L.F("export.fail", ex.Message);
            }
            RenderSession();
        }
    }
}
