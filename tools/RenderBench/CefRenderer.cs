using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using CefSharp;
using CefSharp.Enums;
using CefSharp.OffScreen;
using CefSharp.Structs;
using D3D11 = SharpDX.Direct3D11;
using DComp = SharpDX.DirectComposition;
using DXGI = SharpDX.DXGI;

// CEF offscreen rendering, shown the ways OverlayPlugin can show it (see Program).
sealed class CefRenderer : IRendererUnderTest
{
    readonly ChromiumWebBrowser browser;
    readonly HandlerBase handler;
    readonly string mode;
    readonly bool externalBeginFrame;

    public CefRenderer(OverlayWindow form, string mode, string url, int fps, int w, int h, bool probe, string logDir)
    {
        this.mode = mode;
        var cache = Path.Combine(Path.GetTempPath(), "RenderBenchCefCache");
        var settings = new CefSettings
        {
            WindowlessRenderingEnabled = true,
            RootCachePath = cache,
            CachePath = cache,
            MultiThreadedMessageLoop = true,
            LogSeverity = LogSeverity.Warning,
            LogFile = Path.Combine(logDir, "cef-" + mode + ".log"),
        };
        // As OverlayPlugin sets them (HtmlRenderer/Renderer.cs).
        settings.CefCommandLineArgs["enable-begin-frame-scheduling"] = "1";
        settings.CefCommandLineArgs["allow-file-access-from-files"] = "1";
        settings.CefCommandLineArgs["do-not-de-elevate"] = "1";
        if (mode == "cef-cpu")
            settings.SetOffScreenRenderingBestPerformanceArgs();
        foreach (var kv in (Environment.GetEnvironmentVariable("RB_CEFARGS") ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = kv.Split(new[] { '=' }, 2);
            settings.CefCommandLineArgs[parts[0]] = parts.Length > 1 ? parts[1] : "1";
        }
        // RB_SUBPROCESS=<name>: runs the subprocess under another name, as OverlayPlugin does.
        var subprocess = Environment.GetEnvironmentVariable("RB_SUBPROCESS");
        if (!string.IsNullOrEmpty(subprocess))
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            File.Copy(Path.Combine(dir, "CefSharp.BrowserSubprocess.exe"), Path.Combine(dir, subprocess), true);
            settings.BrowserSubprocessPath = Path.Combine(dir, subprocess);
        }
        if (!Cef.Initialize(settings,performDependencyCheck: true, browserProcessHandler: null))
            throw new InvalidOperationException("Cef.Initialize failed: " + Cef.GetExitCode());

        handler = mode == "cef-shared" ? new SharedTextureHandler(form, w, h) : (HandlerBase)new LayeredHandler(form, w, h);
        var bs = new BrowserSettings { WindowlessFrameRate = fps };
        browser = new ChromiumWebBrowser(url, bs, null, false) { RenderHandler = handler };
        if (probe)
        {
            browser.FrameLoadEnd += (s, e) =>
            {
                if (e.Frame.IsMain) e.Frame.ExecuteJavaScriptAsync(Program.ProbeScript);
            };
        }
        externalBeginFrame = Environment.GetEnvironmentVariable("RB_EXTBF") == "1";
        var wi = new WindowInfo();
        wi.SetAsWindowless(IntPtr.Zero);
        wi.Width = w;
        wi.Height = h;
        wi.SharedTextureEnabled = mode == "cef-shared";
        wi.ExternalBeginFrameEnabled = externalBeginFrame;
        browser.CreateBrowser(wi, bs);
    }

    public void Start() { }

    public void OnVBlank()
    {
        if (!externalBeginFrame || !browser.IsBrowserInitialized) return;
        try { browser.GetBrowserHost().SendExternalBeginFrame(); } catch { }
    }

    public string Report() => $"cef: {Stats.Total} frames painted; {Stats.Timing()}";

    public void Dispose()
    {
        browser.Dispose();
        handler.Dispose();
        Cef.Shutdown();
    }
}

static class Stats
{
    public static int Total;
    public static long OpenTicks, CopyTicks, PresentTicks, MaxPresentTicks, Calls;

    public static void Frame()
    {
        Interlocked.Increment(ref Total);
        Pacing.Frame();
    }

