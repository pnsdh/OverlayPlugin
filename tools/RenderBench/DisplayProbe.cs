using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using D3D11 = SharpDX.Direct3D11;
using DXGI = SharpDX.DXGI;

// Reads what is actually on screen (DXGI Desktop Duplication, i.e. DWM's output) on every screen
// update: where the page's motion probe is.  The same measurement for every renderer.
sealed class DisplayProbe : IDisposable
{
    readonly Point window;
    readonly int width;
    readonly List<(long time, int x)> samples = new List<(long, int)>(1 << 16);
    Thread thread;
    volatile bool stop;
    string error;

    public DisplayProbe(Point windowLocation, int width)
    {
        window = windowLocation;
        this.width = width;
    }

    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    void Run()
    {
        try
        {
            using (var factory = new DXGI.Factory1())
            using (var adapter = factory.GetAdapter1(0))
            using (var device = new D3D11.Device(adapter))
            using (var output = adapter.GetOutput(0))
            using (var output1 = output.QueryInterface<DXGI.Output1>())
            using (var duplication = output1.DuplicateOutput(device))
            using (var staging = new D3D11.Texture2D(device, new D3D11.Texture2DDescription
            {
                Width = width,
                Height = 1,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new DXGI.SampleDescription(1, 0),
                Usage = D3D11.ResourceUsage.Staging,
                CpuAccessFlags = D3D11.CpuAccessFlags.Read,
            }))
            {
                var bounds = output.Description.DesktopBounds;
                var left = window.X - bounds.Left;
                var top = window.Y - bounds.Top + 1;
                var ctx = device.ImmediateContext;
                var row = new byte[width * 4];
                while (!stop)
                {
                    var result = duplication.TryAcquireNextFrame(100, out var info, out var resource);
                    if (result.Code == DXGI.ResultCode.WaitTimeout.Result.Code) continue;
                    result.CheckError();
                    try
                    {
                        // 0: only the mouse pointer changed.
                        if (info.LastPresentTime == 0) continue;
                        using (var texture = resource.QueryInterface<D3D11.Texture2D>())
                            ctx.CopySubresourceRegion(texture, 0, new D3D11.ResourceRegion(left, top, 0, left + width, top + 1, 1), staging, 0);
                        var box = ctx.MapSubresource(staging, 0, D3D11.MapMode.Read, D3D11.MapFlags.None);
                        Marshal.Copy(box.DataPointer, row, 0, row.Length);
                        ctx.UnmapSubresource(staging, 0);
                        lock (samples) samples.Add((info.LastPresentTime, FindProbe(row)));
                    }
                    finally
                    {
                        resource.Dispose();
                        duplication.ReleaseFrame();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
    }

    // The probe's left edge (it is #ff00ff over anything), or -1.
    static int FindProbe(byte[] row)
    {
        int sum = 0, n = 0;
        for (var x = 0; x < row.Length / 4; x++)
        {
            byte b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
            if (r > 200 && b > 200 && g < 80) { sum += x; n++; }
        }
        return n == 0 ? -1 : (int)Math.Round((double)sum / n) - 2;
    }

    // Screen updates are QPC times, the same clock as Stopwatch.
    public string Report(long from, double periodTicks)
    {
        if (error != null) return "display: duplication failed: " + error;
        (long time, int x)[] s;
        lock (samples) s = samples.Where(e => e.time >= from && e.x >= 0).ToArray();
        if (s.Length < 10 || periodTicks <= 0) return $"display: not enough samples ({s.Length})";

        var refreshes = (s[s.Length - 1].time - s[0].time) / periodTicks;
        int moved = 0, still = 0, irregular = 0;
        var errors = new List<double>();
        for (var i = 1; i < s.Length; i++)
        {
            var dt = (double)(s[i].time - s[i - 1].time) / Stopwatch.Frequency;
            var expected = Program.ProbeSpeed * dt;
            var actual = ((s[i].x - s[i - 1].x) % Program.ProbeTrack + Program.ProbeTrack) % Program.ProbeTrack;
            if (actual == 0) { still++; continue; }
            moved++;
            var err = Math.Abs(actual - expected);
            errors.Add(err);
            // More than half a refresh's movement off: the step shown does not match the time.
            if (err > Program.ProbeSpeed * periodTicks / Stopwatch.Frequency / 2) irregular++;
        }
        return $"display: refreshes={refreshes:F0} new probe position on {moved / refreshes * 100:F1}% of refreshes " +
            $"(screen updates {s.Length}, unchanged {still}); step error mean={errors.Average():F2}px, " +
            $"off by more than half a refresh {irregular * 100.0 / Math.Max(1, moved):F1}% of steps";
    }

    public void Dispose()
    {
        stop = true;
        thread?.Join(500);
    }
}
