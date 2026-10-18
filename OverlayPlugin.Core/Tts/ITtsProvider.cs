using System.Collections.Generic;
using System.Threading;

namespace RainbowMage.OverlayPlugin.Tts
{
    // Fork: a source of voices.  A quick one (IsSlow false) is called on the TTS worker thread
    // only, one call at a time; a slow one (over the network, or heavy on the CPU) may be called
    // from several threads at once, as a slow answer must not hold the next alert.
    public interface ITtsProvider
    {
        string Id { get; }
        string Name { get; }

        // Slow (over the network, or a heavy local model): speech is kept on disk, fetched
        // ahead, and Windows' voice says it when it is late.
        bool IsSlow { get; }

        // How many texts it makes at once when fetching ahead.
        int Parallel { get; }

        // What the user sets for it (address, key, voice...), shown in the TTS tab.
        IReadOnlyList<TtsField> Fields { get; }

        // The voices to choose from (a remote provider's are fetched with FetchVoices).
        IReadOnlyList<TtsVoice> GetVoices(TtsSettings settings);

        // The speech, or null if this provider cannot say it.  Throws with a readable message
        // when it fails (bad key, no network...).
        TtsClip Synthesize(TtsSynthesis request, CancellationToken cancel);
    }

    // A provider whose files are downloaded on the user's request (e.g. a local model).
    public interface ITtsDownloadable
    {
        bool Ready { get; }
        long DownloadSize { get; }
        long SizeOnDisk();
        string LicenseUrl { get; }
        string Folder { get; }
        System.Threading.Tasks.Task Download(System.IProgress<(long Done, long Total)> progress, CancellationToken cancel);
        void Delete();
    }

    // A provider that can list its voices from the service (with the user's key).
    public interface ITtsVoiceSource
    {
        IReadOnlyList<TtsVoice> FetchVoices(TtsSettings settings, CancellationToken cancel);
    }

    public class TtsSynthesis
    {
        public string Text { get; set; }
        public TtsVoice Voice { get; set; }
        // -10 to 10 (see TtsConfig.Rate); Speed is the same as a factor (0.5 to 2).
        public int Rate { get; set; }
        public double Speed => System.Math.Pow(2, Rate / 10.0);
        public string Lang { get; set; }
        public TtsSettings Settings { get; set; }
    }

    public class TtsVoice
    {
        public string Id { get; set; }
        public string Name { get; set; }
        // Two-letter language ("ko", "en"...).
        public string Lang { get; set; }

        public override string ToString() => Name;
    }

    public enum TtsFieldKind
    {
        Text,
        // Kept encrypted, shown as dots.
        Secret,
        Choice,
        Multiline,
    }

    public class TtsField
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public string LabelKo { get; set; }
        public TtsFieldKind Kind { get; set; }
        public string Default { get; set; } = "";
        public string[] Choices { get; set; }
        // A short hint under the field (e.g. where to get a key).
        public string Hint { get; set; }
        public string HintKo { get; set; }
        // Changes how, not what, is said (e.g. CPU threads): not part of made speech's key.
        public bool NotInKey { get; set; }
    }

    // Mono samples (-1 to 1) at a rate, ready to mix.
    public class TtsClip
    {
        public float[] Samples { get; }
        public int SampleRate { get; }

        public TtsClip(float[] samples, int sampleRate)
        {
            Samples = samples;
            SampleRate = sampleRate;
        }

        public int Bytes => Samples.Length * sizeof(float);
    }
}
