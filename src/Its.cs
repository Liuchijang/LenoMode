using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace LenoMode
{
    /// <summary>
    /// Live thermal mode through Lenovo ITS (Intelligent Thermal Solution): DYTC commands sent to the
    /// Lenovo energy driver, the path Fn+Q / Vantage use. Takes effect immediately, unlike the BIOS
    /// SystemPerformanceMode value, which only sets the mode for the next boot (measured 2026-10-31).
    /// Command values and sequence follow Lenovo Legion Toolkit's ITSModeFeatureDriver.cs (ThinkBook path).
    /// </summary>
    static class Its
    {
        const uint IoctlEnergySmartPower = 0x8310213C;
        const uint CmdGetMode = 2, CmdGetCapabilities = 3, CmdGetExtCapabilities = 10, CmdReset = 0x1FF;
        const uint RequiredCapabilities = 0x08200001;      // valid bit + functions 5 (intelligent cooling) and 11 (ITS mode control)
        const uint FullSpeedMask = 0x00020001, FullSpeedOff = 0x000F100B;
        // Disable every auto function before a switch, in LLT's order (fn 15, 8, 7, 6, 5, 11; mode 0xF, no enable bit).
        static readonly uint[] DisableCmds = { 0x000FF001, 0x000F8001, 0x000F7001, 0x000F6001, 0x000F5001, 0x000FB001 };
        // Indexed by ThermalMode: Quiet = intelligent cooling (fn 5), Balanced = cool (fn 11 / 3), Performance = fn 11 / 2.
        static readonly uint[] SetCmds = { 0x001F5001, 0x0013B001, 0x0012B001 };

        static readonly object Gate = new object();
        static SafeFileHandle handle;
        static bool probed, supported, fullSpeed;
        static DateTime lastCall;

        public static volatile string LastRaw;
        public static volatile bool Ready;

        /// <summary>
        /// Each DYTC call goes through ACPI to the EC and is slow: the probe took ~300 ms on the UI thread
        /// (measured 2026-10-31), so it runs in the background at start.
        /// </summary>
        public static void BeginProbe()
        {
            ThreadPool.QueueUserWorkItem(delegate { bool unused = Supported; });
        }

        public static bool Supported
        {
            get
            {
                lock (Gate)
                {
                    if (!probed) Probe();
                    return supported;
                }
            }
        }

        static void Probe()
        {
            probed = true;
            try
            {
                handle = CreateFile(@"\\.\EnergyDrv", 0x3 /*FILE_READ_DATA|FILE_WRITE_DATA*/, 0x3, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0x80, IntPtr.Zero);
                if (handle.IsInvalid) { handle = null; return; }
                supported = (Send(CmdGetCapabilities) & RequiredCapabilities) == RequiredCapabilities && (Send(CmdGetMode) & 1) == 1;
                fullSpeed = (Send(CmdGetExtCapabilities) & FullSpeedMask) == FullSpeedMask;
            }
            catch (Exception) { supported = false; }
            finally { Ready = true; }
        }

        static uint Send(uint cmd)
        {
            // The EC drops commands that arrive back to back; LLT keeps a 20 ms gap.
            int wait = 20 - (int)(DateTime.UtcNow - lastCall).TotalMilliseconds;
            if (wait > 0) Thread.Sleep(wait);
            uint outv, ret;
            bool ok = DeviceIoControl(handle, IoctlEnergySmartPower, ref cmd, 4, out outv, 4, out ret, IntPtr.Zero);
            lastCall = DateTime.UtcNow;
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            return outv;
        }

        public static ThermalMode Read()
        {
            lock (Gate)
            {
                if (!probed) Probe();
                if (!supported) return ThermalMode.Unknown;
                try { return Decode(Send(CmdGetMode)); }
                catch (Exception) { return ThermalMode.Unknown; }
            }
        }

        static ThermalMode Decode(uint raw)
        {
            LastRaw = string.Format("0x{0:X8}", raw);
            if ((raw & 1) == 0) return ThermalMode.Unknown;
            uint fn = (raw >> 8) & 0xF, p = (raw >> 12) & 0xF;
            if (fn == 11 && p == 3) return ThermalMode.Balanced;
            if (fn == 11 && p == 2) return ThermalMode.Performance;
            if (fn == 0 || fn == 5 || fn == 6 || fn == 7 || fn == 8 || fn == 15) return ThermalMode.Quiet;
            if (fn == 3 && p <= 3) return ThermalMode.Quiet;
            if (fn == 13 && (p == 1 || p == 2)) return ThermalMode.Quiet;
            if (fn == 11 && (raw >> 16) == 1) return ThermalMode.Quiet;
            return ThermalMode.Unknown;
        }

        /// <summary>Switches the live mode and waits up to 3 s for the read-back. Null on success, else the error.</summary>
        public static string Apply(ThermalMode m)
        {
            if (m == ThermalMode.Unknown) return "unknown mode";
            lock (Gate)
            {
                if (!probed) Probe();
                if (!supported) return "ITS not supported";
                try
                {
                    if (fullSpeed) Send(FullSpeedOff);
                    bool reset = false;
                    if (m == ThermalMode.Quiet)
                    {
                        uint fn = (Send(CmdGetMode) >> 8) & 0xF;
                        reset = fn != 3 && fn != 4;
                    }
                    foreach (uint c in DisableCmds) Send(c);
                    if (reset) Send(CmdReset);
                    Send(SetCmds[(int)m]);

                    for (int i = 0; i < 15; i++)
                    {
                        Thread.Sleep(200);
                        if (Decode(Send(CmdGetMode)) == m) return null;
                    }
                    return "mode did not change (read " + LastRaw + ")";
                }
                catch (Exception ex) { return ex.Message; }
            }
        }

        /// <summary>Lenovo Vantage drives ITS too and switches the mode back (user-observed 2026-10-31).</summary>
        public static bool VantageRunning()
        {
            try
            {
                using (var s = new ServiceController("LenovoVantageService"))
                    return s.Status == ServiceControllerStatus.Running;
            }
            catch (Exception) { return false; }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref uint inBuf, uint inSize, out uint outBuf, uint outSize, out uint returned, IntPtr overlapped);
    }

    /// <summary>
    /// The app's thermal mode: live ITS when the driver supports it, else the BIOS value (next boot).
    /// The BIOS value is kept as the boot default.
    /// </summary>
    static class Thermal
    {
        public static string[] Names { get { return LenovoWmi.BiosValues; } }
        // Until the background probe finishes, assume ITS is there (it is on this machine) so the UI does not block or jump.
        public static bool Live { get { return !Its.Ready || Its.Supported; } }
        public static bool Supported { get { return !Its.Ready || Its.Supported || LenovoWmi.Supported; } }
        public static string LastRaw { get { return Live ? Its.LastRaw : LenovoWmi.LastRaw; } }

        public static ThermalMode Read() { return Live ? Its.Read() : LenovoWmi.Read(); }
        public static string Apply(ThermalMode m) { return Live ? Its.Apply(m) : LenovoWmi.Apply(m); }
    }
}
