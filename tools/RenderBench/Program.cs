// RenderBench: measures an HTML overlay the way OverlayPlugin shows it, in a click-through
// topmost window: CPU use of the whole process tree, and what actually reaches the screen.
// Usage: RenderBench <mode> <url> <fps> <seconds> <logfile> [width] [height]
//   cef-cpu     CEF software rendering, OnPaint -> UpdateLayeredWindow (OverlayPlugin default)
//   cef-gpu     CEF GPU rendering read back, OnPaint -> UpdateLayeredWindow
//   cef-shared  CEF GPU rendering, shared texture -> DirectComposition (the fork's GPU option)
//   webview2    Edge WebView2 drawing into a DirectComposition visual (fps is ignored)
// Environment:
//   RB_CEFARGS="switch=value;switch2"  extra CEF switches
//   RB_EXTBF=1                          CEF: external begin frames on every vblank
//   RB_DUMP=<file> RB_DUMP_AT=<frame>   cef-shared: raw BGRA dump of one frame
//   RB_NOPROBE=1                        no motion probe (and no on-screen measurement)
// See README.md.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;

interface IRendererUnderTest : IDisposable
{
    void Start();
    // Called on the vblank thread (see Pacing).
    void OnVBlank();
    string Report();
}

static class Program
{
    public static StreamWriter Log;

    // A small square injected into the page that the compositor moves across the top left
    // corner at a constant speed: DisplayProbe reads where it is on every screen update.
    public const int ProbeTrack = 480;
    public const double ProbeSpeed = 720; // px/s, i.e. 4px per refresh at 180Hz.
    public static readonly string ProbeScript =
        "(function () {" +
        "  const add = () => {" +
        "    const s = document.createElement('style');" +
        "    s.textContent = '@keyframes __rbProbe { from { transform: translateX(0) } to { transform: translateX(" + ProbeTrack + "px) } }" +
        "      #__rbProbe { position: fixed; left: 0; top: 0; width: 4px; height: 4px; background: #ff00ff;" +
        "      z-index: 2147483647; pointer-events: none; will-change: transform;" +
        "      animation: __rbProbe " + (ProbeTrack / ProbeSpeed * 1000).ToString("F0") + "ms linear infinite }';" +
        "    document.documentElement.appendChild(s);" +
        "    const d = document.createElement('div');" +
        "    d.id = '__rbProbe';" +
        "    document.documentElement.appendChild(d);" +
        "  };" +
        "  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', add);" +
        "  else add();" +
        "})();";

    [STAThread]
    static void Main(string[] args)
    {
        // A measuring tool: log failures and exit instead of leaving an error dialog on screen.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try { Log?.WriteLine("UNHANDLED " + e.ExceptionObject); } catch { }
            Environment.Exit(3);
        };

        if (args.Length < 5)
        {
            Console.Error.WriteLine("Usage: RenderBench <cef-cpu|cef-gpu|cef-shared|webview2> <url> <fps> <seconds> <logfile> [width] [height]");
            Environment.Exit(2);
        }
        var mode = args[0];
        var url = args[1];
        var fps = int.Parse(args[2]);
        var seconds = int.Parse(args[3]);
        Log = new StreamWriter(args[4]) { AutoFlush = true };
        var w = args.Length > 5 ? int.Parse(args[5]) : 600;
        var h = args.Length > 6 ? int.Parse(args[6]) : 400;
        var probe = Environment.GetEnvironmentVariable("RB_NOPROBE") != "1";

        var form = new OverlayWindow(noRedirection: mode == "cef-shared" || mode == "webview2", w, h);
        form.Show();
        IRendererUnderTest renderer;
        switch (mode)
        {
            case "webview2":
                renderer = new WebView2Renderer(form, url, w, h, probe);
                break;
            case "cef-cpu":
            case "cef-gpu":
            case "cef-shared":
                renderer = new CefRenderer(form, mode, url, fps, w, h, probe, Path.GetDirectoryName(Path.GetFullPath(args[4])));
                break;
            default:
                throw new ArgumentException("unknown mode " + mode);
        }
        Log.WriteLine($"mode={mode} fps={fps} size={w}x{h} url={url}");
        renderer.Start();
        Pacing.Start(renderer.OnVBlank);
        var display = probe ? new DisplayProbe(form.Location, ProbeTrack + 4) : null;
        display?.Start();

