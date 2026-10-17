using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace RainbowMage.HtmlRenderer
{
    // Fork, for development: delays along the way from a log line to the overlays, summarized each
    // minute into %TEMP%\op-latency.log.  Off unless %TEMP%\op-probe.enable exists when ACT starts
    // (pages add their own lines: cactbot's raidboss.html?devProbe=latency).
    public static class LatencyProbe
    {
        public static readonly bool Enabled = File.Exists(Path.Combine(Path.GetTempPath(), "op-probe.enable"));
        private static readonly object sync = new object();
        private static readonly Dictionary<string, List<double>> samples = new Dictionary<string, List<double>>();
        private static readonly string path = Path.Combine(Path.GetTempPath(), "op-latency.log");
        private static Timer flushTimer;
        private static System.Windows.Forms.Timer uiTimer;
        private static long uiLast;

        public static void Add(string key, double ms)
        {
            lock (sync)
            {
                if (!samples.TryGetValue(key, out var list))
                    samples[key] = list = new List<double>();
                list.Add(ms);
                if (flushTimer == null)
                    flushTimer = new Timer(_ => Flush(), null, 60000, 60000);
            }
        }

        public static void Write(string text)
        {
            try
            {
                lock (sync)
                    File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss} {text}\r\n", Encoding.UTF8);
            }
            catch (Exception)
            {
            }
        }

        // From the UI thread: a 50ms WinForms timer, which only ticks once that thread is free; how
        // late each tick is shows how long the thread was busy.
        public static void StartUiWatch()
        {
            if (uiTimer != null)
                return;
            Write("--- probe started");
            uiLast = System.Diagnostics.Stopwatch.GetTimestamp();
            uiTimer = new System.Windows.Forms.Timer { Interval = 50 };
            uiTimer.Tick += (s, e) =>
            {
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                var ms = (now - uiLast) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                uiLast = now;
                Add("ui thread tick (50ms timer)", ms);
            };
            uiTimer.Start();
        }

        private static void Flush()
        {
            Dictionary<string, List<double>> taken;
            lock (sync)
            {
                taken = samples.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
                foreach (var key in taken.Keys.ToList())
                    samples[key] = new List<double>();
            }
            foreach (var kv in taken.OrderBy(kv => kv.Key))
            {
                var v = kv.Value;
                v.Sort();
                Func<double, double> q = p => v[Math.Min(v.Count - 1, (int)(p * v.Count))];
                Write(string.Format(CultureInfo.InvariantCulture,
                    "{0}: n={1} p50={2:F1} p90={3:F1} p99={4:F1} max={5:F1} >16ms={6} >50ms={7} >100ms={8} >250ms={9}",
                    kv.Key, v.Count, q(0.5), q(0.9), q(0.99), v[v.Count - 1],
                    v.Count(x => x > 16), v.Count(x => x > 50), v.Count(x => x > 100), v.Count(x => x > 250)));
            }
        }
    }
}
