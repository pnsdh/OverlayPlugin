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
        private readonly Supertonic.SupertonicProvider supertonic = new Supertonic.SupertonicProvider();
        private readonly Dictionary<string, ITtsProvider> providers = new Dictionary<string, ITtsProvider>();
        private readonly List<ITtsProvider> order = new List<ITtsProvider>();

        private readonly object sync = new object();
        private readonly List<Request> queue = new List<Request>();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Thread worker;
        private volatile bool stopping;

        // Taken from the queue and being made (a more important alert can cut it before it plays).
        private Request making;
        private Request playing;
        private TtsPlayback playback;
        // When the playing one started and how long it is: if its end is never heard (an output
        // gone without saying), it is let go after that, so speech never stops for good.
        private long playingSince;
        private double playingSeconds;
        private bool warmed;
        // Speech being fetched by a slow voice, by key: asked twice (made ahead, then needed), it
        // is fetched once.
        private readonly Dictionary<string, Task<TtsClip>> fetching = new Dictionary<string, Task<TtsClip>>();
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

        // Alerts fetched ahead (see Prepare): done of total, for the settings.
        public event Action<int, int> Prepared;
        public (int Done, int Total) PrepareProgress { get; private set; }
        private CancellationTokenSource preparing;
        private const int PrepareMax = 500;

        public TtsService(TinyIoCContainer container, TtsConfig config)
        {
            logger = container.Resolve<ILogger>();
            this.config = config;
            player = new TtsPlayer(logger);
            Usage = new TtsUsageCounter(config);
            disk = new TtsDiskCache(CacheFolder(), () => Math.Max(0, config.DiskCacheMB) * 1024L * 1024L);
            Supertonic.SupertonicModel.CleanUp();
            AddProvider(windows);
            AddProvider(supertonic);
            AddProvider(new AzureProvider());
            AddProvider(new GoogleProvider());
            AddProvider(new GeminiProvider());
            AddProvider(new ElevenLabsProvider());
            AddProvider(new OpenAiCompatibleProvider());
            AddProvider(new ClovaProvider());
            AddProvider(new HttpTemplateProvider());
            Usage.Prepare(providers.Values);
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

        // The zone now (for what made speech is kept with, see TtsDiskCache).
        public string CurrentZone { get; private set; }

        // Zone changes from the FFXIV plugin, once it is there (the plugin's second start phase).
        public void WatchZone(FFXIVRepository repository)
        {
            repository.RegisterZoneChangeDelegate((id, name) =>
                CurrentZone = string.IsNullOrEmpty(name) ? (id == 0 ? null : $"#{id}") : name);
        }

        private TtsCacheInfo Info(ITtsProvider provider, TtsVoice voice, string text) => new TtsCacheInfo
        {
            Provider = provider.Id,
            ProviderName = provider.Name,
            Voice = voice?.Id,
            Zone = CurrentZone,
            Text = text,
            Paid = TtsUsageCounter.Billed(provider),
        };

        // Made speech forgotten in memory (after files were removed from disk).
        public void ClearMemory()
        {
            lock (sync)
            {
                cache.Clear();
                cacheOrder.Clear();
                cacheBytes = 0;
            }
        }
        public TtsUsageCounter Usage { get; }
        public TtsDiskCache DiskCache => disk;

        // Free ones first, then services (as added).
        public IEnumerable<ITtsProvider> Providers => order;

        public void AddProvider(ITtsProvider provider)
        {
            if (providers.ContainsKey(provider.Id))
                order.Remove(providers[provider.Id]);
            providers[provider.Id] = provider;
            order.Add(provider);
        }

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
            if (provider.IsSlow)
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
            if (stopping)
                return;
            lock (sync)
            {
                processor = null;
                settings = null;
            }
            // A zone being fetched ahead with the old settings (another voice, key...) is not
            // wanted any more.
            preparing?.Cancel();
            LoadOrUnload();
        }

        // A local model chosen is loaded ahead (the first alert would wait seconds for it), and
        // unloaded when another voice is chosen (its memory back).
        public void LoadOrUnload()
        {
            if (stopping)
            {
                supertonic.Unload();
                return;
            }
            if (config.Enabled && Provider == supertonic)
                supertonic.Warm(Settings);
            else
                supertonic.Unload();
        }

        // Fetching ahead stopped (its option turned off).
        public void CancelPrepare() => preparing?.Cancel();

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

                // Waited too long: dropped here too, so the queue never grows while one plays.
                long nowTicks = Stopwatch.GetTimestamp();
                queue.RemoveAll(r => !r.Preview && Ms(r.Asked, nowTicks) > config.MaxWaitMs);
                queue.Add(request);
                // Cut: what is being made is not played, what plays (or is starting) stops.
                if (making != null && Cuts(request, making))
                    making.Cut = true;
                if (playing != null && Cuts(request, playing))
                {
                    playing.Cut = true;
                    playback?.Stop();
                }
                // Only the last one is wanted: what waits is dropped.  (Otherwise what waits is
                // read after, the most important first, unless it waited too long.)
                if (config.QueueMode == TtsQueueMode.InterruptAll)
                    queue.RemoveAll(r => r != request);
            }
            // Trying Supertonic before turning TTS on: loaded for it (until a setting changes).
            if (request.Preview && Provider == supertonic && !config.Enabled)
                supertonic.Warm(Settings);
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
            try
            {
                LoadOrUnload();
            }
            catch (Exception e)
            {
                LogError($"could not load the voice: {e.Message}");
            }
            while (!stopping)
            {
                // Windows' voice (also the fallback) loaded once TTS is on, before an alert needs it.
                if (!warmed && config.Enabled)
                {
                    warmed = true;
                    Warm();
                }
                Request next;
                lock (sync)
                {
                    if (playing != null && Ms(playingSince, Stopwatch.GetTimestamp()) > playingSeconds * 1000 + 2000)
                    {
                        playing = null;
                        playback = null;
                    }
                    next = playing == null ? TakeNext() : null;
                    making = next;
                }
                if (next == null)
                {
                    // Meanwhile, the one after is made ready.
                    try
                    {
                        MakeAhead();
                    }
                    catch (Exception e)
                    {
                        LogError(e.Message);
                    }
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
                    {
                        playing = null;
                        playback = null;
                    }
                }
                finally
                {
                    lock (sync)
                        making = null;
                }
            }
        }

        // The most important, then the oldest; anything that waited too long is dropped.
        private Request TakeNext()
        {
            long now = Stopwatch.GetTimestamp();
            queue.RemoveAll(r => !r.Preview && Ms(r.Asked, now) > config.MaxWaitMs);
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
                making = null;
                // Waited too long while being made, or cut by a more important one meanwhile:
                // dropped.
                if ((!request.Preview && Ms(request.Asked, made) > config.MaxWaitMs) || stopping || request.Cut)
                    return;
                playing = request;
                // Until it plays (opening a slow device): a long margin, set again once started.
                playingSince = made + Stopwatch.Frequency * 10;
                playingSeconds = clip.Samples.Length / (double)Math.Max(1, clip.SampleRate);
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
                if (playing == request)
                    playingSince = now;
                if (started == null)
                {
                    playing = null;
                    request.Error = request.Error ?? "no audio output";
                }
                else
                {
                    playback = started;
                    // Cut while it was starting.
                    if (request.Cut)
                        started.Stop();
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
            // A slow voice is fetched off this thread (an alarm coming meanwhile must not wait for
            // it); found in the cache, or joined while still fetching, when its turn comes.
            if (Provider.IsSlow)
            {
                try
                {
                    var plan = Plan(ahead);
                    if (plan != null && FromMemory(plan.Key) == null)
                    {
                        var saved = disk.Get(plan.Key);
                        if (saved != null)
                            Keep(plan.Key, saved, null);
                        else
                            Fetch(plan);
                    }
                }
                catch (Exception e)
                {
                    LogError($"could not say \"{ahead.Text}\": {e.Message}");
                }
                return;
            }
            try
            {
                ahead.Clip = Make(ahead);
            }
            catch (Exception e)
            {
                LogError($"could not say \"{ahead.Text}\": {e.Message}");
            }
        }

        // What identifies a text's speech with the current settings (memory and disk caches).
        // (The language when the voice has none of its own, as some voices speak what they are told.)
        private string KeyFor(ITtsProvider provider, TtsSettings s, TtsVoice voice, string lang, string spoken) =>
            $"{provider.Id}|{s.Fingerprint(provider)}|{voice?.Id}|{(voice?.Lang == null ? lang : "")}|{SpeedFor(provider)}|{spoken}";

        // The speed the engine says it at (made speech is kept by it, so a speed it cannot reach
        // does not make the same speech twice).
        private string SpeedFor(ITtsProvider provider) =>
            Math.Max(provider.MinSpeed, Math.Min(provider.MaxSpeed, config.Speed)).ToString("0.00", CultureInfo.InvariantCulture);

        // What a request becomes with the current settings: the text to say, its key, the call.
        private class Planned
        {
            public ITtsProvider Provider;
            public string Key;
            public TtsSynthesis Synthesis;
        }

        private Planned Plan(Request request)
        {
            var provider = Provider;
            var s = Settings;
            var voice = VoiceFor(provider, request.Lang);
            string lang = voice?.Lang ?? request.Lang;
            request.Spoken = Processor(lang).Process(request.Text);
            if (request.Spoken == "")
                return null;
            return new Planned
            {
                Provider = provider,
                Key = KeyFor(provider, s, voice, lang, request.Spoken),
                Synthesis = new TtsSynthesis { Text = request.Spoken, Voice = voice, Speed = config.Speed, Lang = lang, Settings = s },
            };
        }

        // A slow voice's speech, fetched once however many ask (kept in memory and on disk).
        private Task<TtsClip> Fetch(Planned plan)
        {
            lock (fetching)
            {
                if (fetching.TryGetValue(plan.Key, out var running))
                    return running;
                var task = Task.Run(() =>
                {
                    try
                    {
                        Usage.Check(plan.Provider, plan.Synthesis.Text);
                        var clip = plan.Provider.Synthesize(plan.Synthesis, CancellationToken.None);
                        if (clip != null)
                            Usage.Sent(plan.Provider, plan.Synthesis.Text, false);
                        return Keep(plan.Key, clip, Info(plan.Provider, plan.Synthesis.Voice, plan.Synthesis.Text));
                    }
                    finally
                    {
                        lock (fetching)
                            fetching.Remove(plan.Key);
                    }
                });
                fetching[plan.Key] = task;
                return task;
            }
        }

        // A zone's alerts (from an overlay, on entering it) fetched ahead from a network voice and
        // kept on disk, two at a time, so they play at once the first time too.  A new list stops
        // the last; repeated failures (no network, a bad key) stop it too.
        public void Prepare(IEnumerable<string> texts, string lang)
        {
            preparing?.Cancel();
            var provider = Provider;
            if (!config.PrepareAhead || !provider.IsSlow || stopping)
                return;
            // A local model only if asked for: it would take the CPU for minutes beside the game.
            if (provider is ITtsDownloadable && !config.PrepareAheadLocal)
                return;
            var cancel = (preparing = new CancellationTokenSource()).Token;
            var s = Settings;
            lang = string.IsNullOrEmpty(lang) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : lang;
            var voice = VoiceFor(provider, lang);
            var processor = Processor(voice?.Lang ?? lang);
            var todo = texts.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => processor.Process(t)).Where(t => t != "")
                .Distinct().Take(PrepareMax).Select(spoken => (spoken, key: KeyFor(provider, s, voice, voice?.Lang ?? lang, spoken)))
                .Where(x => FromMemory(x.key) == null && !disk.Has(x.key)).ToList();
            int done = 0, failures = 0;
            PrepareProgress = (0, todo.Count);
            Prepared?.Invoke(0, todo.Count);
            if (todo.Count == 0)
                return;
            Task.Run(() =>
            {
                try
                {
                    System.Threading.Tasks.Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, provider.Parallel), CancellationToken = cancel }, (x, state) =>
                    {
                        try
                        {
                            try
                            {
                                Usage.Check(provider, x.spoken);
                            }
                            catch (InvalidOperationException e)
                            {
                                // The monthly limit: no more fetching ahead.
                                LogError($"{provider.Name}: {e.Message}");
                                state.Stop();
                                return;
                            }
                            var clip = provider.Synthesize(new TtsSynthesis { Text = x.spoken, Voice = voice, Speed = config.Speed, Lang = voice?.Lang ?? lang, Settings = s }, cancel);
                            if (clip != null)
                            {
                                Usage.Sent(provider, x.spoken, true);
                                disk.Put(x.key, clip, Info(provider, voice, x.spoken));
                            }
                            Interlocked.Exchange(ref failures, 0);
                        }
                        catch (Exception e) when (!(e is OperationCanceledException))
                        {
                            if (Interlocked.Increment(ref failures) >= 3)
                            {
                                LogError($"{provider.Name}: fetching ahead stopped: {e.GetBaseException().Message}");
                                state.Stop();
                            }
                        }
                        int n = Interlocked.Increment(ref done);
                        PrepareProgress = (n, todo.Count);
                        Prepared?.Invoke(n, todo.Count);
                    });
                }
                catch (OperationCanceledException)
                {
                }
            });
        }

        private TtsClip Make(Request request)
        {
            var plan = Plan(request);
            if (plan == null)
                return null;
            var kept = FromMemory(plan.Key);
            if (kept != null)
            {
                request.FromCache = true;
                if (plan.Provider.IsSlow)
                    Usage.Kept(plan.Provider);
                return kept;
            }
            if (!plan.Provider.IsSlow)
                return Keep(plan.Key, plan.Provider.Synthesize(plan.Synthesis, CancellationToken.None), null);

            kept = disk.Get(plan.Key);
            if (kept != null)
            {
                request.FromCache = true;
                Usage.Kept(plan.Provider);
                return Keep(plan.Key, kept, null);
            }
            // Slow: not waited for longer than the fallback time; when late it is kept for next time.
            // The preview waits for it (that is what is being tried) and says why when it fails.
            var fetch = Fetch(plan);
            bool inTime;
            try
            {
                inTime = fetch.Wait(request.Preview ? 60000 : Math.Max(50, config.FallbackMs));
            }
            catch (AggregateException e)
            {
                request.Error = $"{plan.Provider.Name}: {e.GetBaseException().Message}";
                LogError(request.Error);
                return request.Preview ? null : WindowsInstead(request, plan.Synthesis.Lang);
            }
            if (request.Preview && !inTime)
            {
                request.Error = $"{plan.Provider.Name}: no answer in 60 s";
                return null;
            }
            if (inTime && fetch.Result != null)
                return fetch.Result;
            if (!inTime)
            {
                fetch.ContinueWith(t =>
                {
                    if (t.Exception != null)
                        LogError($"{plan.Provider.Name}: {t.Exception.GetBaseException().Message}");
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
            return WindowsInstead(request, plan.Synthesis.Lang);
        }

        private TtsClip WindowsInstead(Request request, string lang)
        {
            if (stopping)
                return null;
            request.FellBack = true;
            var voice = VoiceFor(windows, lang);
            string key = $"{windows.Id}||{voice?.Id}|{SpeedFor(windows)}|{request.Spoken}";
            return FromMemory(key) ?? Keep(key, windows.Synthesize(request.Spoken, voice, config.Speed), null);
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
        private TtsClip Keep(string key, TtsClip clip, TtsCacheInfo toDisk)
        {
            if (clip == null)
                return null;
            // Written off the caller's thread: an alert plays before its file is saved.
            if (toDisk != null)
            {
                var saved = clip;
                ThreadPool.QueueUserWorkItem(_ => disk.Put(key, saved, toDisk));
            }
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
                windows.Synthesize(".", voice, config.Speed);
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
            preparing?.Cancel();
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
            // A more important alert came while it was made or started.
            public bool Cut;
            // The settings' preview: the chosen voice itself, however long it takes.
            public bool Preview => Source == "preview";
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