        var timer = new Timer { Interval = 1000 };
        var start = DateTime.Now;
        TimeSpan cpu0 = TimeSpan.Zero;
        var t0 = DateTime.Now;
        long t0Ticks = long.MaxValue;
        timer.Tick += (s, e) =>
        {
            var elapsed = (DateTime.Now - start).TotalSeconds;
            Log.WriteLine($"t={elapsed:F0}s");
            if (elapsed >= 5 && cpu0 == TimeSpan.Zero)
            {
                // Measure after loading and warm-up.
                cpu0 = TotalCpu();
                t0 = DateTime.Now;
                t0Ticks = Stopwatch.GetTimestamp();
            }
            if (elapsed >= seconds)
            {
                timer.Stop();
                var cpu = (TotalCpu() - cpu0).TotalSeconds;
                var wall = (DateTime.Now - t0).TotalSeconds;
                Log.WriteLine($"RESULT mode={mode} cpu={cpu / wall * 100:F1}% of one core (process tree) {Environment.GetEnvironmentVariable("RB_CEFARGS")}");
                Log.WriteLine(renderer.Report());
                Log.WriteLine(Pacing.Report(t0Ticks));
                if (display != null) Log.WriteLine(display.Report(t0Ticks, Pacing.Period));
                long privateBytes = 0, workingSet = 0;
                foreach (var p in Tree())
                {
                    try
                    {
                        p.Refresh();
                        privateBytes += p.PrivateMemorySize64;
                        workingSet += p.WorkingSet64;
                        Log.WriteLine($"  {p.ProcessName} {p.Id} cpu={p.TotalProcessorTime.TotalSeconds:F2}s private={p.PrivateMemorySize64 >> 20}MB ws={p.WorkingSet64 >> 20}MB");
                    }
                    catch { }
                }
                Log.WriteLine($"MEMORY private={privateBytes >> 20}MB working set={workingSet >> 20}MB (process tree)");
                display?.Dispose();
                renderer.Dispose();
                Application.Exit();
            }
        };
        timer.Start();
        Application.Run();
    }

    // This process and all of its descendants (CEF and WebView2 start nested helper processes).
    static List<Process> Tree()
    {
        var parents = new Dictionary<int, List<int>>();
        using (var q = new ManagementObjectSearcher("select ProcessId, ParentProcessId from Win32_Process"))
        {
            foreach (ManagementObject o in q.Get())
            {
                var id = Convert.ToInt32(o["ProcessId"]);
                var parent = Convert.ToInt32(o["ParentProcessId"]);
                if (!parents.TryGetValue(parent, out var children))
                    parents[parent] = children = new List<int>();
                children.Add(id);
            }
        }
        var result = new List<Process>();
        var queue = new Queue<int>();
        queue.Enqueue(Process.GetCurrentProcess().Id);
        var seen = new HashSet<int>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id)) continue;
            try { result.Add(Process.GetProcessById(id)); } catch { }
            if (parents.TryGetValue(id, out var children))
                foreach (var c in children) queue.Enqueue(c);
        }
        return result;
    }

    static TimeSpan TotalCpu()
    {
        var sum = TimeSpan.Zero;
        foreach (var p in Tree())
        {
            try { p.Refresh(); sum += p.TotalProcessorTime; } catch { }
        }
        return sum;
    }
}

// The overlay window: borderless, topmost, click-through and never activated, like OverlayPlugin's.
class OverlayWindow : Form
{
    readonly bool noRedirection;

    public OverlayWindow(bool noRedirection, int w, int h)
    {
        this.noRedirection = noRedirection;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new System.Drawing.Point(200, 200);
        Size = new System.Drawing.Size(w, h);
        TopMost = true;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x8 /* TOPMOST */ | 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */ | 0x20 /* TRANSPARENT */ | 0x80000 /* LAYERED */;
            if (noRedirection) cp.ExStyle |= 0x00200000; /* NOREDIRECTIONBITMAP */
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (noRedirection)
            Native.SetLayeredWindowAttributes(Handle, 0, 255, 2 /* LWA_ALPHA */);
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnPaintBackground(PaintEventArgs e) { }
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant, bmiColors;
    }
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] public static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);
    [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
}
