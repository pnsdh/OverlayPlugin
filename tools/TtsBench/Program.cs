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
        // ACT itself, the copy the plugin is built against (not running: only its types).
        string act = Path.GetFullPath(Path.Combine(libs, @"..\..\..\Thirdparty\ACT"));
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            foreach (var file in new[] { Path.Combine(libs, new AssemblyName(e.Name).Name + ".dll"), Path.Combine(act, new AssemblyName(e.Name).Name + ".exe") })
                if (File.Exists(file))
                    return Assembly.LoadFrom(file);
            return null;
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
        var voices = tts.Provider.GetVoices(tts.Settings);
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
        return RemoteTest(container, volume);
    }

    // A network voice against a fake OpenAI-compatible server on this PC (no real service, no
    // key): a quick answer is used, a slow one is replaced by Windows' voice and kept for next
    // time, an error says why, and what came is found on disk by a new engine.
    static int RemoteTest(TinyIoCContainer container, int volume)
    {
        Console.WriteLine("\n== network voice (fake server on 127.0.0.1)");
        var listener = new System.Net.HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:18765/");
        listener.Start();
        int requests = 0;
        new Thread(() =>
        {
            while (listener.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = listener.GetContext(); } catch { return; }
                Interlocked.Increment(ref requests);
                string body = new StreamReader(ctx.Request.InputStream).ReadToEnd();
                var json = Newtonsoft.Json.Linq.JObject.Parse(body);
                string input = (string)json["input"];
                if (ctx.Request.Headers["Authorization"] != "Bearer good-key")
                {
                    ctx.Response.StatusCode = 401;
                    var err = System.Text.Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"Incorrect API key provided\"}}");
                    ctx.Response.OutputStream.Write(err, 0, err.Length);
                    ctx.Response.Close();
                    continue;
                }
                Thread.Sleep(input.Contains("느림") ? 1500 : 150);
                var wav = Wav(0.4);
                ctx.Response.ContentType = "audio/wav";
                ctx.Response.OutputStream.Write(wav, 0, wav.Length);
                ctx.Response.Close();
            }
        }) { IsBackground = true }.Start();

        string cacheDir = Path.Combine(Path.GetTempPath(), "OverlayPluginTtsCache");
        var config = new TtsConfig { Volume = volume, Provider = "openai", FallbackMs = 800, RepeatWindowMs = 0 };
        config.ProviderSettings["openai"] = new Dictionary<string, string>
        {
            ["url"] = "http://127.0.0.1:18765/v1",
            ["key"] = TtsSecrets.Protect("good-key"),
            ["model"] = "fake",
            ["voice"] = "bench-" + Guid.NewGuid().ToString("N").Substring(0, 6),
        };
        var tts = new TtsService(container, config);
        var results = new List<TtsResult>();
        var done = new AutoResetEvent(false);
        tts.Spoken += r => { lock (results) results.Add(r); done.Set(); };
        TtsResult Say(string text)
        {
            lock (results) results.Clear();
            tts.Speak(text, TtsSeverity.Alarm, "bench", "ko");
            done.WaitOne(5000);
            Thread.Sleep(700);
            lock (results) return results.LastOrDefault();
        }
        void Show(string what, TtsResult r) => Console.WriteLine($"  {what,-34} made {r?.MadeMs,6:0} ms  {(r?.FromCache == true ? "kept " : "")}{(r?.FellBack == true ? "WINDOWS: " + (r.Error ?? "slow") : "network")}");
        Thread.Sleep(1500);
        // A zone's alerts fetched ahead: the first time is from disk too.
        int beforePrepare = requests;
        tts.Prepare(new[] { "미리 받은 문장 하나", "미리 받은 문장 둘", "미리 받은 문장 하나" }, "ko");
        Thread.Sleep(1200);
        Console.WriteLine($"  fetched ahead: {tts.PrepareProgress.Done}/{tts.PrepareProgress.Total} ({requests - beforePrepare} requests)");
        Show("an alert fetched ahead", Say("미리 받은 문장 둘"));
        Show("quick answer (150 ms)", Say("빠른 응답"));
        Show("slow answer (1500 ms > 800)", Say("느림 응답"));
        Thread.Sleep(1200);
        Show("slow one again (came meanwhile)", Say("느림 응답"));
        Show("quick one again", Say("빠른 응답"));
        tts.Dispose();

        var tts2 = new TtsService(container, config);
        tts2.Spoken += r => { lock (results) results.Add(r); done.Set(); };
        tts = tts2;
        Thread.Sleep(1500);
        int before = requests;
        Show("after restart (from disk)", Say("빠른 응답"));
        Console.WriteLine($"  requests to the server for it: {requests - before}");
        config.ProviderSettings["openai"]["key"] = TtsSecrets.Protect("bad-key");
        tts2.ConfigChanged();
        Show("bad key", Say("새 문장"));
        tts2.Dispose();
        listener.Stop();
        Console.WriteLine($"  disk cache: {cacheDir}");
        return 0;
    }

    // A WAV of a short tone at 24 kHz.
    static byte[] Wav(double seconds)
    {
        int rate = 24000, n = (int)(rate * seconds);
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
        for (int i = 0; i < n; i++)
            w.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / rate) * 8000));
        return ms.ToArray();
    }
}
