using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RainbowMage.OverlayPlugin.Tts
{
    // A limit stopped a text from being made (the monthly one, or a service that said it is over
    // its own): told apart from failures in the stats.
    public class TtsLimitException : InvalidOperationException
    {
        public TtsLimitException(string message) : base(message)
        {
        }
    }

    // Fork: how a slow voice (network, Supertonic) did since ACT started (or the last reset): its
    // alerts read from made speech, made in time, or read by Windows' voice instead (late, failed,
    // or the service paused), and how long making one took.  For deciding whether making ahead is
    // needed (e.g. a fight played with it off).
    public class TtsStats
    {
        private readonly object sync = new object();
        private readonly List<double> makeMs = new List<double>();
        private string voice;
        private DateTime since = DateTime.Now;
        private int fromMade, inTime, late, failed, paused;

        public void Reset()
        {
            lock (sync)
            {
                makeMs.Clear();
                voice = null;
                since = DateTime.Now;
                fromMade = inTime = late = failed = paused = 0;
            }
        }

        // Counted for one voice at a time: another one chosen starts again.
        private void For(string name)
        {
            if (voice == name)
                return;
            if (voice != null)
            {
                makeMs.Clear();
                since = DateTime.Now;
                fromMade = inTime = late = failed = paused = 0;
            }
            voice = name;
        }

        // How long making a text took (not the preview, not making ahead).
        public void Made(string name, double ms)
        {
            lock (sync)
            {
                For(name);
                // The last 2000 are enough for the median and the slowest tenth.
                if (makeMs.Count >= 2000)
                    makeMs.RemoveAt(0);
                makeMs.Add(ms);
            }
        }

        public void Read(string name, bool fromCache, bool fellBack, bool pausedBy, bool error)
        {
            lock (sync)
            {
                For(name);
                if (fromCache)
                    fromMade++;
                else if (pausedBy)
                    paused++;
                else if (error)
                    failed++;
                else if (fellBack)
                    late++;
                else
                    inTime++;
            }
        }

        public string Describe()
        {
            bool ko = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";
            lock (sync)
            {
                int total = fromMade + inTime + late + failed + paused;
                if (voice == null || total == 0 && makeMs.Count == 0)
                    return ko ? $"{since:HH:mm}부터 아직 읽은 알림이 없습니다." : $"No alerts read since {since:HH:mm}.";
                int instead = late + failed + paused;
                string Pct(int n) => total == 0 ? "" : $" ({n * 100.0 / total:0}%)";
                var text = ko
                    ? $"{since:HH:mm}부터 {voice}: 알림 {total}개 · 만들어 둔 음성 {fromMade}{Pct(fromMade)} · 그때 만들어 제시간 {inTime}{Pct(inTime)} · Windows 음성으로 대신 {instead}{Pct(instead)}"
                    : $"Since {since:HH:mm}, {voice}: {total} alerts · from made speech {fromMade}{Pct(fromMade)} · made in time {inTime}{Pct(inTime)} · Windows' voice instead {instead}{Pct(instead)}";
                if (instead > 0)
                    text += ko ? $" (늦음 {late}, 오류 {failed}, 한도 {paused})" : $" (late {late}, failed {failed}, at a limit {paused})";
                if (makeMs.Count > 0)
                {
                    var sorted = makeMs.OrderBy(x => x).ToList();
                    double median = sorted[sorted.Count / 2];
                    double slowTenth = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.9))];
                    text += "\n" + (ko
                        ? $"그때 만든 {sorted.Count}개의 시간: 보통 {median / 1000:0.00}초, 느린 10% {slowTenth / 1000:0.00}초, 가장 느림 {sorted[sorted.Count - 1] / 1000:0.00}초"
                        : $"Making {sorted.Count} texts took: median {median / 1000:0.00} s, slowest tenth {slowTenth / 1000:0.00} s, slowest {sorted[sorted.Count - 1] / 1000:0.00} s");
                }
                return text;
            }
        }
    }
}
