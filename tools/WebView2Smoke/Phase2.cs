using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using RainbowMage.HtmlRenderer;

static class Phase2
{
    public static void Run(OverlayForm form)
    {
        var r = form.Renderer;
        // Box: page y 150..210, scrolled by 100 -> window y 50..110.
        var points = new[] { new System.Drawing.Point(150, 70), new System.Drawing.Point(150, 10), new System.Drawing.Point(50, 70), new System.Drawing.Point(299, 109), new System.Drawing.Point(301, 70) };
        Action<int> next = null;
        next = i =>
        {
            if (i >= points.Length) {
        Task.Run(() =>
        {
            var bmp = r.Screenshot();
            Program.Log(bmp == null ? "screenshot null" : $"screenshot {bmp.Width}x{bmp.Height} alpha box={bmp.GetPixel(150, 70).A} empty={bmp.GetPixel(10, 10).A}");
        });
                return;
            }
            var q = points[i];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            r.QueryOpaqueAt(q.X, q.Y, opaque => { Program.Log($"opaque at {q.X},{q.Y} = {opaque} ({sw.Elapsed.TotalMilliseconds:F0}ms)"); next(i + 1); });
        };
        next(0);
        r.SendMouseMove(150, 70, CefSharp.MouseButtonType.Left);
        r.SendMouseUpDown(150, 70, CefSharp.MouseButtonType.Left, false);
        r.SendMouseUpDown(150, 70, CefSharp.MouseButtonType.Left, true);
        r.SendMouseUpDown(150, 70, CefSharp.MouseButtonType.Left, false);
        r.SendMouseUpDown(150, 70, CefSharp.MouseButtonType.Left, true);
    }
}
