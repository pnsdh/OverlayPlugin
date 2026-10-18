using System;
using System.Windows.Forms;
using RainbowMage.HtmlRenderer;

// SMOKE_HIDDEN=1: a hidden overlay's timers (cactbot's delayed alerts, TTS) keep their pace.  The
// page (hidden.html) says each second how often a 50 ms timer ran and how late it was at most;
// the overlay is shown, hidden for 6 s (SMOKE_HIDDEN_SECONDS: longer, e.g. 360 for Edge's
// stronger limit after five minutes; as when the game is not the active window, or in a
// cutscene), then shown again.
static class Hidden
{
    public static void Run(string url, string appData)
    {
        Renderer.InitializeWebView2(Program.PluginDir, appData);
        Program.Log("available=" + Renderer.WebView2Available + " status=" + Renderer.WebView2Status);
        var form = new OverlayForm("hidden", "uuid-hidden", url, 60, new TestApi(), true);
        form.Renderer.BrowserConsoleLog += (s, e) => Program.Log((form.Visible ? "shown  " : "HIDDEN ") + e.Message);
        form.Location = new System.Drawing.Point(200, 200);
        form.Size = new System.Drawing.Size(400, 150);
        form.Show();
        form.Renderer.BeginRender();
        int step = 0;
        var t = new Timer { Interval = 3000 };
        t.Tick += (s, e) =>
        {
            step++;
            if (step == 1)
            {
                Program.Log("--- hiding");
                form.Hide();
                t.Interval = 1000 * int.Parse(Environment.GetEnvironmentVariable("SMOKE_HIDDEN_SECONDS") ?? "6");
            }
            else if (step == 2)
            {
                Program.Log("--- showing");
                form.Show();
                t.Interval = 3000;
            }
            else
            {
                t.Stop();
                form.Close();
                form.Dispose();
                Application.Exit();
            }
        };
        t.Start();
        Application.Run();
    }
}
