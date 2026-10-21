using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Security.Permissions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CefSharp;
using CefSharp.Enums;
using CefSharp.Structs;
using RainbowMage.HtmlRenderer;
using CefRenderer = RainbowMage.HtmlRenderer.Renderer;
using Point = System.Drawing.Point;

namespace RainbowMage.HtmlRenderer
{
    public partial class OverlayForm : Form, IWinFormsTarget, IAcceleratedRenderTarget
    {
        private DIBitmap surfaceBuffer;
        private bool terminated = false;

        // Fork: with shared textures the window shows its frames through DirectComposition
        // instead of UpdateLayeredWindow (see DCompPresenter).  With WebView2, WebView2 draws into
        // the window's DirectComposition visual itself (see WebView2Browser).
        private readonly bool useWebView2;
        private readonly bool useDComp;
        private DCompPresenter presenter;
        private int sharedFrameFailures = 0;
        private System.Windows.Forms.Timer hitTestTimer;
        private bool hitTestPending;
        // Fork: the window handle for UpdateLayeredWindow, which runs on CEF's paint thread.
        private IntPtr layeredHandle;
        private Point lastHitTest;
        private DateTime lastHitTestTime;

        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int CP_NOCLOSE_BUTTON = 0x200;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        public WinFormsOffScreenRenderer Renderer { get; private set; }

        private string url;
        public string Url
        {
            get { return this.url; }
            set
            {
                this.url = value;
                this.Renderer.Load(value);
            }
        }

        private bool isClickThru;
        public bool IsClickThru
        {
            get
            {
                return this.isClickThru;
            }
            set
            {
                if (this.isClickThru != value)
                {
                    this.isClickThru = value;
                    UpdateMouseClickThru();
                }
            }
        }

        private int maxFrameRate;
        public int MaxFrameRate
        {
            get
            {
                return this.maxFrameRate;
            }
            set
            {
                this.maxFrameRate = value;
                this.Renderer.SetMaxFramerate(value);
            }
        }

        public bool Locked
        {
            get
            {
                return Renderer.Locked;
            }
            set
            {
                Renderer.Locked = value;
            }
        }

        public OverlayForm(string overlayName, string overlayUuid, string url, int maxFrameRate = 30, object api = null, bool useWebView2 = false)
        {
            // Before the window exists: they decide its style (CreateParams, OnHandleCreated).
            this.useWebView2 = useWebView2 && CefRenderer.WebView2Available;
            this.useDComp = this.useWebView2 || CefRenderer.SharedTextures;

            InitializeComponent();

            this.Renderer = new WinFormsOffScreenRenderer(overlayName, overlayUuid, url, this, api, this.useWebView2);
            this.maxFrameRate = maxFrameRate;
            this.url = url;

            ClearFrame();

            // Alt+Tab を押したときに表示されるプレビューから除外する
            HidePreview();
        }

        /// <summary>
        /// 指定されたフォームを Windows の Alt+Tab の切り替え候補から除外します。
        /// </summary>
        /// <param name="form"></param>
        public void HidePreview()
        {
            int ex = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
            ex |= NativeMethods.WS_EX_TOOLWINDOW;
            NativeMethods.SetWindowLongA(Handle, NativeMethods.GWL_EXSTYLE, (IntPtr)ex);
        }

        public void SetAcceptFocus(bool accept)
        {
            int ex = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
            if (accept)
            {
                ex &= ~WS_EX_NOACTIVATE;
            }
            else
            {
                ex |= WS_EX_NOACTIVATE;
            }
            NativeMethods.SetWindowLongA(Handle, NativeMethods.GWL_EXSTYLE, (IntPtr)ex);
        }

        public void Reload()
        {
            this.Renderer.Reload();
        }

        #region Layered window related stuffs
        protected override System.Windows.Forms.CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle = cp.ExStyle | WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_NOACTIVATE;
                if (useDComp)
                {
                    cp.ExStyle |= NativeMethods.WS_EX_NOREDIRECTIONBITMAP;
                }
                cp.ClassStyle = cp.ClassStyle | CP_NOCLOSE_BUTTON;

                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            layeredHandle = Handle;

            if (useDComp)
            {
                // Layered (for click-through) but drawn by DirectComposition: fully opaque layer.
                NativeMethods.SetLayeredWindowAttributes(Handle, 0, 255, NativeMethods.LWA_ALPHA);
                // WebView2 sets up the window's composition itself.
                if (!useWebView2)
                {
                    presenter?.Dispose();
                    presenter = new DCompPresenter(Handle);
                }

                if (hitTestTimer == null)
                {
                    hitTestTimer = new System.Windows.Forms.Timer { Interval = 30 };
                    hitTestTimer.Tick += HitTestTimer_Tick;
                    hitTestTimer.Start();
                }
            }
        }

