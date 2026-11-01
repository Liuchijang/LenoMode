using System;
using System.Management;
using System.Runtime.InteropServices;

namespace LenoMode
{
    public enum ThermalMode { Unknown = -1, Quiet = 0, Balanced = 1, Performance = 2 }

    /// <summary>
    /// BIOS "Thermal mode" (SystemPerformanceMode) through Lenovo's WMI BIOS interface.
    /// Same effect as changing it in the BIOS menu, applied immediately. Needs admin.
    /// </summary>
    public static class LenovoWmi
    {
        // Ordered by how the firmware behaves on this ThinkBook (user-observed 2026-10-31), not by the names:
        // Intelligent Cooling holds temps low (~50 C) and drops FPS, Battery Saving is the balanced one.
        public static readonly string[] BiosValues = { "Intelligent Cooling", "Battery Saving", "Extreme Performance" };
        const string Ns = @"root\WMI";
        const string Key = "SystemPerformanceMode";
        static readonly object Gate = new object();

        public static volatile bool Supported = true;
        public static volatile string LastRaw;

        public static ThermalMode Read()
        {
            lock (Gate)
            {
                string raw = null;
                try
                {
                    using (var s = new ManagementObjectSearcher(Ns, "SELECT CurrentSetting FROM Lenovo_BiosSetting"))
                    using (var col = s.Get())
                    {
                        foreach (ManagementBaseObject o in col)
                        {
                            using (o)
                            {
                                var v = o["CurrentSetting"] as string;
                                if (v == null || !v.StartsWith(Key + ",", StringComparison.OrdinalIgnoreCase)) continue;
                                raw = v.Substring(v.IndexOf(',') + 1);
                                int semi = raw.IndexOf(';');
                                if (semi >= 0) raw = raw.Substring(0, semi);
                            }
                        }
                    }
                }
                catch (ManagementException) { }
                catch (COMException) { }
                catch (UnauthorizedAccessException) { }

                Supported = raw != null;
                LastRaw = raw;
                for (int i = 0; i < BiosValues.Length; i++)
                    if (string.Equals(raw, BiosValues[i], StringComparison.OrdinalIgnoreCase)) return (ThermalMode)i;
                return ThermalMode.Unknown;
            }
        }

        /// <summary>Sets and saves the mode, then reads it back. Returns null on success, else the error.</summary>
        public static string Apply(ThermalMode m)
        {
            if (m == ThermalMode.Unknown) return "unknown mode";
            lock (Gate)
            {
                try
                {
                    string r = Invoke("Lenovo_SetBiosSetting", "SetBiosSetting", Key + "," + BiosValues[(int)m] + ";");
                    if (r != "Success") return "SetBiosSetting: " + r;
                    r = Invoke("Lenovo_SaveBiosSettings", "SaveBiosSettings", "");
                    if (r != "Success") return "SaveBiosSettings: " + r;
                }
                catch (Exception ex) { return ex.Message; }
            }
            ThermalMode now = Read();
            return now == m ? null : "read back " + (LastRaw ?? "?");
        }

        static string Invoke(string cls, string method, string arg)
        {
            using (var s = new ManagementObjectSearcher(Ns, "SELECT * FROM " + cls))
            using (var col = s.Get())
            {
                foreach (ManagementObject o in col)
                {
                    using (o)
                    using (var inp = o.GetMethodParameters(method))
                    {
                        inp["parameter"] = arg;
                        using (var outp = o.InvokeMethod(method, inp, null))
                            return Convert.ToString(outp["return"]);
                    }
                }
            }
            return "no instance";
        }
    }

    /// <summary>Windows "Power mode" slider (power overlay). Applies to the current power source only.</summary>
    public static class PowerMode
    {
        public static readonly Guid[] Overlays =
        {
            new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a"), // best power efficiency
            Guid.Empty,                                       // balanced
            new Guid("ded574b5-45a0-4f42-8737-46345c09c238"), // best performance
        };

        [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid g);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(Guid g);

        public static int Read()
        {
            try
            {
                Guid g;
                if (PowerGetEffectiveOverlayScheme(out g) != 0) return -1;
                return Array.IndexOf(Overlays, g);
            }
            catch (Exception) { return -1; }
        }

        public static bool Set(int i)
        {
            try { return PowerSetActiveOverlayScheme(Overlays[i]) == 0; }
            catch (Exception) { return false; }
        }
    }
}
