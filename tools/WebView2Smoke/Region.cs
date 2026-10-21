using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using RainbowMage.HtmlRenderer;

// SMOKE_REGION=1 (with region.html): an unlocked overlay whose page marks a no-drag box in a
// draggable page (CSS app-region).  Real drags (moves the mouse): inside the box the window stays
// and the page gets the drag, elsewhere it moves.  SMOKE_REGION_ZOOM=<zoom level>: page zoomed.
static class Region
{
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] static extern void mouse_event(int flags, int dx, int dy, int data, IntPtr extra);
    const int LEFTDOWN = 2, LEFTUP = 4;

    public static void Run(OverlayForm form)
    {
        form.Locked = false;
        form.Location = new Point(200, 200);
        var level = double.Parse(Environment.GetEnvironmentVariable("SMOKE_REGION_ZOOM") ?? "0");
        form.Renderer.SetZoomLevel(level);
        // Window pixels of the page's CSS ones.
        var f = Math.Pow(1.2, level);
        GetCursorPos(out var saved);
        Task.Run(() =>
        {
            try
            {
                Thread.Sleep(1000);
                Program.Log("region: " + (form.Renderer.DraggableRegion == null ? "none" :
                    form.Renderer.DraggableRegion.GetBounds(Graphics.FromHwnd(IntPtr.Zero)).ToString()));
                Drag(form, "no-drag box", (int)(150 * f), (int)(100 * f), 40, 20);
                Drag(form, "just outside the box", (int)(206 * f), (int)(100 * f), 30, 20);
                Drag(form, "draggable page", 450, 250, 30, 20);
            }
            finally
            {
                SetCursorPos(saved.X, saved.Y);
                Thread.Sleep(300);
                form.Invoke((Action)(() => System.Windows.Forms.Application.Exit()));
            }
        });
    }

    static void Drag(OverlayForm form, string what, int x, int y, int dx, int dy)
    {
        Rectangle before = Rectangle.Empty;
        form.Invoke((Action)(() => before = form.Bounds));
        SetCursorPos(before.Left + x, before.Top + y);
        // The click-through hit test (twice a second) must find the pixel opaque first.
        Thread.Sleep(1200);
        mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(150);
        for (var i = 1; i <= 10; i++)
        {
            SetCursorPos(before.Left + x + dx * i / 10, before.Top + y + dy * i / 10);
            Thread.Sleep(30);
        }
        Thread.Sleep(150);
        mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(500);
        Rectangle after = Rectangle.Empty;
        form.Invoke((Action)(() => after = form.Bounds));
        Program.Log($"drag {what} by ({dx},{dy}): {before} -> {after}");
    }
}
