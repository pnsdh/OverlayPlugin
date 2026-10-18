using System;
using System.Drawing;
using System.Windows.Forms;
using RainbowMage.HtmlRenderer;

// SMOKE_CTRL=lazy|eager: cactbot's config tab, an OverlayControl (a CEF browser as a child
// window) inited when it becomes visible.  lazy: nothing started CEF before it; eager: a CEF
// overlay did.  Saves a screen capture of the control to tell whether it draws.
static class Ctrl
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    struct RECT { public int Left, Top, Right, Bottom; }

    public static void Run(string url, string appData, string mode, string shot)
    {
        Renderer.Initialize(Program.PluginDir, appData, false, false);
        Renderer.InitializeWebView2(Program.PluginDir, appData);
        // Its own cache: the real one is in use by ACT.
        var cache = Environment.GetEnvironmentVariable("SMOKE_CEFCACHE");
        if (cache != null)
            typeof(Renderer).GetField("CachePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).SetValue(null, cache);
        OverlayForm cef = null;
        // As in ACT with every overlay drawn by WebView2: one runs before the config tab opens.
        OverlayForm wv = null;
        if (mode.StartsWith("wv"))
        {
            wv = new OverlayForm("wv", "uuid-wv", url, 60, new TestApi(), true);
            wv.Location = new Point(900, 450); wv.Size = new Size(300, 200);
            wv.Show(); wv.Renderer.BeginRender();
            Program.Log("webview2 overlay: " + wv.Renderer.UsesWebView2);
        }
        if (mode == "eager")
        {
            cef = new OverlayForm("cef", "uuid-cef", "about:blank", 60, null, false);
            cef.Location = new Point(900, 200); cef.Size = new Size(300, 200);
            cef.Show(); cef.Renderer.BeginRender();
        }
        Program.Log("CefStarted before control=" + Renderer.CefStarted);

        var form = new Form { Text = "ctrl", Location = new Point(200, 200), Size = new Size(600, 400), StartPosition = FormStartPosition.Manual };
        var control = new OverlayControl { Dock = DockStyle.Fill };
        var initDone = false;
        var ensureCef = typeof(Renderer).GetMethod("EnsureCef", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        control.VisibleChanged += (o, e) =>
        {
            if (initDone || !control.Visible)
                return;
            initDone = true;
            if (mode == "startfirst" || mode == "startfirst-now")
            {
                ensureCef.Invoke(null, null);
                Program.Log("CEF started by hand");
                if (mode == "startfirst")
                {
                    var later = new Timer { Interval = 500 };
                    later.Tick += (s3, e3) =>
                    {
                        later.Stop();
                        control.Init(url);
                        control.Renderer.BrowserLoad += (s2, e2) => Program.Log("control load: " + e2.HttpStatusCode + " " + e2.Url);
                        Program.Log("control inited 0.5s later");
                    };
                    later.Start();
                    return;
                }
            }
            control.Init(url);
            // As cactbot: MinimalApi.AttachTo(control.Renderer, ...) right after Init.
            control.Renderer.SetApi(new TestApi());
            control.Renderer.BrowserLoad += (s2, e2) => Program.Log("control load: " + e2.HttpStatusCode + " " + e2.Url);
            control.Renderer.BrowserConsoleLog += (s2, e2) => Program.Log("control console: " + e2.Message);
            control.Renderer.BrowserError += (s2, e2) => Program.Log("control error: " + e2.ErrorCode + " " + e2.ErrorText);
            Program.Log("control inited, CefStarted=" + Renderer.CefStarted);
        };
        form.Controls.Add(control);
        // wv: open the "tab" a while after the WebView2 overlay started, as a user would.
        if (mode.StartsWith("wv"))
        {
            var open = new Timer { Interval = 3000 };
            open.Tick += (s4, e4) => { open.Stop(); form.Show(); };
            open.Start();
        }
        else
            form.Show();

        var t = new Timer { Interval = mode == "stuck" ? 26000 : mode.StartsWith("wv") ? 9000 : 5000 };
        t.Tick += (s, e) =>
        {
            t.Stop();
            var p = control.PointToScreen(Point.Empty);
            using (var bmp = new Bitmap(control.Width, control.Height))
            {
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(p, Point.Empty, bmp.Size);
                bmp.Save(shot);
                // How much of it isn't the plain background.
                int colored = 0, total = 0;
                for (int y = 0; y < bmp.Height; y += 4)
                    for (int x = 0; x < bmp.Width; x += 4)
                    {
                        total++;
                        var c = bmp.GetPixel(x, y);
                        if (Math.Abs(c.R - c.G) > 20 || Math.Abs(c.G - c.B) > 20) colored++;
                    }
                Program.Log("control capture: colored " + colored + "/" + total);
            }
            // The browser's child windows and its state.
            var kids = new System.Collections.Generic.List<string>();
            EnumChildWindows(control.Handle, (h, l) =>
            {
                var cls = new System.Text.StringBuilder(256);
                GetClassName(h, cls, 256);
                RECT r;
                GetWindowRect(h, out r);
                kids.Add(cls + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) + (IsWindowVisible(h) ? "" : " hidden"));
                return true;
            }, IntPtr.Zero);
            Program.Log("children: " + string.Join(", ", kids));
            var bw = typeof(Renderer).GetField("_browser", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(control.Renderer) as CefSharp.IWebBrowser;
            Program.Log("browser initialized=" + bw?.IsBrowserInitialized + " loading=" + bw?.IsLoading + " address=" + bw?.Address);
            form.Close();
            cef?.Close();
            wv?.Close();
            Program.Log("Shutdown=" + Renderer.Shutdown());
            Application.Exit();
        };
        t.Start();
        Application.Run();
    }
}
