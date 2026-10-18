using System.Windows.Forms;
using RainbowMage.HtmlRenderer;
static class Wheel
{
    public static void Run(OverlayForm form)
    {
        var r = form.Renderer;
        r.SendMouseMove(100, 100, CefSharp.MouseButtonType.Left);
        // As WinFormsOffScreenRenderer.OnMouseWheel: wheel down without Shift, then with Shift.
        r.SendMouseWheel(100, 100, -120, false);
        var t = new Timer { Interval = 600 };
        t.Tick += (s, e) => { t.Stop(); r.SendMouseWheel(100, 100, -120, true); Program.Log("sent shift wheel"); };
        t.Start();
    }
}
