using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using D3D11 = SharpDX.Direct3D11;
using DComp = SharpDX.DirectComposition;
using DXGI = SharpDX.DXGI;

// Edge WebView2 in visual hosting mode: the browser draws straight into a DirectComposition visual
// of the overlay window, paced by the display like Edge itself.  There is no frame rate setting.
sealed class WebView2Renderer : IRendererUnderTest
{
    readonly OverlayWindow form;
    readonly string url;
    readonly int w, h;
    readonly bool probe;
    D3D11.Device device;
    DComp.Device dcomp;
    DComp.Target target;
    DComp.Visual visual;
    CoreWebView2CompositionController controller;
    string error;

    public WebView2Renderer(OverlayWindow form, string url, int w, int h, bool probe)
    {
        this.form = form;
        this.url = url;
        this.w = w;
        this.h = h;
        this.probe = probe;
    }

    public async void Start()
    {
        try
        {
            device = new D3D11.Device(SharpDX.Direct3D.DriverType.Hardware, D3D11.DeviceCreationFlags.BgraSupport);
            using (var dxgiDevice = device.QueryInterface<DXGI.Device>())
                dcomp = new DComp.Device(dxgiDevice);
            target = DComp.Target.FromHwnd(dcomp, form.Handle, true);
            visual = new DComp.Visual(dcomp);
            target.Root = visual;

            var options = new CoreWebView2EnvironmentOptions("--allow-file-access-from-files");
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Path.GetTempPath(), "RenderBenchWebView2"), options);
            Program.Log.WriteLine("webview2 runtime=" + env.BrowserVersionString);
            controller = await env.CreateCoreWebView2CompositionControllerAsync(form.Handle);
            controller.RootVisualTarget = Marshal.GetObjectForIUnknown(visual.NativePointer);
            controller.DefaultBackgroundColor = Color.Transparent;
            controller.Bounds = new Rectangle(0, 0, w, h);
            controller.IsVisible = true;
            dcomp.Commit();
            if (probe)
                await controller.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(Program.ProbeScript);
            controller.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            error = ex.ToString();
            Program.Log.WriteLine("webview2 failed: " + error);
        }
    }

    public void OnVBlank() { }

    public string Report() => error == null ? "webview2: ok" : "webview2: failed";

    public void Dispose()
    {
        controller?.Close();
        visual?.Dispose();
        target?.Dispose();
        dcomp?.Dispose();
        device?.Dispose();
    }
}
