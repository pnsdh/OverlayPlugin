using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Advanced_Combat_Tracker;

namespace RainbowMage.OverlayPlugin.Tts
{
    // Fork: reads alerts aloud.  Speak only queues (it is called from overlays' and ACT's threads
    // and must never wait); one worker thread turns text into speech and starts it.  For speed:
    // the output is kept open (TtsPlayer), speech already made is kept in memory (and on disk
    // when it came over the network), the next text in line is made while the current one plays,
    // the voice is loaded at start, and a network voice that is slow is replaced by Windows' for
    // that once (its speech is kept for the next time).
    public class TtsService : IDisposable
    {
        private const int CacheBytes = 48 * 1024 * 1024;

        private readonly ILogger logger;
        private readonly TtsConfig config;
        private readonly TtsPlayer player;
        private readonly TtsDiskCache disk;
        private readonly SapiProvider windows = new SapiProvider();
        private readonly Dictionary<string, ITtsProvider> providers = new Dictionary<string, ITtsProvider>();

        private readonly object sync = new object();
        private readonly List<Request> queue = new List<Request>();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Thread worker;
        private volatile bool stopping;

        private Request playing;
        private TtsPlayback playback;
        private readonly Dictionary<string, DateTime> recent = new Dictionary<string, DateTime>();

        // Made speech by provider, settings, voice, rate and text, least recently used dropped first.
        private readonly Dictionary<string, LinkedListNode<(string Key, TtsClip Clip)>> cache = new Dictionary<string, LinkedListNode<(string, TtsClip)>>();
        private readonly LinkedList<(string Key, TtsClip Clip)> cacheOrder = new LinkedList<(string, TtsClip)>();
        private int cacheBytes;

        private TtsTextProcessor processor;
        private string processorKey;
        private TtsSettings settings;
        private readonly Dictionary<string, DateTime> loggedErrors = new Dictionary<string, DateTime>();

        // For the settings' preview: how the last speech went.
        public event Action<TtsResult> Spoken;

        public TtsService(TinyIoCContainer container, TtsConfig config)
        {
            logger = container.Resolve<ILogger>();
            this.config = config;
            player = new TtsPlayer(logger);
            disk = new TtsDiskCache(CacheFolder(), () => Math.Max(0, config.DiskCacheMB) * 1024L * 1024L);
            AddProvider(windows);
            AddProvider(new OpenAiCompatibleProvider());
            AddProvider(new AzureProvider());
            AddProvider(new GoogleProvider());
            AddProvider(new ElevenLabsProvider());
            AddProvider(new ClovaProvider());
            AddProvider(new HttpTemplateProvider());
            worker = new Thread(Run) { IsBackground = true, Name = "OverlayPlugin TTS" };
            worker.Start();
        }

        private static string CacheFolder()
        {
            string root;
            try
            {
                root = Path.Combine(ActGlobals.oFormActMain.AppDataFolder.FullName, "Config");
            }
            catch (Exception)
            {
                root = Path.GetTempPath();
            }
            return Path.Combine(root, "OverlayPluginTtsCache");
        }

        public TtsConfig Config => config;
        public TtsDiskCache DiskCache => disk;

        public IEnumerable<ITtsProvider> Providers => providers.Values;

        public void AddProvider(ITtsProvider provider) => providers[provider.Id] = provider;

        public ITtsProvider Provider => providers.TryGetValue(config.Provider ?? "", out var p) ? p : windows;

        public TtsSettings Settings
        {
            get
            {
                lock (sync)
                    return settings ?? (settings = new TtsSettings(Provider, config));
            }
        }

        // The voice to use: the chosen one, or the first for the language asked, then any.
        public TtsVoice VoiceFor(string lang) => VoiceFor(Provider, lang);

