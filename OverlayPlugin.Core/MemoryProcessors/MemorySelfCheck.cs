using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using RainbowMage.OverlayPlugin.MemoryProcessors.Combatant;
using RainbowMage.OverlayPlugin.MemoryProcessors.JobGauge;
using RainbowMage.OverlayPlugin.MemoryProcessors.Target;
using PluginCombatant = FFXIV_ACT_Plugin.Common.Models.Combatant;

namespace RainbowMage.OverlayPlugin.MemoryProcessors
{
    // Fork, for development (a game patch): whether the memory OverlayPlugin reads still has the
    // layout it reads it with.  Every few seconds the combatants read from memory are matched (by
    // ID) with FFXIV_ACT_Plugin's, which reads the same memory with its own, separately maintained
    // layout, and each field is compared; the target and the job gauge are checked against the
    // player.  A field whose offset moved disagrees for (nearly) every combatant; moving ones
    // disagree a little anyway, the two being read at slightly different moments.  Summaries go to
    // %TEMP%\op-selfcheck.log each minute.  Off unless %TEMP%\op-selfcheck.enable exists when ACT
    // starts.  Right after a patch FFXIV_ACT_Plugin may be the one that is wrong: if both disagree
    // on a field, look at which values make sense.
    class MemorySelfCheck : IDisposable
    {
        public static readonly bool Enabled = File.Exists(Path.Combine(Path.GetTempPath(), "op-selfcheck.enable"));
        private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "op-selfcheck.log");
        private const int SampleMs = 3000;
        private const int SamplesPerSummary = 20;
        // OverlayPlugin keeps HP and such only for these (others have garbage there).
        private static readonly HashSet<byte> HpTypes = new HashSet<byte> { (byte)ObjectType.PC, (byte)ObjectType.Monster };

        private readonly TinyIoCContainer container;
        private readonly FFXIVRepository repository;
        private readonly Timer timer;
        private readonly object sync = new object();
        private int samples;
        private Window window = new Window();

        private class Field
        {
            public int Compared;
            public int Differ;
            public string Example;
        }

        private class Window
        {
            public readonly SortedDictionary<string, Field> Fields = new SortedDictionary<string, Field>();
            public int Matched;
            public int OnlyMemory;
            public int OnlyPlugin;
            public int Samples;
        }

        public MemorySelfCheck(TinyIoCContainer container)
        {
            this.container = container;
            repository = container.Resolve<FFXIVRepository>();
            Write("--- self check started");
            timer = new Timer(_ => Sample(), null, SampleMs, SampleMs);
        }

        public void Dispose()
        {
            timer.Dispose();
        }