    public static string Timing()
    {
        if (Calls == 0) return "";
        double us(long t) => t * 1e6 / Stopwatch.Frequency / Calls;
        return $"per frame: open={us(OpenTicks):F0}us copy={us(CopyTicks):F0}us present={us(PresentTicks):F0}us max present={MaxPresentTicks * 1000.0 / Stopwatch.Frequency:F2}ms";
    }
}

abstract class HandlerBase : IRenderHandler
{
    protected readonly OverlayWindow Window;
    protected readonly int W, H;
    protected HandlerBase(OverlayWindow window, int w, int h) { Window = window; W = w; H = h; }
    public ScreenInfo? GetScreenInfo() => new ScreenInfo { DeviceScaleFactor = 1.0f };
    public Rect GetViewRect() => new Rect(0, 0, W, H);
    public bool GetScreenPoint(int viewX, int viewY, out int screenX, out int screenY) { screenX = viewX; screenY = viewY; return false; }
    public virtual void OnAcceleratedPaint(PaintElementType type, Rect dirtyRect, AcceleratedPaintInfo info) { }
    public virtual void OnPaint(PaintElementType type, Rect dirtyRect, IntPtr buffer, int width, int height) { }
    public void OnCursorChange(IntPtr cursor, CursorType type, CursorInfo customCursorInfo) { }
    public bool StartDragging(IDragData dragData, DragOperationsMask mask, int x, int y) => false;
    public void UpdateDragCursor(DragOperationsMask operation) { }
    public void OnPopupShow(bool show) { }
    public void OnPopupSize(Rect rect) { }
    public void OnImeCompositionRangeChanged(CefSharp.Structs.Range selectedRange, Rect[] characterBounds) { }
    public void OnVirtualKeyboardRequested(IBrowser browser, TextInputMode inputMode) { }
    public virtual void Dispose() { }
}

// OverlayPlugin's default path: copy the frame into a DIB, then UpdateLayeredWindow.
sealed class LayeredHandler : HandlerBase
{
    readonly IntPtr hdc, bits, hwnd;
    readonly Native.POINT position;

    public LayeredHandler(OverlayWindow window, int w, int h) : base(window, w, h)
    {
        hwnd = window.Handle;
        position = new Native.POINT { x = window.Left, y = window.Top };
        var screen = Native.GetDC(IntPtr.Zero);
        hdc = Native.CreateCompatibleDC(screen);
        Native.ReleaseDC(IntPtr.Zero, screen);
        var bi = new Native.BITMAPINFO { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var hbmp = Native.CreateDIBSection(hdc, ref bi, 0, out bits, IntPtr.Zero, 0);
        Native.SelectObject(hdc, hbmp);
    }

    public override void OnPaint(PaintElementType type, Rect dirtyRect, IntPtr buffer, int width, int height)
    {
        if (type != PaintElementType.View || width != W || height != H) return;
        Native.CopyMemory(bits, buffer, (uint)(width * height * 4));
        var pos = position;
        var size = new Native.SIZE { cx = width, cy = height };
        var src = new Native.POINT();
        var blend = new Native.BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        Native.UpdateLayeredWindow(hwnd, IntPtr.Zero, ref pos, ref size, hdc, ref src, 0, ref blend, 2);
        Stats.Frame();
    }
}

// The fork's GPU option: copy CEF's shared texture into a composition swap chain shown by
// DirectComposition (HtmlRenderer/DCompPresenter.cs).
sealed class SharedTextureHandler : HandlerBase
{
    readonly D3D11.Device device;
    readonly D3D11.Device1 device1;
    readonly DXGI.SwapChain1 swapChain;
    readonly DComp.Device dcomp;
    readonly DComp.Target target;
    readonly DComp.Visual visual;
    readonly D3D11.Query copyDone;
    readonly string dumpPath = Environment.GetEnvironmentVariable("RB_DUMP");
    readonly int dumpAt = int.Parse(Environment.GetEnvironmentVariable("RB_DUMP_AT") ?? "900");
    int frameCount;

