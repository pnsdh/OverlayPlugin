using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using CefSharp;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using D3D11 = SharpDX.Direct3D11;
using DComp = SharpDX.DirectComposition;
using DXGI = SharpDX.DXGI;

namespace RainbowMage.HtmlRenderer
{
    // Fork: an overlay drawn by Edge WebView2 (the renderer option of each overlay).  WebView2
    // draws straight into a DirectComposition visual of the overlay window, paced by the display
    // like Edge itself, while CEF's offscreen frames are not (see tools/RenderBench).  The page
    // gets the same OverlayPluginApi object through a small script over WebView2's messages.
    // WebView2 doesn't hand over its frames: pixels for hit tests and screenshots come through the
    // DevTools protocol.  All WebView2 calls happen on the window's (ACT's UI) thread.
    internal sealed class WebView2Browser : IDisposable
    {
        private static string userDataFolder;
        private static Task<CoreWebView2Environment> environment;
        private static D3D11.Device device;
        private static DComp.Device dcomp;

        private readonly Renderer owner;
        private readonly Form form;
        private readonly object api;
        private readonly string overlayName;
        private readonly string overlayUuid;
        private readonly Func<int, int, bool> contextMenuCallback;
        private DComp.Target target;
        private DComp.Visual visual;
        private CoreWebView2CompositionController controller;
        private string url;
        private double zoomFactor = 1;
        private bool muted;
        private bool navigated;
        private bool disposed;
        private readonly Dictionary<string, MethodInfo> apiMethods = new Dictionary<string, MethodInfo>();
        // Scripts from any thread, run together on the UI thread (see ExecuteScript).
        private readonly ConcurrentQueue<string> scripts = new ConcurrentQueue<string>();
        private int scriptFlushQueued;
        // The last button press, to send a second one as a double click (see SendMouseUpDown).
        private DateTime lastDownTime;
        private Point lastDownPosition;
        private MouseButtonType lastDownButton;

