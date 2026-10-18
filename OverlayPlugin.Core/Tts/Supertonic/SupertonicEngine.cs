using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Newtonsoft.Json.Linq;

namespace RainbowMage.OverlayPlugin.Tts.Supertonic
{
    // Fork: Supertone's Supertonic 3 (on-device multilingual TTS, ONNX), ported from its C# example
    // (github.com/supertone-inc/supertonic, csharp/Helper.cs, MIT) to .NET Framework: text to
    // unicode ids, duration predictor, text encoder, a few denoising steps of the vector estimator,
    // vocoder.  The model (OpenRAIL-M) is downloaded on request (see SupertonicModel).
    public sealed class SupertonicEngine : IDisposable
    {
        private static readonly string[] languages = { "en", "ko", "ja", "ar", "bg", "cs", "da", "de", "el", "es", "et", "fi", "fr", "hi", "hr", "hu", "id", "it", "lt", "lv", "nl", "pl", "pt", "ro", "ru", "sk", "sl", "sv", "tr", "uk", "vi", "na" };

        private readonly InferenceSession durationPredictor;
        private readonly InferenceSession textEncoder;
        private readonly InferenceSession vectorEstimator;
        private readonly InferenceSession vocoder;
        private readonly long[] indexer;
        private readonly int baseChunkSize;
        private readonly int chunkCompressFactor;
        private readonly int latentDim;
        private readonly Dictionary<string, Style> styles = new Dictionary<string, Style>();
        private readonly string folder;

        public int SampleRate { get; }

        public SupertonicEngine(string folder, int threads)
        {
            this.folder = folder;
            var onnx = Path.Combine(folder, "onnx");
            var cfg = JObject.Parse(File.ReadAllText(Path.Combine(onnx, "tts.json")));
            SampleRate = (int)cfg["ae"]["sample_rate"];
            baseChunkSize = (int)cfg["ae"]["base_chunk_size"];
            chunkCompressFactor = (int)cfg["ttl"]["chunk_compress_factor"];
            latentDim = (int)cfg["ttl"]["latent_dim"];
            indexer = JArray.Parse(File.ReadAllText(Path.Combine(onnx, "unicode_indexer.json"))).Select(v => (long)v).ToArray();

            // Few threads: it runs beside the game, a short alert needs little.
            var options = new SessionOptions
            {
                IntraOpNumThreads = Math.Max(1, threads),
                InterOpNumThreads = 1,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            durationPredictor = new InferenceSession(Path.Combine(onnx, "duration_predictor.onnx"), options);
            textEncoder = new InferenceSession(Path.Combine(onnx, "text_encoder.onnx"), options);
            vectorEstimator = new InferenceSession(Path.Combine(onnx, "vector_estimator.onnx"), options);
            vocoder = new InferenceSession(Path.Combine(onnx, "vocoder.onnx"), options);
        }

        private class Style
        {
            public float[] Ttl;
            public int[] TtlShape;
            public float[] Dp;
            public int[] DpShape;
        }

        private Style LoadStyle(string name)
        {
            lock (styles)
            {
                if (styles.TryGetValue(name, out var cached))
                    return cached;
                var json = JObject.Parse(File.ReadAllText(Path.Combine(folder, "voice_styles", name + ".json")));
                (float[], int[]) Read(JToken t)
                {
                    var dims = t["dims"].Select(d => (int)d).ToArray();
                    var data = t["data"].SelectMany(b => b.SelectMany(r => r.Select(v => (float)v))).ToArray();
                    return (data, dims);
                }
                var (ttl, ttlShape) = Read(json["style_ttl"]);
                var (dp, dpShape) = Read(json["style_dp"]);
                var style = new Style { Ttl = ttl, TtlShape = ttlShape, Dp = dp, DpShape = dpShape };
                styles[name] = style;
                return style;
            }
        }

        // The speech for a text: sentences of up to 120 characters (Korean, Japanese) or 300 said
        // one after another with a short pause, as the example does.
        public float[] Synthesize(string text, string lang, string voice, int steps, float speed)
        {
            lang = languages.Contains(lang) ? lang : "na";
            var style = LoadStyle(voice);
            var chunks = ChunkText(text, lang == "ko" || lang == "ja" ? 120 : 300);
            var all = new List<float>();
            foreach (var chunk in chunks)
            {
                if (all.Count > 0)
                    all.AddRange(new float[(int)(0.3f * SampleRate)]);
                all.AddRange(Infer(chunk, lang, style, steps, speed));
            }
            return all.ToArray();
        }

        private float[] Infer(string text, string lang, Style style, int steps, float speed)
        {
            var processed = Preprocess(text, lang);
            int length = processed.Length;
            var ids = new long[length];
            for (int i = 0; i < length; i++)
            {
                int c = processed[i];
                ids[i] = c < indexer.Length ? indexer[c] : 0;
            }
            var idsTensor = new DenseTensor<long>(ids, new[] { 1, length });
            var textMask = new DenseTensor<float>(Enumerable.Repeat(1f, length).ToArray(), new[] { 1, 1, length });
            var styleTtl = new DenseTensor<float>(style.Ttl, style.TtlShape);
            var styleDp = new DenseTensor<float>(style.Dp, style.DpShape);

            float duration;
            using (var outputs = durationPredictor.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("text_ids", idsTensor),
                NamedOnnxValue.CreateFromTensor("style_dp", styleDp),
                NamedOnnxValue.CreateFromTensor("text_mask", textMask),
            }))
            {
                duration = outputs.First(o => o.Name == "duration").AsTensor<float>().ToArray()[0] / speed;
            }

