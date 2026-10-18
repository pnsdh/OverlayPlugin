using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using RainbowMage.HtmlRenderer;

static class Keys2
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    public static void Run(OverlayForm form)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(form.Handle, (h, l) =>
        {
            var sb = new StringBuilder(256); GetClassName(h, sb, 256);
            uint pid; var tid = GetWindowThreadProcessId(h, out pid);
            Program.Log("child hwnd " + h + " class=" + sb + " pid=" + pid + " tid=" + tid + " ourpid=" + System.Diagnostics.Process.GetCurrentProcess().Id + " ourtid=" + GetCurrentThreadId());
            if (found == IntPtr.Zero) found = h;
            return true;
        }, IntPtr.Zero);
        Program.Log("children enumerated, first=" + found);
        // Click into the input, then type into the window that WebView2 would read keys from.
        form.Renderer.SendMouseMove(40, 30, CefSharp.MouseButtonType.Left);
        form.Renderer.SendMouseUpDown(40, 30, CefSharp.MouseButtonType.Left, false);
        form.Renderer.SendMouseUpDown(40, 30, CefSharp.MouseButtonType.Left, true);
        var target = found != IntPtr.Zero ? found : form.Handle;
        var t = new Timer { Interval = 300 };
        t.Tick += (s, e) =>
        {
            t.Stop();
            // As OverlayForm.WndProc does for keys that reach the overlay's own window.
            foreach (var ch in "가나ab")
                form.Renderer.ForwardKeyMessage(0x0102 /* WM_CHAR */, (IntPtr)ch, (IntPtr)1);
            Program.Log("forwarded WM_CHAR xyz through the renderer");
        };
        t.Start();
    }
}
