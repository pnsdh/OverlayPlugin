using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RainbowMage.OverlayPlugin.Tts.Supertonic
{
    // Fork: Supertonic voices on this PC: free and natural, but the model must be downloaded first
    // (on request, see SupertonicModel), takes a few hundred megabytes of memory while chosen, and
    // a moment of CPU per new text, so it is treated like a network voice (kept on disk, Windows'
    // voice when it is late).
    //
    // The engine is only loaded while wanted (chosen and TTS on), is loaded outside the lock (the
    // UI thread unloading never waits for a load), and is disposed only when no synthesis uses it:
    // disposing ONNX Runtime's sessions during a run would crash ACT.
    public class SupertonicProvider : ITtsProvider, ITtsDownloadable, IDisposable
    {
        private static bool Ko => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";

        public string Id => "supertonic";
        public string Name => "Supertonic";
        public string Note => Ko ? "무료, 내 컴퓨터에서 실행, 다운로드 필요" : "free, runs on your computer, download needed";
        public bool IsSlow => true;
        // Its model speaks well only between 0.9 and 1.5 times its own speed (slower skips words).
        public double MinSpeed => 0.9;
        public double MaxSpeed => 1.5;
        // One text at a time: it runs on the CPU beside the game.
        public int Parallel => 1;

        public IReadOnlyList<TtsField> Fields { get; } = new[]
        {
            new TtsField { Key = "voice", Label = "Voice", LabelKo = "목소리", Kind = TtsFieldKind.Choice, Default = "F1", Choices = SupertonicModel.Voices,
                Hint = "F: female, M: male", HintKo = "F: 여성, M: 남성" },
            new TtsField { Key = "steps", Label = "Quality (steps)", LabelKo = "품질 (단계 수)", Kind = TtsFieldKind.Choice, Default = "5", Choices = new[] { "3", "5", "8", "12" },
                ChoiceNames = new[] { "3", "5 (default)", "8", "12" }, ChoiceNamesKo = new[] { "3", "5 (기본)", "8", "12" },
                Hint = "More: better, slower. 5 is enough near the normal speed; further from it words can break, so below 0.9x or above 1.1x it is made with 8 or more.",
                HintKo = "많을수록 좋지만 느립니다. 기본 속도 근처에서는 5로 충분하고, 속도를 많이 바꾸면 낱말이 깨질 수 있어서 0.9배보다 느리거나 1.1배보다 빠르면 8 이상으로 만듭니다." },
            // Only how fast: the speech is the same, so made speech is kept when it changes.
            new TtsField { Key = "threads", Label = "CPU threads", LabelKo = "CPU 스레드", Kind = TtsFieldKind.Choice, Default = "2", Choices = ThreadChoices(),
                ChoiceNames = ThreadChoices().Select(c => c == "2" ? "2 (default)" : c).ToArray(),
                ChoiceNamesKo = ThreadChoices().Select(c => c == "2" ? "2 (기본)" : c).ToArray(),
                Hint = "While speech is made. 2 is about the fastest: more is no quicker (measured), and using every core is slower. 1 is lighter beside the game but slower. Alerts made ahead (on entering a zone) take the CPU for a while then, and none in the fight.",
                HintKo = "음성을 만드는 동안 쓰는 수. 2 정도가 가장 빠릅니다. 더 올려도 빨라지지 않고(측정 결과), 코어를 다 쓰면 오히려 느려집니다. 1이면 게임에 덜 부담이지만 느립니다. 미리 만들기를 켜면 지역에 들어갈 때 한동안 CPU를 쓰고, 전투 중에는 만들어 둔 음성을 씁니다.",
                NotInKey = true },
        };

        // Up to 4 (measured on a 6-core Ryzen: 2 is about the fastest, more is no quicker, 12 is
        // slower), and the computer's cores; a saved larger number is held to it too (Synthesize).
        private static string[] ThreadChoices() =>
            new[] { 1, 2, 3, 4 }.Where(n => n == 1 || n <= Environment.ProcessorCount).Select(n => n.ToString()).ToArray();

        private class Loaded
        {
            public SupertonicEngine Engine;
            public int Threads;
            public int Users;
            public bool Retired;
        }

        private readonly object sync = new object();
        private readonly object loadSync = new object();
        private Loaded current;
        private bool wanted;
        private int generation;
        // A load that failed: not tried again (each try costs memory and seconds) until the files
        // change (a new download, a delete).
        private string loadError;

        public IReadOnlyList<TtsVoice> GetVoices(TtsSettings settings)
        {
            var voice = settings["voice"];
            return new[] { new TtsVoice { Id = voice, Name = voice } };
        }

        public TtsClip Synthesize(TtsSynthesis request, CancellationToken cancel)
        {
            int.TryParse(request.Settings["threads"], out int threads);
            var loaded = Lease(Math.Max(1, Math.Min(Math.Min(threads, 4), Environment.ProcessorCount)));
            try
            {
                int.TryParse(request.Settings["steps"], out int steps);
                var speed = (float)Math.Max(MinSpeed, Math.Min(MaxSpeed, request.Speed));
                // Few steps break words away from the normal speed (heard at 0.9x with 5).
                if (Math.Abs(speed - 1f) > 0.1f)
                    steps = Math.Max(steps, 8);
                var samples = loaded.Engine.Synthesize(request.Text, request.Lang ?? "ko", request.Settings["voice"], Math.Max(1, steps), speed);
                return new TtsClip(samples, loaded.Engine.SampleRate);
            }
            finally
            {
                Release(loaded);
            }
        }

        // The engine for a synthesis, counted as in use until Release.  Loads it if wanted and not
        // loaded (or loaded with other threads); throws if not wanted, not downloaded or broken, so
        // the caller says it with Windows' voice instead.
        private Loaded Lease(int threads)
        {
            int startedAt;
            lock (sync)
            {
                if (!wanted)
                    throw new InvalidOperationException(Ko ? "Supertonic을 고르지 않았거나 TTS가 꺼져 있습니다" : "Supertonic is not chosen or TTS is off");
                if (current != null && current.Threads == threads)
                {
                    current.Users++;
                    return current;
                }
                if (loadError != null)
                    throw new InvalidOperationException(loadError);
                if (!SupertonicModel.Ready)
                    throw new InvalidOperationException(Ko ? "Supertonic을 아직 다운로드하지 않았습니다 (TTS 탭에서 다운로드)" : "Supertonic is not downloaded (download it in the TTS tab)");
                startedAt = generation;
            }

            // One load at a time; whoever waited finds it done.
            lock (loadSync)
            {
                lock (sync)
                {
                    // What changed while it waited for the load before it.
                    if (!wanted)
                        throw new InvalidOperationException(Ko ? "Supertonic을 고르지 않았거나 TTS가 꺼져 있습니다" : "Supertonic is not chosen or TTS is off");
                    if (loadError != null)
                        throw new InvalidOperationException(loadError);
                    startedAt = generation;
                    if (current != null && current.Threads == threads)
                    {
                        current.Users++;
                        return current;
                    }
                }
                SupertonicEngine engine;
                try
                {
                    SupertonicModel.LoadRuntime();
                    engine = new SupertonicEngine(SupertonicModel.Folder, threads);
                }
                catch (Exception e)
                {
                    lock (sync)
                        loadError = (Ko ? "Supertonic을 불러오지 못했습니다 (다시 다운로드해 보세요): " : "Supertonic could not be loaded (try downloading it again): ") + e.GetBaseException().Message;
                    throw new InvalidOperationException(loadError, e);
                }
                lock (sync)
                {
                    // Unwanted meanwhile (another voice chosen, TTS turned off): not kept.
                    if (!wanted || generation != startedAt)
                    {
                        engine.Dispose();
                        throw new InvalidOperationException(Ko ? "Supertonic을 고르지 않았거나 TTS가 꺼져 있습니다" : "Supertonic is not chosen or TTS is off");
                    }
                    Retire(current);
                    current = new Loaded { Engine = engine, Threads = threads, Users = 1 };
                    return current;
                }
            }
        }

        private void Release(Loaded loaded)
        {
            lock (sync)
            {
                loaded.Users--;
                if (loaded.Retired && loaded.Users == 0)
                    loaded.Engine.Dispose();
            }
        }

        // Under the lock: no longer the current one; disposed now if unused, else by its last user.
        private void Retire(Loaded loaded)
        {
            if (loaded == null)
                return;
            loaded.Retired = true;
            if (loaded.Users == 0)
                loaded.Engine.Dispose();
            if (current == loaded)
                current = null;
        }

        // Wanted (chosen, TTS on): loaded ahead (a few seconds), so the first alert does not wait.
        public void Warm(TtsSettings settings)
        {
            lock (sync)
                wanted = true;
            if (!SupertonicModel.Ready)
                return;
            int.TryParse(settings["threads"], out int threads);
            Task.Run(() =>
            {
                try
                {
                    Release(Lease(Math.Max(1, threads)));
                }
                catch (Exception)
                {
                    // Said when used.
                }
            });
        }

        // Not wanted any more: its memory back (once the synthesis in progress, if any, ends).
        public void Unload()
        {
            lock (sync)
            {
                wanted = false;
                generation++;
                Retire(current);
                loadError = null;
            }
        }

        public bool Ready => SupertonicModel.Ready;
        public long DownloadSize => SupertonicModel.DownloadSize;
        public long SizeOnDisk() => SupertonicModel.SizeOnDisk();
        public string LicenseUrl => SupertonicModel.LicenseUrl;
        public string Folder => SupertonicModel.Folder;

        public string LoadError
        {
            get
            {
                lock (sync)
                    return loadError;
            }
        }

        public async Task Download(IProgress<(long Done, long Total)> progress, CancellationToken cancel)
        {
            await SupertonicModel.Download(progress, cancel);
            lock (sync)
                loadError = null;
        }

        public void Delete()
        {
            lock (sync)
            {
                generation++;
                Retire(current);
                loadError = null;
            }
            SupertonicModel.Delete();
        }

        public void Dispose() => Unload();
    }
}
