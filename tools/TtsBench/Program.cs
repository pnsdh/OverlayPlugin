using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using RainbowMage.OverlayPlugin;
using RainbowMage.OverlayPlugin.Tts;

// The plugin's TTS without ACT: how long alerts take from asked for to playing (cold, then
// cached), what the text rules make of cactbot-like alerts, and the queue rules (a more
// important alert cutting in, repeats dropped).  Silent unless --volume is given.
//
// TtsBench.exe [--volume 0-100] [--voice <name>] [--text "..."]
// Needs the plugin built (out\Release\libs).
static class Program
{
    static int Main(string[] args)
    {
        string libs = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\out\Release\libs"));
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string file = Path.Combine(libs, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(file) ? Assembly.LoadFrom(file) : null;
        };
        return Run(args);
    }

    static int Run(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        int volume = int.TryParse(Opt("--volume"), out var v) ? v : 0;

        var container = new TinyIoCContainer();
        var logger = new Logger();
        logger.RegisterListener(e => Console.WriteLine($"  [{e.Level}] {e.Message}"));
        container.Register<ILogger>(logger);
        var config = new TtsConfig { Volume = volume };
        var tts = new TtsService(container, config);
        var voices = tts.Provider.GetVoices();
        Console.WriteLine("voices: " + string.Join(", ", voices.Select(x => x.Name)));
        if (Opt("--voice") is string voice)
            config.Voice = voices.FirstOrDefault(x => x.Name.Contains(voice))?.Id ?? "";

        var results = new List<TtsResult>();
        var done = new AutoResetEvent(false);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        tts.Spoken += r =>
        {
            if (args.Contains("--trace"))
                Console.WriteLine($"  t={clock.ElapsedMilliseconds}ms playing: {r.Spoken}");
            lock (results)
                results.Add(r);
            done.Set();
        };
        void Say(string text, TtsSeverity severity = TtsSeverity.Alert)
        {
            tts.Speak(text, severity, "bench", "ko");
        }

        Console.WriteLine("\n== text rules");
        foreach (var t in new[] { "보스와 멀리 떨어지기 => 행동 멈추기", "돌진 + 넉백", "장판 바깥에 깔기 (3번)", "탱버 x2", "↖ 앞 왼쪽 / 뒤 오른쪽 ↘", "${rotation} 45° 이동", "분신：북->동->남", "힘내라!~", Opt("--text") }.Where(t => t != null))
            Console.WriteLine($"  {t}\n    -> {tts.ProcessText(t, "ko")}");

        // Let the voice load (done at start, as in ACT).
        Thread.Sleep(1500);
        Console.WriteLine("\n== latency (ms from asked to made / to playing)");
        foreach (var round in new[] { "first", "cached" })
        {
            foreach (var t in new[] { "산개", "보스 뒤로", "탱버 두 번", "쉐어 모이기 다음 산개" })
            {
                Say(t);
                done.WaitOne(3000);
                Thread.Sleep(900);
            }
        }
        lock (results)
        {
            foreach (var r in results)
                Console.WriteLine($"  {(r.FromCache ? "cached" : "made  ")} made {r.MadeMs,6:0.0}  playing {r.StartedMs,6:0.0}  {r.Spoken}");
            results.Clear();
        }

        Console.WriteLine("\n== queue: info, then alarm 50ms later (cuts in), the same alarm again (dropped as a repeat); info is not read again (it was cut)");
        Thread.Sleep(2500);
        Console.WriteLine($"  t={clock.ElapsedMilliseconds}ms asking");
        Say("정보 알림 아주 길게 읽는 문장입니다 하나 둘 셋 넷 다섯", TtsSeverity.Info);
        Thread.Sleep(50);
        Say("경고 알림", TtsSeverity.Alarm);
        Thread.Sleep(50);
        Say("경고 알림", TtsSeverity.Alarm);
        Thread.Sleep(4000);
        lock (results)
            foreach (var r in results)
                Console.WriteLine($"  played: {r.Spoken} (playing {r.StartedMs:0.0}ms after asked)");

        tts.Dispose();
        return 0;
    }
}
