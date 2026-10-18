using System;
using System.IO;
using System.Net.Http;
using System.Text;
using NAudio.Wave;

namespace RainbowMage.OverlayPlugin.Tts
{
    // Fork: audio as services send it, made into a clip: WAV (PCM or float), raw 16-bit PCM at a
    // known rate, or anything Windows can decode (MP3, AAC...).
    public static class TtsAudio
    {
        public static TtsClip Decode(byte[] data, int rawPcmRate = 0)
        {
            if (data == null || data.Length < 4)
                throw new InvalidDataException("no audio in the answer");
            if (data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F')
                return FromReader(new WaveFileReader(new MemoryStream(data)));
            if (rawPcmRate > 0 && !LooksLikeText(data))
                return FromPcm16(data, 0, data.Length, rawPcmRate);
            if (LooksLikeText(data))
                throw new InvalidDataException(Snippet(data));
            // MP3 and others, through Windows' own decoders.
            return FromReader(new StreamMediaFoundationReader(new MemoryStream(data)));
        }

        public static TtsClip FromPcm16(byte[] data, int offset, int length, int rate)
        {
            int count = length / 2;
            var samples = new float[count];
            for (int i = 0; i < count; i++)
                samples[i] = (short)(data[offset + 2 * i] | data[offset + 2 * i + 1] << 8) / 32768f;
            return new TtsClip(samples, rate);
        }

        private static TtsClip FromReader(WaveStream reader)
        {
            using (reader)
            {
                var provider = reader.ToSampleProvider();
                int channels = provider.WaveFormat.Channels;
                var all = new System.Collections.Generic.List<float>();
                var buffer = new float[provider.WaveFormat.SampleRate * channels];
                int read;
                while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
                {
                    // Mono: the channels averaged.
                    for (int i = 0; i + channels <= read; i += channels)
                    {
                        float sum = 0;
                        for (int c = 0; c < channels; c++)
                            sum += buffer[i + c];
                        all.Add(sum / channels);
                    }
                }
                return new TtsClip(all.ToArray(), provider.WaveFormat.SampleRate);
            }
        }

        // An error page or JSON instead of audio.
        private static bool LooksLikeText(byte[] data)
        {
            int n = Math.Min(data.Length, 64);
            for (int i = 0; i < n; i++)
            {
                byte b = data[i];
                if (b < 9 || (b > 13 && b < 32))
                    return false;
            }
            return data[0] == '{' || data[0] == '<' || data[0] == '[';
        }

        public static string Snippet(byte[] data)
        {
            var text = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 300));
            return text.Replace("\r", " ").Replace("\n", " ");
        }
    }

    // Fork: one HTTP client for all providers (connections kept open, so an alert does not wait
    // for a new TLS handshake each time).
    public static class TtsHttp
    {
        public static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // For big downloads (a model): no time limit, cancelled by the user instead.
        public static readonly HttpClient LongClient = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        static TtsHttp()
        {
            Client.DefaultRequestHeaders.UserAgent.ParseAdd("OverlayPlugin-TTS/1.0");
        }

        // For tools/TtsBench (set by reflection): answers requests instead of the network, to see
        // what each service is sent.
        internal static Func<HttpRequestMessage, byte[]> Intercept = null;

        // The answer's bytes; a failure as an exception with the service's own message.
        public static byte[] Send(HttpRequestMessage request, System.Threading.CancellationToken cancel)
        {
            var intercept = Intercept;
            if (intercept != null)
                return intercept(request);
            // .NET Framework allows 2 connections per host by default: fetching ahead (2 at a time)
            // would make a live alert wait for a free one.
            try
            {
                var point = System.Net.ServicePointManager.FindServicePoint(request.RequestUri);
                if (point.ConnectionLimit < 6)
                    point.ConnectionLimit = 6;
            }
            catch (Exception)
            {
            }
            using (var response = Client.SendAsync(request, cancel).GetAwaiter().GetResult())
            {
                var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    var retry = response.Headers.RetryAfter;
                    TimeSpan? after = retry?.Delta ?? (retry?.Date != null ? retry.Date.Value - DateTimeOffset.UtcNow : (TimeSpan?)null);
                    var body = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 8192));
                    throw new TtsHttpException((int)response.StatusCode, after, $"{(int)response.StatusCode} {response.ReasonPhrase}: {TtsAudio.Snippet(bytes)}", body);
                }
                return bytes;
            }
        }
    }

    // A service's refusal: its status and, if it said so, when to ask again.
    public class TtsHttpException : HttpRequestException
    {
        public int Status { get; }
        public TimeSpan? RetryAfter { get; }
        // The answer (up to 8 KB; the message has only its start, for the log).
        public string Body { get; }

        public TtsHttpException(int status, TimeSpan? retryAfter, string message, string body = "") : base(message)
        {
            Status = status;
            RetryAfter = retryAfter;
            Body = body ?? "";
        }
    }
}
