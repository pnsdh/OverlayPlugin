using System;
using System.Drawing;
using System.Windows.Forms;
using RainbowMage.HtmlRenderer;

// SMOKE_MIXED=1: a WebView2 overlay showing alerts-like content (red box 0.5s every 1.5s), then
// cactbot's config tab (an OverlayControl) started late, which is when CEF starts.  Samples
// whether the overlay's box keeps showing up before and after CEF starts.
static class Mixed
{
    static bool RedAt(Point p)
    {
        using (var bmp = new Bitmap(1, 1))
        {
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(p, Point.Empty, new Size(1, 1));
            var c = bmp.GetPixel(0, 0);
            return c.R > 180 && c.G < 90 && c.B < 90;
        }
    }

    [System.Runtime.InteropServices.DllImport("shcore.dll")] static extern int GetProcessDpiAwareness(IntPtr process, out int value);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetThreadDpiAwarenessContext();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetAwarenessFromDpiAwarenessContext(IntPtr ctx);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    public static string Dpi(string when, Control w = null)
    {
        int p;
        GetProcessDpiAwareness(IntPtr.Zero, out p);
        var thread = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
        return $"{when}: process awareness={p} thread awareness={thread}" + (w != null ? $" window dpi={GetDpiForWindow(w.Handle)}" : "");
    }

    public static void Run(string alertUrl, string configUrl, string appData)
    {
        Program.Log(Dpi("start"));
        Renderer.Initialize(Program.PluginDir, appData, false, false);
        Renderer.InitializeWebView2(Program.PluginDir, appData);
        var cache = Environment.GetEnvironmentVariable("SMOKE_CEFCACHE");
        if (cache != null)
            typeof(Renderer).GetField("CachePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).SetValue(null, cache);

        var wv = new OverlayForm("alerts", "uuid-alerts", alertUrl, 60, new TestApi(), true);
        wv.Location = new Point(900, 300); wv.Size = new Size(300, 200);
        wv.Show(); wv.Renderer.BeginRender();
        Program.Log("alerts overlay WebView2=" + wv.Renderer.UsesWebView2);
        var probe = new Point(1050, 400);

        Form form = null;
        OverlayControl control = null;
        int openAt = int.Parse(Environment.GetEnvironmentVariable("SMOKE_MIXED_AT") ?? "90");
        int tick = 0, redBefore = 0, redAfter = 0, samplesBefore = 0, samplesAfter = 0;
        var t = new Timer { Interval = 100 };
        t.Tick += (s, e) =>
        {
            tick++;
            // 3-9s: before CEF starts.  At 9s the config tab opens.  10-24s: after.
            if (tick >= 30 && tick < openAt) { samplesBefore++; if (RedAt(probe)) redBefore++; }
            if (tick == openAt)
            {
                form = new Form { Text = "config", Location = new Point(100, 100), Size = new Size(600, 400), StartPosition = FormStartPosition.Manual };
                control = new OverlayControl { Dock = DockStyle.Fill };
                var done = false;
                control.VisibleChanged += (o, e2) =>
                {
                    if (done || !control.Visible) return;
                    done = true;
                    control.Init(configUrl);
                    control.Renderer.SetApi(new TestApi());
                    control.Renderer.BrowserLoad += (s2, e3) => Program.Log("config load: " + e3.Url);
                    control.Renderer.BrowserError += (s2, e3) => Program.Log("config error: " + e3.ErrorText);
                    Program.Log("config inited, CefStarted=" + Renderer.CefStarted);
                    Program.Log(Dpi("after CEF", wv));
                    var later = new Form { Location = new Point(300, 300), Size = new Size(100, 100), StartPosition = FormStartPosition.Manual };
                    later.Show();
                    Program.Log(Dpi("window made after CEF", later));
                    later.Close();
                };
                form.Controls.Add(control);
                form.Show();
                wv.BringToFront();
            }
            if (tick >= Math.Max(openAt + 10, 30) && tick < 240) { samplesAfter++; if (RedAt(probe)) redAfter++; }
            if (tick == 240)
            {
                t.Stop();
                Program.Log($"alerts box seen: before CEF {redBefore}/{samplesBefore}, after CEF {redAfter}/{samplesAfter}");
                form?.Close(); wv.Close();
                Program.Log("Shutdown=" + Renderer.Shutdown());
                Application.Exit();
            }
        };
        t.Start();
        Application.Run();
    }
}