        // Returns the WebView2 runtime version, or null with the reason when it can't be used.
        public static string Initialize(string loaderFolder, string userData, out string error)
        {
            error = null;
            try
            {
                CoreWebView2Environment.SetLoaderDllFolderPath(loaderFolder);
                userDataFolder = userData;
                return CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        public WebView2Browser(Renderer owner, Form form, string url, object api, string overlayName, string overlayUuid,
            Func<int, int, bool> contextMenuCallback, double zoomLevel, bool muted)
        {
            this.owner = owner;
            this.form = form;
            this.url = url ?? "about:blank";
            this.api = api;
            this.overlayName = overlayName;
            this.overlayUuid = overlayUuid;
            this.contextMenuCallback = contextMenuCallback;
            this.zoomFactor = ZoomFactor(zoomLevel);
            this.muted = muted;
            if (api != null)
            {
                foreach (var method in api.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    apiMethods[method.Name] = method;
            }
        }

        // CEF's zoom levels: each step is 20% larger.
        private static double ZoomFactor(double level)
        {
            return Math.Pow(1.2, level);
        }

        public async void Start()
        {
            try
            {
                if (dcomp == null)
                {
                    device = new D3D11.Device(SharpDX.Direct3D.DriverType.Hardware, D3D11.DeviceCreationFlags.BgraSupport);
                    using (var dxgiDevice = device.QueryInterface<DXGI.Device>())
                        dcomp = new DComp.Device(dxgiDevice);
                }
                target = DComp.Target.FromHwnd(dcomp, form.Handle, true);
                visual = new DComp.Visual(dcomp);
                target.Root = visual;

                if (environment == null)
                {
                    var options = new CoreWebView2EnvironmentOptions("--allow-file-access-from-files --autoplay-policy=no-user-gesture-required");
                    environment = CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
                }
                var env = await environment;
                if (disposed) return;
                var created = await env.CreateCoreWebView2CompositionControllerAsync(form.Handle);
                if (disposed)
                {
                    created.Close();
                    return;
                }
                controller = created;
                controller.RootVisualTarget = Marshal.GetObjectForIUnknown(visual.NativePointer);
                controller.DefaultBackgroundColor = Color.Transparent;
                controller.Bounds = new Rectangle(Point.Empty, form.ClientSize);
                controller.ZoomFactor = zoomFactor;
                controller.IsVisible = form.Visible;
                dcomp.Commit();

                var core = controller.CoreWebView2;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.IsMuted = muted;
                core.NavigationStarting += OnNavigationStarting;
                core.NavigationCompleted += OnNavigationCompleted;
                core.WebMessageReceived += OnWebMessageReceived;
                core.ContextMenuRequested += OnContextMenuRequested;
                core.ProcessFailed += OnProcessFailed;
                controller.CursorChanged += OnCursorChanged;
                await core.AddScriptToExecuteOnDocumentCreatedAsync(ApiScript());
                await ForwardConsole(core);
                if (disposed) return;

                form.Resize += OnFormResize;
                form.VisibleChanged += OnFormVisibleChanged;
                form.Activated += OnFormActivated;
                core.Navigate(url);
            }
            catch (Exception ex)
            {
                owner.RaiseConsoleLog("WebView2: " + ex, "internal", 1);
            }
        }

        // OverlayPluginApi for the page: every public method of the API object, with a function
        // in the "callback" parameter answered through a message back.
        private string ApiScript()
        {
            if (api == null) return "";
            var methods = new JArray();
            foreach (var method in apiMethods.Values)
            {
                var parameters = method.GetParameters();
                var callbackIndex = Array.FindIndex(parameters, p => p.Name == "callback");
                methods.Add(new JObject { ["name"] = method.Name, ["callback"] = callbackIndex });
            }
            return @"(() => {
  const methods = " + methods.ToString(Formatting.None) + @";
  const pending = new Map();
  let next = 1;
  window.chrome.webview.addEventListener('message', (e) => {
    const cb = pending.get(e.data.callback);
    if (cb === undefined) return;
    pending.delete(e.data.callback);
    cb(e.data.result);
  });
  const api = {
    overlayName: " + JsonConvert.SerializeObject(overlayName) + @",
    overlayUuid: " + JsonConvert.SerializeObject(overlayUuid) + @",
  };
  for (const m of methods) {
    api[m.name] = (...args) => {
      const msg = { method: m.name, args: args };
      if (m.callback >= 0 && typeof args[m.callback] === 'function') {
        const id = next++;
        pending.set(id, args[m.callback]);
        msg.args = args.slice();
        msg.args[m.callback] = null;
        msg.callback = id;
      }
      window.chrome.webview.postMessage(msg);
    };
  }
  api.ready = true;
  window.OverlayPluginApi = api;
})();";
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JObject message;
            try
            {
                message = JObject.Parse(e.WebMessageAsJson);
            }
            catch (JsonException)
            {
                return;
            }
            MethodInfo method;
            if (!apiMethods.TryGetValue((string)message["method"] ?? "", out method)) return;
            var args = message["args"] as JArray ?? new JArray();
            var callbackId = message["callback"]?.Value<int>();

            var parameters = method.GetParameters();
            var values = new object[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].Name == "callback")
                    values[i] = callbackId.HasValue ? new Callback(this, callbackId.Value) : null;
                else if (i < args.Count && args[i].Type != JTokenType.Null)
                    values[i] = args[i].ToObject(parameters[i].ParameterType);
                else
                    values[i] = parameters[i].ParameterType.IsValueType ? Activator.CreateInstance(parameters[i].ParameterType) : null;
            }
            // Off the UI thread, like CEF's bound object calls.
            Task.Run(() =>
            {
                try
                {
                    method.Invoke(api, values);
                }
                catch (Exception ex)
                {
                    owner.RaiseConsoleLog($"OverlayPluginApi.{method.Name}: {ex.InnerException ?? ex}", "internal", 1);
                }
            });
        }

        // A callback for Renderer.ExecuteCallback.
        internal sealed class Callback : IScriptCallback
        {
            private readonly WebView2Browser browser;
            private readonly int id;

            public Callback(WebView2Browser browser, int id)
            {
                this.browser = browser;
                this.id = id;
            }

            public void Invoke(object result)
            {
                var json = JsonConvert.SerializeObject(new { callback = id, result });
                browser.OnUiThread(() => browser.controller?.CoreWebView2?.PostWebMessageAsJson(json));
            }
        }

        private void OnUiThread(Action action)
        {
            if (disposed || !form.IsHandleCreated) return;
            try
            {
                form.BeginInvoke((Action)(() =>
                {
                    if (!disposed) action();
                }));
            }
            catch (InvalidOperationException)
            {
                // The window is closing.
            }
        }

