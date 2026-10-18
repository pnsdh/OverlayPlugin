using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace RainbowMage.OverlayPlugin.Tts
{
    // Fork: reads alerts aloud.  Speak only queues (it is called from overlays' and ACT's threads
    // and must never wait); one worker thread turns text into speech and starts it.  For speed:
    // the output is kept open (TtsPlayer), speech already made is kept in memory, the next text
    // in line is made while the current one plays, and the voice is loaded at start.
    public class TtsService : IDisposable
    {
        private const int CacheBytes = 48 * 1024 * 1024;

        private readonly ILogger logger;
        private readonly TtsConfig config;
        private readonly TtsPlayer player;
        private readonly Dictionary<string, ITtsProvider> providers = new Dictionary<string, ITtsProvider>();

        private readonly object sync = new object();
        private readonly List<Request> queue = new List<Request>();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Thread worker;
        private volatile bool stopping;

        private Request playing;
        private TtsPlayback playback;
        private readonly Dictionary<string, DateTime> recent = new Dictionary<string, DateTime>();

        // Made speech by provider, voice, rate and text, least recently used dropped first.
        private readonly Dictionary<string, LinkedListNode<(string Key, TtsClip Clip)>> cache = new Dictionary<string, LinkedListNode<(string, TtsClip)>>();
        private readonly LinkedList<(string Key, TtsClip Clip)> cacheOrder = new LinkedList<(string, TtsClip)>();
        private int cacheBytes;

        private TtsTextProcessor processor;
        private string processorKey;

        // For the settings' preview: how long the last speech took, from asked for to playing.
        public event Action<TtsResult> Spoken;

        public TtsService(TinyIoCContainer container, TtsConfig config)
        {
            logger = container.Resolve<ILogger>();
            this.config = config;
            player = new TtsPlayer(logger);
            AddProvider(new SapiProvider());
            worker = new Thread(Run) { IsBackground = true, Name = "OverlayPlugin TTS" };
            worker.Start();
        }

        public TtsConfig Config => config;

        public IEnumerable<ITtsProvider> Providers => providers.Values;

        public void AddProvider(ITtsProvider provider) => providers[provider.Id] = provider;

        public ITtsProvider Provider => providers.TryGetValue(config.Provider ?? "", out var p) ? p : providers[SapiProvider.ProviderId];

        // The voice to use: the chosen one, or the first for the language asked, then any.
        public TtsVoice VoiceFor(string lang)
        {
            var voices = Provider.GetVoices();
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

        // Settings changed: rules are made again, made speech no longer matches.
        public void ConfigChanged()
        {
            lock (sync)
            {
                processor = null;
                cache.Clear();
                cacheOrder.Clear();
                cacheBytes = 0;
            }
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
                // The same alert again, soon after: once is enough.
                var now = DateTime.UtcNow;
                if (recent.TryGetValue(text, out var last) && (now - last).TotalMilliseconds < config.RepeatWindowMs)
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
            Thread.CurrentThread.CurrentUICulture = CultureInfo.CurrentUICulture;
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
                    logger.Log(LogLevel.Warning, $"TTS: could not say \"{next.Text}\": {e.Message}");
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
                return;
            long made = Stopwatch.GetTimestamp();
            lock (sync)
            {
                // Waited too long while being made (a slow voice): dropped all the same.
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
                    return;
                }
                playback = started;
            }
            Spoken?.Invoke(new TtsResult(request.Text, request.Spoken, Ms(request.Asked, made), Ms(request.Asked, now), request.FromCache));
        }

        // The next in line, made while something plays, so it starts as soon as that ends.
        private void MakeAhead()
        {
            Request ahead;
            lock (sync)
            {
                ahead = queue.OrderByDescending(r => r.Severity).ThenBy(r => r.Asked).FirstOrDefault(r => r.Clip == null);
            }
            if (ahead == null)
                return;
            try
            {
                ahead.Clip = Make(ahead);
            }
            catch (Exception e)
            {
                logger.Log(LogLevel.Warning, $"TTS: could not say \"{ahead.Text}\": {e.Message}");
                lock (sync)
                    queue.Remove(ahead);
            }
        }

        private TtsClip Make(Request request)
        {
            var provider = Provider;
            var voice = VoiceFor(request.Lang);
            request.Spoken = Processor(voice?.Lang ?? request.Lang).Process(request.Text);
            if (request.Spoken == "")
                return null;
            string key = $"{provider.Id}|{voice?.Id}|{config.Rate}|{request.Spoken}";
            lock (sync)
            {
                if (cache.TryGetValue(key, out var node))
                {
                    cacheOrder.Remove(node);
                    cacheOrder.AddFirst(node);
                    request.FromCache = true;
                    return node.Value.Clip;
                }
            }
            var clip = provider.Synthesize(request.Spoken, voice, config.Rate, CancellationToken.None);
            if (clip == null)
                return null;
            // Kept at the output's rate, so playing it again is only mixing.
            int rate = player.MixRate;
            if (rate > 0 && rate != clip.SampleRate)
                clip = new TtsClip(TtsPlayer.Resample(clip, rate), rate);
            lock (sync)
            {
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

        // The voice loaded and the output opened before the first alert needs them.
        private void Warm()
        {
            try
            {
                var voice = VoiceFor(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
                Provider.Synthesize(".", voice, config.Rate, CancellationToken.None);
            }
            catch (Exception e)
            {
                logger.Log(LogLevel.Warning, $"TTS: could not load the voice: {e.Message}");
            }
        }

        private static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

        public void Dispose()
        {
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
            public bool FromCache;
        }
    }

    public class TtsResult
    {
        public string Text { get; }
        public string Spoken { get; }
        public double MadeMs { get; }
        public double StartedMs { get; }
        public bool FromCache { get; }

        public TtsResult(string text, string spoken, double madeMs, double startedMs, bool fromCache)
        {
            Text = text;
            Spoken = spoken;
            MadeMs = madeMs;
            StartedMs = startedMs;
            FromCache = fromCache;
        }
    }
}
