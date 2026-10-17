using RainbowMage.HtmlRenderer;
using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Windows.Forms;

// SMOKE_KILL: ends this process' WebView2 browser process (not other apps' msedgewebview2),
// then reports whether the overlay page comes back.
static class Kill
{
    public static void Run(OverlayForm form)
    {
        var me = Process.GetCurrentProcess().Id;
        int[] browsers;
        using (var s = new ManagementObjectSearcher("SELECT ProcessId FROM Win32_Process WHERE Name='msedgewebview2.exe' AND ParentProcessId=" + me))
            browsers = s.Get().Cast<ManagementObject>().Select(o => Convert.ToInt32(o["ProcessId"])).ToArray();
        Program.Log("killing browser processes: " + string.Join(",", browsers));
        foreach (var pid in browsers)
            try { Process.GetProcessById(pid).Kill(); } catch (Exception e) { Program.Log("kill failed " + e.Message); }
        var t = new Timer { Interval = 6000 };
        t.Tick += (s, e) =>
        {
            t.Stop();
            using (var q = new ManagementObjectSearcher("SELECT ProcessId FROM Win32_Process WHERE Name='msedgewebview2.exe' AND ParentProcessId=" + me))
                Program.Log("browser processes 6s later: " + q.Get().Count);
            form.Renderer.ExecuteScript("console.log('alive after kill')");
        };
        t.Start();
    }
}
