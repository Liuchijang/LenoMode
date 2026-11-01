using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace LenoMode
{
    /// <summary>
    /// Counts frames per process from the DXGI and D3D9 "Present start" ETW events, the same source PresentMon
    /// reads. Covers DirectX 9/10/11/12 games; Vulkan/OpenGL titles that skip DXGI are not seen. Needs admin.
    /// Native structs are written by offset (x64 layouts) to keep the P/Invoke surface small.
    /// </summary>
    public sealed class FrameCounter : IDisposable
    {
        const string SessionName = "LenoMode-Presents";
        const uint DxgiData1 = 0xCA11C036, D3D9Data1 = 0x783ACA0A;
        static readonly Guid DxgiProvider = new Guid("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
        static readonly Guid D3D9Provider = new Guid("783ACA0A-790E-4D7F-8451-AA850511C6B9");
        const long Second = 10000000L;          // event timestamps are FILETIME (100 ns)
        const long FpsWindow = 2 * Second, Keep = 10 * Second;

        // EVENT_TRACE_PROPERTIES (x64): 120 bytes, logger name appended after it.
        const int PropsSize = 120, PropsTotal = PropsSize + 1024;
        // EVENT_TRACE_LOGFILEW (x64)
        const int LogfileSize = 448, LfLoggerName = 8, LfProcessTraceMode = 28, LfEventRecordCallback = 424;
        const uint ProcessTraceModeRealTime = 0x100, ProcessTraceModeEventRecord = 0x10000000;
        const ulong InvalidTrace = ulong.MaxValue;

        readonly object gate = new object();
        readonly Dictionary<int, Queue<long>> frames = new Dictionary<int, Queue<long>>();
        readonly EventRecordCallback callback;
        ulong session, trace = InvalidTrace;
        IntPtr logfile, loggerName;
        Thread thread;

        public string Error;

        public FrameCounter()
        {
            callback = OnEvent;   // kept in a field so the GC cannot collect it while ETW calls it
        }

        public bool Running { get { return thread != null; } }

        public void Start()
        {
            try
            {
                Control(1);   // stop a session left over from a crash
                IntPtr props = NewProperties();
                uint r;
                try { r = StartTrace(out session, SessionName, props); }
                finally { Marshal.FreeHGlobal(props); }
                if (r != 0) { Error = "StartTrace " + r; return; }

                Guid g = DxgiProvider;
                r = EnableTraceEx2(session, ref g, 1, 5, ulong.MaxValue, 0, 0, IntPtr.Zero);
                if (r != 0) { Error = "EnableTrace DXGI " + r; Stop(); return; }
                g = D3D9Provider;
                EnableTraceEx2(session, ref g, 1, 5, ulong.MaxValue, 0, 0, IntPtr.Zero);

                logfile = Zeroed(LogfileSize);
                loggerName = Marshal.StringToHGlobalUni(SessionName);
                Marshal.WriteIntPtr(logfile, LfLoggerName, loggerName);
                Marshal.WriteInt32(logfile, LfProcessTraceMode, unchecked((int)(ProcessTraceModeRealTime | ProcessTraceModeEventRecord)));
                Marshal.WriteIntPtr(logfile, LfEventRecordCallback, Marshal.GetFunctionPointerForDelegate(callback));
                trace = OpenTrace(logfile);
                if (trace == InvalidTrace) { Error = "OpenTrace " + Marshal.GetLastWin32Error(); Stop(); return; }

                ulong t = trace;
                thread = new Thread(delegate() { ProcessTrace(new[] { t }, 1, IntPtr.Zero, IntPtr.Zero); })
                {
                    IsBackground = true, Name = "etw-presents",
                };
                thread.Start();
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                Stop();
            }
        }

        public void Stop()
        {
            try { Control(1); } catch (Exception) { }   // ends ProcessTrace
            if (trace != InvalidTrace) { CloseTrace(trace); trace = InvalidTrace; }
            if (thread != null) { thread.Join(2000); thread = null; }
            if (logfile != IntPtr.Zero) { Marshal.FreeHGlobal(logfile); logfile = IntPtr.Zero; }
            if (loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(loggerName); loggerName = IntPtr.Zero; }
        }

        public void Dispose() { Stop(); }

        void Control(uint code)
        {
            IntPtr p = NewProperties();
            try { ControlTrace(0, SessionName, p, code); }
            finally { Marshal.FreeHGlobal(p); }
        }

        static IntPtr Zeroed(int size)
        {
            IntPtr p = Marshal.AllocHGlobal(size);
            Marshal.Copy(new byte[size], 0, p, size);
            return p;
        }

        static IntPtr NewProperties()
        {
            IntPtr p = Zeroed(PropsTotal);
            Marshal.WriteInt32(p, 0, PropsTotal);       // Wnode.BufferSize
            Marshal.WriteInt32(p, 40, 1);               // Wnode.ClientContext = QPC
            Marshal.WriteInt32(p, 44, 0x00020000);      // Wnode.Flags = WNODE_FLAG_TRACED_GUID
            Marshal.WriteInt32(p, 48, 64);              // BufferSize (KB)
            Marshal.WriteInt32(p, 64, 0x100);           // LogFileMode = EVENT_TRACE_REAL_TIME_MODE
            Marshal.WriteInt32(p, 68, 1);               // FlushTimer (s): events reach us within ~1 s
            Marshal.WriteInt32(p, 116, PropsSize);      // LoggerNameOffset
            return p;
        }

        // EVENT_RECORD starts with EVENT_HEADER: ProcessId @12, TimeStamp @16, ProviderId @24, Descriptor.Id @40.
        void OnEvent(IntPtr rec)
        {
            uint provider = unchecked((uint)Marshal.ReadInt32(rec, 24));
            ushort id = unchecked((ushort)Marshal.ReadInt16(rec, 40));
            bool present = provider == DxgiData1 ? id == 42 || id == 55   // Present_Start, PresentMultiplaneOverlay_Start
                         : provider == D3D9Data1 && id == 1;              // D3D9 Present_Start
            if (!present) return;

            int pid = Marshal.ReadInt32(rec, 12);
            long ts = Marshal.ReadInt64(rec, 16);
            lock (gate)
            {
                Queue<long> q;
                if (!frames.TryGetValue(pid, out q)) frames[pid] = q = new Queue<long>();
                q.Enqueue(ts);
                while (q.Count > 0 && q.Peek() < ts - Keep) q.Dequeue();
            }
        }

        /// <summary>Processes that presented in the last few seconds, busiest first.</summary>
        public List<int> ActivePids()
        {
            long now = DateTime.UtcNow.ToFileTimeUtc();
            var list = new List<KeyValuePair<int, int>>();
            lock (gate)
            {
                var dead = new List<int>();
                foreach (var kv in frames)
                {
                    if (kv.Value.Count == 0 || now - Last(kv.Value) > 3 * Second) { dead.Add(kv.Key); continue; }
                    list.Add(new KeyValuePair<int, int>(kv.Key, kv.Value.Count));
                }
                foreach (int pid in dead) frames.Remove(pid);
            }
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            return list.ConvertAll(kv => kv.Key);
        }

        /// <summary>FPS over the last 2 s of frames, 1% low over the last 10 s, average frame time.</summary>
        public bool Read(int pid, out double fps, out double low1, out double frameMs)
        {
            fps = low1 = frameMs = double.NaN;
            long[] ts;
            lock (gate)
            {
                Queue<long> q;
                if (!frames.TryGetValue(pid, out q) || q.Count < 3) return false;
                ts = q.ToArray();
            }
            long last = ts[ts.Length - 1];
            if (DateTime.UtcNow.ToFileTimeUtc() - last > 3 * Second) return false;   // stopped presenting

            int first = ts.Length - 1;
            while (first > 0 && ts[first - 1] >= last - FpsWindow) first--;
            int n = ts.Length - 1 - first;
            if (n < 2) return false;
            double span = last - ts[first];
            fps = n * (double)Second / span;
            frameMs = span / n / 10000.0;

            var deltas = new long[ts.Length - 1];
            for (int i = 1; i < ts.Length; i++) deltas[i - 1] = ts[i] - ts[i - 1];
            Array.Sort(deltas);
            long p99 = deltas[(int)(0.99 * (deltas.Length - 1))];
            low1 = p99 > 0 ? Second / (double)p99 : fps;
            return true;
        }

        static long Last(Queue<long> q)
        {
            long last = 0;
            foreach (long t in q) last = t;
            return last;
        }

        delegate void EventRecordCallback(IntPtr record);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "StartTraceW")]
        static extern uint StartTrace(out ulong handle, string name, IntPtr properties);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "ControlTraceW")]
        static extern uint ControlTrace(ulong handle, string name, IntPtr properties, uint code);

        [DllImport("advapi32.dll")]
        static extern uint EnableTraceEx2(ulong handle, ref Guid provider, uint controlCode, byte level,
            ulong matchAny, ulong matchAll, uint timeout, IntPtr parameters);

        [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "OpenTraceW")]
        static extern ulong OpenTrace(IntPtr logfile);

        [DllImport("advapi32.dll")]
        static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);

        [DllImport("advapi32.dll")]
        static extern uint CloseTrace(ulong handle);
    }
}
