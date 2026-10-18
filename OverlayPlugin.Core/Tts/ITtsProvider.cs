using System.Collections.Generic;
using System.Threading;

namespace RainbowMage.OverlayPlugin.Tts
{
    // Fork: a source of voices.  Synthesize is called on the TTS worker thread only, one call at a
    // time, so a provider needs no locking of its own.
    public interface ITtsProvider
    {
        string Id { get; }
        string Name { get; }

        IReadOnlyList<TtsVoice> GetVoices();

        // The speech, or null if this provider cannot say it (the caller falls back).
        TtsClip Synthesize(string text, TtsVoice voice, int rate, CancellationToken cancel);
    }

    public class TtsVoice
    {
        public string Id { get; set; }
        public string Name { get; set; }
        // Two-letter language ("ko", "en"...).
        public string Lang { get; set; }

        public override string ToString() => Name;
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
