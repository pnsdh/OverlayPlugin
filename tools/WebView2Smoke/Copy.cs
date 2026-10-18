using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RainbowMage.HtmlRenderer;

static class Copy
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    static void Switch(IntPtr w)
    {
        // A key event lets this process change the foreground (Windows refuses otherwise).
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        uint pid; var ft = GetWindowThreadProcessId(GetForegroundWindow(), out pid); var me = GetCurrentThreadId();
        var att = ft != 0 && ft != me && AttachThreadInput(me, ft, true);
        SetForegroundWindow(w);
        if (att) AttachThreadInput(me, ft, false);
    }

    static string ReadClipboard() { try { return Clipboard.GetText(); } catch (Exception ex) { return "<busy " + ex.HResult + ">"; } }

    static void Click(OverlayForm form, int x, int y)
    {
        var r = form.Renderer;
        r.SendMouseMove(x, y, CefSharp.MouseButtonType.Left);
        r.SendMouseUpDown(x, y, CefSharp.MouseButtonType.Left, false);
        r.SendMouseUpDown(x, y, CefSharp.MouseButtonType.Left, true);
    }

    // Counts the samples where the overlay had the foreground since the last reset.
    static volatile int overlaySamples;
    static volatile bool sampling = true;

    public static void Run(OverlayForm form)
    {
        var game = System.Diagnostics.Process.Start("notepad.exe");
        game.WaitForInputIdle(3000);
        for (int i = 0; i < 30 && game.MainWindowHandle == IntPtr.Zero; i++) { System.Threading.Thread.Sleep(100); game.Refresh(); }
        var before = game.MainWindowHandle;
        var overlay = form.Handle;
        form.FormClosed += (s0, e0) => { sampling = false; try { game.Kill(); } catch { } };
        try { Clipboard.SetDataObject("before", true, 20, 50); } catch (Exception ex) { Program.Log("clipboard reset failed: " + ex.HResult); }
        Switch(before);
        new System.Threading.Thread(() =>
        {
            while (sampling)
            {
                if (GetForegroundWindow() == overlay) overlaySamples++;
                System.Threading.Thread.Sleep(1);
            }
        }) { IsBackground = true }.Start();

        var t = new Timer { Interval = 100 };
        int step = 0;
        t.Tick += (s, e) =>
        {
            step++;
            switch (step)
            {
                case 2:
                    Program.Log($"start: foreground is game={GetForegroundWindow() == before} (fg={GetForegroundWindow()} game={before} overlay={overlay})");
                    overlaySamples = 0;
                    Click(form, 40, 120);
                    break;
                case 8:
                    Program.Log($"after copy click: overlay had foreground in {overlaySamples} samples, foreground is game={GetForegroundWindow() == before}, clipboard=[{ReadClipboard()}]");
                    overlaySamples = 0;
                    Click(form, 40, 30);
                    break;
                case 12:
                    Program.Log($"after text field click: foreground is overlay={GetForegroundWindow() == overlay} (samples {overlaySamples})");
                    Switch(before);
                    break;
                case 15:
                    t.Stop();
                    Program.Log($"end: foreground is game={GetForegroundWindow() == before}");
                    break;
            }
        };
        t.Start();
    }
}