        private TtsVoice VoiceFor(ITtsProvider provider, string lang)
        {
            var voices = provider.GetVoices(provider == Provider ? Settings : new TtsSettings(provider, config));
            if (provider.IsRemote)
                return voices.FirstOrDefault();
            return voices.FirstOrDefault(v => v.Id == config.Voice)
                ?? voices.FirstOrDefault(v => v.Lang == lang)
                ?? voices.FirstOrDefault(v => v.Lang == CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)
                ?? voices.FirstOrDefault();
        }

        public string ProcessText(string text, string lang)
        {
            var voice = VoiceFor(lang);
            return Processor(voice?.Lang ?? lang).Process(text);
        }

        public Task<IReadOnlyList<TtsVoice>> FetchVoicesAsync()
        {
            var provider = Provider;
            var s = Settings;
            return Task.Run(() => provider is ITtsVoiceSource source ? source.FetchVoices(s, CancellationToken.None) : provider.GetVoices(s));
        }

        // Settings changed: rules and settings read again; speech made by the old ones is not
        // found any more (its key differs) and ages out of the cache.
        public void ConfigChanged()
        {
            lock (sync)
            {
                processor = null;
                settings = null;
            }
        }

        public void ClearCaches()
        {
            lock (sync)
            {
                cache.Clear();
                cacheOrder.Clear();
                cacheBytes = 0;
            }
            disk.Clear();
        }

        public void Speak(string text, TtsSeverity severity, string source, string lang = null)
        {
            if (string.IsNullOrWhiteSpace(text) || stopping)
                return;
            var request = new Request
            {
                Text = text,
                Severity = severity,
                Source = source,
                Lang = string.IsNullOrEmpty(lang) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : lang,
                Asked = Stopwatch.GetTimestamp(),
            };
            lock (sync)
            {
                // The same alert again, soon after: once is enough (not for the preview button).
                var now = DateTime.UtcNow;
                if (source != "preview" && recent.TryGetValue(text, out var last) && (now - last).TotalMilliseconds < config.RepeatWindowMs)
                    return;
                recent[text] = now;
                if (recent.Count > 64)
                {
                    foreach (var old in recent.Where(r => (now - r.Value).TotalMilliseconds > config.RepeatWindowMs).Select(r => r.Key).ToList())
                        recent.Remove(old);
                }

                queue.Add(request);
                if (playing != null && Cuts(request, playing))
                    playback?.Stop();
                // Only the last one is wanted: what waits is dropped.  (Otherwise what waits is
                // read after, the most important first, unless it waited too long.)
                if (config.QueueMode == TtsQueueMode.InterruptAll)
                    queue.RemoveAll(r => r != request);
            }
            // The output opens while the speech is made.
            ThreadPool.QueueUserWorkItem(_ => player.Prepare(config.OutputDevice));
            wake.Set();
        }

        private bool Cuts(Request next, Request current)
        {
            switch (config.QueueMode)
            {
                case TtsQueueMode.InterruptAll:
                    return true;
                case TtsQueueMode.InterruptLower:
                    return next.Severity > current.Severity;
                default:
                    return false;
            }
        }

        private void Run()
        {
            Warm();
            while (!stopping)
            {
                Request next;
                lock (sync)
                {
                    next = playing == null ? TakeNext() : null;
                }
                if (next == null)
                {
                    // Meanwhile, the one after is made ready.
                    MakeAhead();
                    wake.WaitOne(250);
                    continue;
                }
                try
                {
                    Start(next);
                }
                catch (Exception e)
                {
                    LogError($"could not say \"{next.Text}\": {e.Message}");
                    lock (sync)
                        playing = null;
                }
            }
        }

        // The most important, then the oldest; anything that waited too long is dropped.
        private Request TakeNext()
        {
            long now = Stopwatch.GetTimestamp();
            queue.RemoveAll(r => Ms(r.Asked, now) > config.MaxWaitMs);
            var next = queue.OrderByDescending(r => r.Severity).ThenBy(r => r.Asked).FirstOrDefault();
            if (next != null)
                queue.Remove(next);
            return next;
        }