        // Layered windows drawn by UpdateLayeredWindow let clicks through fully transparent pixels.
        // A DirectComposition window doesn't, so unless the overlay is click-through, the window
        // becomes transparent to the mouse while the cursor is over a transparent pixel.
        private void HitTestTimer_Tick(object sender, EventArgs e)
        {
            if (terminated || isClickThru || MouseButtons != MouseButtons.None) return;

            var cursor = Cursor.Position;
            if (!Bounds.Contains(cursor)) return;

            // WebView2's pixels come asynchronously (WebView2Browser.QueryOpaqueAt).
            if (useWebView2)
            {
                // Each answer takes a capture: again only when the cursor moved, or the page may
                // have changed under it.
                // An answer that never came (renderer gone or stuck) doesn't stop hit tests for good.
                if (hitTestPending && (DateTime.Now - lastHitTestTime).TotalSeconds > 2)
                    hitTestPending = false;
                if (hitTestPending || Renderer == null) return;
                if (cursor == lastHitTest && (DateTime.Now - lastHitTestTime).TotalMilliseconds < 250) return;
                lastHitTest = cursor;
                lastHitTestTime = DateTime.Now;
                hitTestPending = true;
                Renderer.QueryOpaqueAt(cursor.X - Left, cursor.Y - Top, result =>
                {
                    hitTestPending = false;
                    if (terminated || isClickThru || MouseButtons != MouseButtons.None) return;
                    if (result)
                        DisableMouseClickThru();
                    else
                        EnableMouseClickThru();
                });
                return;
            }

            if (presenter == null) return;
            // Fork: each read waits for the GPU while holding the presenters' lock (stalling every
            // GPU overlay's frames): again only when the cursor moved, or the page may have
            // changed under it.
            if (cursor == lastHitTest && (DateTime.Now - lastHitTestTime).TotalMilliseconds < 250) return;
            lastHitTest = cursor;
            lastHitTestTime = DateTime.Now;
            bool opaque;
            try
            {
                opaque = presenter.IsOpaqueAt(cursor.X - Left, cursor.Y - Top);
            }
            catch
            {
                return;
            }

            if (opaque)
            {
                DisableMouseClickThru();
            }
            else
            {
                EnableMouseClickThru();
            }
        }

        [SecurityPermission(SecurityAction.Demand, Flags = SecurityPermissionFlag.UnmanagedCode)]
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            const int WM_NCHITTEST = 0x84;

            if (m.Msg == WM_NCHITTEST && !this.Locked)
            {
                // Signed: a monitor left of or above the main one has negative coordinates.
                var lParam = m.LParam.ToInt64();
                var position = this.PointToClient(new Point((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF)));
                var hit = ResizeHitTest(position, this.ClientSize);
                if (hit != 0)
                {
                    m.Result = (IntPtr)hit;
                    return;
                }
            }