        // Events reach the page through scripts from many threads, often several per frame: they
        // are queued and run together, each on its own so one error doesn't stop the others.
        public void ExecuteScript(string script)
        {
            scripts.Enqueue(script);
            if (navigated && System.Threading.Interlocked.Exchange(ref scriptFlushQueued, 1) == 0)
                OnUiThread(FlushScripts);
        }

        private void FlushScripts()
        {
            scriptFlushQueued = 0;
            var core = controller?.CoreWebView2;
            if (core == null || !navigated) return;
            var batch = new StringBuilder();
            string script;
            while (scripts.TryDequeue(out script))
                batch.Append("try {\n").Append(script).Append("\n} catch (e) { console.error(e); }\n");
            if (batch.Length > 0)
                core.ExecuteScriptAsync(batch.ToString());
        }

        public void Load(string newUrl)
        {
            url = newUrl;
            controller?.CoreWebView2?.Navigate(newUrl);
        }

        public void Reload()
        {
            controller?.CoreWebView2?.Reload();
        }

        public void SetZoomLevel(double level)
        {
            zoomFactor = ZoomFactor(level);
            if (controller != null) controller.ZoomFactor = zoomFactor;
        }

        public void SetMuted(bool value)
        {
            muted = value;
            if (controller?.CoreWebView2 != null) controller.CoreWebView2.IsMuted = value;
        }

        public void ShowDevTools()
        {
            controller?.CoreWebView2?.OpenDevToolsWindow();
        }

        public void SendMouseMove(int x, int y, MouseButtonType button)
        {
            SendMouse(CoreWebView2MouseEventKind.Move, x, y, ButtonKeys(button, true), 0);
        }

        public void SendMouseLeave()
        {
            SendMouse(CoreWebView2MouseEventKind.Leave, 0, 0, CoreWebView2MouseEventVirtualKeys.None, 0);
        }

        public void SendMouseUpDown(int x, int y, MouseButtonType button, bool isMouseUp)
        {
            // Windows' order for a double click: down, up, double click, up.
            var doubleClick = false;
            if (!isMouseUp)
            {
                var now = DateTime.Now;
                doubleClick = button == lastDownButton && lastDownPosition == new Point(x, y) &&
                    (now - lastDownTime).TotalMilliseconds <= SystemInformation.DoubleClickTime;
                lastDownTime = doubleClick ? DateTime.MinValue : now;
                lastDownPosition = new Point(x, y);
                lastDownButton = button;
            }

            CoreWebView2MouseEventKind kind;
            switch (button)
            {
                case MouseButtonType.Middle:
                    kind = isMouseUp ? CoreWebView2MouseEventKind.MiddleButtonUp
                        : doubleClick ? CoreWebView2MouseEventKind.MiddleButtonDoubleClick : CoreWebView2MouseEventKind.MiddleButtonDown;
                    break;
                case MouseButtonType.Right:
                    kind = isMouseUp ? CoreWebView2MouseEventKind.RightButtonUp
                        : doubleClick ? CoreWebView2MouseEventKind.RightButtonDoubleClick : CoreWebView2MouseEventKind.RightButtonDown;
                    break;
                default:
                    kind = isMouseUp ? CoreWebView2MouseEventKind.LeftButtonUp
                        : doubleClick ? CoreWebView2MouseEventKind.LeftButtonDoubleClick : CoreWebView2MouseEventKind.LeftButtonDown;
                    break;
            }
            SendMouse(kind, x, y, ButtonKeys(button, !isMouseUp), 0);
        }

        // Whether the page's pixel under (x, y) of the window isn't fully transparent, like
        // DCompPresenter.IsOpaqueAt.  A capture costs about as much CPU whatever its size, so the
        // whole window's alpha is captured at most twice a second and the answers come from it:
        // at once while it is fresh, else after a new capture (about 20ms), on the UI thread.
        public async void QueryOpaqueAt(int x, int y, Action<bool> result)
        {
            var core = controller?.CoreWebView2;
            if (core == null || !navigated)
            {
                result(false);
                return;
            }
            var size = form.ClientSize;
            if (alphaMap == null || alphaMapSize != size || (DateTime.Now - alphaMapTime).TotalMilliseconds > 500)
            {
                try
                {
                    using (var bmp = await CapturePage(core, 0, 0, size.Width, size.Height))
                        alphaMap = ReadAlpha(bmp);
                    alphaMapSize = size;
                }
                catch (Exception)
                {
                    alphaMap = null;
                }
                alphaMapTime = DateTime.Now;
            }
            // Without a map, keep taking the mouse, as without hit tests.
            var map = alphaMap;
            result(map == null || x < 0 || y < 0 || x >= alphaMapSize.Width || y >= alphaMapSize.Height ||
                map[y * alphaMapSize.Width + x] != 0);
        }

