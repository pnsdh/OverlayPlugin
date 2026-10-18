using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace RainbowMage.OverlayPlugin.Tts
{
    // Fork: voices over the network, each with the user's own key (the plugin pays for nothing).
    // All ask for uncompressed audio where the service offers it: nothing to decode, the clip is
    // ready at once.
    public abstract class RemoteProvider : ITtsProvider, ITtsVoiceSource
    {
        public abstract string Id { get; }
        public abstract string Name { get; }
        public bool IsRemote => true;
        public abstract IReadOnlyList<TtsField> Fields { get; }

        // The voice set in the settings (remote voices are not listed until fetched).
        public virtual IReadOnlyList<TtsVoice> GetVoices(TtsSettings settings)
        {
            var id = settings["voice"];
            return string.IsNullOrEmpty(id) ? new TtsVoice[0] : new[] { new TtsVoice { Id = id, Name = id, Lang = LangOf(id) } };
        }

        public abstract TtsClip Synthesize(TtsSynthesis request, CancellationToken cancel);

        public virtual IReadOnlyList<TtsVoice> FetchVoices(TtsSettings settings, CancellationToken cancel) => null;

        // "ko-KR-SunHiNeural" is Korean; an id without a language ("alloy") says nothing.
        protected static string LangOf(string voiceId)
        {
            if (voiceId != null && voiceId.Length >= 5 && voiceId[2] == '-' && char.IsLetter(voiceId[0]) && char.IsLetter(voiceId[1]))
                return voiceId.Substring(0, 2).ToLowerInvariant();
            return null;
        }

        protected static HttpRequestMessage Json(HttpMethod method, string url, JObject body)
        {
            return new HttpRequestMessage(method, url)
            {
                Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"),
            };
        }

        protected static TtsField Field(string key, string label, string labelKo, TtsFieldKind kind = TtsFieldKind.Text, string def = "", string hint = null, string hintKo = null, string[] choices = null) =>
            new TtsField { Key = key, Label = label, LabelKo = labelKo, Kind = kind, Default = def, Hint = hint, HintKo = hintKo, Choices = choices };

        protected static string Require(TtsSettings settings, string key, string what)
        {
            var value = settings[key];
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"{what} not set");
            return value.Trim();
        }
    }

    // OpenAI's speech API, and every service and local server that copies it (Kokoro-FastAPI,
    // openedai-speech, LocalAI...): an address, maybe a key, a model and a voice.
    public class OpenAiCompatibleProvider : RemoteProvider
    {
        public override string Id => "openai";
        public override string Name => "OpenAI / compatible (/v1/audio/speech)";

        public override IReadOnlyList<TtsField> Fields { get; } = new[]
        {
            Field("url", "API address", "API 주소", def: "https://api.openai.com/v1",
                hint: "OpenAI, or a compatible server (e.g. http://localhost:8880/v1 for a local Kokoro-FastAPI)",
                hintKo: "OpenAI, 또는 호환 서버 (예: 내 PC의 Kokoro-FastAPI는 http://localhost:8880/v1)"),
            Field("key", "API key", "API 키", TtsFieldKind.Secret, hint: "Empty for a local server without one", hintKo: "키가 없는 로컬 서버면 비워 두기"),
            Field("model", "Model", "모델", def: "gpt-4o-mini-tts"),
            Field("voice", "Voice", "목소리", def: "alloy"),
            Field("instructions", "How to speak (models that take it)", "말투 지시 (지원하는 모델만)", TtsFieldKind.Multiline,
                hint: "e.g. \"Calm, clear, quick raid callouts.\"", hintKo: "예: \"차분하고 또렷하게, 레이드 콜처럼 짧게\""),
        };

        public override TtsClip Synthesize(TtsSynthesis request, CancellationToken cancel)
        {
            var s = request.Settings;
            var body = new JObject
            {
                ["model"] = Require(s, "model", "model"),
                ["input"] = request.Text,
                ["voice"] = Require(s, "voice", "voice"),
                ["response_format"] = "wav",
                ["speed"] = Math.Round(Math.Max(0.25, Math.Min(4, request.Speed)), 2),
            };
            if (!string.IsNullOrWhiteSpace(s["instructions"]))
                body["instructions"] = s["instructions"];
            var message = Json(HttpMethod.Post, Require(s, "url", "API address").TrimEnd('/') + "/audio/speech", body);
            if (!string.IsNullOrWhiteSpace(s["key"]))
                message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", s["key"].Trim());
            return TtsAudio.Decode(TtsHttp.Send(message, cancel));
        }

        public override IReadOnlyList<TtsVoice> FetchVoices(TtsSettings settings, CancellationToken cancel)
        {
            // OpenAI has no list; compatible servers often do (Kokoro-FastAPI: /audio/voices).
            try
            {
                var message = new HttpRequestMessage(HttpMethod.Get, Require(settings, "url", "API address").TrimEnd('/') + "/audio/voices");
                if (!string.IsNullOrWhiteSpace(settings["key"]))
                    message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings["key"].Trim());
                var json = JToken.Parse(Encoding.UTF8.GetString(TtsHttp.Send(message, cancel)));
                var list = (json is JObject o ? o["voices"] : json) as JArray;
                if (list != null)
                    return list.Select(v => v.Type == JTokenType.String ? (string)v : (string)(v["id"] ?? v["name"]))
                        .Where(v => !string.IsNullOrEmpty(v)).Select(v => new TtsVoice { Id = v, Name = v }).ToList();
            }
            catch (HttpRequestException)
            {
            }
            return new[] { "alloy", "ash", "ballad", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer", "verse" }
                .Select(v => new TtsVoice { Id = v, Name = v }).ToList();
        }
    }

    // Azure AI Speech: Korean neural voices (선희, 인준...), speaking styles; a free tier of
    // 500,000 characters a month.
    public class AzureProvider : RemoteProvider
    {
        public override string Id => "azure";
        public override string Name => "Azure AI Speech";

        public override IReadOnlyList<TtsField> Fields { get; } = new[]
        {
            Field("region", "Region", "지역", def: "koreacentral", hint: "Of your Speech resource (e.g. koreacentral, eastus)", hintKo: "Speech 리소스의 지역 (예: koreacentral, eastus)"),
            Field("key", "Key", "키", TtsFieldKind.Secret, hint: "Azure portal > Speech resource > Keys", hintKo: "Azure 포털 > Speech 리소스 > 키"),
            Field("voice", "Voice", "목소리", def: "ko-KR-SunHiNeural"),
            Field("style", "Style (voices that have it)", "말투 (지원하는 목소리만)", hint: "e.g. cheerful, serious; empty for none", hintKo: "예: cheerful, serious. 비우면 기본"),
        };

        private static string Host(TtsSettings s) => $"https://{Require(s, "region", "region")}.tts.speech.microsoft.com";

        public override TtsClip Synthesize(TtsSynthesis request, CancellationToken cancel)
        {
            var s = request.Settings;
            var voice = Require(s, "voice", "voice");
            var lang = voice.Length >= 5 ? voice.Substring(0, 5) : "ko-KR";
            string rate = ((int)Math.Round((request.Speed - 1) * 100)).ToString("+0;-0", CultureInfo.InvariantCulture) + "%";
            string text = $"<prosody rate=\"{rate}\">{SecurityElement.Escape(request.Text)}</prosody>";
            if (!string.IsNullOrWhiteSpace(s["style"]))
                text = $"<mstts:express-as style=\"{SecurityElement.Escape(s["style"].Trim())}\">{text}</mstts:express-as>";
            var ssml = $"<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xmlns:mstts=\"https://www.w3.org/2001/mstts\" xml:lang=\"{lang}\"><voice name=\"{SecurityElement.Escape(voice)}\">{text}</voice></speak>";
            var message = new HttpRequestMessage(HttpMethod.Post, Host(s) + "/cognitiveservices/v1")
            {
                Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml"),
            };
            message.Headers.Add("Ocp-Apim-Subscription-Key", Require(s, "key", "key"));
            message.Headers.Add("X-Microsoft-OutputFormat", "riff-24khz-16bit-mono-pcm");
            return TtsAudio.Decode(TtsHttp.Send(message, cancel));
        }

        public override IReadOnlyList<TtsVoice> FetchVoices(TtsSettings settings, CancellationToken cancel)
        {
            var message = new HttpRequestMessage(HttpMethod.Get, Host(settings) + "/cognitiveservices/voices/list");
            message.Headers.Add("Ocp-Apim-Subscription-Key", Require(settings, "key", "key"));
            var list = JArray.Parse(Encoding.UTF8.GetString(TtsHttp.Send(message, cancel)));
            return list.Select(v => new TtsVoice
            {
                Id = (string)v["ShortName"],
                Name = $"{v["ShortName"]} ({v["LocalName"]}, {v["Gender"]})",
                Lang = LangOf((string)v["ShortName"]),
            }).ToList();
        }
    }

    // Google Cloud Text-to-Speech (Neural2, WaveNet, Chirp 3 HD...), with an API key.
    public class GoogleProvider : RemoteProvider
    {
        public override string Id => "google";
        public override string Name => "Google Cloud Text-to-Speech";

        public override IReadOnlyList<TtsField> Fields { get; } = new[]
        {
            Field("key", "API key", "API 키", TtsFieldKind.Secret, hint: "Google Cloud console > APIs > Credentials (Text-to-Speech API enabled)", hintKo: "Google Cloud 콘솔 > API 및 서비스 > 사용자 인증 정보 (Text-to-Speech API 사용 설정)"),
            Field("voice", "Voice", "목소리", def: "ko-KR-Neural2-A"),
        };

        public override TtsClip Synthesize(TtsSynthesis request, CancellationToken cancel)
        {
            var s = request.Settings;
            var voice = Require(s, "voice", "voice");
            var body = new JObject
            {
                ["input"] = new JObject { ["text"] = request.Text },
                ["voice"] = new JObject { ["languageCode"] = voice.Length >= 5 ? voice.Substring(0, 5) : "ko-KR", ["name"] = voice },
                ["audioConfig"] = new JObject
                {
                    ["audioEncoding"] = "LINEAR16",
                    ["sampleRateHertz"] = 24000,
                    ["speakingRate"] = Math.Round(Math.Max(0.25, Math.Min(4, request.Speed)), 2),
                },
            };
            var url = "https://texttospeech.googleapis.com/v1/text:synthesize?key=" + Uri.EscapeDataString(Require(s, "key", "API key"));
            var json = JObject.Parse(Encoding.UTF8.GetString(TtsHttp.Send(Json(HttpMethod.Post, url, body), cancel)));
            return TtsAudio.Decode(Convert.FromBase64String((string)json["audioContent"] ?? ""));
        }

        public override IReadOnlyList<TtsVoice> FetchVoices(TtsSettings settings, CancellationToken cancel)
        {
            var url = "https://texttospeech.googleapis.com/v1/voices?key=" + Uri.EscapeDataString(Require(settings, "key", "API key"));
            var json = JObject.Parse(Encoding.UTF8.GetString(TtsHttp.Send(new HttpRequestMessage(HttpMethod.Get, url), cancel)));
            return ((JArray)json["voices"] ?? new JArray()).Select(v => new TtsVoice
            {
                Id = (string)v["name"],
                Name = $"{v["name"]} ({v["ssmlGender"]})",
                Lang = LangOf((string)v["name"]),
            }).ToList();
        }
    }

    // ElevenLabs: many voices (and your own cloned ones); Flash models answer quickly.
    public class ElevenLabsProvider : RemoteProvider
    {
        public override string Id => "elevenlabs";
        public override string Name => "ElevenLabs";

        public override IReadOnlyList<TtsField> Fields { get; } = new[]
        {
            Field("key", "API key", "API 키", TtsFieldKind.Secret, hint: "elevenlabs.io > Developers > API Keys", hintKo: "elevenlabs.io > Developers > API Keys"),
            Field("voice", "Voice ID", "목소리 ID", hint: "Fetch the list, or paste a voice's ID", hintKo: "목록을 불러오거나 목소리 ID 붙여 넣기"),
            Field("model", "Model", "모델", def: "eleven_flash_v2_5"),
        };

        public override TtsClip Synthesize(TtsSynthesis request, CancellationToken cancel)
        {
            var s = request.Settings;
            var body = new JObject
            {
                ["text"] = request.Text,
                ["model_id"] = Require(s, "model", "model"),
                ["voice_settings"] = new JObject { ["speed"] = Math.Round(Math.Max(0.7, Math.Min(1.2, request.Speed)), 2) },
            };
            if (!string.IsNullOrEmpty(request.Lang))
                body["language_code"] = request.Lang;
            var url = $"https://api.elevenlabs.io/v1/text-to-speech/{Uri.EscapeDataString(Require(s, "voice", "voice ID"))}?output_format=pcm_24000";
            var message = Json(HttpMethod.Post, url, body);
            message.Headers.Add("xi-api-key", Require(s, "key", "API key"));
            return TtsAudio.Decode(TtsHttp.Send(message, cancel), 24000);
        }

        public override IReadOnlyList<TtsVoice> FetchVoices(TtsSettings settings, CancellationToken cancel)
        {
            var message = new HttpRequestMessage(HttpMethod.Get, "https://api.elevenlabs.io/v1/voices");
            message.Headers.Add("xi-api-key", Require(settings, "key", "API key"));
            var json = JObject.Parse(Encoding.UTF8.GetString(TtsHttp.Send(message, cancel)));
            return ((JArray)json["voices"] ?? new JArray()).Select(v => new TtsVoice
            {
                Id = (string)v["voice_id"],
                Name = $"{v["name"]} ({v["voice_id"]})",
            }).ToList();
        }
    }

    // NAVER CLOVA Voice (Premium): voices made for Korean.
    public class ClovaProvider : RemoteProvider
    {
        public override string Id => "clova";
        public override string Name => "NAVER CLOVA Voice";

        public override IReadOnlyList<TtsField> Fields { get; } = new[]
        {
            Field("url", "API address", "API 주소", def: "https://naveropenapi.apigw.ntruss.com/tts-premium/v1/tts"),
            Field("id", "Client ID", "Client ID", hint: "NAVER Cloud console > Application (CLOVA Voice selected)", hintKo: "네이버 클라우드 콘솔 > Application 등록 (CLOVA Voice 선택)"),
            Field("key", "Client Secret", "Client Secret", TtsFieldKind.Secret),
            Field("voice", "Speaker", "목소리 (speaker)", def: "nara", hint: "e.g. nara, nminyoung, vdain, ndain", hintKo: "예: nara, nminyoung, vdain, ndain"),
        };

        public override IReadOnlyList<TtsVoice> GetVoices(TtsSettings settings)
        {
            var id = settings["voice"];
            return string.IsNullOrEmpty(id) ? new TtsVoice[0] : new[] { new TtsVoice { Id = id, Name = id, Lang = "ko" } };
        }

        public override TtsClip Synthesize(TtsSynthesis request, CancellationToken cancel)
        {
            var s = request.Settings;
            // CLOVA: -5 fastest to 5 slowest.
            int speed = Math.Max(-5, Math.Min(5, -(int)Math.Round(request.Rate / 2.0)));
            var message = new HttpRequestMessage(HttpMethod.Post, Require(s, "url", "API address"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["speaker"] = Require(s, "voice", "speaker"),
                    ["text"] = request.Text,
                    ["speed"] = speed.ToString(CultureInfo.InvariantCulture),
                    ["format"] = "wav",
                    ["sampling-rate"] = "24000",
                }),
            };
            message.Headers.Add("X-NCP-APIGW-API-KEY-ID", Require(s, "id", "Client ID"));
            message.Headers.Add("X-NCP-APIGW-API-KEY", Require(s, "key", "Client Secret"));
            return TtsAudio.Decode(TtsHttp.Send(message, cancel));
        }
    }

    // Any other service: the request written out with placeholders.  {text} {text_json}
    // {text_url} {text_xml} {voice} {speed} {rate} {lang} {key}.
    public class HttpTemplateProvider : RemoteProvider
    {
        public override string Id => "http";
        public override string Name => T("Other service (HTTP request)", "다른 서비스 (HTTP 요청 직접 작성)");

        private static string T(string en, string ko) => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko" ? ko : en;

        public override IReadOnlyList<TtsField> Fields { get; } = new[]
        {
            Field("method", "Method", "방식", TtsFieldKind.Choice, "POST", choices: new[] { "POST", "GET" }),
            Field("url", "Address", "주소", hint: "Placeholders: {text} {text_json} {text_url} {text_xml} {voice} {speed} {rate} {lang} {key}",
                hintKo: "자리 표시자: {text} {text_json} {text_url} {text_xml} {voice} {speed} {rate} {lang} {key}"),
            Field("headers", "Headers (one \"Name: value\" a line)", "헤더 (한 줄에 \"이름: 값\")", TtsFieldKind.Multiline, hint: "e.g. Authorization: Bearer {key}", hintKo: "예: Authorization: Bearer {key}"),
            Field("body", "Body", "본문", TtsFieldKind.Multiline, hint: "e.g. {\"text\": \"{text_json}\", \"voice\": \"{voice}\"}", hintKo: "예: {\"text\": \"{text_json}\", \"voice\": \"{voice}\"}"),
            Field("contentType", "Body type", "본문 형식", def: "application/json"),
            Field("response", "Answer", "응답", TtsFieldKind.Choice, "audio", choices: new[] { "audio", "json-base64" },
                hint: "audio: the audio itself (WAV, MP3...); json-base64: base64 audio in a JSON field", hintKo: "audio: 음성 파일 그대로 (WAV, MP3 등), json-base64: JSON 안의 base64 음성"),
            Field("jsonPath", "JSON field (json-base64)", "JSON 필드 (json-base64일 때)", def: "audioContent", hint: "e.g. audioContent, data.audio", hintKo: "예: audioContent, data.audio"),
            Field("pcmRate", "Raw PCM rate (if it sends 16-bit PCM without a header)", "헤더 없는 16비트 PCM이면 샘플레이트", hint: "e.g. 24000; empty otherwise", hintKo: "예: 24000. 아니면 비우기"),
            Field("voice", "Voice", "목소리"),
            Field("key", "Key ({key})", "키 ({key})", TtsFieldKind.Secret),
        };

        public override TtsClip Synthesize(TtsSynthesis request, CancellationToken cancel)
        {
            var s = request.Settings;
            string Fill(string template, bool url) => (template ?? "")
                .Replace("{text_json}", JsonString(request.Text))
                .Replace("{text_url}", Uri.EscapeDataString(request.Text))
                .Replace("{text_xml}", SecurityElement.Escape(request.Text))
                .Replace("{text}", url ? Uri.EscapeDataString(request.Text) : request.Text)
                .Replace("{voice}", url ? Uri.EscapeDataString(s["voice"]) : s["voice"])
                .Replace("{speed}", request.Speed.ToString("0.##", CultureInfo.InvariantCulture))
                .Replace("{rate}", request.Rate.ToString(CultureInfo.InvariantCulture))
                .Replace("{lang}", request.Lang ?? "")
                .Replace("{key}", s["key"]);
            var method = s["method"] == "GET" ? HttpMethod.Get : HttpMethod.Post;
            var message = new HttpRequestMessage(method, Fill(Require(s, "url", "address"), true));
            if (method == HttpMethod.Post)
                message.Content = new StringContent(Fill(s["body"], false), Encoding.UTF8, string.IsNullOrWhiteSpace(s["contentType"]) ? "application/json" : s["contentType"].Trim());
            foreach (var line in (s["headers"] ?? "").Split('\n'))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;
                var name = line.Substring(0, colon).Trim();
                var value = Fill(line.Substring(colon + 1).Trim(), false);
                if (!message.Headers.TryAddWithoutValidation(name, value))
                    message.Content?.Headers.TryAddWithoutValidation(name, value);
            }
            var bytes = TtsHttp.Send(message, cancel);
            int.TryParse(s["pcmRate"], out int pcmRate);
            if (s["response"] == "json-base64")
            {
                JToken token = JToken.Parse(Encoding.UTF8.GetString(bytes));
                foreach (var part in (s["jsonPath"] ?? "").Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
                    token = token?[part];
                bytes = Convert.FromBase64String((string)token ?? "");
            }
            return TtsAudio.Decode(bytes, pcmRate);
        }

        private static string JsonString(string text)
        {
            var quoted = Newtonsoft.Json.JsonConvert.ToString(text);
            return quoted.Substring(1, quoted.Length - 2);
        }
    }
}
