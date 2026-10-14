using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using D3D11 = SharpDX.Direct3D11;
using DComp = SharpDX.DirectComposition;
using DXGI = SharpDX.DXGI;

namespace RainbowMage.HtmlRenderer
{
    // Fork: shows an overlay's frames through DirectComposition instead of UpdateLayeredWindow.
    // With CEF's shared textures (OSR + GPU) the frame stays on the GPU: it is copied into a
    // composition swap chain with premultiplied alpha, so nothing is read back to the CPU.
    // Frames from OnPaint (CPU buffers) can be shown the same way, as a fallback.
    // All D3D work runs under one lock: CEF paints on its UI thread, hit tests and screenshots
    // come from other threads.
    // When the GPU device is lost (driver update or reset, TDR), the shared device is created
    // again and each presenter rebuilds its own objects on it (see EnsureCurrent).
    internal sealed class DCompPresenter : IDisposable
    {
        private static readonly object deviceLock = new object();
        private static D3D11.Device device;
        private static D3D11.Device1 device1;
        private static DComp.Device dcomp;
        // Signals when the copies out of CEF's shared texture are done (see PresentShared).
        private static D3D11.Query copyDone;
        // Counts the shared devices made, so presenters notice a new one.
        private static int deviceGeneration;
        private static DateTime lastDeviceAttempt;

        private readonly IntPtr hwnd;
        private int generation = -1;
        private DComp.Target target;
        private DComp.Visual visual;
        private DXGI.SwapChain1 swapChain;
        // Copy of the last frame, for hit tests and screenshots (back buffers rotate).
        private D3D11.Texture2D lastFrame;
        private D3D11.Texture2D pixelStaging;
        private int width, height;
        private bool disposed;

        // Creates the shared devices once.  False if this system can't do it (no D3D11.1 or
        // DirectComposition), in which case overlays keep using layered windows.
        public static bool Initialize(out string error)
        {
            lock (deviceLock)
            {
                error = null;
                if (device != null) return true;
                return CreateDevice(out error);
            }
        }

        // Under deviceLock.
        private static bool CreateDevice(out string error)
        {
            error = null;
            lastDeviceAttempt = DateTime.Now;
            try
            {
                device = new D3D11.Device(SharpDX.Direct3D.DriverType.Hardware, D3D11.DeviceCreationFlags.BgraSupport);
                device1 = device.QueryInterface<D3D11.Device1>();
                using (var dxgiDevice = device.QueryInterface<DXGI.Device>())
                {
                    dcomp = new DComp.Device(dxgiDevice);
                }
                deviceGeneration++;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.ToString();
                ReleaseDevice();
                return false;
            }
        }

        // Under deviceLock.
        private static void ReleaseDevice()
        {
            copyDone?.Dispose();
            dcomp?.Dispose();
            device1?.Dispose();
            device?.Dispose();
            copyDone = null;
            dcomp = null;
            device1 = null;
            device = null;
        }

        // Under deviceLock.  Whether an exception came from the device being lost; if so a new
        // one is made, which presenters move to on their next call.
        private static bool HandleDeviceLoss()
        {
            if (device == null) return true;
            SharpDX.Result reason;
            try
            {
                reason = device.DeviceRemovedReason;
            }
            catch (Exception)
            {
                reason = SharpDX.Result.Fail;
            }
            if (reason.Success) return false;

            ReleaseDevice();
            CreateDevice(out _);
            return true;
        }

        public DCompPresenter(IntPtr hwnd)
        {
            this.hwnd = hwnd;
            lock (deviceLock)
            {
                EnsureCurrent();
            }
        }

        // Under deviceLock.  Builds this window's objects on the current shared device, again
        // after it was replaced.  False while there is no device (it is retried every 2 seconds).
        private bool EnsureCurrent()
        {
            if (device == null && (DateTime.Now - lastDeviceAttempt).TotalSeconds >= 2)
                CreateDevice(out _);
            if (device == null) return false;
            if (generation == deviceGeneration) return true;

            ReleaseOwn();
            target = DComp.Target.FromHwnd(dcomp, hwnd, true);
            visual = new DComp.Visual(dcomp);
            target.Root = visual;
            dcomp.Commit();
            generation = deviceGeneration;
            return true;
        }

        // Under deviceLock.
        private void ReleaseOwn()
        {
            pixelStaging?.Dispose();
            lastFrame?.Dispose();
            swapChain?.Dispose();
            visual?.Dispose();
            target?.Dispose();
            pixelStaging = null;
            lastFrame = null;
            swapChain = null;
            visual = null;
            target = null;
            width = 0;
            height = 0;
        }

        // Shows CEF's shared texture.  The handle is only valid during OnAcceleratedPaint.
        public void PresentShared(IntPtr sharedHandle)
        {
            lock (deviceLock)
            {
                if (disposed || !EnsureCurrent()) return;
                try
                {
                    PresentSharedOnDevice(sharedHandle);
                }
                catch (Exception) when (HandleDeviceLoss())
                {
                    // The frame is lost with the device; the next one goes to the new device.
                }
            }
        }

