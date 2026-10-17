using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using RainbowMage.HtmlRenderer;

// SMOKE_TDR=<appdata>: a CEF overlay drawn with the GPU option keeps drawing after the shared
// D3D device is thrown away and made again, as after a driver reset.
static class Tdr
{
    const BindingFlags S = BindingFlags.NonPublic | BindingFlags.Static;

    static string Describe(OverlayForm form)
    {
        using (var bmp = form.CaptureFrame())
        {
            if (bmp == null) return "capture=null";
            int opaque = 0;
            for (var y = 0; y < bmp.Height; y += 10)
                for (var x = 0; x < bmp.Width; x += 10)
                    if (bmp.GetPixel(x, y).A != 0) opaque++;
            return $"capture={bmp.Width}x{bmp.Height} opaque samples={opaque}";
        }
    }

    public static void Run(string url, string appData)
    {
        Renderer.Initialize(Program.PluginDir, appData, false, true);
        Program.Log($"SharedTextures={Renderer.SharedTextures} error={Renderer.SharedTextureError}");
        var form = new OverlayForm("gpu", "uuid-gpu", url, 60, new TestApi(), false);
        form.Location = new Point(200, 200); form.Size = new Size(600, 200);
        form.Show(); form.Renderer.BeginRender();
        var presenterType = typeof(Renderer).Assembly.GetType("RainbowMage.HtmlRenderer.DCompPresenter");
        var step = 0;
        var t = new System.Windows.Forms.Timer { Interval = 3000 };
        t.Tick += (s, e) =>
        {
            step++;
            if (step == 1)
            {
                Program.Log("before loss: " + Describe(form));
                var gen0 = (int)presenterType.GetField("deviceGeneration", S).GetValue(null);
                var lockObj = presenterType.GetField("deviceLock", S).GetValue(null);
                lock (lockObj)
                {
                    presenterType.GetMethod("ReleaseDevice", S).Invoke(null, null);
                    var args = new object[] { null };
                    presenterType.GetMethod("CreateDevice", S).Invoke(null, args);
                }
                Program.Log($"device replaced: generation {gen0} -> {presenterType.GetField("deviceGeneration", S).GetValue(null)}");
            }
            else if (step == 2)
            {
                Program.Log("after loss: " + Describe(form));
                var failures = typeof(OverlayForm).GetField("sharedFrameFailures", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                Program.Log($"shared frame failures={failures}");
            }
            else
            {
                t.Stop();
                form.Close(); form.Dispose();
                Program.Log("Shutdown=" + Renderer.Shutdown());
                Application.Exit();
            }
        };
        t.Start();
        Application.Run();
    }
}
