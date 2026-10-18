using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;
using RainbowMage.OverlayPlugin;
using RainbowMage.OverlayPlugin.Controls;
using RainbowMage.OverlayPlugin.Overlays;

// Pictures of OverlayPlugin's settings panels, as ACT shows them, without ACT and without showing a
// window (dialogs are shown off screen for a moment): the panels are built with stand-in objects
// (default settings) in the given language and drawn to PNG files, a tab page per file.  For
// checking layout and translations after a change.
//
// UiShot.exe <out folder> [ko-KR|en|...] [width] [height]   (UISHOT_TRACE=1: full errors)
// UiShot.exe techsupport [extra overlays]                   the tech support text from sample data
// UiShot.exe censor                                         what it hides, on sample paths
// Needs the plugin built (out\Release\libs).
static class Program
{
    static string libs;

    [STAThread]
    static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "out";
        bool sample = outDir == "techsupport";
        string culture = args.Length > 1 && !sample && outDir != "config" ? args[1] : "ko-KR";
        int width = args.Length > 2 ? int.Parse(args[2]) : 820;
        int height = args.Length > 3 ? int.Parse(args[3]) : 560;
        libs = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\out\Release\libs"));
        // CefSharp comes from the CEF that OverlayPlugin installed (only read from there).
        string cef = Environment.GetEnvironmentVariable("OP_CEF") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Advanced Combat Tracker", "OverlayPluginCef-cbkr", "x64");
        Environment.SetEnvironmentVariable("PATH", cef + ";" + Environment.GetEnvironmentVariable("PATH"));
        // ACT itself, the copy the plugin is built against.
        string act = Path.GetFullPath(Path.Combine(libs, @"..\..\..\Thirdparty\ACT"));
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            // A short name (e.g. "System.Drawing" in a Korean designer resource) means the one
            // already loaded, as the plugin loader in ACT does.
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == new AssemblyName(e.Name).Name);
            if (loaded != null)
                return loaded;
            foreach (string dir in new[] { libs, cef, act })
                foreach (string ext in new[] { ".dll", ".exe" })
                {
                    string file = Path.Combine(dir, new AssemblyName(e.Name).Name + ext);
                    if (File.Exists(file))
                        return Assembly.LoadFrom(file);
                }
            return null;
        };
        Thread.CurrentThread.CurrentUICulture = new CultureInfo(culture);
        Thread.CurrentThread.CurrentCulture = new CultureInfo(culture);
        Application.EnableVisualStyles();
        // Designer resources that need a type looked up (e.g. TableLayoutSettings) are read only with this on.
        AppContext.SetSwitch("System.Resources.ResourceManager.AllowCustomResourceTypes", true);
        if (outDir == "censor")
            return CensorCheck();
        if (outDir == "config")
            return ConfigCheck(args[1]);
        if (sample)
            return TechSupportSample(args.Length > 1 ? int.Parse(args[1]) : 5);
        Directory.CreateDirectory(outDir);
        // As on a PC with the WebView2 runtime (UISHOT_NO_WEBVIEW2=1: as without it).
        if (Environment.GetEnvironmentVariable("UISHOT_NO_WEBVIEW2") == null)
            SimulateWebView2();
        return Run(outDir, culture, width, height);
    }

    // A config as ACT loads it (overlays included), saved and loaded again: what came of the
    // renderer settings and the server's address (e.g. the one-time move to WebView2).
    // UiShot.exe config <a copy of RainbowMage.OverlayPlugin.config.json>  (the copy is rewritten)
    static int ConfigCheck(string path)
    {
        var container = new TinyIoCContainer();
        container.Register<ILogger>(new Logger());
        var main = Stub<PluginMain>();
        SetProperty(main, "PluginDirectory", libs);
        container.Register(main);
        string Describe(PluginConfig c) =>
            $"WebView2Moved={c.WebView2Moved} panels={c.PanelsUseWebView2} gpu={c.UseGpu} cutscene={c.HideOverlayDuringCutscene} ws={c.WSServerIP}:{c.WSServerPort} overlays=[" +
            string.Join(", ", c.Overlays.Select(o => $"{o.Name}:{((o as OverlayConfigBase)?.UseWebView2 == true ? "wv2" : "cef")}/{o.MaxFrameRate}fps")) + "]";
        var first = new PluginConfig(path, container);
        Console.WriteLine("loaded:  " + Describe(first));
        // One switched back to CEF after the move: kept.
        if (first.Overlays.FirstOrDefault() is OverlayConfigBase back)
            back.UseWebView2 = false;
        first.SaveJson(true);
        var second = new PluginConfig(path, container);
        Console.WriteLine("again:   " + Describe(second));
        return 0;
    }

    static int Run(string outDir, string culture, int width, int height)
    {
        var container = new TinyIoCContainer();
        container.Register<ILogger>(new Logger());
        var main = Stub<PluginMain>();
        SetProperty(main, "PluginDirectory", libs);
        container.Register(main);
        var config = new PluginConfig(Path.Combine(Path.GetTempPath(), "uishot-nonexistent.json"), container);
        container.Register(config);
        container.Register<IPluginConfig>(config);
        container.Register(Stub<KeyboardHook>());
        // ACT's main window, only for the folder it keeps its settings in (looked up by some panels).
        var act = Stub<Advanced_Combat_Tracker.FormActMain>();
        SetMember(act, "AppDataFolder", new DirectoryInfo(Path.Combine(Path.GetTempPath(), "uishot-act")));
        Advanced_Combat_Tracker.ActGlobals.oFormActMain = act;
        var registry = new Registry(container);
        container.Register(registry);
        registry.RegisterOverlay<MiniParseOverlay>();
        registry.RegisterOverlay<SpellTimerOverlay>();
        registry.RegisterOverlay<LabelOverlay>();
        registry.RegisterOverlay<LogParseOverlay>();
        var presetType = typeof(PluginConfig).Assembly.GetType("RainbowMage.OverlayPlugin.OverlayPreset");
        var presetFile = Path.Combine(libs, @"..\..\..\OverlayPlugin.Core\resources\presets.json");
        var presets = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(presetFile));
        foreach (var pair in presets)
        {
            var preset = (IOverlayPreset)pair.Value.ToObject(presetType);
            SetMember(preset, "Name", pair.Key);
            registry.RegisterOverlayPreset2(preset);
        }
        container.Register(new WSServer(container));
        container.Register(new RainbowMage.OverlayPlugin.EventSources.BuiltinEventConfig());

        int failed = 0;
        void Shoot(string name, Func<Control> make)
        {
            try
            {
                var control = make();
                Draw(control, outDir, $"{name}.{culture}", width, height);
            }
            catch (Exception e)
            {
                failed++;
                Console.WriteLine($"{name}: {e.GetBaseException().GetType().Name}: {e.GetBaseException().Message}"); if (Environment.GetEnvironmentVariable("UISHOT_TRACE") != null) Console.WriteLine(e);
            }
        }

        Shoot("GeneralConfigTab", () => new GeneralConfigTab(container));
        Shoot("MiniParseConfigPanel", () =>
        {
            var overlay = Stub<MiniParseOverlay>();
            var overlayConfig = new MiniParseOverlayConfig("Raidboss : Timeline") { Url = "file:///C:/.../raidboss.html?timeline=1" };
            SetProperty(overlay, "Config", overlayConfig);
            return new MiniParseConfigPanel(container, overlay);
        });
        Shoot("TtsConfigPanel", () =>
        {
            if (!container.CanResolve<RainbowMage.OverlayPlugin.Tts.TtsService>())
                container.Register(new RainbowMage.OverlayPlugin.Tts.TtsService(container, config.Tts));
            return new RainbowMage.OverlayPlugin.Tts.TtsConfigPanel(container);
        });
        Shoot("TtsConfigPanel-network", () =>
        {
            // As with a service chosen (its fields shown instead of Windows' voices).
            config.Tts.Provider = "azure";
            container.Resolve<RainbowMage.OverlayPlugin.Tts.TtsService>().ConfigChanged();
            var panel = new RainbowMage.OverlayPlugin.Tts.TtsConfigPanel(container);
            config.Tts.Provider = "sapi";
            return panel;
        });
        Shoot("TtsConfigPanel-supertonic", () =>
        {
            // A model downloaded on request: download panel, then its settings.
            config.Tts.Provider = "supertonic";
            container.Resolve<RainbowMage.OverlayPlugin.Tts.TtsService>().ConfigChanged();
            var panel = new RainbowMage.OverlayPlugin.Tts.TtsConfigPanel(container);
            config.Tts.Provider = "sapi";
            return panel;
        });
        Shoot("WSConfigPanel", () => new WSConfigPanel(container));
        Shoot("BuiltinEventConfigPanel", () =>
        {
            // Internal to the plugin.
            var type = typeof(PluginConfig).Assembly.GetType("RainbowMage.OverlayPlugin.EventSources.BuiltinEventConfigPanel");
            var panel = (Control)Activator.CreateInstance(type, container);
            // Its values are filled in once the event sources have started.
            var started = (EventHandler)typeof(Registry).GetField("EventSourcesStarted", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(registry);
            started?.Invoke(registry, EventArgs.Empty);
            return panel;
        });
        Shoot("ControlPanel", () => new ControlPanel(container));
        try { ShootDialog(outDir, $"NewOverlayDialog.{culture}", new NewOverlayDialog(container)); }
        catch (Exception e) { failed++; Console.WriteLine($"NewOverlayDialog: {e.GetBaseException().Message}"); }
        try { ShootDialog(outDir, $"RenameOverlayDialog.{culture}", new RenameOverlayDialog("Raidboss : Timeline")); }
        catch (Exception e) { failed++; Console.WriteLine($"RenameOverlayDialog: {e.GetBaseException().Message}"); }
        try { ShootDialog(outDir, $"ConfigErrorPrompt.{culture}", new ConfigErrorPrompt()); }
        catch (Exception e) { failed++; Console.WriteLine($"ConfigErrorPrompt: {e.GetBaseException().Message}"); }
        Shoot("SpellTimerConfigPanel", () =>
        {
            var overlay = Stub<SpellTimerOverlay>();
            SetProperty(overlay, "Config", new SpellTimerOverlayConfig("Spell Timer"));
            return new SpellTimerConfigPanel(overlay);
        });
        Shoot("LabelOverlayConfigPanel", () =>
        {
            var overlay = Stub<LabelOverlay>();
            SetProperty(overlay, "Config", new LabelOverlayConfig(container, "Label"));
            return new LabelOverlayConfigPanel(container, overlay);
        });
        Shoot("LogParseConfigPanel", () =>
        {
            var overlay = Stub<LogParseOverlay>();
            SetProperty(overlay, "Config", new LogParseOverlayConfig("Log Parse"));
            return new LogParseConfigPanel(overlay);
        });
        return failed;
    }

    // A dialog as it opens, at its own size: shown off screen (not in the taskbar) for a moment.
    static void ShootDialog(string outDir, string name, Form dialog)
    {
        dialog.ShowInTaskbar = false;
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new Point(-32000, -32000);
        dialog.Show();
        Application.DoEvents();
        Save(dialog, Path.Combine(outDir, name + ".png"));
        dialog.Close();
    }

    // The control in a hidden form at the given size, each tab of its tab controls drawn on its own.
    static void Draw(Control control, string outDir, string name, int width, int height)
    {
        using (var form = new Form { ClientSize = new Size(width, height), ShowInTaskbar = false })
        {
            control.Dock = DockStyle.Fill;
            form.Controls.Add(control);
            form.CreateControl();
            CreateHandles(control);
            form.PerformLayout();
            // What each drop-down holds (drawn ones can look empty in the picture).
            if (Environment.GetEnvironmentVariable("UISHOT_COMBOS") == "1")
                foreach (var box in All(control).OfType<ComboBox>())
                {
                    Console.WriteLine($"  {name} combo: [{box.SelectedIndex}] \"{box.Text}\" ({box.Items.Count} items)");
                    // A list drawn by the panel itself (e.g. with lines between groups): as opened.
                    if (box.DrawMode == DrawMode.OwnerDrawFixed && box.Items.Count > 0)
                        SaveList(box, Path.Combine(outDir, $"{name}.list{box.Items.Count}.png"));
                }
            var tabs = All(control).OfType<TabControl>().FirstOrDefault();
            if (tabs == null || tabs.TabCount == 0)
            {
                Save(control, Path.Combine(outDir, name + ".png"));
                return;
            }
            for (int i = 0; i < tabs.TabCount; i++)
            {
                tabs.SelectedIndex = i;
                CreateHandles(tabs.TabPages[i]);
                tabs.TabPages[i].PerformLayout();
                Save(control, Path.Combine(outDir, $"{name}.{i}-{Safe(tabs.TabPages[i].Text)}.png"));
            }
        }
    }

    // An owner-drawn drop-down's items, each drawn as the open list draws it.
    static void SaveList(ComboBox box, string file)
    {
        int h = box.ItemHeight;
        using (var bmp = new Bitmap(box.Width, h * box.Items.Count))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(SystemColors.Window);
            var draw = typeof(ComboBox).GetMethod("OnDrawItem", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            for (int i = 0; i < box.Items.Count; i++)
                draw.Invoke(box, new object[] { new DrawItemEventArgs(g, box.Font, new Rectangle(0, i * h, box.Width, h), i, DrawItemState.None, SystemColors.WindowText, SystemColors.Window) });
            bmp.Save(file, ImageFormat.Png);
        }
        Console.WriteLine($"wrote {file}");
    }

    static void Save(Control control, string file)
    {
        using (var bmp = new Bitmap(control.Width, control.Height))
        {
            control.DrawToBitmap(bmp, new Rectangle(Point.Empty, control.Size));
            bmp.Save(file, ImageFormat.Png);
        }
        Console.WriteLine($"wrote {file}");
    }

    static void CreateHandles(Control c)
    {
        var _ = c.Handle;
        foreach (Control child in c.Controls)
            CreateHandles(child);
    }

    static System.Collections.Generic.IEnumerable<Control> All(Control c)
    {
        foreach (Control child in c.Controls)
        {
            yield return child;
            foreach (var d in All(child))
                yield return d;
        }
    }

    static string Safe(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    // The tech support text from sample data like a typical setup (the real one needs ACT), with
    // `extra` more overlays than usual, to see its length and how it shortens itself.
    static int TechSupportSample(int extra)
    {
        var type = typeof(PluginConfig).Assembly.GetType("RainbowMage.OverlayPlugin.ClipboardTechSupport");
        var info = FormatterServices.GetUninitializedObject(type);
        // Its lists are made by field initializers, which an uninitialized object skips.
        object Field(string name)
        {
            var f = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (f.GetValue(info) == null)
                f.SetValue(info, Activator.CreateInstance(f.FieldType));
            return f.GetValue(info);
        }
        foreach (var name in new[] { "warnings", "plugins", "overlays", "header", "fork", "pluginOptions", "cactbot", "problems" })
            Field(name);
        void Add(string name, object item) => Field(name).GetType().GetMethod("Add").Invoke(Field(name), new[] { item });
        Add("header", "OverlayPlugin 0.19.110.4 (custom) | ACT 3.8.3.290 | FFXIV_ACT_Plugin 3.0.3.3");
        Add("header", "Game 2026.10.06.0000.0000 Korean, client Korean, parse Korean, borderless | ACT admin, FFXIV admin");
        Add("fork", "GPU on, panels WebView2 | CEF 152.0.100.0 (Chromium 152.0.7700.0) | WebView2 148.0.3456.12 | update check on");
        Add("fork", "Memory Combatant 7.5, Target 7.0, Aggro 6.0, Enmity 6.0, EnmityHud 7.3, InCombat 7.3, JobGauge 7.4, ContentFinder 7.1, Framework 7.0, Party 7.0, AtkStage 6.2");
        Add("fork", "Opcodes Korean 2026.10.06.0000.0000: file (12)");
        Add("fork", "Dev switches on: op-probe.enable, op-selfcheck.enable");
        foreach (var (n, c, d) in new[] { ("Deucalion", true, true), ("HideChatLog", false, true), ("WinPCap", false, true), ("NoSocketFilter", false, true), ("NoCombinePets", true, false), ("NoDamageShield", false, true), ("Debug", false, true) })
            Add("pluginOptions", ValueTuple.Create(n, c, d));
        Add("cactbot", @"user dir <ACT>\cbkr-user");
        Add("cactbot", "skin cbkr");
        foreach (var (n, v, f) in new[] { ("FFXIV_ACT_Plugin.dll", "3.0.3.3", @"<ACT>\Plugins\FFXIV_ACT_Plugin_KR"), ("OverlayPlugin.dll", "0.19.110.4", @"<ACT>\Plugins\OverlayPlugin"), ("CactbotOverlay.dll", "0.37.5.8", @"<ACT>\Plugins\cbkr"), ("ACT_Notifications.dll", "1.2.0.0", @"<ACT>\Plugins") })
            Add("plugins", ValueTuple.Create(n, v, true, f));
        var urls = new[]
        {
            ("MopiMopi", "WebView2", "https://haeruhaeru.github.io/mopimopi/?HOST_PORT=ws://127.0.0.1/fake/"),
            ("Titan Jail", "off WebView2", "https://chalkpe.github.io/ffxiv-titan-jail/"),
            ("Oopsyraidsy", "WebView2", "<ACT>/Plugins/cbkr/ui/oopsyraidsy/oopsyraidsy.html"),
            ("Raidboss : Alerts", "WebView2", "<ACT>/Plugins/cbkr/ui/raidboss/raidboss.html?timeline=0&alerts=1&devProbe=latency"),
            ("Raidboss : Timeline", "WebView2", "<ACT>/Plugins/cbkr/ui/raidboss/raidboss.html?timeline=1&alerts=0&devProbe=latency"),
        };
        foreach (var u in urls)
            Add("overlays", ValueTuple.Create(u.Item1, u.Item2, u.Item3));
        Add("problems", ValueTuple.Create("21:03:12", "W", "Raidboss : Alerts: Uncaught TypeError: Cannot read properties of undefined (reading 'name') at <ACT>/Plugins/cbkr/ui/raidboss/raidboss.bundle.js:1:123456", 3));
        Add("problems", ValueTuple.Create("21:05:40", "E", "WSServer: Failed to start: Address already in use", 1));
        for (int i = 0; i < extra; i++)
            Add("overlays", ValueTuple.Create($"Extra overlay {i + 1}", "CEF 60fps", $"<ACT>/Plugins/some-long-folder-name/ui/overlay-number-{i + 1}/index.html?option=value&other=thing"));
        var text = (string)type.GetMethod("Compose", new[] { typeof(int) }).Invoke(info, new object[] { 2000 });
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine(text);
        Console.WriteLine($"[{text.Length} characters]");
        return 0;
    }

    // What the tech support text hides, checked on paths written the ways overlays and logs write
    // them, for an ACT folder under a Korean user name (as a browser reports it, percent-encoded)
    // and under this PC's user name.  Prints each case; returns how many still show the name.
    static int CensorCheck()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var type = typeof(PluginConfig).Assembly.GetType("RainbowMage.OverlayPlugin.ClipboardTechSupport");
        var setup = type.GetMethod("SetupCensor", BindingFlags.Instance | BindingFlags.NonPublic);
        var censor = type.GetMethod("Censor", BindingFlags.Instance | BindingFlags.NonPublic);
        int failed = 0;
        void Check(string actFolder, string secret, params string[] inputs)
        {
            var info = FormatterServices.GetUninitializedObject(type);
            type.GetField("ACTPathReplace", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(info, "<ACT>");
            setup.Invoke(info, new object[] { actFolder });
            foreach (var input in inputs)
            {
                var output = (string)censor.Invoke(info, new object[] { input });
                bool leaks = output.IndexOf(secret, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    output.IndexOf(Uri.EscapeDataString(secret), StringComparison.OrdinalIgnoreCase) >= 0;
                if (leaks)
                    failed++;
                Console.WriteLine($"{(leaks ? "LEAK" : "ok  ")} {output}");
            }
        }
        string korean = "홍길동";
        string act = $@"C:\Users\{korean}\AppData\Roaming\Advanced Combat Tracker";
        string enc = Uri.EscapeDataString(korean);
        Check(act, korean,
            $@"{act}\Plugins\cbkr\ui\raidboss\raidboss.html",
            $"file:///C:/Users/{korean}/AppData/Roaming/Advanced Combat Tracker/Plugins/cbkr/ui/raidboss/raidboss.html?timeline=1",
            $"file:///C:/Users/{korean}/AppData/Roaming/Advanced%20Combat%20Tracker/Plugins/x.html",
            $"BrowserError: ERR_FILE_NOT_FOUND, file:///C:/Users/{enc}/AppData/Roaming/Advanced%20Combat%20Tracker/Plugins/x.html",
            $@"c:\users\{korean}\appdata\roaming\advanced combat tracker\cbkr-user");
        string me = Environment.UserName;
        Check($@"D:\Games\ACT", me,
            $@"C:\Users\{me}\Documents\cactbot-user",
            $@"c:\users\{me.ToLowerInvariant()}\Documents\cactbot-user",
            $"file:///C:/Users/{Uri.EscapeDataString(me)}/Desktop/overlay.html",
            @"D:\Games\ACT\Plugins\cbkr\ui\raidboss\raidboss.html");
        Console.WriteLine(failed == 0 ? "nothing shows" : $"{failed} still show it");
        return failed;
    }

    static void SimulateWebView2()
    {
        var renderer = typeof(RainbowMage.HtmlRenderer.Renderer);
        SetStatic(renderer, "WebView2Available", true);
        SetStatic(renderer, "WebView2Status", "148.0.0.0 (UiShot)");
    }

    static void SetStatic(Type type, string name, object value)
    {
        var p = type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        p.GetSetMethod(true).Invoke(null, new[] { value });
    }

    static T Stub<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));

    static void SetMember(object target, string name, object value)
    {
        for (var t = target.GetType(); t != null; t = t.BaseType)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var f = t.GetField(name, flags) ?? t.GetField($"<{name}>k__BackingField", flags);
            if (f != null)
            {
                f.SetValue(target, value);
                return;
            }
            var p = t.GetProperty(name, flags);
            if (p != null && p.GetSetMethod(true) != null)
            {
                p.SetValue(target, value);
                return;
            }
        }
        throw new MissingMemberException(target.GetType().Name, name);
    }

    static void SetProperty(object target, string name, object value)
    {
        for (var t = target.GetType(); t != null; t = t.BaseType)
        {
            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (p != null && p.GetSetMethod(true) != null)
            {
                p.SetValue(target, value);
                return;
            }
            var f = t.GetField($"<{name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            if (f != null)
            {
                f.SetValue(target, value);
                return;
            }
        }
        throw new MissingMemberException(target.GetType().Name, name);
    }
}