        private void PresentSharedOnDevice(IntPtr sharedHandle)
        {
            var ctx = device.ImmediateContext;
            using (var texture = device1.OpenSharedResource1<D3D11.Texture2D>(sharedHandle))
            {
                var desc = texture.Description;
                EnsureSize(desc.Width, desc.Height);
                using (var back = swapChain.GetBackBuffer<D3D11.Texture2D>(0))
                {
                    ctx.CopyResource(texture, back);
                    ctx.CopyResource(texture, lastFrame);
                }
                // CEF reuses the texture for a later frame as soon as OnAcceleratedPaint
                // returns, so wait until the GPU has copied it (well under a millisecond).
                copyDone = copyDone ?? new D3D11.Query(device, new D3D11.QueryDescription { Type = D3D11.QueryType.Event });
                ctx.End(copyDone);
                ctx.Flush();
                // The query answers a 4-byte BOOL: GetData<bool> asks for 1 byte and never succeeds.
                // Bounded: a GPU being reset may never answer, and this holds deviceLock.
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (!ctx.GetData(copyDone, D3D11.AsynchronousFlags.None, out int done) || done == 0)
                {
                    if (waited.ElapsedMilliseconds > 100)
                        throw new TimeoutException("The GPU didn't finish copying the frame.");
                    System.Threading.Thread.Yield();
                }
            }
            swapChain.Present(0, DXGI.PresentFlags.None);
        }

        // Shows a BGRA buffer with premultiplied alpha from OnPaint.
        public void PresentBuffer(IntPtr buffer, int bufferWidth, int bufferHeight)
        {
            lock (deviceLock)
            {
                if (disposed || !EnsureCurrent()) return;
                try
                {
                    EnsureSize(bufferWidth, bufferHeight);
                    var ctx = device.ImmediateContext;
                    ctx.UpdateSubresource(new SharpDX.DataBox(buffer, bufferWidth * 4, 0), lastFrame, 0);
                    using (var back = swapChain.GetBackBuffer<D3D11.Texture2D>(0))
                    {
                        ctx.CopyResource(lastFrame, back);
                    }
                    swapChain.Present(0, DXGI.PresentFlags.None);
                }
                catch (Exception) when (HandleDeviceLoss())
                {
                }
            }
        }

        private void EnsureSize(int w, int h)
        {
            if (swapChain != null && w == width && h == height) return;

            lastFrame?.Dispose();
            // Not left pointing at the disposed texture if creating the new one fails.
            lastFrame = null;
            lastFrame = new D3D11.Texture2D(device, new D3D11.Texture2DDescription
            {
                Width = w,
                Height = h,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new DXGI.SampleDescription(1, 0),
                Usage = D3D11.ResourceUsage.Default,
            });

            if (swapChain == null)
            {
                using (var dxgiDevice = device.QueryInterface<DXGI.Device>())
                using (var adapter = dxgiDevice.Adapter)
                using (var factory = adapter.GetParent<DXGI.Factory2>())
                {
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
                }
                visual.Content = swapChain;
                dcomp.Commit();
            }
            else
            {
                swapChain.ResizeBuffers(2, w, h, DXGI.Format.B8G8R8A8_UNorm, DXGI.SwapChainFlags.None);
            }

            width = w;
            height = h;
        }

        // Whether the pixel at (x, y) of the last frame isn't fully transparent.
        public bool IsOpaqueAt(int x, int y)
        {
            lock (deviceLock)
            {
                if (disposed || !EnsureCurrent() || lastFrame == null || x < 0 || y < 0 || x >= width || y >= height) return false;

                try
                {
                    if (pixelStaging == null)
                        pixelStaging = CreateStaging(1, 1);

                    var ctx = device.ImmediateContext;
                    ctx.CopySubresourceRegion(lastFrame, 0, new D3D11.ResourceRegion(x, y, 0, x + 1, y + 1, 1), pixelStaging, 0);
                    var box = ctx.MapSubresource(pixelStaging, 0, D3D11.MapMode.Read, D3D11.MapFlags.None);
                    var alpha = Marshal.ReadByte(box.DataPointer, 3);
                    ctx.UnmapSubresource(pixelStaging, 0);
                    return alpha != 0;
                }
                catch (Exception) when (HandleDeviceLoss())
                {
                    return false;
                }
            }
        }

        // The last frame as a bitmap (premultiplied BGRA, like OnPaint's buffers).
        public Bitmap Capture()
        {
            lock (deviceLock)
            {
                if (disposed || !EnsureCurrent() || lastFrame == null) return null;

                using (var staging = CreateStaging(width, height))
                {
                    var ctx = device.ImmediateContext;
                    ctx.CopyResource(lastFrame, staging);
                    var box = ctx.MapSubresource(staging, 0, D3D11.MapMode.Read, D3D11.MapFlags.None);
                    try
                    {
                        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                        for (var row = 0; row < height; row++)
                        {
                            NativeMethods.CopyMemory(data.Scan0 + row * data.Stride, box.DataPointer + row * box.RowPitch, (uint)(width * 4));
                        }
                        bmp.UnlockBits(data);
                        return bmp;
                    }
                    finally
                    {
                        ctx.UnmapSubresource(staging, 0);
                    }
                }
            }
        }

        private D3D11.Texture2D CreateStaging(int w, int h)
        {
            return new D3D11.Texture2D(device, new D3D11.Texture2DDescription
            {
                Width = w,
                Height = h,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new DXGI.SampleDescription(1, 0),
                Usage = D3D11.ResourceUsage.Staging,
                CpuAccessFlags = D3D11.CpuAccessFlags.Read,
            });
        }

        public void Dispose()
        {
            lock (deviceLock)
            {
                if (disposed) return;
                disposed = true;
                if (visual != null && generation == deviceGeneration)
                {
                    visual.Content = null;
                }
                ReleaseOwn();
                dcomp?.Commit();
            }
        }
    }
}