            if (m.Msg == NativeMethods.WM_KEYDOWN ||
                m.Msg == NativeMethods.WM_KEYUP ||
                m.Msg == NativeMethods.WM_CHAR ||
                m.Msg == NativeMethods.WM_SYSKEYDOWN ||
                m.Msg == NativeMethods.WM_SYSKEYUP ||
                m.Msg == NativeMethods.WM_SYSCHAR)
            {
                Renderer.OnKeyEvent(ref m);
            }
        }

        // Fork: an unlocked overlay resizes from any edge or corner, as a window does, not only from
        // its bottom right corner (where pages tend to cover their grip image, e.g. cactbot's).
        // The rest still moves it.  0 where it doesn't resize.
        private static int ResizeHitTest(Point p, System.Drawing.Size size)
        {
            const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
            const int HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
            // The edge's width, and how far along it from a corner it resizes both ways.
            const int edge = 8;
            const int corner = 16;

            // The whole corner square, as before.
            if (p.X >= size.Width - corner && p.Y >= size.Height - corner)
                return HTBOTTOMRIGHT;

            var left = p.X < edge;
            var right = p.X >= size.Width - edge;
            var top = p.Y < edge;
            var bottom = p.Y >= size.Height - edge;
            var nearLeft = p.X < corner;
            var nearRight = p.X >= size.Width - corner;
            var nearTop = p.Y < corner;
            var nearBottom = p.Y >= size.Height - corner;

            if ((top && nearLeft) || (left && nearTop)) return HTTOPLEFT;
            if ((top && nearRight) || (right && nearTop)) return HTTOPRIGHT;
            if ((bottom && nearLeft) || (left && nearBottom)) return HTBOTTOMLEFT;
            if (left) return HTLEFT;
            if (right) return HTRIGHT;
            if (top) return HTTOP;
            if (bottom) return HTBOTTOM;
            return 0;
        }

        private void UpdateLayeredWindowBitmap()
        {
            if (useDComp || surfaceBuffer.IsDisposed || this.terminated) { return; }

            var blend = new NativeMethods.BlendFunction
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA
            };
            var windowPosition = new NativeMethods.Point
            {
                X = this.Left,
                Y = this.Top
            };
            var surfaceSize = new NativeMethods.Size
            {
                Width = surfaceBuffer.Width,
                Height = surfaceBuffer.Height
            };
            var surfacePosition = new NativeMethods.Point
            {
                X = 0,
                Y = 0
            };

            IntPtr handle = IntPtr.Zero;
            try
            {
                if (!this.terminated)
                {
                    // Fork: kept from OnHandleCreated instead of waiting for the UI thread on every
                    // frame, which stalled drawing while ACT's UI was busy.
                    handle = layeredHandle;
                    if (handle == IntPtr.Zero)
                        return;

                    NativeMethods.UpdateLayeredWindow(
                        handle,
                        IntPtr.Zero,
                        ref windowPosition,
                        ref surfaceSize,
                        surfaceBuffer.DeviceContext,
                        ref surfacePosition,
                        0,
                        ref blend,
                        NativeMethods.ULW_ALPHA);
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
        #endregion

        #region Mouse click-thru related

        private void UpdateMouseClickThru()
        {
            if (this.isClickThru)
            {
                EnableMouseClickThru();
            }
            else
            {
                DisableMouseClickThru();
            }
        }

        private void EnableMouseClickThru()
        {
            var ex = NativeMethods.GetWindowLong(this.Handle, NativeMethods.GWL_EXSTYLE);
            if ((ex & NativeMethods.WS_EX_TRANSPARENT) != 0) return;
            NativeMethods.SetWindowLong(
                this.Handle,
                NativeMethods.GWL_EXSTYLE,
                ex | NativeMethods.WS_EX_TRANSPARENT);
        }

        private void DisableMouseClickThru()
        {
            var ex = NativeMethods.GetWindowLong(this.Handle, NativeMethods.GWL_EXSTYLE);
            if ((ex & NativeMethods.WS_EX_TRANSPARENT) == 0) return;
            NativeMethods.SetWindowLong(
                this.Handle,
                NativeMethods.GWL_EXSTYLE,
                ex & ~NativeMethods.WS_EX_TRANSPARENT);
        }

        #endregion

        public void RenderFrame(PaintElementType type, Rect dirtyRect, IntPtr buffer, int width, int height)
        {
            if (useDComp)
            {
                // CPU frames (no shared textures, or after falling back) are shown the same way.
                if (!this.terminated && presenter != null && type == PaintElementType.View)
                {
                    try
                    {
                        presenter.PresentBuffer(buffer, width, height);
                    }
                    catch
                    {
                    }
                }
                return;
            }

            if (!this.terminated)
            {
                try
                {
                    if (surfaceBuffer != null &&
                        (surfaceBuffer.Width != width || surfaceBuffer.Height != height))
                    {
                        surfaceBuffer.Dispose();
                        surfaceBuffer = null;
                    }

                    if (surfaceBuffer == null)
                    {
                        surfaceBuffer = new DIBitmap(width, height);
                    }

                    // TODO: DirtyRect に対応
                    surfaceBuffer.SetSurfaceData(buffer, (uint)(width * height * 4));

                    UpdateLayeredWindowBitmap();
                }
                catch
                {

                }
            }
        }

        public void RenderAcceleratedFrame(PaintElementType type, Rect dirtyRect, IntPtr sharedHandle)
        {
            if (this.terminated || presenter == null || type != PaintElementType.View) return;

            try
            {
                presenter.PresentShared(sharedHandle);
                sharedFrameFailures = 0;
            }
            catch
            {
                // E.g. CEF renders on another GPU than this device, so its textures can not be
                // opened here.  Switch this overlay to CPU frames.
                if (++sharedFrameFailures == 3 && IsHandleCreated)
                {
                    try
                    {
                        BeginInvoke(new Action(() => Renderer?.FallBackToCpuFrames()));
                    }
                    catch (InvalidOperationException)
                    {
                        // The window closed meanwhile; this runs on CEF's thread, where it would end ACT.
                    }
                }
            }
        }

        public Bitmap CaptureFrame()
        {
            return presenter?.Capture();
        }

        public void ClearFrame()
        {
            if (!this.terminated)
            {
                if (surfaceBuffer != null)
                {
                    surfaceBuffer.Dispose();
                }

                surfaceBuffer = new DIBitmap(Width, Height);
                UpdateLayeredWindowBitmap();
            }
        }

        private void OverlayForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            this.Renderer?.EndRender();
            terminated = true;
        }

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            terminated = true;

            if (disposing)
            {
                hitTestTimer?.Dispose();
                hitTestTimer = null;
                presenter?.Dispose();
                presenter = null;
                surfaceBuffer?.Dispose();
                surfaceBuffer = null;
                Renderer?.Dispose();
                Renderer = null;
                components?.Dispose();
                components = null;
            }

            base.Dispose(disposing);
        }

        public void MovePopup(Rect rect)
        {

        }

        public void SetPopupVisible(bool visible)
        {

        }
    }
}