        private byte[] alphaMap;
        private Size alphaMapSize;
        private DateTime alphaMapTime;

        private static byte[] ReadAlpha(Bitmap bmp)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[bmp.Width * 4];
                var alpha = new byte[bmp.Width * bmp.Height];
                for (var y = 0; y < bmp.Height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    for (var x = 0; x < bmp.Width; x++)
                        alpha[y * bmp.Width + x] = row[x * 4 + 3];
                }
                return alpha;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        // A PNG of the page through the DevTools protocol, transparency included.  The region is in
        // window pixels; the protocol takes CSS pixels of the document.
        private async Task<Bitmap> CapturePage(CoreWebView2 core, int x, int y, int width, int height)
        {
            var scale = controller.RasterizationScale * controller.ZoomFactor;
            var metrics = JObject.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getLayoutMetrics", "{}"));
            var viewport = metrics["cssVisualViewport"];
            // Within the viewport: capturing beyond it resizes the page for the capture.
            var args = new JObject
            {
                ["format"] = "png",
                ["captureBeyondViewport"] = false,
                ["clip"] = new JObject
                {
                    ["x"] = x / scale + ((double?)viewport?["pageX"] ?? 0),
                    ["y"] = y / scale + ((double?)viewport?["pageY"] ?? 0),
                    ["width"] = width / scale,
                    ["height"] = height / scale,
                    ["scale"] = scale,
                },
            };
            var json = await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", args.ToString(Formatting.None));
            var data = Convert.FromBase64String((string)JObject.Parse(json)["data"]);
            using (var stream = new MemoryStream(data))
            using (var decoded = new Bitmap(stream))
                return new Bitmap(decoded);
        }

