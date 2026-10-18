using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using CefSharp;
using CefSharp.Enums;
using CefSharp.Internals;
using CefSharp.OffScreen;
using CefSharp.Structs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RainbowMage.HtmlRenderer
{
    public class Renderer : IDisposable
    {
        const string CRASH_SERVER = "https://sentry.gruenprint.de/api/12/minidump/?sentry_key=5742b37c4b71484e9ba590c6a6a0e137";
        public static bool noMoreRenders = false;

        // Fork: with the GPU option, overlay windows show CEF's shared textures through
        // DirectComposition (see DCompPresenter).  Set by Initialize.
        public static bool SharedTextures { get; private set; } = false;
        public static string SharedTextureError { get; private set; } = null;
        // Fork: overlays can be drawn by Edge WebView2 instead (see WebView2Browser).  Set by
        // InitializeWebView2: the runtime's version, or why it can't be used.
        public static bool WebView2Available { get; private set; } = false;
        public static string WebView2Status { get; private set; } = null;

        // Fork: why CEF's subprocess runs under its original name (see SubprocessPath), if it does.
        // CEF starts late (see EnsureCef), so this is also raised when it's known.
        public static string SubprocessStatus { get; private set; } = null;
        public static event Action<string> SubprocessCopyFailed;

        // Fork: plugins' config panels inside ACT's tabs (OverlayControl, e.g. cactbot's config
        // page) drawn by WebView2 too, rather than CEF (PluginConfig.PanelsUseWebView2).
        public static bool PanelsUseWebView2 { get; set; } = false;

        // Set when this renderer's shared textures failed (e.g. CEF's GPU differs from ours), so
        // its browser is recreated with CPU frames.
        private bool sharedTexturesFailed = false;

        public event EventHandler<BrowserErrorEventArgs> BrowserError;
        public event EventHandler<BrowserLoadEventArgs> BrowserStartLoading;
        public event EventHandler<BrowserLoadEventArgs> BrowserLoad;
        public event EventHandler<BrowserConsoleLogEventArgs> BrowserConsoleLog;

        private BrowserWrapper _browser;
        private bool _isWindowless;
        protected IRenderTarget _target;
        private object _api;
        private Func<int, int, bool> _ctxMenuCallback = null;
        private List<String> scriptQueue = new List<string>();
        private string urlToLoad = null;
        private string lastUrl = null;

        private int clickCount;
        private MouseButtonType lastClickButton;
        private DateTime lastClickTime;
        private int lastClickPosX;
        private int lastClickPosY;
        private string overlayName;
        private string overlayUuid;
        public Region DraggableRegion;

        // Fork: set when this overlay is drawn by WebView2; every method below then goes to _wv
        // instead of CEF.
        private bool _useWebView2;
        private WebView2Browser _wv;
        private double _zoomLevel;
        private bool _muted;

        public Renderer(string overlayName, string overlayUuid, string url, IRenderTarget target, object api, bool useWebView2 = false)
        {
            this.overlayName = overlayName;
            this.overlayUuid = overlayUuid;
            this._target = target;
            this.lastUrl = url;
            this._api = api;
            this._useWebView2 = useWebView2 && WebView2Available && target is Control;

            InitBrowser();
        }

        public bool UsesWebView2 => _useWebView2;

        // Fork: a config panel whose WebView2 couldn't start is drawn by CEF (see
        // WebView2Browser.StartInPanel).
        internal void FallBackToCef()
        {
            if (noMoreRenders || !_useWebView2) return;
            _useWebView2 = false;
            // Posted to ACT's UI thread: CEF failing to start too mustn't take it down.
            try
            {
                BeginRender();
            }
            catch (Exception ex)
            {
                RaiseError("CEF", ex.Message, lastUrl);
            }
        }

        public void InitBrowser()
        {
            if (noMoreRenders || _useWebView2) return;
            EnsureCef();

            this._browser = new BrowserWrapper(lastUrl ?? "about:blank", automaticallyCreateBrowser: false, target: _target);
            _browser.RequestHandler = new CustomRequestHandler(this);
            _browser.MenuHandler = new ContextMenuHandler(_ctxMenuCallback);
            _browser.BrowserInitialized += _browser_BrowserInitialized;
            _browser.FrameLoadStart += Browser_FrameLoadStart;
            _browser.FrameLoadEnd += Browser_FrameLoadEnd;
            _browser.LoadError += Browser_LoadError;
            _browser.ConsoleMessage += Browser_ConsoleMessage;
            _browser.DragHandler = new DragHandler(this);

            if (_api != null)
            {
                SetApi(_api);
            }
        }

        public void SetApi(object api)
        {
            _api = api;
            _wv?.SetApi(api);
            if (api != null && _browser != null)
                _browser.JavascriptObjectRepository.Register("OverlayPluginApi", api, isAsync: true);
        }

        public void SetContextMenuCallback(Func<int, int, bool> ctxMenuCallback)
        {
            _ctxMenuCallback = ctxMenuCallback;
            _wv?.SetContextMenuCallback(ctxMenuCallback);
            if (_browser != null)
                _browser.MenuHandler = new ContextMenuHandler(ctxMenuCallback);
        }

        private void _browser_BrowserInitialized(object sender, EventArgs e)
        {
            // Fork: here and in the other CEF event handlers, an exception thrown back into CEF
            // ends ACT.
            try
            {
                // Make sure we use the correct size for rendering. CEF sometimes ignores the size passed in WindowInfo (see BeginRender()).
                Resize(_target.Width, _target.Height);
            }
            catch (Exception ex)
            {
                RaiseCallbackError(ex);
            }
        }

        private void RaiseCallbackError(Exception ex)
        {
            try
            {
                BrowserConsoleLog?.Invoke(this, new BrowserConsoleLogEventArgs(ex.ToString(), "", 1));
            }
            catch (Exception)
            {
            }
        }

        private void Browser_FrameLoadStart(object sender, FrameLoadStartEventArgs e)
        {
            if (e.Frame?.IsValid != true || !e.Frame.IsMain)
                return;

            loadStarted = true;
            lastUrl = e.Url;

            try
            {
                if (_api != null)
                {
                    var initScript = @"(async () => {
                        await CefSharp.BindObjectAsync('OverlayPluginApi');
                        OverlayPluginApi.overlayName = " + JsonConvert.SerializeObject(this.overlayName) + @";
                        OverlayPluginApi.overlayUuid = " + JsonConvert.SerializeObject(this.overlayUuid) + @";
                        OverlayPluginApi.ready = true;
                    })();";
                    e.Frame.ExecuteJavaScriptAsync(initScript, "init");
                }

                foreach (var item in TakeQueuedScripts())
                {
                    e.Frame.ExecuteJavaScriptAsync(item, "injectOnLoad");
                }
            }
            catch (Exception ex)
            {
                RaiseCallbackError(ex);
            }

            try
            {
                BrowserStartLoading?.Invoke(this, new BrowserLoadEventArgs(0, e.Url));
            }
            catch (Exception ex)
            {
                BrowserConsoleLog?.Invoke(this, new BrowserConsoleLogEventArgs(ex.ToString(), "", 1));
            }
        }

        private void Browser_LoadError(object sender, LoadErrorEventArgs e)
        {
            // A load that failed did start (see WatchLoadStart).
            if (e.Frame?.IsMain == true)
                loadStarted = true;
            try
            {
                BrowserError?.Invoke(sender, new BrowserErrorEventArgs(e.ErrorCode, e.ErrorText, e.FailedUrl));
            }
            catch (Exception ex)
            {
                BrowserConsoleLog?.Invoke(this, new BrowserConsoleLogEventArgs(ex.ToString(), "", 1));
            }
        }

        public void Browser_ConsoleMessage(object sender, ConsoleMessageEventArgs e)
        {
            // Ignore FLoC exception errors. CEF doesn't include FLoC code which means that it doesn't understand
            // the FLoC exception rule. However, since it can't use FLoC to begin with, that's not an issue either way.
            try
            {
                if (!e.Message.StartsWith("Error with Permissions-Policy header:"))
                    BrowserConsoleLog?.Invoke(sender, new BrowserConsoleLogEventArgs(e.Message, e.Source, e.Line));
            }
            catch (Exception)
            {
                // Logging the failure would go through the same handlers.
            }
        }

        private void Browser_FrameLoadEnd(object sender, FrameLoadEndEventArgs e)
        {
            if (e.Frame?.IsValid != true || !e.Frame.IsMain)
                return;

            try
            {
                if (urlToLoad != null)
                {
                    _browser.Load(urlToLoad);
                    urlToLoad = null;
                }

                BrowserLoad?.Invoke(sender, new BrowserLoadEventArgs(e.HttpStatusCode, e.Url));
            }
            catch (Exception ex)
            {
                BrowserConsoleLog?.Invoke(this, new BrowserConsoleLogEventArgs(ex.ToString(), "", 1));
            }
        }

        public void Load(string url)
        {
            if (_useWebView2)
            {
                lastUrl = url;
                _wv?.Load(url);
                return;
            }
            if (this._browser != null && _browser.IsBrowserInitialized)
            {
                this._browser.Load(url);
            }
            else
            {
                urlToLoad = url;
            }
        }

        protected virtual WindowInfo CreateWindowInfo()
        {
            var cefWindowInfo = new WindowInfo();
            cefWindowInfo.SetAsWindowless(IntPtr.Zero);
            cefWindowInfo.Width = _target.Width;
            cefWindowInfo.Height = _target.Height;
            cefWindowInfo.SharedTextureEnabled = SharedTextures && !sharedTexturesFailed && _target is IAcceleratedRenderTarget;

            return cefWindowInfo;
        }

        public void BeginRender()
        {
            if (noMoreRenders) return;

            EndRender();
            if (_useWebView2)
            {
                _wv = new WebView2Browser(this, (Control)_target, lastUrl, _api, overlayName, overlayUuid, _ctxMenuCallback, _zoomLevel, _muted);
                foreach (var script in TakeQueuedScripts())
                    _wv.ExecuteScript(script);
                _wv.Start();
                return;
            }
            InitBrowser();

            var cefWindowInfo = CreateWindowInfo();
            _isWindowless = cefWindowInfo.WindowlessRenderingEnabled;

            var cefBrowserSettings = new BrowserSettings();
            cefBrowserSettings.WindowlessFrameRate = _target.MaxFrameRate;
            _browser.CreateBrowser(cefWindowInfo, cefBrowserSettings);

            cefBrowserSettings.Dispose();
            cefWindowInfo.Dispose();
            WatchLoadStart(_browser);
        }

        // Fork: a browser made right after CEF started once never loaded anything (cactbot's
        // config tab stayed blank until ACT restarted, not reproduced since): made again, once,
        // if it hasn't started loading after a while.
        private const int LoadStartTimeoutMs = 10000;
        private volatile bool loadStarted;
        private bool loadRetried;
        // The browser being watched, until EndRender (e.g. the overlay disabled meanwhile).
        private volatile BrowserWrapper watchedBrowser;

        private void WatchLoadStart(BrowserWrapper browser)
        {
            // Nothing to load without a URL (e.g. a new overlay): no load would start.
            if (loadRetried || string.IsNullOrEmpty(lastUrl) || !(_target is Control control))
                return;
            loadStarted = false;
            watchedBrowser = browser;
            Task.Delay(LoadStartTimeoutMs).ContinueWith(_ =>
            {
                if (loadStarted || noMoreRenders || browser != _browser || browser != watchedBrowser)
                    return;
                try
                {
                    control.BeginInvoke((Action)(() =>
                    {
                        if (loadStarted || noMoreRenders || browser != _browser ||
                            browser != watchedBrowser || loadRetried)
                            return;
                        loadRetried = true;
                        BrowserError?.Invoke(this, new BrowserErrorEventArgs(CefErrorCode.None, "The page didn't start loading in " + (LoadStartTimeoutMs / 1000) + "s: making the browser again.", lastUrl));
                        _browser = null;
                        browser.Dispose();
                        BeginRender();
                    }));
                }
                catch (Exception)
                {
                    // The window is gone.
                }
            });
        }

        // Fork: recreates the browser with CPU frames (OnPaint) after its shared textures failed.
        public void FallBackToCpuFrames()
        {
            if (sharedTexturesFailed) return;
            sharedTexturesFailed = true;
            BeginRender();
        }

        public void EndRender()
        {
            watchedBrowser = null;
            _wv?.Dispose();
            _wv = null;
            if (_browser != null && _browser.IsBrowserInitialized)
            {
                _browser.GetBrowser().CloseBrowser(true);
                _browser.GetBrowserHost().CloseBrowser(true);
                _browser.Dispose();
                _browser = null;
            }
        }

        public void SetMaxFramerate(int fps)
        {
            if (_browser != null && _browser.IsBrowserInitialized)
            {
                _browser.GetBrowserHost().WindowlessFrameRate = fps;
            }
        }

        public void Reload()
        {
            _wv?.Reload();
            if (this._browser != null && _browser.IsBrowserInitialized)
            {
                this._browser.Reload();
            }
        }

        public void Resize(int width, int height)
        {
            if (this._browser != null)
            {
                this._browser.Size = new System.Drawing.Size(width, height);

                if (!this._isWindowless && this._browser.IsBrowserInitialized)
                {
                    this._browser.Resize(width, height);
                }
            }
        }

        public void SetZoomLevel(double zoom)
        {
            _zoomLevel = zoom;
            _wv?.SetZoomLevel(zoom);
            if (this._browser != null && this._browser.IsBrowserInitialized)
            {
                this._browser.SetZoomLevel(zoom);
            }
        }

        public void SetMuted(bool muted)
        {
            _muted = muted;
            _wv?.SetMuted(muted);
            if (this._browser != null && this._browser.IsBrowserInitialized)
            {
                this._browser.GetBrowserHost().SetAudioMuted(muted);
            }
        }

        public void NotifyMoveStarted()
        {
            if (this._browser != null && this._browser.IsBrowserInitialized)
            {
                this._browser.GetBrowserHost().NotifyMoveOrResizeStarted();
            }
        }

        public void SetVisible(bool visible)
        {
            if (this._browser != null && this._browser.IsBrowserInitialized)
            {
                // Sometimes, CEF stops emitting OnPaint events after this call.
                // Not sure why, probably a timing issue.
                // TODO: Debug this further. Will probably require debugging libcef.dll directly.
                //       Modifying _browser.Size immidiately after the WasHidden() call leads to a crash in libcef.dll,
                //       that might be a good place to start investigating.
                //Task.Run(() => this._browser.GetBrowserHost().WasHidden(!visible));
            }
        }

        public void SendMouseMove(int x, int y, MouseButtonType button)
        {
            _wv?.SendMouseMove(x, y, button);
            if (this._browser != null && this._browser.IsBrowserInitialized)
            {
                var host = this._browser.GetBrowserHost();
                var modifiers = CefEventFlags.None;
                if (button == MouseButtonType.Left)
                {
                    modifiers = CefEventFlags.LeftMouseButton;
                }
                else if (button == MouseButtonType.Middle)
                {
                    modifiers = CefEventFlags.MiddleMouseButton;
                }
                else if (button == MouseButtonType.Right)
                {
                    modifiers = CefEventFlags.RightMouseButton;
                }

                var mouseEvent = new MouseEvent(x, y, modifiers);
                host.SendMouseMoveEvent(mouseEvent, false);
            }
        }

        public void SendMouseLeave()
        {
            _wv?.SendMouseLeave();
            if (this._browser != null && this._browser.IsBrowserInitialized)
            {
                this._browser.GetBrowserHost().SendMouseMoveEvent(new MouseEvent(0, 0, 0), true);
            }
        }

        public void SendMouseUpDown(int x, int y, MouseButtonType button, bool isMouseUp)
        {
            _wv?.SendMouseUpDown(x, y, button, isMouseUp);
            if (this._browser != null && this._browser.IsBrowserInitialized)
            {
                var host = this._browser.GetBrowserHost();

                if (!isMouseUp)
                {
                    if (IsContinuousClick(x, y, button))
                    {
                        clickCount++;
                    }
                    else
                    {
                        clickCount = 1;
                    }
                }

                var mouseEvent = new MouseEvent(x, y, CefEventFlags.None);
                host.SendMouseClickEvent(mouseEvent, button, isMouseUp, clickCount);

                lastClickPosX = x;
                lastClickPosY = y;
                lastClickButton = button;
                lastClickTime = DateTime.Now;
            }
        }

        public void SendMouseWheel(int x, int y, int delta, bool isVertical)
        {
            _wv?.SendMouseWheel(x, y, delta, isVertical);
            if (this._browser != null && this._browser.IsBrowserInitialized)
            {
                var host = this._browser.GetBrowserHost();
                var mouseEvent = new MouseEvent(x, y, CefEventFlags.None);
                host.SendMouseWheelEvent(mouseEvent, isVertical ? delta : 0, !isVertical ? delta : 0);
            }
        }

        private bool IsContinuousClick(int x, int y, MouseButtonType button)
        {
            // ダブルクリックとして認識するクリックの間隔よりも大きかった場合は継続的なクリックとみなさない
            var delta = (DateTime.Now - lastClickTime).TotalMilliseconds;
            if (delta > System.Windows.Forms.SystemInformation.DoubleClickTime)
            {
                return false;
            }

            // クリック位置が違う、もしくはボタンが違う場合にも継続的なクリックとはみなさない
            if (lastClickPosX != x || lastClickPosY != y || lastClickButton != button)
            {
                return false;
            }

            return true;
        }

        public void SendKeyEvent(KeyEvent keyEvent)
        {
            if (this._browser != null && this._browser.IsBrowserInitialized)
            {
                var host = this._browser.GetBrowserHost();

                host.SendKeyEvent(keyEvent);
            }
        }

        public void showDevTools(bool firstwindow = true)
        {
            _wv?.ShowDevTools();
            if (_browser != null && _browser.IsBrowserInitialized)
            {
                WindowInfo wi = new WindowInfo();
                wi.SetAsPopup(_browser.GetBrowserHost().GetWindowHandle(), "DevTools");
                _browser.GetBrowserHost().ShowDevTools(wi);
            }
        }

        // Fork: see WebView2Browser.ForwardKeyMessage.
        public void ForwardKeyMessage(int msg, IntPtr wParam, IntPtr lParam)
        {
            _wv?.ForwardKeyMessage(msg, wParam, lParam);
        }

        // Fork: see WebView2Browser.QueryOpaqueAt.
        public void QueryOpaqueAt(int x, int y, Action<bool> result)
        {
            if (_wv != null)
                _wv.QueryOpaqueAt(x, y, result);
            else
                result(false);
        }

        public Bitmap Screenshot()
        {
            if (_useWebView2)
                return _wv?.Screenshot();
            if (_browser != null && _browser.IsBrowserInitialized)
            {
                return _browser.Screenshot();
            }

            return null;
        }

        private bool _disposed = false;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    this._wv?.Dispose();
                    this._wv = null;
                    this._browser?.Dispose();
                    this._browser = null;
                }
                _disposed = true;
            }
        }

        static bool initialized = false;
        static bool configured = false;
        private static readonly object cefLock = new object();
        private static string cefPluginDirectory;
        private static string cefAppDataDirectory;
        private static bool cefUseGpu;
        private static string CachePath = null;

        // Fork: CEF and its profile live in folders of their own, so switching between this fork
        // and upstream (older CEF) never re-downloads CEF or opens a profile written by a newer
        // Chromium (which Chromium doesn't support).  Must match PluginLoader.GetCefPath.
        public const string CefFolderName = "OverlayPluginCef-cbkr";
        private const string CacheFolderName = "OverlayPluginCache-cbkr";
        private const string UpstreamCacheFolderName = "OverlayPluginCache";

        // Delete all folders which don't potentially contain overlay configuration information
        private static readonly string[] CacheFolders = new string[] { "Cache", "Code Cache", "GPUCache" };

        // Fork: CEF starts when the first overlay drawn by it is created (see EnsureCef), so
        // with every overlay drawn by WebView2 its processes never run.  This only prepares it.
        public static void Initialize(string pluginDirectory, string appDataDirectory, bool reportErrors, bool useGpu = false)
        {
            if (!configured)
            {
                // Fork: remove crash reporter configs even when reports are off (see EnableErrorReports).
                try
                {
                    DisableErrorReports(appDataDirectory);
                }
                catch (Exception)
                {
                }

                if (reportErrors)
                {
                    try
                    {
                        // Enable the Crashpad reporter. This *HAS* to happen before libcef.dll is loaded.
                        EnableErrorReports(appDataDirectory);
                    }
                    catch (Exception)
                    {
                        // TODO: Log this exception.
                    }
                }

                CachePath = Path.Combine(appDataDirectory, CacheFolderName);
                CopyUpstreamProfile(Path.Combine(appDataDirectory, UpstreamCacheFolderName), CachePath);

                // Fork: optional GPU rendering whose frames go straight to the overlay windows as
                // shared textures (no readback to the CPU).  Set up now: overlay windows read
                // SharedTextures before their renderer starts CEF.
                if (useGpu)
                {
                    string error;
                    SharedTextures = DCompPresenter.Initialize(out error);
                    SharedTextureError = error;
                }

                cefPluginDirectory = pluginDirectory;
                cefAppDataDirectory = appDataDirectory;
                cefUseGpu = useGpu;
                configured = true;
            }
        }

        public static bool CefStarted => initialized;

        private static void EnsureCef()
        {
            lock (cefLock)
            {
                if (initialized || !configured) return;
                StartCef(cefPluginDirectory, cefAppDataDirectory, cefUseGpu);
                initialized = true;
            }
        }

        // Kept apart so that CefSharp (and libcef.dll) only loads when CEF starts.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void StartCef(string pluginDirectory, string appDataDirectory, bool useGpu)
        {
#if DEBUG
            var cefPath = Path.Combine(pluginDirectory, "libs");
#else
            var cefPath = Path.Combine(appDataDirectory, CefFolderName, Environment.Is64BitProcess ? "x64" : "x86");
#endif

            var lang = System.Globalization.CultureInfo.CurrentCulture.Name;
            var langPak = Path.Combine(cefPath, "locales", lang + ".pak");

            // Fall back to en-US if we can't find the current locale.
            if (!File.Exists(langPak))
            {
                lang = "en-US";
            }

            var cefSettings = new CefSettings
            {
                WindowlessRenderingEnabled = true,
                Locale = lang,
                // CEF 120+ locks the root cache path per process and requires the cache path
                // to be inside it.
                RootCachePath = CachePath,
                CachePath = CachePath,
                MultiThreadedMessageLoop = true,
                LogFile = Path.Combine(appDataDirectory, "OverlayPluginCEF.log"),
#if DEBUG
                LogSeverity = LogSeverity.Info,
#else
                LogSeverity = LogSeverity.Error,
#endif
                BrowserSubprocessPath = SubprocessPath(cefPath),
            };

            try
            {
                File.WriteAllText(cefSettings.LogFile, "");
            }
            catch (Exception)
            {
                // Ignore; if we can't open the log, CEF can't do it either which means that we don't have to worry about log size.
            }

            // Necessary to avoid input lag with a framerate limit below 60.
            cefSettings.CefCommandLineArgs["enable-begin-frame-scheduling"] = "1";

            // Allow websites to play sound even if the user never interacted with that site (pretty common for our overlays)
            cefSettings.CefCommandLineArgs["autoplay-policy"] = "no-user-gesture-required";

            // Disable Flash. We don't need it and it can cause issues.
            cefSettings.CefCommandLineArgs.Remove("enable-system-flash");

            cefSettings.EnableAudio();

            // Fork: Chromium relaunches itself de-elevated when started elevated (CEF 120+), which
            // makes Cef.Initialize fail inside an ACT that runs as administrator.
            cefSettings.CefCommandLineArgs["do-not-de-elevate"] = "1";

            // Enables software compositing instead of GPU compositing -> less CPU load but no WebGL
            if (!useGpu)
            {
                cefSettings.SetOffScreenRenderingBestPerformanceArgs();
            }

            Cef.Initialize(cefSettings, performDependencyCheck: true, browserProcessHandler: null);
        }

        // Fork: other programs bundle CefSharp too, and Hancom Office's updater ends every process
        // named CefSharp.BrowserSubprocess.exe, wherever it runs from, which took the overlays and
        // then ACT down.  CEF runs a copy under another name instead, kept the same as the original.
        private const string SubprocessName = "OverlayPlugin.BrowserSubprocess.exe";

        private static string SubprocessPath(string cefPath)
        {
            var original = Path.Combine(cefPath, "CefSharp.BrowserSubprocess.exe");
            var copy = Path.Combine(cefPath, SubprocessName);
            try
            {
                if (!File.Exists(copy) || !File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(original)))
                    File.Copy(original, copy, true);
                SubprocessStatus = null;
                return copy;
            }
            catch (Exception e)
            {
                // Once, though CEF may be started again after failing.
                var known = SubprocessStatus == e.Message;
                SubprocessStatus = e.Message;
                try
                {
                    if (!known)
                        SubprocessCopyFailed?.Invoke(e.Message);
                }
                catch (Exception)
                {
                }
                return original;
            }
        }

        // Fork: checks for the WebView2 runtime (part of Windows 10/11 through Edge updates).
        public static void InitializeWebView2(string pluginDirectory, string appDataDirectory, bool panels = false)
        {
            var userData = Path.Combine(appDataDirectory, "OverlayPluginWebView2-cbkr");
            if (CachePath != null)
            {
                CopyCefStorage(Path.Combine(CachePath, "Default"), Path.Combine(userData, "EBWebView", "Default"));
                // Config panels' own folder (see WebView2Browser.GetEnvironment), when they use
                // it: their pages (e.g. cactbot's config) keep what they remembered in CEF then.
                if (panels)
                    CopyCefStorage(Path.Combine(CachePath, "Default"), Path.Combine(userData + "-panels", "EBWebView", "Default"));
            }
            string error;
            var version = WebView2Browser.Initialize(
                Path.Combine(pluginDirectory, "libs", "runtimes", Environment.Is64BitProcess ? "win-x64" : "win-x86", "native"),
                userData,
                out error);
            WebView2Available = version != null;
            WebView2Status = version ?? error;
        }

        // Fork: events from WebView2Browser, raised as the CEF handlers above raise them.
        internal void RaiseStartLoading(string url)
        {
            lastUrl = url;
            try
            {
                BrowserStartLoading?.Invoke(this, new BrowserLoadEventArgs(0, url));
            }
            catch (Exception ex)
            {
                BrowserConsoleLog?.Invoke(this, new BrowserConsoleLogEventArgs(ex.ToString(), "", 1));
            }
        }

        internal void RaiseLoad(int httpStatusCode, string url)
        {
            try
            {
                BrowserLoad?.Invoke(this, new BrowserLoadEventArgs(httpStatusCode, url));
            }
            catch (Exception ex)
            {
                BrowserConsoleLog?.Invoke(this, new BrowserConsoleLogEventArgs(ex.ToString(), "", 1));
            }
        }

        internal void RaiseError(string errorCode, string errorText, string url)
        {
            try
            {
                BrowserError?.Invoke(this, new BrowserErrorEventArgs(errorCode, errorText, url));
            }
            catch (Exception ex)
            {
                BrowserConsoleLog?.Invoke(this, new BrowserConsoleLogEventArgs(ex.ToString(), "", 1));
            }
        }

        internal void RaiseConsoleLog(string message, string source, int line)
        {
            BrowserConsoleLog?.Invoke(this, new BrowserConsoleLogEventArgs(message, source, line));
        }

        // True when nothing holds CEF's files anymore (see ClearCache).
        public static bool Shutdown()
        {
            if (initialized)
            {
                ShutdownCef();
                return true;
            }
            return configured;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void ShutdownCef()
        {
            Cef.Shutdown();
        }

        public static void EnableErrorReports(string appDataDirectory)
        {
            // Fork: crash reports would go to upstream's server (CRASH_SERVER) for builds upstream
            // didn't make, so they stay off and any config written by upstream is removed.
            DisableErrorReports(appDataDirectory);
            return;

#pragma warning disable CS0162 // Unreachable code: kept unchanged to ease upstream merges.
            // Unfortunately, we can't change the path to this configuration file without using our own .exe for
            // all overlays (which would add IPC overhead) or recompiling CEF.
            // For details, see this https://bitbucket.org/chromiumembedded/cef/wiki/CrashReporting.md

            var subProcess = Path.Combine(appDataDirectory,
                                          CefFolderName,
                                          Environment.Is64BitProcess ? "x64" : "x86");
            var subCfgPath = Path.Combine(subProcess, "crash_reporter.cfg");

            var cfgPath = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            cfgPath = Path.Combine(cfgPath, "crash_reporter.cfg");

            var version = Assembly.GetExecutingAssembly().GetName().Version.ToString();

            var cfgText = $@"
# This file was generated by OverlayPlugin to configure CEF's Crahspad reporter.
# For details on the purpose of this file see https://bitbucket.org/chromiumembedded/cef/wiki/CrashReporting.md.
# Please don't modify this file manually. Instead, disable it in OverlayPlugin's settings.

[Config]
ProductName=OverlayPlugin
ProductVersion={version}
AppName=ACT_OverlayPlugin
ExternalHandler={subProcess}\CefSharp.BrowserSubprocess.exe
ServerURL={CRASH_SERVER}

# Disable rate limiting so that all crashes are uploaded.
RateLimitEnabled=false
MaxUploadsPerDay=0
";
            File.WriteAllText(cfgPath, cfgText);
            File.WriteAllText(subCfgPath, cfgText);
#pragma warning restore CS0162
        }

        // Starts the fork's profile from upstream's once, so overlay settings kept in local storage
        // carry over.  Disposable caches are skipped.  Upstream's CEF 95 keeps the profile in the
        // cache folder itself; CEF 152 (Chrome bootstrap) uses its "Default" subfolder when the
        // cache path is the root cache path.
        private static void CopyUpstreamProfile(string from, string to)
        {
            if (Directory.Exists(to) || !Directory.Exists(from))
                return;

            var tmp = to + ".tmp";
            try
            {
                if (Directory.Exists(tmp))
                    Directory.Delete(tmp, true);
                CopyDirectory(from, Path.Combine(tmp, "Default"), true);
                Directory.Move(tmp, to);
            }
            catch (Exception)
            {
                // Start with an empty profile instead.
                try
                {
                    if (Directory.Exists(tmp))
                        Directory.Delete(tmp, true);
                }
                catch (Exception)
                {
                }
            }
        }

        // Fork: WebView2 keeps a profile of its own.  Before it exists, it starts with the pages'
        // data from the CEF profile (local storage and IndexedDB, where overlays such as MopiMopi
        // keep their settings), as the CEF profile started with upstream's.  CEF hasn't opened
        // them yet: no overlay exists at this point.
        private static void CopyCefStorage(string from, string to)
        {
            if (Directory.Exists(to) || !Directory.Exists(from))
                return;

            var tmp = to + ".tmp";
            try
            {
                if (Directory.Exists(tmp))
                    Directory.Delete(tmp, true);
                Directory.CreateDirectory(tmp);
                foreach (var name in new[] { "Local Storage", "IndexedDB" })
                {
                    var dir = Path.Combine(from, name);
                    if (Directory.Exists(dir))
                        CopyDirectory(dir, Path.Combine(tmp, name), false);
                }
                Directory.Move(tmp, to);
            }
            catch (Exception)
            {
                // Start with empty storage instead.
                try
                {
                    if (Directory.Exists(tmp))
                        Directory.Delete(tmp, true);
                }
                catch (Exception)
                {
                }
            }
        }

        private static void CopyDirectory(string from, string to, bool isRoot)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from))
            {
                var name = Path.GetFileName(file);
                // Chromium's lock files of a running instance and leftover temp files.
                if (name == "lockfile" || name == "LOCK" || name.EndsWith(".tmp"))
                    continue;
                File.Copy(file, Path.Combine(to, name));
            }
            foreach (var dir in Directory.GetDirectories(from))
            {
                var name = Path.GetFileName(dir);
                if (isRoot && Array.IndexOf(CacheFolders, name) >= 0)
                    continue;
                CopyDirectory(dir, Path.Combine(to, name), false);
            }
        }

        // Fork: CEF 120+ keeps the caches in the profile ("Default") and its shader caches at the
        // root; the root folders above are CEF 95's.
        private static readonly string[] ClearedCacheFolders = new string[]
        {
            "Cache", "Code Cache", "GPUCache", "GrShaderCache", "ShaderCache", "GPUPersistentCache",
            @"Default\Cache", @"Default\Code Cache", @"Default\GPUCache", @"Default\DawnGraphiteCache", @"Default\DawnWebGPUCache",
        };

        public static void ClearCache()
        {
            if (CachePath != null)
            {
                foreach (var folder in ClearedCacheFolders)
                {
                    var fullPath = Path.Combine(CachePath, folder);
                    try
                    {
                        if (Directory.Exists(fullPath))
                        {
                            Directory.Delete(fullPath, true);
                        }
                    }
                    catch (Exception)
                    {
                        // A file in use; the other folders are still cleared.
                    }
                }
            }
        }

        public static void DisableErrorReports(string appDataDirectory)
        {
            var subProcess = Path.Combine(appDataDirectory,
                                          CefFolderName,
                                          Environment.Is64BitProcess ? "x64" : "x86");
            var subCfgPath = Path.Combine(subProcess, "crash_reporter.cfg");

            var cfgPath = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            cfgPath = Path.Combine(cfgPath, "crash_reporter.cfg");

            if (File.Exists(cfgPath))
                File.Delete(cfgPath);

            if (File.Exists(subCfgPath))
                File.Delete(subCfgPath);
        }

        public void ExecuteScript(string script)
        {
            if (_useWebView2)
            {
                if (_wv != null)
                    _wv.ExecuteScript(script);
                else
                    QueueScript(script);
                return;
            }
            if (_browser != null && _browser.IsBrowserInitialized)
            {
                // Fork: disposed, as each frame object otherwise waits for the finalizer (one per event).
                using (var frame = _browser.GetMainFrame())
                    frame.ExecuteJavaScriptAsync(script, "injected");
            }
            else
            {
                QueueScript(script);
            }
        }

        // Fork: scripts come from event threads while the page loads on another, so the queue is
        // locked; and it is capped, as a disabled overlay keeps receiving events it never runs.
        private const int MaxQueuedScripts = 1000;

        private void QueueScript(string script)
        {
            lock (scriptQueue)
            {
                if (scriptQueue.Count >= MaxQueuedScripts)
                    scriptQueue.RemoveAt(0);
                scriptQueue.Add(script);
            }
        }

        private List<string> TakeQueuedScripts()
        {
            lock (scriptQueue)
            {
                var scripts = new List<string>(scriptQueue);
                scriptQueue.Clear();
                return scripts;
            }
        }

        // IJavascriptCallback can't be used outside HtmlRenderer. This helper allows other code
        // to invoke callbacks regardless.

        public static void ExecuteCallback(object callback, object param)
        {
            var scriptCallback = callback as IScriptCallback;
            if (scriptCallback != null)
            {
                scriptCallback.Invoke(param);
                return;
            }
            if (callback.GetType().GetInterface("IJavascriptCallback") == null)
            {
                throw new Exception("Invalid parameter passed for callback!");
            }

            var cb = (IJavascriptCallback)callback;
            using (cb)
            {
                if (cb.CanExecute)
                {
                    cb.ExecuteAsync(param);
                }
            }
        }
    }

    public class BrowserErrorEventArgs : EventArgs
    {
        public string ErrorCode { get; private set; }
        public string ErrorText { get; private set; }
        public string Url { get; private set; }
        public BrowserErrorEventArgs(CefErrorCode errorCode, string errorText, string url)
            : this(errorCode.ToString(), errorText, url)
        {
        }

        public BrowserErrorEventArgs(string errorCode, string errorText, string url)
        {
            this.ErrorCode = errorCode;
            this.ErrorText = errorText;
            this.Url = url;
        }
    }

    public class BrowserLoadEventArgs : EventArgs
    {
        public int HttpStatusCode { get; private set; }
        public string Url { get; private set; }
        public BrowserLoadEventArgs(int httpStatusCode, string url)
        {
            this.HttpStatusCode = httpStatusCode;
            this.Url = url;
        }
    }

    public class BrowserConsoleLogEventArgs : EventArgs
    {
        public string Message { get; private set; }
        public string Source { get; private set; }
        public int Line { get; private set; }
        public BrowserConsoleLogEventArgs(string message, string source, int line)
        {
            this.Message = message;
            this.Source = source;
            this.Line = line;
        }
    }

    public class RendererFeatureRequestEventArgs : EventArgs
    {
        public string Request { get; private set; }
        public RendererFeatureRequestEventArgs(string request)
        {
            this.Request = request;
        }
    }

    public class HandlerCallEventArgs : EventArgs
    {
        public JObject Payload { get; private set; }
        public Action<JObject> Callback { get; private set; }
        public HandlerCallEventArgs(JObject payload, Action<JObject> callback)
        {
            this.Payload = payload;
            this.Callback = callback;
        }
    }

    internal class BrowserWrapper : ChromiumWebBrowser, IRenderWebBrowser
    {
        IRenderTarget target;
        TaskCompletionSource<Bitmap> screenshotSource = null;
        object screenshotLock = new object();

        public BrowserWrapper(string address = "", BrowserSettings browserSettings = null,
            RequestContext requestContext = null, bool automaticallyCreateBrowser = true, IRenderTarget target = null) :
            base(address, browserSettings, requestContext, automaticallyCreateBrowser)
        {
            this.target = target;
        }

        public void Resize(int width, int height)
        {
            ((IWebBrowserInternal)this).BrowserAdapter.Resize(width, height);
        }

        public Bitmap Screenshot()
        {
            lock (screenshotLock)
            {
                var source = new TaskCompletionSource<Bitmap>();
                screenshotSource = source;
                // Fork: a page that doesn't change paints no frame to take; don't wait forever.
                if (!source.Task.Wait(5000))
                {
                    screenshotSource = null;
                    return null;
                }

                if (source.Task.Exception != null) throw source.Task.Exception;
                return source.Task.Result;
            }
        }

        void IRenderWebBrowser.OnPaint(PaintElementType type, Rect dirtyRect, IntPtr buffer, int width, int height)
        {
            if (screenshotSource != null)
            {
                try
                {
                    var bmp = new Bitmap(width, height);
                    var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    NativeMethods.CopyMemory(data.Scan0, buffer, (uint)(width * height * 4));
                    bmp.UnlockBits(data);

                    screenshotSource.SetResult(bmp);
                    screenshotSource = null;
                }
                catch (Exception ex)
                {
                    screenshotSource.SetException(ex);
                    screenshotSource = null;
                }
            }
            else
            {
                target.RenderFrame(type, dirtyRect, buffer, width, height);
            }
        }

        // Fork: shared texture frames (see Renderer.SharedTextures).
        void IRenderWebBrowser.OnAcceleratedPaint(PaintElementType type, Rect dirtyRect, AcceleratedPaintInfo acceleratedPaintInfo)
        {
            if (!(target is IAcceleratedRenderTarget accelerated)) return;

            accelerated.RenderAcceleratedFrame(type, dirtyRect, acceleratedPaintInfo.SharedTextureHandle);

            if (screenshotSource != null && type == PaintElementType.View)
            {
                try
                {
                    screenshotSource.SetResult(accelerated.CaptureFrame());
                }
                catch (Exception ex)
                {
                    screenshotSource.SetException(ex);
                }
                screenshotSource = null;
            }
        }

        void IRenderWebBrowser.OnPopupSize(Rect rect)
        {
            target.MovePopup(rect);
        }

        void IRenderWebBrowser.OnPopupShow(bool show)
        {
            target.SetPopupVisible(show);
        }

        void IRenderWebBrowser.OnCursorChange(IntPtr cursor, CursorType type, CursorInfo customCursorInfo)
        {
            try
            {
                target.Cursor = new Cursor(cursor);
            }
            catch (Exception)
            {
                // Fork: see _browser_BrowserInitialized.
            }
        }

        bool IRenderWebBrowser.GetScreenPoint(int contentX, int contentY, out int screenX, out int screenY)
        {
            screenX = (contentX + target.Location.X);
            screenY = (contentY + target.Location.Y);

            return true;
        }
    }
}