    public SharedTextureHandler(OverlayWindow window, int w, int h) : base(window, w, h)
    {
        device = new D3D11.Device(SharpDX.Direct3D.DriverType.Hardware, D3D11.DeviceCreationFlags.BgraSupport);
        device1 = device.QueryInterface<D3D11.Device1>();
        using (var dxgiDevice = device.QueryInterface<DXGI.Device>())
        using (var adapter = dxgiDevice.Adapter)
        using (var factory = adapter.GetParent<DXGI.Factory2>())
        {
            Program.Log.WriteLine($"adapter={adapter.Description.Description}");
            var desc = new DXGI.SwapChainDescription1
            {
                Width = w,
                Height = h,
                Format = DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new DXGI.SampleDescription(1, 0),
                Usage = DXGI.Usage.RenderTargetOutput,
                BufferCount = 2,
                SwapEffect = DXGI.SwapEffect.FlipSequential,
                AlphaMode = DXGI.AlphaMode.Premultiplied,
                Scaling = DXGI.Scaling.Stretch,
            };
            swapChain = new DXGI.SwapChain1(factory, device, ref desc, null);
            dcomp = new DComp.Device(dxgiDevice);
        }
        target = DComp.Target.FromHwnd(dcomp, window.Handle, true);
        visual = new DComp.Visual(dcomp) { Content = swapChain };
        target.Root = visual;
        dcomp.Commit();
        copyDone = new D3D11.Query(device, new D3D11.QueryDescription { Type = D3D11.QueryType.Event });
    }

    public override void OnAcceleratedPaint(PaintElementType type, Rect dirtyRect, AcceleratedPaintInfo info)
    {
        if (type != PaintElementType.View) return;
        var sw = Stopwatch.StartNew();
        var ctx = device.ImmediateContext;
        using (var texture = device1.OpenSharedResource1<D3D11.Texture2D>(info.SharedTextureHandle))
        {
            Stats.OpenTicks += sw.ElapsedTicks;
            sw.Restart();
            var d = texture.Description;
            if (dumpPath != null && ++frameCount == dumpAt) Dump(texture, dumpPath);
            if (d.Width != W || d.Height != H) return;
            using (var back = swapChain.GetBackBuffer<D3D11.Texture2D>(0))
                ctx.CopyResource(texture, back);
            // As DCompPresenter: CEF reuses the texture once this returns.
            ctx.End(copyDone);
            ctx.Flush();
            while (!ctx.GetData(copyDone, D3D11.AsynchronousFlags.None, out int done) || done == 0)
                Thread.Yield();
        }
        Stats.CopyTicks += sw.ElapsedTicks;
        sw.Restart();
        swapChain.Present(0, DXGI.PresentFlags.None);
        var present = sw.ElapsedTicks;
        Stats.PresentTicks += present;
        if (present > Stats.MaxPresentTicks) Stats.MaxPresentTicks = present;
        Stats.Calls++;
        Stats.Frame();
    }

    // Raw BGRA: 8-byte header (width, height), then the rows.
    void Dump(D3D11.Texture2D texture, string path)
    {
        var d = texture.Description;
        var sd = new D3D11.Texture2DDescription
        {
            Width = d.Width,
            Height = d.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = d.Format,
            SampleDescription = new DXGI.SampleDescription(1, 0),
            Usage = D3D11.ResourceUsage.Staging,
            CpuAccessFlags = D3D11.CpuAccessFlags.Read,
        };
        using (var staging = new D3D11.Texture2D(device, sd))
        using (var f = new BinaryWriter(File.Create(path)))
        {
            var ctx = device.ImmediateContext;
            ctx.CopyResource(texture, staging);
            var box = ctx.MapSubresource(staging, 0, D3D11.MapMode.Read, D3D11.MapFlags.None);
            f.Write(d.Width);
            f.Write(d.Height);
            var row = new byte[d.Width * 4];
            for (var y = 0; y < d.Height; y++)
            {
                Marshal.Copy(box.DataPointer + y * box.RowPitch, row, 0, row.Length);
                f.Write(row);
            }
            ctx.UnmapSubresource(staging, 0);
        }
        Program.Log.WriteLine("dumped frame to " + path);
    }

    public override void Dispose()
    {
        copyDone.Dispose();
        visual.Dispose();
        target.Dispose();
        dcomp.Dispose();
        swapChain.Dispose();
        device1.Dispose();
        device.Dispose();
    }
}
