using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LenoMode
{
    public sealed class Sample
    {
        public DateTime Time;
        public ThermalMode Mode = ThermalMode.Unknown;
        public bool ModeRead;
        public double CpuLoad, CpuGhz, CpuThrottle;   // CpuThrottle: % below the allowed max clock (0 = not limited)
        public double GpuTotal, GpuGame;
        public string GameName;
        public double TempC = double.NaN;
        public double PowerW = double.NaN;   // only while discharging (battery draw = whole system)
        public bool OnAc;
        public int BatteryPct = -1;
        public double RamPct, RamUsedGb, RamTotalGb;
        public double GpuSharedGb, GpuDedicatedGb;
        public double Fps = double.NaN, Fps1Low = double.NaN, FrameMs = double.NaN;
        public string FpsName;
    }

    /// <summary>
    /// Reads every instance of a perf counter category in one call and cooks rate counters
    /// from two consecutive samples (much cheaper than one PerformanceCounter per instance).
    /// </summary>
    sealed class CategoryReader
    {
        readonly PerformanceCounterCategory cat;
        readonly string[] counters;
        Dictionary<string, CounterSample> prev = new Dictionary<string, CounterSample>();

        public CategoryReader(string category, params string[] counters)
        {
            try { if (PerformanceCounterCategory.Exists(category)) cat = new PerformanceCounterCategory(category); }
            catch (Exception) { }
            this.counters = counters;
        }

        /// <summary>counter → instance → value. Empty on the first call.</summary>
        public Dictionary<string, Dictionary<string, double>> Read()
        {
            var result = new Dictionary<string, Dictionary<string, double>>();
            if (cat == null) return result;
            InstanceDataCollectionCollection data;
            try { data = cat.ReadCategory(); }
            catch (Exception) { return result; }

            var next = new Dictionary<string, CounterSample>();
            foreach (string c in counters)
            {
                InstanceDataCollection col = data[c];
                if (col == null) continue;
                var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (InstanceData d in col.Values)
                {
                    string key = c + "|" + d.InstanceName;
                    CounterSample p;
                    if (prev.TryGetValue(key, out p))
                    {
                        try { values[d.InstanceName] = CounterSample.Calculate(p, d.Sample); }
                        catch (Exception) { }
                    }
                    next[key] = d.Sample;
                }
                result[c] = values;
            }
            prev = next;
            return result;
        }
    }

    public sealed class Sensors
    {
        static readonly Regex Gpu3D = new Regex(@"^pid_(\d+)_.*engtype_3D$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly HashSet<string> NotGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dwm.exe", "csrss.exe", "System", "Idle", "LenoMode.exe", "explorer.exe",
        };

        readonly CategoryReader proc = new CategoryReader("Processor Information",
            "% Processor Utility", "% Processor Performance", "% Performance Limit");
        readonly CategoryReader gpu = new CategoryReader("GPU Engine", "Utilization Percentage");
        readonly CategoryReader gmem = new CategoryReader("GPU Adapter Memory", "Shared Usage", "Dedicated Usage");
        readonly Dictionary<int, string> names = new Dictionary<int, string>();
        readonly double baseGhz;
        readonly FrameCounter frames;
        DateTime namesFlushed = DateTime.UtcNow;

        public Sensors(FrameCounter frames)
        {
            this.frames = frames;
            baseGhz = 0;
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor"))
                using (var col = s.Get())
                    foreach (ManagementBaseObject o in col) using (o) baseGhz = Convert.ToDouble(o["MaxClockSpeed"]) / 1000.0;
            }
            catch (Exception) { }
        }

        public Sample Read(bool readMode)
        {
            var s = new Sample { Time = DateTime.Now };

            var p = proc.Read();
            s.CpuLoad = Math.Min(100, Get(p, "% Processor Utility", "_Total"));
            // % Processor Performance is relative to the base clock, so this is the effective clock.
            s.CpuGhz = baseGhz * Get(p, "% Processor Performance", "_Total") / 100.0;
            // % Performance Limit is the share of max performance still allowed (100 = no limit).
            Dictionary<string, double> lim;
            double allowed;
            s.CpuThrottle = p.TryGetValue("% Performance Limit", out lim) && lim.TryGetValue("_Total", out allowed)
                ? Math.Max(0, 100 - allowed) : 0;

            int gamePid = ReadGpu(s);
            ReadFps(s, gamePid);
            ReadGpuMemory(s);
            ReadMemory(s);
            ReadPower(s);
            s.TempC = ReadTemp();

            if (readMode)
            {
                s.Mode = Thermal.Read();
                s.ModeRead = true;
            }
            return s;
        }

        static double Get(Dictionary<string, Dictionary<string, double>> d, string counter, string inst)
        {
            Dictionary<string, double> m;
            double v;
            return d.TryGetValue(counter, out m) && m.TryGetValue(inst, out v) ? v : 0;
        }

        /// <summary>Fills GPU load; returns the pid of the top GPU app (the game), or -1.</summary>
        int ReadGpu(Sample s)
        {
            Dictionary<string, double> util;
            if (!gpu.Read().TryGetValue("Utilization Percentage", out util)) return -1;

            var perPid = new Dictionary<int, double>();
            double total = 0;
            foreach (var kv in util)
            {
                var m = Gpu3D.Match(kv.Key);
                if (!m.Success) continue;
                int pid = int.Parse(m.Groups[1].Value);
                double cur;
                perPid.TryGetValue(pid, out cur);
                perPid[pid] = cur + kv.Value;
                total += kv.Value;
            }
            s.GpuTotal = Math.Min(100, total);

            if ((DateTime.UtcNow - namesFlushed).TotalSeconds > 60) { names.Clear(); namesFlushed = DateTime.UtcNow; }
            int best = -1;
            double bestV = 0.5;
            foreach (var kv in perPid)
            {
                if (kv.Value <= bestV) continue;
                string n = Name(kv.Key);
                if (n == null || NotGames.Contains(n)) continue;
                best = kv.Key;
                bestV = kv.Value;
            }
            if (best >= 0)
            {
                s.GameName = Name(best);
                s.GpuGame = Math.Min(100, bestV);
            }
            return best;
        }

        /// <summary>FPS of the top GPU app; if it presents nothing, of the busiest presenting app.</summary>
        void ReadFps(Sample s, int gamePid)
        {
            if (frames == null || !frames.Running) return;
            var candidates = frames.ActivePids();
            if (gamePid > 0 && candidates.Remove(gamePid)) candidates.Insert(0, gamePid);
            foreach (int pid in candidates)
            {
                string n = Name(pid);
                if (n == null || NotGames.Contains(n)) continue;
                if (!frames.Read(pid, out s.Fps, out s.Fps1Low, out s.FrameMs)) continue;
                s.FpsName = n;
                return;
            }
        }

        string Name(int pid)
        {
            string n;
            if (names.TryGetValue(pid, out n)) return n;
            try { using (var pr = Process.GetProcessById(pid)) n = pr.ProcessName + ".exe"; }
            catch (Exception) { n = null; }
            names[pid] = n;
            return n;
        }

        void ReadGpuMemory(Sample s)
        {
            var d = gmem.Read();
            Dictionary<string, double> shared, dedicated;
            if (!d.TryGetValue("Shared Usage", out shared) || !d.TryGetValue("Dedicated Usage", out dedicated)) return;
            double best = -1;
            foreach (var kv in shared)
            {
                double ded;
                dedicated.TryGetValue(kv.Key, out ded);
                if (kv.Value + ded <= best) continue;
                best = kv.Value + ded;
                s.GpuSharedGb = kv.Value / 1073741824.0;
                s.GpuDedicatedGb = ded / 1073741824.0;
            }
        }

        static void ReadMemory(Sample s)
        {
            var m = new MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (!GlobalMemoryStatusEx(ref m)) return;
            s.RamTotalGb = m.ullTotalPhys / 1073741824.0;
            s.RamUsedGb = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
            s.RamPct = s.RamTotalGb > 0 ? s.RamUsedGb / s.RamTotalGb * 100 : 0;
        }

        static void ReadPower(Sample s)
        {
            SYSTEM_POWER_STATUS ps;
            if (GetSystemPowerStatus(out ps))
            {
                s.OnAc = ps.ACLineStatus == 1;
                if (ps.BatteryLifePercent <= 100) s.BatteryPct = ps.BatteryLifePercent;
            }
            try
            {
                using (var q = new ManagementObjectSearcher(@"root\WMI", "SELECT Discharging, DischargeRate FROM BatteryStatus"))
                using (var col = q.Get())
                {
                    foreach (ManagementBaseObject o in col)
                    {
                        using (o)
                        {
                            if (!(o["Discharging"] is bool) || !(bool)o["Discharging"]) continue;
                            double mw = Convert.ToDouble(o["DischargeRate"]);
                            if (mw > 0) s.PowerW = (double.IsNaN(s.PowerW) ? 0 : s.PowerW) + mw / 1000.0;
                        }
                    }
                }
            }
            catch (Exception) { }
        }

        /// <summary>ACPI thermal zone (board/chassis sensor, not the CPU die). Max across zones.</summary>
        static double ReadTemp()
        {
            double max = double.NaN;
            try
            {
                using (var q = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                using (var col = q.Get())
                {
                    foreach (ManagementBaseObject o in col)
                    {
                        using (o)
                        {
                            double c = Convert.ToDouble(o["CurrentTemperature"]) / 10.0 - 273.15;
                            if (c > 0 && c < 150 && (double.IsNaN(max) || c > max)) max = c;
                        }
                    }
                }
            }
            catch (Exception) { }
            return max;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
    }
}
