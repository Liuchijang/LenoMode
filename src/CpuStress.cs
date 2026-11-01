using System;
using System.Threading;

namespace LenoMode
{
    /// <summary>
    /// Fixed, repeatable CPU load (one busy thread per logical core) for the auto test, so the power
    /// difference between thermal modes is not hidden by a game scene that keeps changing.
    /// </summary>
    static class CpuStress
    {
        static volatile bool run;
        static Thread[] workers;

        public static bool Running { get { return workers != null; } }

        public static void Start()
        {
            if (workers != null) return;
            run = true;
            workers = new Thread[Environment.ProcessorCount];
            for (int i = 0; i < workers.Length; i++)
            {
                // Below normal so the UI and the sampler still get their 1 s ticks; the cores stay at 100% anyway.
                workers[i] = new Thread(Spin) { IsBackground = true, Name = "stress" + i, Priority = ThreadPriority.BelowNormal };
                workers[i].Start();
            }
        }

        public static void Stop()
        {
            if (workers == null) return;
            run = false;
            foreach (var t in workers) t.Join(1000);
            workers = null;
        }

        static void Spin()
        {
            double x = 1.0001;
            while (run)
                for (int i = 0; i < 100000; i++) x = Math.Sqrt(x * 1.0000001 + i);
            GC.KeepAlive(x);
        }
    }
}
