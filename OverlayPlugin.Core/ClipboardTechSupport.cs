using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using Newtonsoft.Json.Linq;
using RainbowMage.OverlayPlugin.MemoryProcessors;
using RainbowMage.OverlayPlugin.MemoryProcessors.Aggro;
using RainbowMage.OverlayPlugin.MemoryProcessors.AtkStage;
using RainbowMage.OverlayPlugin.MemoryProcessors.ClientFramework;
using RainbowMage.OverlayPlugin.MemoryProcessors.Combatant;
using RainbowMage.OverlayPlugin.MemoryProcessors.ContentFinderSettings;
using RainbowMage.OverlayPlugin.MemoryProcessors.Enmity;
using RainbowMage.OverlayPlugin.MemoryProcessors.EnmityHud;
using RainbowMage.OverlayPlugin.MemoryProcessors.InCombat;
using RainbowMage.OverlayPlugin.MemoryProcessors.JobGauge;
using RainbowMage.OverlayPlugin.MemoryProcessors.Party;
using RainbowMage.OverlayPlugin.MemoryProcessors.Target;

namespace RainbowMage.OverlayPlugin
{
    // Facilitates copying useful info to the clipboard about OP plugins and overlays
    // so that folks don't have to paste screenshots in discord.
    //
    // Fork: one compact block that fits a Discord message (2000 characters without Nitro), with
    // what this fork adds: each overlay's renderer, the GPU and panel options, the CEF and WebView2
    // versions, which memory reader version was picked (none: not found, e.g. after a game patch),
    // where the opcodes come from, and development switches left on.  When it is too long, the
    // least useful details go first (see Compose).
    class ClipboardTechSupport
    {
        public const int DiscordLimit = 2000;
        public const string DiscordInvite = "https://discord.com/invite/R75tq68bA9";

        string ACTPathPattern;
        string ACTPathPatternAlt;
        readonly string ACTPathReplace = "<ACT>";

        private readonly List<string> warnings = new List<string>();
        private readonly List<(string Name, string Version, bool Enabled, string Folder)> plugins = new List<(string, string, bool, string)>();
        private readonly List<(string Name, string Flags, string Url)> overlays = new List<(string, string, string)>();
        private readonly List<string> header = new List<string>();
        private readonly List<string> fork = new List<string>();
        private readonly List<(string Text, bool Checked, bool Default)> pluginOptions = new List<(string, bool, bool)>();
        private readonly List<string> cactbot = new List<string>();

        private const ulong WS_POPUP = 0x80000000L;
        private const ulong WS_CAPTION = 0x00C00000L;
        private const uint TOKEN_QUERY = 0x0008;

        private static IntPtr ph = IntPtr.Zero;
        private static WindowsIdentity actWi = null;
        private static WindowsIdentity ffxivWi = null;
        private static Process[] actProcesses = null;
        private static string gameLanguage = null;
        private static string actIsAdmin = null;
        private static string ffxivIsAdmin = null;
        private static string screenMode = null;

        [DllImport("user32.dll")]
        static extern ulong GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        static string hideChatLogForPrivacyName = "chkDisableCombatLog";

        // FFXIV_ACT_Plugin's checkboxes: a short name and the usual value (only others are listed
        // when the text is too long).
        static List<(string, string, bool)> pluginCheckboxMap = new List<(string, string, bool)> {
            ( "chkUseDeucalion", "Deucalion", true ),
            ( hideChatLogForPrivacyName, "HideChatLog", false ),
            ( "chkUsePcap", "WinPCap", false ),
            ( "chkDisableSocketFilter", "NoSocketFilter", false ),
            ( "chkDisableCombinePets", "NoCombinePets", false ),
            ( "chkDisableDamageShield", "NoDamageShield", false ),
            ( "chkShowDebug", "Debug", false ),
            ( "chkLogAllNetwork", "DBG:LogAllNetwork", false ),
            ( "chkShowRealDoTs", "DBG:RealDoTs", false ),
            ( "chkSimulateDoTCrits", "DBG:SimDoTCrits", false ),
            ( "chkGraphPotency", "DBG:GraphPotency", false ),
            ( "chkEnableBenchmark", "DBG:Benchmark", false ),
        };

