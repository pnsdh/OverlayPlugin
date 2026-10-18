using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace RainbowMage.OverlayPlugin.Tts
{
    // Fork: how much a paid voice was used, by provider (saved with the plugin config): characters
    // sent (what services bill), requests, how many were for fetching ahead, how many were not
    // sent as the speech was kept from before; this month, last month and in all.  With the user's
    // price an estimate, and with a monthly limit a stop (Windows' voice reads the rest of the
    // month), so a bill never surprises.
    public class TtsUsage
    {
        public string Month { get; set; } = "";
        public long Chars { get; set; }
        public long Requests { get; set; }
        public long PreparedChars { get; set; }
        public long Saved { get; set; }

        public string LastMonth { get; set; } = "";
        public long LastMonthChars { get; set; }

        public long TotalChars { get; set; }

        // The user's: price per million characters (their currency), 0 for none.
        public double PricePerMillion { get; set; }
        // The user's: characters a month before Windows' voice takes over, 0 for no limit.
        public long MonthlyLimit { get; set; }

        public static string ThisMonth => DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        // A new month: this one becomes last month's.
        public void Roll()
        {
            var now = ThisMonth;
            if (Month == now)
                return;
            if (Month != "")
            {
                LastMonth = Month;
                LastMonthChars = Chars;
            }
            Month = now;
            Chars = 0;
            Requests = 0;
            PreparedChars = 0;
            Saved = 0;
        }

        public double? Cost(long chars) => PricePerMillion > 0 ? chars * PricePerMillion / 1000000.0 : (double?)null;
    }

    // Counting, shared by the speaking and fetching-ahead threads.
    public class TtsUsageCounter
    {
        private readonly TtsConfig config;
        private readonly object sync = new object();

        // Something counted (for the settings to show it).
        public event Action Changed;

        public TtsUsageCounter(TtsConfig config)
        {
            this.config = config;
        }

        // Only voices that cost something are counted (not Windows' or Supertonic).
        public static bool Billed(ITtsProvider provider) => provider is RemoteProvider;

        // An entry for each paid voice made at start, so none is added while the config is being
        // saved (which reads the same dictionary on the UI thread).
        public void Prepare(IEnumerable<ITtsProvider> providers)
        {
            lock (sync)
            {
                foreach (var provider in providers)
                {
                    if (Billed(provider))
                        Get(provider);
                }
            }
        }

        public TtsUsage Get(ITtsProvider provider)
        {
            lock (sync)
            {
                if (config.Usage == null)
                    config.Usage = new Dictionary<string, TtsUsage>();
                if (!config.Usage.TryGetValue(provider.Id, out var usage) || usage == null)
                    config.Usage[provider.Id] = usage = new TtsUsage();
                usage.Roll();
                return usage;
            }
        }

        // Before sending: throws once the monthly limit would be passed (the caller says it with
        // Windows' voice instead, or stops fetching ahead).
        public void Check(ITtsProvider provider, string text)
        {
            if (!Billed(provider))
                return;
            lock (sync)
            {
                var usage = Get(provider);
                if (usage.MonthlyLimit > 0 && usage.Chars + text.Length > usage.MonthlyLimit)
                {
                    throw new InvalidOperationException(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko"
                        ? $"이번 달 한도({usage.MonthlyLimit:N0}자)에 닿아 Windows 음성으로 읽습니다"
                        : $"this month's limit ({usage.MonthlyLimit:N0} characters) is reached: Windows' voice reads instead");
                }
            }
        }

        public void Sent(ITtsProvider provider, string text, bool prepared)
        {
            if (!Billed(provider))
                return;
            lock (sync)
            {
                var usage = Get(provider);
                usage.Chars += text.Length;
                usage.TotalChars += text.Length;
                usage.Requests++;
                if (prepared)
                    usage.PreparedChars += text.Length;
            }
            Changed?.Invoke();
        }

        public void Kept(ITtsProvider provider)
        {
            if (!Billed(provider))
                return;
            lock (sync)
                Get(provider).Saved++;
            Changed?.Invoke();
        }

        public void Reset(ITtsProvider provider)
        {
            lock (sync)
            {
                var usage = Get(provider);
                usage.Chars = usage.Requests = usage.PreparedChars = usage.Saved = 0;
                usage.LastMonth = "";
                usage.LastMonthChars = 0;
                usage.TotalChars = 0;
            }
            Changed?.Invoke();
        }
    }

    // A provider that can tell its own count (with the user's key), e.g. ElevenLabs' characters of
    // the subscription.
    public interface ITtsUsageSource
    {
        string FetchUsage(TtsSettings settings, CancellationToken cancel);
    }
}