        private void Start(Request request)
        {
            var clip = request.Clip ?? Make(request);
            if (clip == null)
            {
                Report(request, 0, 0);
                return;
            }
            long made = Stopwatch.GetTimestamp();
            lock (sync)
            {
                // Waited too long while being made: dropped all the same.
                if (Ms(request.Asked, made) > config.MaxWaitMs || stopping)
                    return;
                playing = request;
            }
            player.Prepare(config.OutputDevice);
            var started = player.Play(clip, Volume(request.Severity), () =>
            {
                lock (sync)
                {
                    if (playing == request)
                    {
                        playing = null;
                        playback = null;
                    }
                }
                wake.Set();
            });
            long now = Stopwatch.GetTimestamp();
            lock (sync)
            {
                if (started == null)
                {
                    playing = null;
                    request.Error = request.Error ?? "no audio output";
                }
                else
                {
                    playback = started;
                }
            }
            Report(request, Ms(request.Asked, made), Ms(request.Asked, now));
        }

        private void Report(Request request, double made, double started) =>
            Spoken?.Invoke(new TtsResult(request.Text, request.Spoken, made, started, request.FromCache, request.FellBack, request.Error));

        // The next in line, made while something plays, so it starts as soon as that ends.
        private void MakeAhead()
        {
            Request ahead;
            lock (sync)
            {
                ahead = queue.OrderByDescending(r => r.Severity).ThenBy(r => r.Asked).FirstOrDefault(r => r.Clip == null && !r.Tried);
            }
            if (ahead == null)
                return;
            ahead.Tried = true;
            try
            {
                ahead.Clip = Make(ahead);
            }
            catch (Exception e)
            {
                LogError($"could not say \"{ahead.Text}\": {e.Message}");
            }
        }

