using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RainbowMage.OverlayPlugin.Tts
{
    // Fork: alert text made readable aloud: symbols that a voice reads badly or not at all become
    // words or pauses, then the user's own replacements.  The built-in rules come from what
    // cactbot's alerts actually contain (e.g. "a => b", "(3번)", "탱버 x2", "↖ 앞 왼쪽").
    public class TtsTextProcessor
    {
        private readonly List<(Regex Find, string Replace)> rules;
        private readonly bool readBrackets;

        public TtsTextProcessor(TtsConfig config, string lang)
        {
            readBrackets = config.ReadBrackets;
            rules = new List<(Regex, string)>();
            foreach (var r in config.Replacements ?? new List<TtsReplacement>())
            {
                if (!r.Enabled || string.IsNullOrEmpty(r.Find))
                    continue;
                if (!string.IsNullOrEmpty(r.Lang) && !string.Equals(r.Lang, lang, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    var pattern = r.Regex ? r.Find : Regex.Escape(r.Find);
                    rules.Add((new Regex(pattern, RegexOptions.CultureInvariant), r.Regex ? r.Replace ?? "" : (r.Replace ?? "").Replace("$", "$$")));
                }
                catch (ArgumentException)
                {
                    // A broken pattern is skipped (the settings show it as such).
                }
            }
        }

        public string Process(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            // Markup and links (descriptions) are never meant to be read.
            text = Regex.Replace(text, "<[^>]+>", " ");
            text = Regex.Replace(text, @"https?://\S+", " ");
            if (!readBrackets)
                text = Regex.Replace(text, @"\([^)]*\)|（[^）]*）|\[[^\]]*\]", " ");
            foreach (var (find, replace) in rules)
                text = find.Replace(text, replace);
            // Pauses made by the rules, tidied.
            text = Regex.Replace(text, @"\s*,\s*(,\s*)+", ", ");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text.Trim(',', ' ');
        }

        public static bool IsValidPattern(string pattern)
        {
            try
            {
                _ = new Regex(pattern);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        // Korean native numbers for counts ("x3" is "세 번", not "삼 번").
        private static readonly string[] koCounts = { "", "한", "두", "세", "네", "다섯", "여섯", "일곱", "여덟", "아홉" };

        public static List<TtsReplacement> DefaultReplacements()
        {
            var list = new List<TtsReplacement>();
            void Add(string find, string replace, bool regex, string lang, string note) =>
                list.Add(new TtsReplacement { Find = find, Replace = replace, Regex = regex, Lang = lang, BuiltIn = true, Note = note });

            // Sequences: "a => b", "a -> b", "a → b".
            Add(@"\s*(=>|->|→|⇒)\s*", " 다음 ", true, "ko", "a => b: 다음");
            Add(@"\s*(=>|->|→|⇒)\s*", " then ", true, "en", "a => b: then");
            Add(@"\s*(<=|<-|←)\s*", ", ", true, "", "a <= b: pause");
            // Counts: "x2" .. "x9".
            for (int n = 1; n <= 9; n++)
                Add($@"(?<![A-Za-z0-9])[xX×]\s*{n}(?![0-9])", $" {koCounts[n]} 번", true, "ko", $"x{n}: {koCounts[n]} 번");
            Add(@"(?<![A-Za-z0-9])[xX×]\s*(\d+)(?![0-9])", " $1 times", true, "en", "x2: 2 times");
            // Joiners.
            Add(@"\s*\+\s*", " 그리고 ", true, "ko", "a + b: 그리고");
            Add(@"\s*\+\s*", " and ", true, "en", "a + b: and");
            Add(@"\s*/\s*", " 또는 ", true, "ko", "a / b: 또는");
            Add(@"\s*/\s*", " or ", true, "en", "a / b: or");
            Add("°", "도", false, "ko", "45°: 도");
            Add("°", " degrees", false, "en", "45°: degrees");
            // Pauses.
            Add(@"\s*[:：;·]\s*", ", ", true, "", ": ; · pause");
            Add(@"\s*[\(（\[]\s*", ", ", true, "", "( pause");
            Add(@"\s*[\)）\]]\s*", ", ", true, "", ") pause");
            // Not read: arrows drawn beside the words, emphasis and markers.
            Add(@"[↖↗↘↙↑↓]", " ", true, "", "drawn arrows");
            Add(@"[~#""'`*_|]+", " ", true, "", "~ # \" ' marks");
            Add(@"\{\{CD\}\}", " ", true, "", "countdown marker");
            return list;
        }
    }
}
