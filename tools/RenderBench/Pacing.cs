using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using DXGI = SharpDX.DXGI;

// Display vblanks (IDXGIOutput.WaitForVBlank on the primary output) and the times renderers
// hand over frames, to see how frames land between vblanks.
static class Pacing
{
    static readonly List<long> vblanks = new List<long>(1 << 16);
    static readonly List<long> frames = new List<long>(1 << 16);
    static volatile bool stop;

    // Mean vblank interval in Stopwatch ticks (0 until known).
    public static double Period
    {
        get
        {
            lock (vblanks)
            {
                if (vblanks.Count < 10) return 0;
                return (double)(vblanks[vblanks.Count - 1] - vblanks[0]) / (vblanks.Count - 1);
            }
        }
    }

    public static void Frame() { lock (frames) frames.Add(Stopwatch.GetTimestamp()); }

    public static void Start(Action onVBlank)
    {
        var t = new Thread(() =>
        {
            using (var factory = new DXGI.Factory1())
            using (var adapter = factory.GetAdapter1(0))
            using (var output = adapter.GetOutput(0))
            {
                Program.Log.WriteLine($"vblank output={output.Description.DeviceName}");
                while (!stop)
                {
                    output.WaitForVerticalBlank();
                    lock (vblanks) vblanks.Add(Stopwatch.GetTimestamp());
                    onVBlank?.Invoke();
                }
            }
        })
        { IsBackground = true, Priority = ThreadPriority.Highest };
        t.Start();
    }

    public static string Report(long from)
    {
        stop = true;
        long[] v, f;
        lock (vblanks) v = vblanks.Where(x => x >= from).ToArray();
        lock (frames) f = frames.Where(x => x >= from).ToArray();
        if (v.Length < 2) return "pacing: no vblanks";
        double ms(long t) => t * 1000.0 / Stopwatch.Frequency;
        var vi = Enumerable.Range(0, v.Length - 1).Select(i => ms(v[i + 1] - v[i])).ToArray();
        var line = $"vblank: {vi.Length} intervals avg={vi.Average():F3}ms";
        if (f.Length < 2) return line;

        // Frames handed over per vblank interval (only meaningful for renderers that report them).
        var hist = new int[4];
        var fi = 0;
        while (fi < f.Length && f[fi] < v[0]) fi++;
        for (var i = 0; i + 1 < v.Length; i++)
        {
            var n = 0;
            while (fi < f.Length && f[fi] < v[i + 1]) { n++; fi++; }
            hist[Math.Min(n, 3)]++;
        }
        var total = (double)(v.Length - 1);
        var fiv = Enumerable.Range(0, f.Length - 1).Select(i => ms(f[i + 1] - f[i])).ToArray();
        var avg = fiv.Average();
        var sd = Math.Sqrt(fiv.Select(x => (x - avg) * (x - avg)).Average());
        return line + $"; frames handed over per interval 0={hist[0] / total * 100:F1}% 1={hist[1] / total * 100:F1}% 2={hist[2] / total * 100:F1}% 3+={hist[3] / total * 100:F1}%; frame interval avg={avg:F3}ms sd={sd:F3}ms";
    }
}