        public ClipboardTechSupport(TinyIoCContainer container)
        {
            string ActAppData = ActGlobals.oFormActMain.AppDataFolder.FullName;
            string ActAppDataAlt = ActGlobals.oFormActMain.AppDataFolder.FullName.Replace('\\', '/').Replace(" ", "%20");
            ACTPathPattern = $@"{Regex.Escape(ActAppData)}";
            ACTPathPatternAlt = $@"{Regex.Escape(ActAppDataAlt)}";

            actProcesses = Process.GetProcessesByName("Advanced Combat Tracker");
            if (actProcesses.Length > 1)
            {
                warnings.Add("Multiple instances of ACT running");
            }

            bool foundFFIXVActPlugin = false;
            bool foundOverlayPlugin = false;
            string Version(string name) => plugins.FirstOrDefault(p => p.Name == name).Version ?? "?";

            foreach (var plugin in ActGlobals.oFormActMain.ActPlugins)
            {
                var fullPath = plugin.pluginFile.FullName;
                var version = FileVersionInfo.GetVersionInfo(fullPath);
                string folder = Censor(Path.GetDirectoryName(fullPath) ?? "");
                plugins.Add((plugin.pluginFile.Name, version.FileVersion?.ToString() ?? "", plugin.cbEnabled.Checked, folder));

                if (plugin.pluginFile.Name.StartsWith("FFXIV_ACT_Plugin", StringComparison.OrdinalIgnoreCase))
                {
                    foundFFIXVActPlugin = true;
                }
                else if (plugin.pluginFile.Name == "OverlayPlugin.dll")
                {
                    foundOverlayPlugin = true;
                    if (!foundFFIXVActPlugin)
                    {
                        warnings.Add("OverlayPlugin.dll loaded before FFXIV_ACT_Plugin.dll");
                    }
                }
                else if (!foundFFIXVActPlugin || !foundOverlayPlugin)
                {
                    warnings.Add($"{plugin.pluginFile.Name} loaded before FFXIV_ACT_Plugin.dll or OverlayPlugin.dll");
                }
            }

            var repository = container.Resolve<FFXIVRepository>();
            var pluginConfig = container.Resolve<IPluginConfig>();
            header.Add($"OverlayPlugin {repository.GetOverlayPluginVersion()} (custom) | ACT {typeof(FormActMain).Assembly.GetName().Version}" +
                (repository.IsFFXIVPluginPresent() ? $" | FFXIV_ACT_Plugin {repository.GetPluginVersion()}" : ""));

            foreach (var overlay in pluginConfig.Overlays)
            {
                var flags = new List<string>();
                var config = overlay as OverlayConfigBase;
                if (config != null && config.Disabled)
                    flags.Add("off");
                else if (!overlay.IsVisible)
                    flags.Add("hidden");
                if (config != null && config.UseWebView2)
                    flags.Add("WebView2");
                else
                    flags.Add(config != null && config.MatchRefreshRate ? "CEF monitor-rate" : $"CEF {overlay.MaxFrameRate}fps");
                overlays.Add((overlay.Name, string.Join(" ", flags), Censor(overlay.Url ?? "")));
            }

            if (repository.IsFFXIVPluginPresent())
            {
                gameLanguage = GetGameLanguage(container);
                string gameVersion = repository.GetGameVersion();
                gameVersion = gameVersion != "" ? gameVersion : "not running";
                if (screenMode == null)
                {
                    screenMode = "unknown";
                    repository.RegisterProcessChangedHandler(GetFFXIVScreenMode);
                }
                try
                {
                    actWi = WindowsIdentity.GetCurrent();
                    actIsAdmin = actWi.Owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ? "admin" : "not admin";
                }
                catch (Exception e)
                {
                    actIsAdmin = "unknown";
                    warnings.Add("Could not check for ACT process elevation: " + e.Message);
                }
                if (ffxivIsAdmin == null)
                {
                    ffxivIsAdmin = "unknown";
                    repository.RegisterProcessChangedHandler(GetFFXIVIsRunningAsAdmin);
                }
                header.Add($"Game {gameVersion} {repository.GetMachinaRegion()}, client {gameLanguage}, parse {repository.GetLanguage()}, {screenMode} | ACT {actIsAdmin}, FFXIV {ffxivIsAdmin}");

                var tabPage = repository.GetPluginTabPage();
                if (tabPage != null)
                {
                    var checkboxes = new Dictionary<string, CheckBox>();
                    GetCheckboxes(tabPage.Controls, checkboxes);
                    var debug = true;
                    foreach (var (cbName, settingText, usual) in pluginCheckboxMap)
                    {
                        if (!checkboxes.TryGetValue(cbName, out var cb))
                            continue;
                        if (cb.Name == "chkShowDebug" && !cb.Checked)
                            debug = false;
                        if (!debug && settingText.StartsWith("DBG:"))
                        {
                            checkboxes.Remove(cbName);
                            continue;
                        }
                        pluginOptions.Add((settingText, cb.Checked, cb.Checked == usual));
                        if (cb.Name == hideChatLogForPrivacyName && cb.Checked)
                            warnings.Add("Hide Chat Log for Privacy is enabled");
                        checkboxes.Remove(cbName);
                    }
                    foreach (var cb in checkboxes.Values)
                    {
                        if (!debug && cb.Text.Contains("DEBUG"))
                            continue;
                        pluginOptions.Add((cb.Text, cb.Checked, !cb.Checked));
                    }
                }

                // Fork: memory readers (a version, or none found) and opcodes, which a game patch breaks.
                fork.Add("Memory " + string.Join(", ", new (string, Func<IVersionedMemory>)[]
                {
                    ("Combatant", () => container.Resolve<ICombatantMemory>()),
                    ("Target", () => container.Resolve<ITargetMemory>()),
                    ("Aggro", () => container.Resolve<IAggroMemory>()),
                    ("Enmity", () => container.Resolve<IEnmityMemory>()),
                    ("EnmityHud", () => container.Resolve<IEnmityHudMemory>()),
                    ("InCombat", () => container.Resolve<IInCombatMemory>()),
                    ("JobGauge", () => container.Resolve<IJobGaugeMemory>()),
                    ("ContentFinder", () => container.Resolve<IContentFinderSettingsMemory>()),
                    ("Framework", () => container.Resolve<IClientFrameworkMemory>()),
                    ("Party", () => container.Resolve<IPartyMemory>()),
                    ("AtkStage", () => container.Resolve<IAtkStageMemory>()),
                }.Select(m => $"{m.Item1} {MemoryVersion(m.Item2)}")));
                try
                {
                    var region = repository.GetMachinaRegion().ToString();
                    fork.Add($"Opcodes {region} {repository.GetGameVersion()}: {container.Resolve<OverlayPluginLogLineConfig>().DescribeSource(region, repository.GetGameVersion())}");
                }
                catch (Exception)
                {
                }
            }
            else
            {
                warnings.Add("FFXIV plugin not present");
            }

            // Fork: rendering.
            var full = pluginConfig as PluginConfig;
            string webView2 = HtmlRenderer.Renderer.WebView2Available
                ? HtmlRenderer.Renderer.WebView2Status
                : $"n/a ({HtmlRenderer.Renderer.WebView2Status})";
            var renderer = $"GPU {OnOff(full != null && full.UseGpu)}, panels {(full != null && full.PanelsUseWebView2 ? "WebView2" : "CEF")}" +
                $" | CEF {CefVersion()} | WebView2 {webView2} | update check {OnOff(full != null && full.UpdateCheck)}";
            fork.Insert(0, renderer);
            var switches = new[] { "op-probe.enable", "op-selfcheck.enable" }
                .Where(f => File.Exists(Path.Combine(Path.GetTempPath(), f))).ToList();
            if (switches.Count > 0)
                fork.Add("Dev switches on: " + string.Join(", ", switches));

            // Note: this is a little bit of an abstraction violation to have OverlayPlugin
            // throw up information about cactbot.
            var cactbotConfig = GetCactbotConfig(pluginConfig);
            if (cactbotConfig != null)
            {
                try
                {
                    var userDir = cactbotConfig["options"]["general"]["CactbotUserDirectory"];
                    cactbot.Add("user dir " + Censor(userDir.ToString()));
                }
                catch { }
                try
                {
                    var skin = cactbotConfig["options"]["raidboss"]?["Skin"];
                    if (skin != null)
                        cactbot.Add("skin " + skin);
                }
                catch { }
            }
        }