            using (var encoded = textEncoder.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("text_ids", idsTensor),
                NamedOnnxValue.CreateFromTensor("style_ttl", styleTtl),
                NamedOnnxValue.CreateFromTensor("text_mask", textMask),
            }))
            {
                var textEmb = encoded.First(o => o.Name == "text_emb").AsTensor<float>();

                // Noise in the latent space, as long as the speech will be.
                long wavLength = (long)(duration * SampleRate);
                int chunk = baseChunkSize * chunkCompressFactor;
                int latentLength = (int)((wavLength + chunk - 1) / chunk);
                int dim = latentDim * chunkCompressFactor;
                var random = new Random();
                var latent = new float[dim * latentLength];
                for (int i = 0; i < latent.Length; i++)
                {
                    double u1 = 1.0 - random.NextDouble();
                    double u2 = 1.0 - random.NextDouble();
                    latent[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
                }
                var latentMask = new DenseTensor<float>(Enumerable.Repeat(1f, latentLength).ToArray(), new[] { 1, 1, latentLength });
                var total = new DenseTensor<float>(new[] { (float)steps }, new[] { 1 });

                for (int step = 0; step < steps; step++)
                {
                    using (var outputs = vectorEstimator.Run(new[]
                    {
                        NamedOnnxValue.CreateFromTensor("noisy_latent", new DenseTensor<float>(latent, new[] { 1, dim, latentLength })),
                        NamedOnnxValue.CreateFromTensor("text_emb", textEmb),
                        NamedOnnxValue.CreateFromTensor("style_ttl", styleTtl),
                        NamedOnnxValue.CreateFromTensor("text_mask", textMask),
                        NamedOnnxValue.CreateFromTensor("latent_mask", latentMask),
                        NamedOnnxValue.CreateFromTensor("total_step", total),
                        NamedOnnxValue.CreateFromTensor("current_step", new DenseTensor<float>(new[] { (float)step }, new[] { 1 })),
                    }))
                    {
                        latent = outputs.First(o => o.Name == "denoised_latent").AsTensor<float>().ToArray();
                    }
                }

                using (var outputs = vocoder.Run(new[]
                {
                    NamedOnnxValue.CreateFromTensor("latent", new DenseTensor<float>(latent, new[] { 1, dim, latentLength })),
                }))
                {
                    var wav = outputs.First(o => o.Name == "wav_tts").AsTensor<float>().ToArray();
                    // The vocoder pads to whole chunks: only the predicted length is speech.
                    if (wavLength < wav.Length)
                        Array.Resize(ref wav, (int)wavLength);
                    return wav;
                }
            }
        }

        // As the example normalizes text (its replacements and the language tags it expects).
        private static string Preprocess(string text, string lang)
        {
            text = text.Normalize(NormalizationForm.FormKD);
            text = RemoveEmojis(text);
            foreach (var (from, to) in new[]
            {
                ("–", "-"), ("‑", "-"), ("—", "-"), ("_", " "), ("“", "\""), ("”", "\""),
                ("‘", "'"), ("’", "'"), ("´", "'"), ("`", "'"), ("[", " "), ("]", " "),
                ("|", " "), ("/", " "), ("#", " "), ("→", " "), ("←", " "),
            })
                text = text.Replace(from, to);
            text = Regex.Replace(text, @"[♥☆♡©\\]", "");
            text = text.Replace("@", " at ").Replace("e.g.,", "for example, ").Replace("i.e.,", "that is, ");
            foreach (var p in new[] { ",", ".", "!", "?", ";", ":", "'" })
                text = text.Replace(" " + p, p);
            while (text.Contains("\"\""))
                text = text.Replace("\"\"", "\"");
            while (text.Contains("''"))
                text = text.Replace("''", "'");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            // Ends with punctuation, a quote or a closing bracket (as the example lists them).
            if (!Regex.IsMatch(text, "[.!?;:,'\"“”‘’)\\]}…。」』】〉》›»]$"))
                text += ".";
            return $"<{lang}>{text}</{lang}>";
        }

        private static string RemoveEmojis(string text)
        {
            var result = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                int cp = text[i];
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    cp = char.ConvertToUtf32(text[i], text[++i]);
                bool emoji = (cp >= 0x1F300 && cp <= 0x1FAFF) || (cp >= 0x2600 && cp <= 0x27BF) || (cp >= 0x1F1E6 && cp <= 0x1F1FF);
                if (emoji)
                    continue;
                // A lone surrogate (broken text) as it is; ConvertFromUtf32 would throw.
                if (cp >= 0xD800 && cp <= 0xDFFF)
                    result.Append((char)cp);
                else
                    result.Append(char.ConvertFromUtf32(cp));
            }
            return result.ToString();
        }

        private static List<string> ChunkText(string text, int maxLength)
        {
            var chunks = new List<string>();
            var sentences = Regex.Split(text.Trim(), @"(?<=[.!?])\s+");
            var current = "";
            foreach (var sentence in sentences.Where(s => s != ""))
            {
                if (current.Length + sentence.Length + 1 <= maxLength)
                {
                    current = current == "" ? sentence : current + " " + sentence;
                }
                else
                {
                    if (current != "")
                        chunks.Add(current);
                    current = sentence;
                }
            }
            if (current != "")
                chunks.Add(current);
            if (chunks.Count == 0)
                chunks.Add(text.Trim());
            return chunks;
        }

        public void Dispose()
        {
            durationPredictor?.Dispose();
            textEncoder?.Dispose();
            vectorEstimator?.Dispose();
            vocoder?.Dispose();
        }
    }
}
