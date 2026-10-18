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
        // Unloads Supertonic a while after it was loaded only for a preview (TTS off).
        private Timer previewUnload;
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

        // cactbot's default alert output (e.g. textAndSound), told when it asks what this TTS
        // takes: with no TTS in it, none of its alerts are read (the settings say so).
        public string CactbotOutput { get; set; }

        // How the chosen slow voice did since ACT started (see TtsStats).
        public TtsStats Stats { get; } = new TtsStats();

        // The volume of all speech set from outside the settings (cactbot's config).
        public event Action VolumeChanged;

        public void SetVolume(int volume)
        {
            config.Volume = Clamp(volume);
            VolumeChanged?.Invoke();
        }

        // Alerts fetched ahead (see Prepare): done of total, for the settings.
        public event Action<int, int> Prepared;
        public (int Done, int Total) PrepareProgress { get; private set; }
        // The last zone's list: texts to make, their characters, whether the voice is paid for.
        public (int Count, long Chars, bool Billed) LastPrepare { get; private set; }
        // Why the last list was not made ahead (none: it was).
        public string PrepareNote { get; private set; }
        private CancellationTokenSource preparing;
        private const int PrepareMax = 500;

        // Services that answered "too many requests" or "out of quota": not asked again until then
        // (Windows' voice reads meanwhile), so a limit is neither hammered nor overrun.
        private readonly Dictionary<string, (DateTime Until, string Reason)> paused = new Dictionary<string, (DateTime, string)>();
        public event Action PausedChanged;

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
            // (The FFXIV plugin has no way to unsubscribe: after shutdown the change is ignored.)
            repository.RegisterZoneChangeDelegate((id, name) =>
            {
                if (!stopping)
                    CurrentZone = string.IsNullOrEmpty(name) ? (id == 0 ? null : $"#{id}") : name;
            });
            // The zone ACT started in, by number until its name comes with a change.
            try
            {
                var id = repository.GetCurrentTerritoryID();
                if (CurrentZone == null && id.HasValue && id.Value != 0)
                    CurrentZone = $"#{id.Value}";
            }
            catch (Exception)
            {
            }
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

        public Task<IReadOnlyList<TtsVoice>> FetchChoicesAsync(string field)
        {
            var provider = Provider;
            var s = Settings;
            return Task.Run(() => provider is ITtsChoiceSource source ? source.FetchChoices(field, s, CancellationToken.None) : (IReadOnlyList<TtsVoice>)new TtsVoice[0]);
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

        // volume: 0 to 1, the sender's own for this text (cactbot's by how important it is); none:
        // the volume for ACT's own speech and overlays that send none.
        public void Speak(string text, TtsSeverity severity, string source, string lang = null, double? volume = null)
        {
            if (string.IsNullOrWhiteSpace(text) || stopping)
                return;
            var request = new Request
            {
                Text = text,
                Severity = severity,
                Source = source,
                Gain = volume,
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
            {
                supertonic.Warm(Settings);
                previewUnload?.Dispose();
                previewUnload = new Timer(_ =>
                {
                    if (!config.Enabled && !stopping)
                        supertonic.Unload();
                }, null, TimeSpan.FromMinutes(3), Timeout.InfiniteTimeSpan);
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
                    next.Error = next.Error ?? e.Message;
                    Report(next, 0, 0);
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
                if (request.Error == null)
                {
                    bool ko = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";
                    request.Error = request.Spoken == ""
                        ? (ko ? "읽을 글자가 없습니다 (바꾸기 규칙 뒤 빈 문장)" : "nothing to read (empty after the rules)")
                        : (ko ? "음성을 만들지 못했습니다" : "no speech was made");
                }
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
            var started = player.Play(clip, Volume(request), () =>
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

        private void Report(Request request, double made, double started)
        {
            if (!request.Preview && request.SlowVoice != null)
                Stats.Read(request.SlowVoice, request.FromCache, request.FellBack, request.Paused, request.Error != null && !request.Paused);
            Spoken?.Invoke(new TtsResult(request.Text, request.Spoken, made, started, request.FromCache, request.FellBack, request.Error, request.Preview));
        }

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
                        else if (PausedFor(plan.Provider) == null)
                            Fetch(plan, true);
                        // Over its limit: Windows' voice made ahead instead.
                        else
                            ahead.Clip = Make(ahead);
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
        private string KeyFor(ITtsProvider provider, TtsSettings s, TtsVoice voice, string lang, string spoken, double speed) =>
            $"{provider.Id}|{s.Fingerprint(provider)}|{voice?.Id}|{(voice?.Lang == null ? lang : "")}|{SpeedFor(provider, speed)}|{spoken}";

        // The speed the engine says it at (made speech is kept by it, so a speed it cannot reach
        // does not make the same speech twice).
        private static string SpeedFor(ITtsProvider provider, double speed) =>
            Math.Max(provider.MinSpeed, Math.Min(provider.MaxSpeed, speed)).ToString("0.00", CultureInfo.InvariantCulture);

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
            double speed = config.Speed;
            return new Planned
            {
                Provider = provider,
                Key = KeyFor(provider, s, voice, lang, request.Spoken, speed),
                Synthesis = new TtsSynthesis { Text = request.Spoken, Voice = voice, Speed = speed, Lang = lang, Settings = s },
            };
        }

        // A slow voice's speech, fetched once however many ask (kept in memory and on disk).
        // counted: an alert's (not the preview's), for the stats.
        private Task<TtsClip> Fetch(Planned plan, bool counted)
        {
            lock (fetching)
            {
                if (fetching.TryGetValue(plan.Key, out var running))
                    return running;
                var task = Task.Run(() =>
                {
                    try
                    {
                        ThrowIfPaused(plan.Provider);
                        Usage.Check(plan.Provider, plan.Synthesis.Text);
                        TtsClip clip = null;
                        var took = Stopwatch.StartNew();
                        try
                        {
                            clip = plan.Provider.Synthesize(plan.Synthesis, CancellationToken.None);
                            if (counted)
                                Stats.Made(plan.Provider.Name, took.Elapsed.TotalMilliseconds);
                        }
                        catch (Exception e)
                        {
                            PauseIfLimited(plan.Provider, e);
                            throw;
                        }
                        finally
                        {
                            if (clip != null)
                                Usage.Sent(plan.Provider, plan.Synthesis.Text, false);
                            else
                                Usage.Release(plan.Provider, plan.Synthesis.Text);
                        }
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
            PrepareNote = null;
            var pause = PausedFor(provider);
            if (pause != null)
            {
                LogError(PausedText(provider, pause.Value));
                return;
            }
            // A paid service only with a monthly limit: a zone is hundreds of texts.
            if (TtsUsageCounter.Paid(provider, Settings) && Usage.Get(provider).MonthlyLimit <= 0)
            {
                PrepareNote = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko"
                    ? "유료 서비스는 월 한도를 정해야 미리 만듭니다 (만든 음성·사용량 탭)"
                    : "A paid service makes ahead only with a monthly limit (Made speech and usage)";
                PrepareProgress = (0, 0);
                Prepared?.Invoke(0, 0);
                return;
            }
            var cancel = (preparing = new CancellationTokenSource()).Token;
            var s = Settings;
            lang = string.IsNullOrEmpty(lang) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : lang;
            var voice = VoiceFor(provider, lang);
            var processor = Processor(voice?.Lang ?? lang);
            double speed = config.Speed;
            var all = texts.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => processor.Process(t)).Where(t => t != "")
                .Distinct().Take(PrepareMax).Select(spoken => (spoken, key: KeyFor(provider, s, voice, voice?.Lang ?? lang, spoken, speed))).ToList();
            // What this zone needs and is already kept: the zone recorded for it too.
            disk.Touch(all.Select(x => x.key), CurrentZone);
            var todo = all.Where(x => FromMemory(x.key) == null && !disk.Has(x.key)).ToList();
            int done = 0, failures = 0;
            LastPrepare = (todo.Count, todo.Sum(x => (long)x.spoken.Length), TtsUsageCounter.Billed(provider));
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
                            // Made meanwhile (an alert came), or being made: not again.
                            bool beingMade;
                            lock (fetching)
                                beingMade = fetching.ContainsKey(x.key);
                            if (beingMade || FromMemory(x.key) != null || disk.Has(x.key))
                                return;
                            // An alert being made or waiting comes first (it would wait for the
                            // engine, or the connection, otherwise).
                            while (LiveWaiting())
                            {
                                cancel.ThrowIfCancellationRequested();
                                Thread.Sleep(50);
                            }
                            if (PausedFor(provider) != null)
                            {
                                state.Stop();
                                return;
                            }
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
                            TtsClip clip = null;
                            try
                            {
                                clip = provider.Synthesize(new TtsSynthesis { Text = x.spoken, Voice = voice, Speed = speed, Lang = voice?.Lang ?? lang, Settings = s }, cancel);
                            }
                            finally
                            {
                                if (clip != null)
                                    Usage.Sent(provider, x.spoken, true);
                                else
                                    Usage.Release(provider, x.spoken);
                            }
                            if (clip != null)
                                disk.Put(x.key, clip, Info(provider, voice, x.spoken));
                            Interlocked.Exchange(ref failures, 0);
                        }
                        catch (Exception e) when (!(e is OperationCanceledException))
                        {
                            if (PauseIfLimited(provider, e))
                            {
                                LogError($"{provider.Name}: fetching ahead stopped: {e.GetBaseException().Message}");
                                state.Stop();
                            }
                            else if (Interlocked.Increment(ref failures) >= 3)
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
                    disk.Touch(plan.Key, CurrentZone);
                return kept;
            }
            if (!plan.Provider.IsSlow)
                return Keep(plan.Key, plan.Provider.Synthesize(plan.Synthesis, CancellationToken.None), null);
            request.SlowVoice = plan.Provider.Name;

            kept = disk.Get(plan.Key);
            if (kept != null)
            {
                request.FromCache = true;
                disk.Touch(plan.Key, CurrentZone);
                Usage.Kept(plan.Provider);
                return Keep(plan.Key, kept, null);
            }
            // Over its limit: not asked (see PauseIfLimited).
            var pause = PausedFor(plan.Provider);
            if (pause != null)
            {
                request.Paused = true;
                request.Error = PausedText(plan.Provider, pause.Value);
                return request.Preview ? null : WindowsInstead(request, plan.Synthesis.Lang);
            }
            // Slow: not waited for longer than the fallback time; when late it is kept for next time.
            // The preview waits for it (that is what is being tried) and says why when it fails.
            var fetch = Fetch(plan, !request.Preview);
            bool inTime;
            try
            {
                if (request.Preview)
                {
                    // Waited for in short steps: an alert asked meanwhile (or a cut, or shutting
                    // down) ends the wait, so the preview never holds alerts back.
                    var waited = Stopwatch.StartNew();
                    while (!(inTime = fetch.Wait(100)) && waited.ElapsedMilliseconds < 60000)
                    {
                        bool alert;
                        lock (sync)
                            alert = queue.Any(r => !r.Preview);
                        if (alert || request.Cut || stopping)
                        {
                            request.Error = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko"
                                ? "알림이 와서 미리 듣기를 멈췄습니다 (음성은 계속 만들어 보관)"
                                : "an alert came: the preview stopped (the speech is still made and kept)";
                            return null;
                        }
                    }
                }
                else
                {
                    inTime = fetch.Wait(Math.Max(50, config.FallbackMs));
                }
            }
            catch (AggregateException e)
            {
                var why = e.GetBaseException().Message;
                request.Paused = e.GetBaseException() is TtsLimitException;
                request.Error = why.StartsWith(plan.Provider.Name + ": ") ? why : $"{plan.Provider.Name}: {why}";
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
            double speed = config.Speed;
            string key = $"{windows.Id}||{voice?.Id}|{SpeedFor(windows, speed)}|{request.Spoken}";
            return FromMemory(key) ?? Keep(key, windows.Synthesize(request.Spoken, voice, speed), null);
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
        // An alert (not the preview) waiting to be made, being made, or being fetched.
        private bool LiveWaiting()
        {
            lock (sync)
            {
                if ((making != null && making.Clip == null && !making.Preview) || queue.Any(r => r.Clip == null && !r.Preview))
                    return true;
            }
            lock (fetching)
                return fetching.Count > 0;
        }

        // Until when a service is paused (none: it can be asked).
        public (DateTime Until, string Reason)? PausedFor(ITtsProvider provider)
        {
            lock (paused)
            {
                if (!paused.TryGetValue(provider.Id, out var p))
                    return null;
                if (p.Until > DateTime.Now)
                    return p;
                paused.Remove(provider.Id);
            }
            PausedChanged?.Invoke();
            return null;
        }

        // Asked again now (the settings' button).
        public void Resume(ITtsProvider provider)
        {
            lock (paused)
                paused.Remove(provider.Id);
            PausedChanged?.Invoke();
        }

        public static string PausedText(ITtsProvider provider, (DateTime Until, string Reason) p) =>
            $"{provider.Name}: " + (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko"
                ? $"{p.Reason}. {p.Until:HH:mm}까지 보내지 않고 Windows 음성으로 읽습니다"
                : $"{p.Reason}. Not asked until {p.Until:HH:mm}: Windows' voice reads meanwhile");

        private void ThrowIfPaused(ITtsProvider provider)
        {
            var p = PausedFor(provider);
            if (p != null)
                throw new TtsLimitException(PausedText(provider, p.Value));
        }

        // "Too many requests" (429): paused as long as the service says (or a minute); out of
        // quota or credits (402, or a refusal that says quota): for six hours.  True if paused.
        private bool PauseIfLimited(ITtsProvider provider, Exception e)
        {
            if (!(e.GetBaseException() is TtsHttpException http))
                return false;
            bool ko = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";
            var message = http.Message.ToLowerInvariant();
            bool outOfQuota = http.Status == 402 || message.Contains("insufficient_quota") || message.Contains("quota_exceeded") ||
                ((http.Status == 401 || http.Status == 403) && message.Contains("quota"));
            TimeSpan wait;
            string reason;
            if (outOfQuota)
            {
                wait = TimeSpan.FromHours(6);
                reason = ko ? "사용량이나 크레딧이 다 됨" : "out of quota or credits";
            }
            else if (http.Status == 429)
            {
                // Gemini says when in its answer ("retryDelay": "41s").
                var said = System.Text.RegularExpressions.Regex.Match(http.Message, "\"retryDelay\"\\s*:\\s*\"(\\d+)(?:\\.\\d+)?s\"");
                wait = http.RetryAfter ?? (said.Success ? TimeSpan.FromSeconds(int.Parse(said.Groups[1].Value)) : TimeSpan.FromMinutes(1));
                wait = TimeSpan.FromSeconds(Math.Max(10, Math.Min(6 * 3600, wait.TotalSeconds)));
                reason = ko ? "요청이 너무 많음 (한도 초과)" : "too many requests (over the limit)";
            }
            else
            {
                return false;
            }
            lock (paused)
                paused[provider.Id] = (DateTime.Now + wait, reason);
            LogError(PausedText(provider, (DateTime.Now + wait, reason)) + $" ({http.Message})");
            PausedChanged?.Invoke();
            return true;
        }

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

        private float Volume(Request request)
        {
            float own = request.Preview ? 1f
                : request.Gain is double gain ? (float)Math.Max(0, Math.Min(1, gain))
                : Clamp(config.OtherVolume) / 100f;
            return Clamp(config.Volume) / 100f * own;
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
            previewUnload?.Dispose();
            wake.Set();
            worker.Join(2000);
            player.Dispose();
            foreach (var p in providers.Values.OfType<IDisposable>())
                p.Dispose();
            ClearMemory();
        }

        private class Request
        {
            public string Text;
            public string Spoken;
            public TtsSeverity Severity;
            public string Source;
            public string Lang;
            // The sender's own volume (0 to 1), if it gave one.
            public double? Gain;
            // A slow voice's name, when one made it (for the stats); whether it was paused.
            public string SlowVoice;
            public bool Paused;
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

        // Asked by the settings' preview (the panel shows only those).
        public bool Preview { get; }

        public TtsResult(string text, string spoken, double madeMs, double startedMs, bool fromCache, bool fellBack, string error, bool preview = false)
        {
            Preview = preview;
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