        private string Censor(string text)
        {
            text = Regex.Replace(text, ACTPathPattern, ACTPathReplace, RegexOptions.IgnoreCase);
            text = Regex.Replace(text, ACTPathPatternAlt, ACTPathReplace, RegexOptions.IgnoreCase);
            text = text.Replace("file:///" + ACTPathReplace, ACTPathReplace);
            return text.Replace(Environment.UserName, "<USER>");
        }

        private static string OnOff(bool on) => on ? "on" : "off";

        private static string MemoryVersion(Func<IVersionedMemory> resolve)
        {
            try
            {
                var memory = resolve();
                return memory.IsValid() ? memory.GetVersion()?.ToString() ?? "?" : "NONE";
            }
            catch (Exception)
            {
                return "?";
            }
        }

        // CefSharp's version and the Chromium it runs, when CEF is loaded.
        private static string CefVersion()
        {
            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "CefSharp");
                if (assembly == null)
                    return "not loaded";
                var cef = assembly.GetType("CefSharp.Cef");
                var chromium = cef?.GetProperty("ChromiumVersion")?.GetValue(null) as string;
                return assembly.GetName().Version + (chromium != null ? $" (Chromium {chromium})" : "");
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private Dictionary<string, JToken> GetCactbotConfig(IPluginConfig pluginConfig)
        {
            if (!pluginConfig.EventSourceConfigs.ContainsKey("CactbotESConfig"))
                return null;

            var obj = pluginConfig.EventSourceConfigs["CactbotESConfig"];
            if (!obj.TryGetValue("OverlayData", out JToken value))
                return null;

            try
            {
                return value.ToObject<Dictionary<string, JToken>>();
            }
            catch
            {
                return null;
            }
        }

