using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using RainbowMage.HtmlRenderer;

static class Ime
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] static extern IntPtr LoadKeyboardLayout(string id, uint flags);
    [DllImport("user32.dll")] static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint flags);
    [DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr h);
    [DllImport("imm32.dll")] static extern bool ImmReleaseContext(IntPtr h, IntPtr imc);
    [DllImport("imm32.dll")] static extern bool ImmSetOpenStatus(IntPtr imc, bool open);
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)] static extern bool ImmSetCompositionStringW(IntPtr imc, uint index, string comp, uint compLen, IntPtr read, uint readLen);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool SetFocus(IntPtr h);
    [DllImport("imm32.dll")] static extern bool ImmSetConversionStatus(IntPtr imc, uint conv, uint sent);
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public KEYBDINPUT ki; public long pad1; }
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    static void Key(ushort vk)
    {
        var a = new INPUT[2];
        a[0].type = 1; a[0].ki.wVk = vk;
        a[1].type = 1; a[1].ki.wVk = vk; a[1].ki.dwFlags = 2;
        SendInput(2, a, Marshal.SizeOf(typeof(INPUT)));
    }

    static void Switch(IntPtr w)
    {
        uint pid; var ft = GetWindowThreadProcessId(GetForegroundWindow(), out pid); var me = GetCurrentThreadId();
        var att = ft != 0 && ft != me && AttachThreadInput(me, ft, true);
        SetForegroundWindow(w);
        if (att) AttachThreadInput(me, ft, false);
    }

    public static void Run(OverlayForm form)
    {
        var r = form.Renderer;
        var game = System.Diagnostics.Process.Start("notepad.exe");
        game.WaitForInputIdle(3000);
        for (int i = 0; i < 30 && game.MainWindowHandle == IntPtr.Zero; i++) { System.Threading.Thread.Sleep(100); game.Refresh(); }
        form.FormClosed += (s0, e0) => { try { game.Kill(); } catch { } };
        Switch(game.MainWindowHandle);
        IntPtr input = IntPtr.Zero;
        EnumChildWindows(form.Handle, (h, l) => { input = h; return false; }, IntPtr.Zero);
        Program.Log($"input window={input}");
        // Click the field (the overlay takes the focus), type "ㅂㅈ", then compose "ㅇ".
        r.SendMouseMove(40, 30, CefSharp.MouseButtonType.Left);
        r.SendMouseUpDown(40, 30, CefSharp.MouseButtonType.Left, false);
        r.SendMouseUpDown(40, 30, CefSharp.MouseButtonType.Left, true);
        var t = new Timer { Interval = 300 };
        int step = 0;
        t.Tick += (s, e) =>
        {
            step++;
            if (step == 1)
            {
                var fg = GetForegroundWindow();
                Program.Log($"foreground={fg} overlay={form.Handle}");
                if (fg != form.Handle) { Program.Log("overlay not in front: not sending keys"); t.Stop(); return; }
                ActivateKeyboardLayout(LoadKeyboardLayout("00000412", 1), 0);
                var imc = ImmGetContext(input);
                ImmSetOpenStatus(imc, true);
                ImmSetConversionStatus(imc, 1 /* IME_CMODE_HANGUL */, 0);
                ImmReleaseContext(input, imc);
                Key(0x51); Key(0x57); Key(0x44);   // Q W D = ㅂ ㅈ ㅇ
                if (Environment.GetEnvironmentVariable("SMOKE_IME_BS") == "1") Key(0x08);   // Backspace deletes the composing ㅇ
                Program.Log("typed q w d");
            }
            else if (step == 2)
            {
                // As clicking the game.
                Switch(game.MainWindowHandle);
                Program.Log($"switched away: foreground={GetForegroundWindow()}");
            }
            else
            {
                t.Stop();
                r.ExecuteScript("console.log('final value=' + document.getElementById('i').value)");
            }
        };
        t.Start();
    }
}