        private void Sample()
        {
            lock (sync)
            {
                try
                {
                    if (repository.GetCurrentFFXIVProcess() == null)
                        return;
                    Compare();
                }
                catch (Exception e)
                {
                    Write($"error: {e.GetType().Name}: {e.Message}");
                }
                if (++samples % SamplesPerSummary == 0)
                    Summarize();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Compare()
        {
            var plugin = repository.GetCombatants();
            var memory = container.Resolve<ICombatantMemory>();
            if (plugin == null || !memory.IsValid())
                return;
            var fromPlugin = new Dictionary<uint, PluginCombatant>();
            foreach (var c in plugin)
                fromPlugin[c.ID] = c;
            var fromMemory = memory.GetCombatantList(false);
            window.Samples++;
            var seen = new HashSet<uint>();
            foreach (var m in fromMemory)
            {
                seen.Add(m.ID);
                if (!fromPlugin.TryGetValue(m.ID, out var p))
                {
                    window.OnlyMemory++;
                    continue;
                }
                window.Matched++;
                CompareCombatant(m, p);
            }
            window.OnlyPlugin += fromPlugin.Keys.Count(id => !seen.Contains(id));

            // The player's target and job gauge.
            uint playerId = repository.GetPlayerID();
            if (playerId == 0 || !fromPlugin.TryGetValue(playerId, out var player))
                return;
            var target = container.Resolve<ITargetMemory>();
            if (target.IsValid())
                Check("Target (memory) = player's TargetID", Id(target.GetTargetCombatant()?.ID ?? 0), Id(player.TargetID));
            var gauge = container.Resolve<IJobGaugeMemory>();
            var g = gauge.IsValid() ? gauge.GetJobGauge() : null;
            if (g != null && g.Job != JobGaugeJob.None)
                Check("JobGauge job = player's job", (int)g.Job, player.Job);
        }

        private void CompareCombatant(Combatant.Combatant m, PluginCombatant p)
        {
            Check("Name", m.Name, p.Name, hide: true);
            Check("Type", (byte)m.Type, p.type);
            Check("OwnerID", Id(m.OwnerID), Id(p.OwnerID));
            Check("BNpcID", m.BNpcID, p.BNpcID);
            Check("BNpcNameID", m.BNpcNameID, p.BNpcNameID);
            Check("PosX", m.PosX, p.PosX, 0.1f);
            Check("PosY", m.PosY, p.PosY, 0.1f);
            Check("PosZ", m.PosZ, p.PosZ, 0.1f);
            Check("Heading", m.Heading, p.Heading, 0.05f);
            Check("CastBuffID", m.CastBuffID, p.CastBuffID);
            Check("CastTargetID", Id(m.CastTargetID), Id(p.CastTargetID));
            Check("CastDurationMax", m.CastDurationMax, p.CastDurationMax, 0.05f);
            if (!HpTypes.Contains((byte)m.Type))
                return;
            Check("Job", (int)m.Job, p.Job);
            Check("Level", (int)m.Level, p.Level);
            Check("CurrentHP", (uint)m.CurrentHP, p.CurrentHP);
            Check("MaxHP", (uint)m.MaxHP, p.MaxHP);
            Check("MaxMP", (uint)m.MaxMP, p.MaxMP);
            Check("TargetID", Id(m.TargetID), Id(p.TargetID));
            if (m.Type == ObjectType.PC)
            {
                Check("WorldID", (uint)m.WorldID, p.WorldID);
                Check("CurrentWorldID", (uint)m.CurrentWorldID, p.CurrentWorldID);
            }
        }

        // No target / owner is 0 on one side and 0xE0000000 on the other.
        private static uint Id(uint id) => id == 0xE0000000 ? 0 : id;

        private void Check<T>(string name, T memory, T plugin, bool hide = false)
        {
            Record(name, EqualityComparer<T>.Default.Equals(memory, plugin), hide ? null : $"{memory} vs {plugin}");
        }

        private void Check(string name, float memory, float plugin, float tolerance)
        {
            Record(name, Math.Abs(memory - plugin) <= tolerance,
                string.Format(CultureInfo.InvariantCulture, "{0:F3} vs {1:F3}", memory, plugin));
        }

        private void Record(string name, bool same, string example)
        {
            if (!window.Fields.TryGetValue(name, out var f))
                window.Fields[name] = f = new Field();
            f.Compared++;
            if (!same)
            {
                f.Differ++;
                if (f.Example == null)
                    f.Example = example ?? "(values not shown)";
            }
        }

        private void Summarize()
        {
            var w = window;
            window = new Window();
            if (w.Samples == 0)
                return;
            var text = new StringBuilder();
            text.AppendFormat("{0} samples: {1} matched, {2} only in memory, {3} only in FFXIV_ACT_Plugin (per sample: {4:F1} / {5:F1} / {6:F1})\r\n",
                w.Samples, w.Matched, w.OnlyMemory, w.OnlyPlugin,
                (double)w.Matched / w.Samples, (double)w.OnlyMemory / w.Samples, (double)w.OnlyPlugin / w.Samples);
            foreach (var kv in w.Fields)
            {
                var f = kv.Value;
                double share = 100.0 * f.Differ / Math.Max(f.Compared, 1);
                string level = share > 50 ? "BROKEN?" : share > 10 ? "odd" : "ok";
                text.AppendFormat(CultureInfo.InvariantCulture, "  {0,-7} {1,-36} {2,5:F1}% of {3}{4}\r\n",
                    level, kv.Key, share, f.Compared, f.Differ > 0 ? $"  e.g. {f.Example}" : "");
            }
            Write(text.ToString().TrimEnd());
        }

        private static void Write(string text)
        {
            try
            {
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {text}\r\n", Encoding.UTF8);
            }
            catch (Exception)
            {
            }
        }
    }
}