        // The whole overlay as a bitmap (OverlayPluginApi.makeScreenshot).  Waits for the UI thread,
        // so it can't be called there.
        public Bitmap Screenshot()
        {
            if (!form.InvokeRequired) return null;
            var done = new TaskCompletionSource<Bitmap>();
            OnUiThread(async () =>
            {
                try
                {
                    var core = controller?.CoreWebView2;
                    done.TrySetResult(core == null ? null : await CapturePage(core, 0, 0, form.ClientSize.Width, form.ClientSize.Height));
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            });
            return done.Task.Wait(5000) ? done.Task.Result : null;
        }

        public void SendMouseWheel(int x, int y, int delta, bool isVertical)
        {
            SendMouse(isVertical ? CoreWebView2MouseEventKind.Wheel : CoreWebView2MouseEventKind.HorizontalWheel,
                x, y, CoreWebView2MouseEventVirtualKeys.None, (uint)delta);
        }

        private static CoreWebView2MouseEventVirtualKeys ButtonKeys(MouseButtonType button, bool down)
        {
            if (!down) return CoreWebView2MouseEventVirtualKeys.None;
            switch (button)
            {
                case MouseButtonType.Middle: return CoreWebView2MouseEventVirtualKeys.MiddleButton;
                case MouseButtonType.Right: return CoreWebView2MouseEventVirtualKeys.RightButton;
                default: return CoreWebView2MouseEventVirtualKeys.LeftButton;
            }
        }

        private void SendMouse(CoreWebView2MouseEventKind kind, int x, int y, CoreWebView2MouseEventVirtualKeys keys, uint data)
        {
            try
            {
                controller?.SendMouseInput(kind, keys, data, new Point(x, y));
            }
            catch (Exception)
            {
                // Not ready yet.
            }
        }

        private void OnFormResize(object sender, EventArgs e)
        {
            if (controller != null) controller.Bounds = new Rectangle(Point.Empty, form.ClientSize);
        }

        private void OnCursorChanged(object sender, object e)
        {
            try
            {
                form.Cursor = new Cursor(controller.Cursor);
            }
            catch (Exception)
            {
            }
        }

        // WebView2 reads keys from a window it puts in the overlay's window.  Keys that reach the
        // overlay's window instead (it has the focus, not WebView2's) are passed on to it, as CEF
        // gets them from Renderer.SendKeyEvent.
        public void ForwardKeyMessage(int msg, IntPtr wParam, IntPtr lParam)
        {
            var input = FindInputWindow();
            if (input != IntPtr.Zero)
                PostMessage(input, msg, wParam, lParam);
        }

        private IntPtr inputWindow;

        private IntPtr FindInputWindow()
        {
            if (inputWindow != IntPtr.Zero && IsWindow(inputWindow))
                return inputWindow;
            inputWindow = IntPtr.Zero;
            if (!form.IsHandleCreated) return IntPtr.Zero;
            EnumChildWindows(form.Handle, (hwnd, param) =>
            {
                var name = new StringBuilder(64);
                GetClassName(hwnd, name, name.Capacity);
                if (name.ToString().StartsWith("Chrome_WidgetWin"))
                {
                    inputWindow = hwnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return inputWindow;
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        // An overlay that takes the focus gives it to the page, for keyboard input.
        private void OnFormActivated(object sender, EventArgs e)
        {
            try
            {
                controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
            }
            catch (Exception)
            {
            }
        }

        // Hidden overlays stop drawing (and run their timers less often), like a hidden Edge tab.
        private void OnFormVisibleChanged(object sender, EventArgs e)
        {
            if (controller != null) controller.IsVisible = form.Visible;
        }

        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            navigated = false;
            owner.RaiseStartLoading(e.Uri);
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            var source = controller?.CoreWebView2?.Source ?? url;
            if (e.IsSuccess)
            {
                navigated = true;
                owner.RaiseLoad(e.HttpStatusCode, source);
                FlushScripts();
            }
            else
            {
                owner.RaiseError(e.WebErrorStatus.ToString(), e.WebErrorStatus.ToString(), source);
            }
        }

        private void OnContextMenuRequested(object sender, CoreWebView2ContextMenuRequestedEventArgs e)
        {
            // As ContextMenuHandler: the overlay's own menu if any, never the browser's.
            e.Handled = true;
            contextMenuCallback?.Invoke(e.Location.X, e.Location.Y);
        }

        private void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            owner.RaiseConsoleLog($"WebView2 process failed: {e.ProcessFailedKind} {e.Reason} {e.ExitCode}", "internal", 1);
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited ||
                e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited ||
                e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                OnUiThread(owner.BeginRender);
            }
        }

        // Console messages and uncaught errors, as CEF reports them (Renderer.BrowserConsoleLog).
        private async Task ForwardConsole(CoreWebView2 core)
        {
            core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived += (s, e) =>
            {
                try
                {
                    var data = JObject.Parse(e.ParameterObjectAsJson);
                    var text = string.Join(" ", ((data["args"] as JArray) ?? new JArray()).Select(a =>
                        a["value"] != null ? (a["value"].Type == JTokenType.String ? (string)a["value"] : a["value"].ToString(Formatting.None)) : (string)a["description"] ?? (string)a["type"]));
                    var frame = data["stackTrace"]?["callFrames"]?.FirstOrDefault();
                    owner.RaiseConsoleLog(text, (string)frame?["url"] ?? "", ((int?)frame?["lineNumber"] ?? 0) + 1);
                }
                catch (Exception)
                {
                }
            };
            core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived += (s, e) =>
            {
                try
                {
                    var details = JObject.Parse(e.ParameterObjectAsJson)["exceptionDetails"];
                    var text = (string)details?["exception"]?["description"] ?? (string)details?["text"] ?? "";
                    owner.RaiseConsoleLog(text, (string)details?["url"] ?? "", ((int?)details?["lineNumber"] ?? 0) + 1);
                }
                catch (Exception)
                {
                }
            };
            await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            form.Resize -= OnFormResize;
            form.VisibleChanged -= OnFormVisibleChanged;
            form.Activated -= OnFormActivated;
            try
            {
                controller?.Close();
            }
            catch (Exception)
            {
            }
            controller = null;
            if (target != null)
            {
                target.Root = null;
                dcomp?.Commit();
            }
            visual?.Dispose();
            visual = null;
            target?.Dispose();
            target = null;
        }
    }

    // Fork: a page callback that isn't CEF's (see Renderer.ExecuteCallback).
    internal interface IScriptCallback
    {
        void Invoke(object result);
    }
}
