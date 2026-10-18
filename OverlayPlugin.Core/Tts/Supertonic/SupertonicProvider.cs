using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace RainbowMage.OverlayPlugin.Tts.Supertonic
{
    // Fork: Supertonic voices on this PC: free and natural, but the model must be downloaded first
    // (on request, see SupertonicModel), takes a few hundred megabytes of memory while chosen, and
    // a moment of CPU per new text, so it is treated like a network voice (kept on disk, fetched
    // ahead, Windows' voice when it is late).
    public class SupertonicProvider : ITtsProvider, ITtsDownloadable, IDisposable
    {
        public string Id => "supertonic";
        public string Name => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko"
            ? "Supertonic (이 PC에서, 무료, 내려받기 필요)"
            : "Supertonic (on this PC, free, download needed)";
        public bool IsSlow => true;
        // One text at a time: it runs on the CPU beside the game.
        public int Parallel => 1;

        public IReadOnlyList<TtsField> Fields { get; } = new[]
        {
            new TtsField { Key = "voice", Label = "Voice", LabelKo = "목소리", Kind = TtsFieldKind.Choice, Default = "F1", Choices = SupertonicModel.Voices,
                Hint = "F: female, M: male", HintKo = "F: 여성, M: 남성" },
            new TtsField { Key = "steps", Label = "Quality (steps)", LabelKo = "품질 (단계 수)", Kind = TtsFieldKind.Choice, Default = "5", Choices = new[] { "3", "5", "8", "12" },
                Hint = "More: better, slower", HintKo = "많을수록 좋지만 느림" },
            new TtsField { Key = "threads", Label = "CPU threads", LabelKo = "CPU 스레드", Kind = TtsFieldKind.Choice, Default = "2", Choices = new[] { "1", "2", "4" },
                Hint = "Fewer: lighter beside the game", HintKo = "적을수록 게임에 부담이 적음" },
        };

        private readonly object sync = new object();
        private SupertonicEngine engine;
        private int engineThreads;

        public IReadOnlyList<TtsVoice> GetVoices(TtsSettings settings)
        {
            var voice = settings["voice"];
            return new[] { new TtsVoice { Id = voice, Name = voice } };
        }

        public TtsClip Synthesize(TtsSynthesis request, CancellationToken cancel)
        {
            var engine = Engine(request.Settings);
            int.TryParse(request.Settings["steps"], out int steps);
            var samples = engine.Synthesize(request.Text, request.Lang ?? "ko", request.Settings["voice"], Math.Max(1, steps), (float)(1.05 * request.Speed));
            return new TtsClip(samples, engine.SampleRate);
        }

        private SupertonicEngine Engine(TtsSettings settings)
        {
            int.TryParse(settings["threads"], out int threads);
            lock (sync)
            {
                if (engine != null && engineThreads == threads)
                    return engine;
                if (!SupertonicModel.Ready)
                    throw new InvalidOperationException(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko"
                        ? "Supertonic을 아직 내려받지 않았습니다 (TTS 탭에서 내려받기)"
                        : "Supertonic is not downloaded (download it in the TTS tab)");
                engine?.Dispose();
                SupertonicModel.LoadRuntime();
                engine = new SupertonicEngine(SupertonicModel.Folder, threads);
                engineThreads = threads;
                return engine;
            }
        }

        // Loaded ahead (a few seconds), so the first alert does not wait for it.
        public void Warm(TtsSettings settings)
        {
            if (!SupertonicModel.Ready)
                return;
            Task.Run(() =>
            {
                try
                {
                    Engine(settings);
                }
                catch (Exception)
                {
                    // Said when used.
                }
            });
        }

        // Not chosen any more: its memory back.
        public void Unload()
        {
            lock (sync)
            {
                engine?.Dispose();
                engine = null;
            }
        }

        public bool Ready => SupertonicModel.Ready;
        public long DownloadSize => SupertonicModel.DownloadSize;
        public long SizeOnDisk() => SupertonicModel.SizeOnDisk();
        public string LicenseUrl => SupertonicModel.LicenseUrl;
        public string Folder => SupertonicModel.Folder;
        public Task Download(IProgress<(long Done, long Total)> progress, CancellationToken cancel) => SupertonicModel.Download(progress, cancel);

        public void Delete()
        {
            Unload();
            SupertonicModel.Delete();
        }

        public void Dispose() => Unload();
    }
}
