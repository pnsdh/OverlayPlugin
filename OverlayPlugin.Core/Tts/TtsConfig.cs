using System.Collections.Generic;
using Newtonsoft.Json;

namespace RainbowMage.OverlayPlugin.Tts
{
    // Fork: what reads alerts aloud (see TtsService), saved in the plugin config.
    public class TtsConfig
    {
        // Off: everything goes to ACT's own TTS, as before.
        public bool Enabled { get; set; } = true;

        // Also read what ACT itself speaks (its custom triggers, other plugins), unless another
        // plugin has already taken ACT's TTS over (see ActTtsBridge).
        public bool TakeOverActTts { get; set; } = true;

        // When another plugin owns ACT's TTS: overlays' speech goes to it too (true), or to this
        // engine all the same (false).
        public bool DeferToOtherPlugin { get; set; } = true;

        public string Provider { get; set; } = SapiProvider.ProviderId;

        // A voice of the provider; empty: the first one for the alert language.
        public string Voice { get; set; } = "";

        // -10 (slowest) to 10 (fastest), 0 the voice's own speed.
        public int Rate { get; set; } = 0;

        // 0 to 100: all speech, then by how important the alert is.
        public int Volume { get; set; } = 100;
        public int AlarmVolume { get; set; } = 100;
        public int AlertVolume { get; set; } = 100;
        public int InfoVolume { get; set; } = 100;
        public int OtherVolume { get; set; } = 100;

        // An output device's ID (Windows' audio endpoint); empty: the default one.
        public string OutputDevice { get; set; } = "";

        public TtsQueueMode QueueMode { get; set; } = TtsQueueMode.InterruptLower;

        // Speech not started this long after it was asked for is dropped: a late alert misleads.
        public int MaxWaitMs { get; set; } = 3000;

        // The same text again within this time is not read twice.
        public int RepeatWindowMs { get; set; } = 1500;

        // Text in brackets is read (after a short pause) rather than left out.
        public bool ReadBrackets { get; set; } = true;

        // Replacements made before reading, in order: the built-in rules for symbols, then the
        // user's own words.  Replace, not merge, when loading (the defaults are only for a new
        // config).
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<TtsReplacement> Replacements { get; set; } = TtsTextProcessor.DefaultReplacements();
    }

    public enum TtsQueueMode
    {
        // One after another.
        Queue,
        // A more important alert cuts in; others wait.
        InterruptLower,
        // Whatever comes last is read at once.
        InterruptAll,
    }

    public class TtsReplacement
    {
        public bool Enabled { get; set; } = true;
        public string Find { get; set; } = "";
        public string Replace { get; set; } = "";
        // Find is a regular expression (Replace can use $1...).
        public bool Regex { get; set; }
        // Only for voices of this language ("ko", "en"...); empty: for all.
        public string Lang { get; set; } = "";
        // Built-in rules can be turned off but are told apart from the user's.
        public bool BuiltIn { get; set; }
        public string Note { get; set; } = "";
    }

    public enum TtsSeverity
    {
        // ACT's own TTS and anything that does not say.
        Other,
        Info,
        Alert,
        Alarm,
    }

    public static class TtsSeverityParser
    {
        // As overlays say it (cactbot's alert kinds).
        public static TtsSeverity Parse(string severity)
        {
            switch ((severity ?? "").ToLowerInvariant())
            {
                case "alarm": return TtsSeverity.Alarm;
                case "alert": return TtsSeverity.Alert;
                case "info": return TtsSeverity.Info;
                default: return TtsSeverity.Other;
            }
        }
    }
}
