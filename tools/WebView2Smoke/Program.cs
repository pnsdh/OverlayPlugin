using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using RainbowMage.HtmlRenderer;

public class TestApi
{
    public void callHandler(string data, object callback)
    {
        Program.Log("callHandler(" + data + ")");
        Task.Run(() => Renderer.ExecuteCallback(callback, "{\"echo\":" + data + "}"));
    }
    public void broadcastMessage(string msg) { Program.Log("broadcastMessage(" + msg + ")"); }
}

static class Program
{
    // The built plugin (out\Release of this repo, or OP_OUT): its HtmlRenderer, CEF and WebView2 loader.
    public static readonly string PluginDir = Environment.GetEnvironmentVariable("OP_OUT") ??
        Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\..\out\Release"));
    static StreamWriter log;
    public static void Log(string s) { lock (log) log.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff ") + s); }

    [STAThread]
    static void Main(string[] args)
    {
        log = new StreamWriter(args[1]) { AutoFlush = true };
        AppDomain.CurrentDomain.UnhandledException += (s, e) => { Log("UNHANDLED " + e.ExceptionObject); Environment.Exit(3); };
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        if (Environment.GetEnvironmentVariable("SMOKE_TDR") != null) { Tdr.Run(args[0], Environment.GetEnvironmentVariable("SMOKE_TDR")); return; }
        if (Environment.GetEnvironmentVariable("SMOKE_MIXED") != null) { Mixed.Run(args[0], Environment.GetEnvironmentVariable("SMOKE_MIXED"), Environment.GetEnvironmentVariable("SMOKE_APPDATA")); return; }
        if (Environment.GetEnvironmentVariable("SMOKE_CTRL") != null) { Ctrl.Run(args[0], Environment.GetEnvironmentVariable("SMOKE_APPDATA"), Environment.GetEnvironmentVariable("SMOKE_CTRL"), args[2]); return; }
        if (Environment.GetEnvironmentVariable("SMOKE_LAZY") != null) { Lazy.Run(args[0], Environment.GetEnvironmentVariable("SMOKE_LAZY")); return; }
        if (Environment.GetEnvironmentVariable("SMOKE_HIDDEN") != null) { Hidden.Run(args[0], Environment.GetEnvironmentVariable("SMOKE_APPDATA")); return; }
        var cache = Environment.GetEnvironmentVariable("SMOKE_CEFCACHE");
        if (cache != null)
            typeof(Renderer).GetField("CachePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).SetValue(null, cache);
        var appData = Environment.GetEnvironmentVariable("SMOKE_APPDATA") ?? Path.GetTempPath();
        Renderer.InitializeWebView2(Program.PluginDir, appData);
        Log("available=" + Renderer.WebView2Available + " status=" + Renderer.WebView2Status);
        OverlayForm form = new OverlayForm("smoke", "uuid-1", args[0], 60, new TestApi(), true);
        Log("UsesWebView2=" + form.Renderer.UsesWebView2);
        form.Renderer.BrowserConsoleLog += (s, e) => Log("console: " + e.Message + " @" + e.Source + ":" + e.Line);
        form.Renderer.BrowserLoad += (s, e) => Log("load: " + e.HttpStatusCode + " " + e.Url);
        form.Renderer.BrowserStartLoading += (s, e) => Log("start: " + e.Url);
        form.Renderer.BrowserError += (s, e) => Log("error: " + e.ErrorCode + " " + e.Url);
        form.Location = new System.Drawing.Point(200, 200);
        form.Size = new System.Drawing.Size(600, 300);
        form.Show();
        form.IsClickThru = true;
        form.Renderer.BeginRender();
        int n = 0;
        var tick = new Timer { Interval = 200 };
        tick.Tick += (s, e) => { n++; Task.Run(() => form.Renderer.ExecuteScript("if(window.__OverlayCallback) __OverlayCallback({type:'tick',n:" + n + "});")); };
        tick.Start();
        var edge = Environment.GetEnvironmentVariable("SMOKE_EDGE");
        if (edge != null) { var t16 = new Timer { Interval = 3000 }; t16.Tick += (s17, e17) => { t16.Stop(); form.IsClickThru = false; Edge.Run(form, double.Parse(edge)); }; t16.Start(); }
        if (Environment.GetEnvironmentVariable("SMOKE_KILL") == "1") { var t14 = new Timer { Interval = 4000 }; t14.Tick += (s15, e15) => { t14.Stop(); Kill.Run(form); }; t14.Start(); }
        if (Environment.GetEnvironmentVariable("SMOKE_IME") == "1") { var t12 = new Timer { Interval = 3000 }; t12.Tick += (s13, e13) => { t12.Stop(); form.IsClickThru = false; Ime.Run(form); }; t12.Start(); }
        if (Environment.GetEnvironmentVariable("SMOKE_COPY") == "1") { var t18 = new Timer { Interval = 3000 }; t18.Tick += (s19, e19) => { t18.Stop(); form.IsClickThru = false; Copy.Run(form); }; t18.Start(); }
        if (Environment.GetEnvironmentVariable("SMOKE_FOCUS") == "1") { var t10 = new Timer { Interval = 3000 }; t10.Tick += (s11, e11) => { t10.Stop(); form.IsClickThru = false; Focus.Run(form); }; t10.Start(); }
        if (Environment.GetEnvironmentVariable("SMOKE_WHEEL") == "1") { var t8 = new Timer { Interval = 3000 }; t8.Tick += (s9, e9) => { t8.Stop(); form.IsClickThru = false; Wheel.Run(form); }; t8.Start(); }
        if (Environment.GetEnvironmentVariable("SMOKE_KEYS") == "1") { var t6 = new Timer { Interval = 3000 }; t6.Tick += (s7, e7) => { t6.Stop(); form.IsClickThru = false; Keys2.Run(form); }; t6.Start(); }
        if (Environment.GetEnvironmentVariable("SMOKE_HITLOOP") == "1") { var t4 = new Timer { Interval = 4000 }; t4.Tick += (s5, e5) => { t4.Stop(); form.IsClickThru = false; HitLoop.Run(form); }; t4.Start(); }
        if (Environment.GetEnvironmentVariable("SMOKE_PHASE2") == "1") { var t2 = new Timer { Interval = 3000 }; t2.Tick += (s3, e3) => { t2.Stop(); form.IsClickThru = false; Phase2.Run(form); }; t2.Start(); }
        var probe = Environment.GetEnvironmentVariable("SMOKE_SCRIPT");
        if (probe != null) { var t = new Timer { Interval = 5000 }; t.Tick += (s2, e2) => { t.Stop(); form.Renderer.ExecuteScript(probe); }; t.Start(); }
        var round = 0;
        var end = new Timer { Interval = int.Parse(args[2]) * 1000 };
        end.Tick += (s, e) =>
        {
            Log("round " + round + " done, ticks sent " + n);
            form.Close(); form.Dispose();
            if (++round == 2) { Application.Exit(); return; }
            form = new OverlayForm("smoke2", "uuid-2", args[0], 60, new TestApi(), true);
            form.Renderer.BrowserConsoleLog += (s2, e2) => Log("console: " + e2.Message);
            form.Renderer.BrowserLoad += (s2, e2) => Log("load: " + e2.Url);
            form.Location = new System.Drawing.Point(200, 200);
            form.Size = new System.Drawing.Size(600, 300);
            form.Show();
            form.IsClickThru = true;
            form.Renderer.BeginRender();
            Log("recreated, UsesWebView2=" + form.Renderer.UsesWebView2);
        };
        end.Start();
        Application.Run();
    }
}
