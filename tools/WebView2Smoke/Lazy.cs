using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Windows.Forms;
using RainbowMage.HtmlRenderer;

// SMOKE_LAZY: CEF must not start until an overlay drawn by CEF is created.
static class Lazy
{
    static int CefProcesses()
    {
        var me = Process.GetCurrentProcess().Id;
        using (var s = new ManagementObjectSearcher("SELECT ProcessId FROM Win32_Process WHERE Name='CefSharp.BrowserSubprocess.exe' AND ParentProcessId=" + me))
            return s.Get().Count;
    }

    static string State()
    {
        var mods = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Equals("libcef.dll", StringComparison.OrdinalIgnoreCase));
        var asm = string.Join(",", AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).Where(n => n.StartsWith("CefSharp")));
        return "CefStarted=" + Renderer.CefStarted + " cefProcesses=" + CefProcesses() + " libcef=" + mods + " asm=[" + asm + "]";
    }

    public static void Run(string url, string appData)
    {
        Renderer.Initialize(Program.PluginDir, appData, false, false);
        Renderer.InitializeWebView2(Program.PluginDir, appData);
        Program.Log("after Initialize: " + State());
        var wv = new OverlayForm("wv", "uuid-wv", url, 60, new TestApi(), true);
        wv.Location = new System.Drawing.Point(200, 200); wv.Size = new System.Drawing.Size(500, 200);
        wv.Show(); wv.Renderer.BeginRender();
        Program.Log("webview2 overlay UsesWebView2=" + wv.Renderer.UsesWebView2);
        OverlayForm cef = null;
        var step = 0;
        var t = new Timer { Interval = 4000 };
        t.Tick += (s, e) =>
        {
            step++;
            if (step == 1)
            {
                Program.Log("webview2 only, 4s: " + State());
                cef = new OverlayForm("cef", "uuid-cef", url, 60, new TestApi(), false);
                cef.Renderer.BrowserLoad += (s2, e2) => Program.Log("cef load: " + e2.HttpStatusCode + " " + e2.Url);
                cef.Renderer.BrowserConsoleLog += (s2, e2) => Program.Log("cef console: " + e2.Message);
                cef.Location = new System.Drawing.Point(200, 450); cef.Size = new System.Drawing.Size(500, 200);
                cef.Show(); cef.Renderer.BeginRender();
                Program.Log("cef overlay created UsesWebView2=" + cef.Renderer.UsesWebView2 + ": " + State());
            }
            else
            {
                t.Stop();
                Program.Log("with cef overlay, 4s: " + State());
                cef.Close(); cef.Dispose(); wv.Close(); wv.Dispose();
                Program.Log("Shutdown=" + Renderer.Shutdown());
                Application.Exit();
            }
        };
        t.Start();
        Application.Run();
    }
}
