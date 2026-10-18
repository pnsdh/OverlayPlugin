using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Threading;

namespace RainbowMage.OverlayPlugin.Tts
{
    // Fork: Windows' installed voices (SAPI 5, the ones ACT uses), synthesized to memory: no
    // network, a few tens of milliseconds for an alert.
    public class SapiProvider : ITtsProvider, IDisposable
    {
        public const string ProviderId = "sapi";
        private const int Rate = 24000;

        public string Id => ProviderId;
        public string Name => "Windows";

        private SpeechSynthesizer synth;
        private string selected;
        private List<TtsVoice> voices;

        public IReadOnlyList<TtsVoice> GetVoices()
        {
            if (voices == null)
            {
                using (var s = new SpeechSynthesizer())
                {
                    voices = s.GetInstalledVoices().Where(v => v.Enabled).Select(v => new TtsVoice
                    {
                        Id = v.VoiceInfo.Name,
                        Name = $"{v.VoiceInfo.Name} ({v.VoiceInfo.Culture.Name})",
                        Lang = v.VoiceInfo.Culture.TwoLetterISOLanguageName,
                    }).ToList();
                }
            }
            return voices;
        }

        public TtsClip Synthesize(string text, TtsVoice voice, int rate, CancellationToken cancel)
        {
            if (synth == null)
                synth = new SpeechSynthesizer();
            if (voice != null && voice.Id != selected)
            {
                synth.SelectVoice(voice.Id);
                selected = voice.Id;
            }
            synth.Rate = Math.Max(-10, Math.Min(10, rate));
            using (var stream = new MemoryStream())
            {
                synth.SetOutputToAudioStream(stream, new SpeechAudioFormatInfo(Rate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                try
                {
                    synth.Speak(text);
                }
                finally
                {
                    synth.SetOutputToNull();
                }
                var bytes = stream.GetBuffer();
                int count = (int)stream.Length / 2;
                var samples = new float[count];
                for (int i = 0; i < count; i++)
                    samples[i] = (short)(bytes[2 * i] | bytes[2 * i + 1] << 8) / 32768f;
                return new TtsClip(TrimSilence(samples), Rate);
            }
        }

        // SAPI pads speech with silence; the leading part is pure delay.
        internal static float[] TrimSilence(float[] samples)
        {
            const float threshold = 0.004f;
            int start = 0;
            while (start < samples.Length && Math.Abs(samples[start]) < threshold)
                start++;
            int end = samples.Length;
            while (end > start && Math.Abs(samples[end - 1]) < threshold)
                end--;
            // A little room on either side, so the first and last sounds are not cut.
            start = Math.Max(0, start - Rate / 200);
            end = Math.Min(samples.Length, end + Rate / 20);
            if (start == 0 && end == samples.Length)
                return samples;
            var trimmed = new float[end - start];
            Array.Copy(samples, start, trimmed, 0, trimmed.Length);
            return trimmed;
        }

        public void Dispose()
        {
            synth?.Dispose();
            synth = null;
        }
    }
}