        private static string GetGameLanguage(TinyIoCContainer container)
        {
            var clientFrameworkMemory = container.Resolve<IClientFrameworkMemory>();
            if (!clientFrameworkMemory.IsValid())
            {
                return "unknown";
            }

            var clientFramework = clientFrameworkMemory.GetClientFramework();
            if (!clientFramework.foundLanguage)
            {
                return "unknown";
            }
            return clientFramework.clientLanguage.ToString();
        }

        private void GetFFXIVScreenMode(Process process)
        {
            GetFFXIVScreenMode(process, 0);
        }

        private void GetFFXIVScreenMode(Process process, int attempt)
        {
            if (process == null)
            {
                screenMode = "not running";
                return;
            }

            // If a handler exists when the game is closed and later re-opened, GetFFXIVScreenMode()
            // will be called with the new process before a main window handle is available.
            IntPtr mainWindowHandle;
            try
            {
                mainWindowHandle = process.HasExited ? IntPtr.Zero : process.MainWindowHandle;
            }
            catch (Exception)
            {
                mainWindowHandle = IntPtr.Zero;
            }
            if (mainWindowHandle == IntPtr.Zero)
            {
                screenMode = "not running";
                // Fork: tried again later instead of sleeping here, which froze ACT for 15 seconds
                // when this ran on its UI thread.
                if (attempt < 4)
                    Task.Delay(15000).ContinueWith(_ => GetFFXIVScreenMode(process, attempt + 1));
                return;
            }

            ulong style = GetWindowLongPtr(mainWindowHandle, -16);

            if ((style & WS_POPUP) != 0)
            {
                screenMode = "borderless";
            }
            else if ((style & WS_CAPTION) != 0)
            {
                screenMode = "windowed";
            }
            else
            {
                warnings.Add("Game running in Full Screen mode.");
                screenMode = "full screen";
            }
        }

