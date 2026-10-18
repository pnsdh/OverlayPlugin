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
// window: the panels are built with stand-in objects (default settings) in the given language and
// drawn to PNG files, a tab page per file.  For checking layout and translations after a change.
//
// UiShot.exe <out folder> [ko-KR|en|...] [width] [height]
// Needs the plugin built (out\Release\libs).
static class Program
{
    static string libs;

    [STAThread]
    static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "out";
        bool sample = outDir == "techsupport";
        string culture = args.Length > 1 && !sample ? args[1] : "ko-KR";
        int width = args.Length > 2 ? int.Parse(args[2]) : 820;
        int height = args.Length > 3 ? int.Parse(args[3]) : 560;
        libs = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\out\Release\libs"));
        // CefSharp comes from the CEF that OverlayPlugin installed (only read from there).
        string cef = Environment.GetEnvironmentVariable("OP_CEF") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Advanced Combat Tracker", "OverlayPluginCef-cbkr", "x64");
        Environment.SetEnvironmentVariable("PATH", cef + ";" + Environment.GetEnvironmentVariable("PATH"));
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            foreach (string dir in new[] { libs, cef })
            {
                string file = Path.Combine(dir, new AssemblyName(e.Name).Name + ".dll");
                if (File.Exists(file))
                    return Assembly.LoadFrom(file);
            }
            return null;
        };
        Thread.CurrentThread.CurrentUICulture = new CultureInfo(culture);
        Thread.CurrentThread.CurrentCulture = new CultureInfo(culture);
        Application.EnableVisualStyles();
        if (sample)
            return TechSupportSample(args.Length > 1 ? int.Parse(args[1]) : 5);
        Directory.CreateDirectory(outDir);
        // As on a PC with the WebView2 runtime (UISHOT_NO_WEBVIEW2=1: as without it).
        if (Environment.GetEnvironmentVariable("UISHOT_NO_WEBVIEW2") == null)
            SimulateWebView2();
        return Run(outDir, culture, width, height);
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
                Console.WriteLine($"{name}: {e.GetBaseException().GetType().Name}: {e.GetBaseException().Message}");
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
        return failed;
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
            var tabs = All(control).OfType<TabControl>().FirstOrDefault();
            if (tabs == null)
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
        foreach (var name in new[] { "warnings", "plugins", "overlays", "header", "fork", "pluginOptions", "cactbot" })
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
        for (int i = 0; i < extra; i++)
            Add("overlays", ValueTuple.Create($"Extra overlay {i + 1}", "CEF 60fps", $"<ACT>/Plugins/some-long-folder-name/ui/overlay-number-{i + 1}/index.html?option=value&other=thing"));
        var text = (string)type.GetMethod("Compose", new[] { typeof(int) }).Invoke(info, new object[] { 2000 });
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine(text);
        Console.WriteLine($"[{text.Length} characters]");
        return 0;
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
