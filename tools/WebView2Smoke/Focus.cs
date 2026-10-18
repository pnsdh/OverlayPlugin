using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RainbowMage.HtmlRenderer;

static class Focus
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    static void Switch(IntPtr w)
    {
        uint pid; var ft = GetWindowThreadProcessId(GetForegroundWindow(), out pid); var me = GetCurrentThreadId();
        var att = ft != 0 && ft != me && AttachThreadInput(me, ft, true);
        SetForegroundWindow(w);
        if (att) AttachThreadInput(me, ft, false);
    }

    static void ClickField(OverlayForm form)
    {
        var r = form.Renderer;
        r.SendMouseMove(40, 30, CefSharp.MouseButtonType.Left);
        r.SendMouseUpDown(40, 30, CefSharp.MouseButtonType.Left, false);
        r.SendMouseUpDown(40, 30, CefSharp.MouseButtonType.Left, true);
    }

    public static void Run(OverlayForm form)
    {
        var game = System.Diagnostics.Process.Start("notepad.exe");
        game.WaitForInputIdle(3000);
        for (int i = 0; i < 30 && game.MainWindowHandle == IntPtr.Zero; i++) { System.Threading.Thread.Sleep(100); game.Refresh(); }
        var before = game.MainWindowHandle;
        Switch(before);
        Program.Log($"stand-in game={before} foreground={GetForegroundWindow()} overlay={form.Handle}");
        form.FormClosed += (s0, e0) => { try { game.Kill(); } catch { } };
        ClickField(form);
        var t = new Timer { Interval = 300 };
        int step = 0;
        t.Tick += (s, e) =>
        {
            step++;
            switch (step)
            {
                case 1:
                    Program.Log($"1st click on field: foreground={GetForegroundWindow()}");
                    // As clicking the game: the other window comes to the front.
                    Switch(before);
                    break;
                case 2:
                    Program.Log($"back to the other window: foreground={GetForegroundWindow()}");
                    ClickField(form);
                    break;
                case 3:
                    Program.Log($"2nd click on the same field: foreground={GetForegroundWindow()} (overlay={form.Handle})");
                    Switch(before);
                    break;
                default:
                    t.Stop();
                    Program.Log($"end: foreground={GetForegroundWindow()}");
                    break;
            }
        };
        t.Start();
    }
}