        private void GetFFXIVIsRunningAsAdmin(Process process)
        {
            if (process == null)
            {
                ffxivIsAdmin = "not running";
                return;
            }

            try
            {
                OpenProcessToken(process.Handle, TOKEN_QUERY, out ph);
                ffxivWi = new WindowsIdentity(ph);
                ffxivIsAdmin = ffxivWi.Owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ? "admin" : "not admin";
            }
            catch (Exception e)
            {
                // Will get an access-denied exception if ACT is not elevated, but FFXIV is.
                if (e.Message.Contains("Access is denied"))
                {
                    ffxivIsAdmin = "likely admin (access denied)";
                }
                else
                {
                    ffxivIsAdmin = "unknown";
                    warnings.Add("Could not check for FFXIV process elevation: " + e.Message);
                }
            }
            finally
            {
                if (ph != IntPtr.Zero)
                {
                    CloseHandle(ph);
                }
            }
        }

        private static void GetCheckboxes(Control.ControlCollection controls, Dictionary<string, CheckBox> checkboxes)
        {
            foreach (Control control in controls)
            {
                if (control.GetType() == typeof(CheckBox))
                {
                    CheckBox cb = (CheckBox)control;
                    checkboxes[cb.Name] = cb;
                }
                if (control.Controls.Count > 0)
                {
                    GetCheckboxes(control.Controls, checkboxes);
                }
            }
        }

        // The text in a code block, as short as needed to fit a Discord message: first full, then
        // without plugin folders, with overlay URLs cut to their page, with only unusual plugin
        // options, and as a last resort cut at the end.
        public string Compose(int limit = DiscordLimit)
        {
            for (int level = 0; level <= 3; level++)
            {
                var text = ComposeAt(level);
                if (text.Length <= limit)
                    return text;
            }
            var last = ComposeAt(3);
            const string cut = "\n...(cut)\n```\n";
            // Whole lines only, so the cut is easy to see.
            var head = last.Substring(0, limit - cut.Length);
            int end = head.LastIndexOf('\n');
            return (end > 0 ? head.Substring(0, end) : head) + cut;
        }

        private string ComposeAt(int level)
        {
            var lines = new List<string> { "```" };
            lines.AddRange(header);
            foreach (var w in warnings)
                lines.Add("! " + w);
            lines.AddRange(fork);
            var options = pluginOptions.Where(o => level < 3 || !o.Default).Select(o => $"{o.Text} {OnOff(o.Checked)}").ToList();
            if (options.Count > 0)
                lines.Add("FFXIV plugin: " + string.Join(", ", options));
            if (cactbot.Count > 0)
                lines.Add("cactbot: " + string.Join(", ", cactbot));
            lines.Add("Plugins:");
            foreach (var p in plugins)
                lines.Add($" {p.Name} {p.Version}{(p.Enabled ? "" : " (off)")}{(level >= 1 ? "" : $" [{p.Folder}]")}");
            lines.Add("Overlays:");
            foreach (var o in overlays)
                lines.Add($" {o.Name} [{o.Flags}] {(level >= 2 ? ShortUrl(o.Url) : o.Url)}");
            lines.Add("```");
            return string.Join("\n", lines) + "\n";
        }

        // A URL's page and query (e.g. raidboss.html?timeline=1), or a web page's host and path.
        private static string ShortUrl(string url)
        {
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return Regex.Replace(url, "^https?://", "");
            int slash = url.LastIndexOfAny(new[] { '/', '\\' });
            return slash >= 0 ? url.Substring(slash + 1) : url;
        }

        // Copies the text; returns how long it is.
        public int CopyToClipboard()
        {
            var text = Compose();
            Clipboard.SetText(text);
            return text.Length;
        }
    }
}
