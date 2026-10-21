using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using RainbowMage.HtmlRenderer;

// SMOKE_RESIZE=1 (with resize.html): an unlocked overlay's resize border.  The hit test at points
// all around the window, also with the window at negative screen coordinates, then real drags
// (moves the mouse): an edge and a corner resize, the middle moves.
static class Resize
{
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] static extern void mouse_event(int flags, int dx, int dy, int data, IntPtr extra);
    const int WM_NCHITTEST = 0x84, LEFTDOWN = 2, LEFTUP = 4;

    // SMOKE_RESIZE_SPY=1: logs the mouse and sizing messages the window gets during the drags.
    class Spy : System.Windows.Forms.NativeWindow
    {
        public bool On;
        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            base.WndProc(ref m);
            if (On && (m.Msg == 0x84 || m.Msg == 0xA1 || m.Msg == 0xA2 || m.Msg == 0x112 || m.Msg == 0x231 || m.Msg == 0x232 || m.Msg == 0x214 || m.Msg == 0x201 || m.Msg == 0x202 || m.Msg == 0x215 || m.Msg == 0x46))
                Program.Log($"  msg 0x{m.Msg:X} w=0x{m.WParam.ToInt64():X} result={m.Result}");
        }
    }

    public static void Run(OverlayForm form)
    {
        form.Locked = false;
        var spy = new Spy();
        if (Environment.GetEnvironmentVariable("SMOKE_RESIZE_SPY") == "1")
            spy.AssignHandle(form.Handle);
        HitTests(form, new Point(200, 200));
        HitTests(form, new Point(-300, 200));
        form.Location = new Point(200, 200);
        spy.On = true;
        GetCursorPos(out var saved);
        Task.Run(() =>
        {
            try
            {
                Drag(form, "left edge", 1, 150, -60, 0);
                Drag(form, "bottom right grip", -10, -10, 40, 30);
                Drag(form, "top left corner", 2, 2, -20, -20);
                Drag(form, "middle", 300, 150, 30, 20);
                Drag(form, "right edge", -2, 150, 50, 0);
            }
            finally
            {
                SetCursorPos(saved.X, saved.Y);
                form.Invoke((Action)(() => System.Windows.Forms.Application.Exit()));
            }
        });
    }

    static void HitTests(OverlayForm form, Point at)
    {
        form.Location = at;
        var w = form.ClientSize.Width;
        var h = form.ClientSize.Height;
        var points = new (int, int, int)[]
        {
            (0, h / 2, 10), (7, h / 2, 10), (8, h / 2, 1), (w - 1, h / 2, 11), (w - 8, h / 2, 11), (w - 9, h / 2, 1),
            (w / 2, 0, 12), (w / 2, h - 1, 15), (w / 2, h / 2, 1),
            (0, 0, 13), (15, 3, 13), (3, 15, 13), (16, 3, 12), (w - 1, 0, 14), (0, h - 1, 16),
            (w - 1, h - 1, 17), (w - 16, h - 16, 17), (w - 17, h - 16, 1),
        };
        var bad = 0;
        foreach (var (x, y, want) in points)
        {
            var sx = form.Left + x;
            var sy = form.Top + y;
            var lParam = (IntPtr)(((sy & 0xFFFF) << 16) | (sx & 0xFFFF));
            var got = (int)SendMessage(form.Handle, WM_NCHITTEST, IntPtr.Zero, lParam);
            if (got != want) bad++;
            Program.Log($"at {at}: ({x},{y}) hit={got} want={want}{(got == want ? "" : " MISMATCH")}");
        }
        Program.Log($"hit tests at {at}: {points.Length - bad}/{points.Length} ok");
    }

    static void Drag(OverlayForm form, string what, int x, int y, int dx, int dy)
    {
        Rectangle before = Rectangle.Empty;
        form.Invoke((Action)(() => before = form.Bounds));
        // Negative: from the right or bottom.
        if (x < 0) x += before.Width;
        if (y < 0) y += before.Height;
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