        private TtsClip Make(Request request)
        {
            var provider = Provider;
            var s = Settings;
            var voice = VoiceFor(provider, request.Lang);
            string lang = voice?.Lang ?? request.Lang;
            request.Spoken = Processor(lang).Process(request.Text);
            if (request.Spoken == "")
                return null;
            string key = $"{provider.Id}|{s.Fingerprint(provider)}|{voice?.Id}|{config.Rate}|{request.Spoken}";
            var kept = FromMemory(key);
            if (kept != null)
            {
                request.FromCache = true;
                return kept;
            }
            var synthesis = new TtsSynthesis { Text = request.Spoken, Voice = voice, Rate = config.Rate, Lang = lang, Settings = s };
            if (!provider.IsRemote)
                return Keep(key, provider.Synthesize(synthesis, CancellationToken.None), false);

            kept = disk.Get(key);
            if (kept != null)
            {
                request.FromCache = true;
                return Keep(key, kept, false);
            }
            // Over the network: not waited for longer than the fallback time.
            var fetch = Task.Run(() => provider.Synthesize(synthesis, CancellationToken.None));
            bool inTime;
            try
            {
                inTime = fetch.Wait(Math.Max(50, config.FallbackMs));
            }
            catch (AggregateException e)
            {
                request.Error = $"{provider.Name}: {e.GetBaseException().Message}";
                LogError(request.Error);
                return WindowsInstead(request, lang);
            }
            if (inTime && fetch.Result != null)
                return Keep(key, fetch.Result, true);
            // Late: kept for next time when it comes.
            fetch.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
                    Keep(key, t.Result, true);
                else if (t.Exception != null)
                    LogError($"{provider.Name}: {t.Exception.GetBaseException().Message}");
            });
            return WindowsInstead(request, lang);
        }

        private TtsClip WindowsInstead(Request request, string lang)
        {
            request.FellBack = true;
            var voice = VoiceFor(windows, lang);
            string key = $"{windows.Id}||{voice?.Id}|{config.Rate}|{request.Spoken}";
            return FromMemory(key) ?? Keep(key, windows.Synthesize(request.Spoken, voice, config.Rate), false);
        }

        private TtsClip FromMemory(string key)
        {
            lock (sync)
            {
                if (!cache.TryGetValue(key, out var node))
                    return null;
                cacheOrder.Remove(node);
                cacheOrder.AddFirst(node);
                return node.Value.Clip;
            }
        }

        // Kept at the output's rate, so playing it again is only mixing (and on disk if asked).
        private TtsClip Keep(string key, TtsClip clip, bool onDisk)
        {
            if (clip == null)
                return null;
            if (onDisk)
                disk.Put(key, clip);
            int rate = player.MixRate;
            if (rate > 0 && rate != clip.SampleRate)
                clip = new TtsClip(TtsPlayer.Resample(clip, rate), rate);
            lock (sync)
            {
                if (cache.TryGetValue(key, out var old))
                {
                    cacheOrder.Remove(old);
                    cacheBytes -= old.Value.Clip.Bytes;
                }
                var node = cacheOrder.AddFirst((key, clip));
                cache[key] = node;
                cacheBytes += clip.Bytes;
                while (cacheBytes > CacheBytes && cacheOrder.Last != null)
                {
                    var last = cacheOrder.Last;
                    cacheOrder.RemoveLast();
                    cache.Remove(last.Value.Key);
                    cacheBytes -= last.Value.Clip.Bytes;
                }
            }
            return clip;
        }

        // The same failure (no network, a bad key) logged once a minute, not for every alert.
        private void LogError(string message)
        {
            lock (loggedErrors)
            {
                var now = DateTime.UtcNow;
                if (loggedErrors.TryGetValue(message, out var last) && (now - last).TotalSeconds < 60)
                    return;
                loggedErrors[message] = now;
            }
            logger.Log(LogLevel.Warning, "TTS: " + message);
        }

        private TtsTextProcessor Processor(string lang)
        {
            lock (sync)
            {
                if (processor == null || processorKey != lang)
                {
                    processor = new TtsTextProcessor(config, lang);
                    processorKey = lang;
                }
                return processor;
            }
        }

        private float Volume(TtsSeverity severity)
        {
            int v;
            switch (severity)
            {
                case TtsSeverity.Alarm: v = config.AlarmVolume; break;
                case TtsSeverity.Alert: v = config.AlertVolume; break;
                case TtsSeverity.Info: v = config.InfoVolume; break;
                default: v = config.OtherVolume; break;
            }
            return Clamp(config.Volume) / 100f * Clamp(v) / 100f;
        }

        private static int Clamp(int v) => Math.Max(0, Math.Min(100, v));

        // Windows' voice loaded before the first alert needs it (it is also the fallback).
        private void Warm()
        {
            try
            {
                var voice = VoiceFor(windows, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
                windows.Synthesize(".", voice, config.Rate);
            }
            catch (Exception e)
            {
                logger.Log(LogLevel.Warning, $"TTS: could not load the voice: {e.Message}");
            }
        }

        private static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

        public void Dispose()
        {
            if (stopping)
                return;
            stopping = true;
            wake.Set();
            worker.Join(2000);
            player.Dispose();
            foreach (var p in providers.Values.OfType<IDisposable>())
                p.Dispose();
        }

        private class Request
        {
            public string Text;
            public string Spoken;
            public TtsSeverity Severity;
            public string Source;
            public string Lang;
            public long Asked;
            public TtsClip Clip;
            public bool Tried;
            public bool FromCache;
            public bool FellBack;
            public string Error;
        }
    }

    public class TtsResult
    {
        public string Text { get; }
        public string Spoken { get; }
        public double MadeMs { get; }
        public double StartedMs { get; }
        public bool FromCache { get; }
        // Windows' voice said it, as the chosen one was slow or failed.
        public bool FellBack { get; }
        public string Error { get; }

        public TtsResult(string text, string spoken, double madeMs, double startedMs, bool fromCache, bool fellBack, string error)
        {
            Text = text;
            Spoken = spoken;
            MadeMs = madeMs;
            StartedMs = startedMs;
            FromCache = fromCache;
            FellBack = fellBack;
            Error = error;
        }
    }
}
