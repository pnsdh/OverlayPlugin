using System;
using System.Reflection;
using System.Windows.Forms;
using RainbowMage.HtmlRenderer;

// SMOKE_EDGE=<zoom level>: hit tests at the window's edges with a fractional zoom, where the
// page capture can be a pixel off the window.
static class Edge
{
    public static void Run(OverlayForm form, double level)
    {
        form.Renderer.SetZoomLevel(level);
        var t = new Timer { Interval = 2000 };
        t.Tick += (s, e) =>
        {
            t.Stop();
            var w = form.ClientSize.Width; var h = form.ClientSize.Height;
            var points = new[] { (w - 1, h - 1), (w - 1, 0), (0, h - 1), (w / 2, h / 2) };
            foreach (var (x, y) in points)
                form.Renderer.QueryOpaqueAt(x, y, opaque =>
                {
                    var wv = typeof(Renderer).GetField("_wv", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form.Renderer);
                    var map = wv.GetType().GetField("alphaMapSize", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(wv);
                    Program.Log($"edge ({x},{y}) of {w}x{h}: opaque={opaque} capture={map}");
                });
        };
        t.Start();
    }
}
