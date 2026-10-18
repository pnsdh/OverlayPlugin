using System.Collections.Generic;
using System.Runtime.Serialization;
using Newtonsoft.Json;

namespace RainbowMage.OverlayPlugin.Tts
{
    // Fork: what reads alerts aloud (see TtsService), saved in the plugin config.
    public class TtsConfig
    {
        // Off (as it starts): everything goes to ACT's own TTS, as before.
        public bool Enabled { get; set; } = false;

        // Also read what ACT itself speaks (its custom triggers, other plugins), unless another
        // plugin has already taken ACT's TTS over (see ActTtsBridge).
        public bool TakeOverActTts { get; set; } = true;

        // When another plugin owns ACT's TTS: overlays' speech goes to it too (true), or to this
        // engine all the same (false).
        public bool DeferToOtherPlugin { get; set; } = true;

        public string Provider { get; set; } = SapiProvider.ProviderId;

        // A voice of the provider; empty: the first one for the alert language.
        public string Voice { get; set; } = "";

        // Times the voice's own speed (0.5 to 2); each engine holds it within what it can say.
        public double Speed { get; set; } = 1.0;

        // The old setting (-10 to 10), read from older configs into Speed (see Fix).
        public int Rate { get; set; } = 0;

        // 0 to 100: all speech, then by how important the alert is.
        public int Volume { get; set; } = 100;
        public int AlarmVolume { get; set; } = 100;
        public int AlertVolume { get; set; } = 100;
        public int InfoVolume { get; set; } = 100;
        public int OtherVolume { get; set; } = 100;

        // Each provider's own settings (address, key, voice...) by provider and field.  Secrets
        // are kept encrypted for this Windows user (see TtsSecrets).
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public Dictionary<string, Dictionary<string, string>> ProviderSettings { get; set; } = new Dictionary<string, Dictionary<string, string>>();

        // A voice over the network not ready this soon: Windows' voice says it now (the network's
        // is kept for next time).
        public int FallbackMs { get; set; } = 800;

        // On entering a zone, its alerts (as an overlay lists them) fetched ahead from a network
        // voice, so even the first time an alert plays at once.
        public bool PrepareAhead { get; set; } = true;

        // The same for a voice made on this PC (Supertonic): off unless asked for, as it would take
        // the CPU for minutes beside the game (its first time is quick enough anyway).
        public bool PrepareAheadLocal { get; set; } = false;

        // How much each paid voice was used (see TtsUsage), with the user's price and limit.
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public Dictionary<string, TtsUsage> Usage { get; set; } = new Dictionary<string, TtsUsage>();

        // Speech from the network kept on disk (megabytes), so an alert is fetched once.
        public int DiskCacheMB { get; set; } = 300;

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

        // Which built-in rules the saved ones are (0: before this was kept, or a new config).
        public int RulesVersion { get; set; }

        // A config edited by hand (or broken) with null lists: empty ones, rather than errors.
        [OnDeserialized]
        internal void Fix(StreamingContext context)
        {
            if (ProviderSettings == null)
                ProviderSettings = new Dictionary<string, Dictionary<string, string>>();
            foreach (var key in new List<string>(ProviderSettings.Keys))
            {
                if (ProviderSettings[key] == null)
                    ProviderSettings[key] = new Dictionary<string, string>();
            }
            if (Usage == null)
                Usage = new Dictionary<string, TtsUsage>();
            if (Replacements == null)
                Replacements = new List<TtsReplacement>();
            Replacements.RemoveAll(r => r == null);
            if (RulesVersion < TtsTextProcessor.RulesVersion)
            {
                Replacements = TtsTextProcessor.Upgrade(Replacements);
                RulesVersion = TtsTextProcessor.RulesVersion;
            }
            if (Provider == null)
                Provider = SapiProvider.ProviderId;
            if (Rate != 0)
            {
                if (System.Math.Abs(Speed - 1.0) < 1e-9)
                    Speed = System.Math.Round(System.Math.Pow(2, Rate / 10.0), 2);
                Rate = 0;
            }
            Speed = System.Math.Max(0.5, System.Math.Min(2.0, Speed));
            if (Voice == null)
                Voice = "";
            if (OutputDevice == null)
                OutputDevice = "";
        }
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
