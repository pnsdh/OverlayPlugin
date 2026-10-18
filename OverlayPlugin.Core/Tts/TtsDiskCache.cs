using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace RainbowMage.OverlayPlugin.Tts
{
    // Fork: what a file of made speech is (written beside it in an index): which voice made it, in
    // which zone, the text, whether it was paid for.  For the settings to show it and remove it by
    // voice or by zone.
    public class TtsCacheInfo
    {
        public string File { get; set; }
        public string Provider { get; set; }
        public string ProviderName { get; set; }
        public string Voice { get; set; }
        // Where it was first made.
        public string Zone { get; set; }
        // Every zone it was said (or made ahead) in, so removing a zone's speech keeps what other
        // zones use (none: only Zone, as recorded before).
        public List<string> Zones { get; set; }
        public string Text { get; set; }

        public List<string> AllZones() =>
            Zones != null && Zones.Count > 0 ? Zones : Zone != null ? new List<string> { Zone } : new List<string>();
        public bool Paid { get; set; }
        public long Bytes { get; set; }
        public DateTime Made { get; set; }
    }

    // A group of files shown in the settings: same voice, same zone.
    public class TtsCacheGroup
    {
        public string Provider;
        public string Label;
        public string Zone;
        public bool Paid;
        public int Count;
        // Of them, also used in another zone (kept when this zone's are removed).
        public int Shared;
        public long Bytes;
        public List<string> Files = new List<string>();
    }

    // Fork: speech from slow voices kept on disk (16-bit, a file per text), so an alert heard once
    // is never fetched (or paid for) again, across ACT restarts.  Over its size, the least recently
    // used files go first, free voices' before paid ones'.
    public class TtsDiskCache
    {
        private const string IndexName = "index.jsonl";

        private readonly string folder;
        private readonly Func<long> maxBytes;
        private readonly object sync = new object();
        private Dictionary<string, TtsCacheInfo> index;
        private int writes;

        public TtsDiskCache(string folder, Func<long> maxBytes)
        {
            this.folder = folder;
            this.maxBytes = maxBytes;
            ThreadPool.QueueUserWorkItem(_ => Trim());
        }

        public string Folder => folder;

        private string NameOf(string key)
        {
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                return string.Concat(hash.Select(b => b.ToString("x2"))) + ".pcm";
            }
        }

        private string PathOf(string key) => Path.Combine(folder, NameOf(key));

        // The index, read once (lines of JSON, the last one for a file wins); entries whose file
        // is gone are dropped.
        private Dictionary<string, TtsCacheInfo> Index()
        {
            lock (sync)
            {
                if (index != null)
                    return index;
                index = new Dictionary<string, TtsCacheInfo>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var path = Path.Combine(folder, IndexName);
                    // Left from a swap that did not finish.
                    if (!File.Exists(path) && File.Exists(path + ".tmp"))
                        File.Move(path + ".tmp", path);
                    if (File.Exists(path))
                    {
                        foreach (var line in File.ReadLines(path))
                        {
                            try
                            {
                                var info = JsonConvert.DeserializeObject<TtsCacheInfo>(line);
                                if (info?.File != null)
                                    index[info.File] = info;
                            }
                            catch (Exception)
                            {
                                // A broken line (a crash mid-write): skipped.
                            }
                        }
                    }
                    foreach (var name in index.Keys.ToList())
                    {
                        if (!File.Exists(Path.Combine(folder, name)))
                            index.Remove(name);
                    }
                }
                catch (Exception)
                {
                }
                return index;
            }
        }

        // Under the lock: the index written again (after removals).
        private void SaveIndex()
        {
            try
            {
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, IndexName);
                File.WriteAllLines(path + ".tmp", index.Values.Select(i => JsonConvert.SerializeObject(i)));
                // Swapped in one step (a crash in between must not lose it: without it, paid
                // speech would look free and go first).
                if (File.Exists(path))
                    File.Replace(path + ".tmp", path, null);
                else
                    File.Move(path + ".tmp", path);
            }
            catch (Exception)
            {
            }
        }

        public bool Has(string key)
        {
            try
            {
                return File.Exists(PathOf(key));
            }
            catch (Exception)
            {
                return false;
            }
        }

        public TtsClip Get(string key)
        {
            var path = PathOf(key);
            try
            {
                if (!File.Exists(path))
                    return null;
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length < 4)
                    return null;
                int rate = BitConverter.ToInt32(bytes, 0);
                // Used now: kept longer.
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
                return TtsAudio.FromPcm16(bytes, 4, bytes.Length - 4, rate);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Put(string key, TtsClip clip, TtsCacheInfo info)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var bytes = new byte[4 + clip.Samples.Length * 2];
                BitConverter.GetBytes(clip.SampleRate).CopyTo(bytes, 0);
                for (int i = 0; i < clip.Samples.Length; i++)
                {
                    short s = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, clip.Samples[i] * 32767f));
                    bytes[4 + 2 * i] = (byte)s;
                    bytes[5 + 2 * i] = (byte)(s >> 8);
                }
                var name = NameOf(key);
                var path = Path.Combine(folder, name);
                var temp = path + ".tmp";
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temp, path);
                if (info != null)
                {
                    info.File = name;
                    info.Bytes = bytes.Length;
                    info.Made = DateTime.Now;
                    info.Zones = info.Zone != null ? new List<string> { info.Zone } : null;
                    lock (sync)
                    {
                        Index()[name] = info;
                        File.AppendAllText(Path.Combine(folder, IndexName), JsonConvert.SerializeObject(info) + "\n");
                    }
                }
                if (Interlocked.Increment(ref writes) % 50 == 0)
                    Trim();
            }
            catch (Exception)
            {
                // A full disk or a locked file: not kept, said all the same.
            }
        }

        // Said (or made ahead) in a zone: that zone recorded for it (once; written only when new).
        public void Touch(IEnumerable<string> keys, string zone)
        {
            if (string.IsNullOrEmpty(zone))
                return;
            try
            {
                lock (sync)
                {
                    var known = Index();
                    var lines = new StringBuilder();
                    foreach (var key in keys)
                    {
                        if (!known.TryGetValue(NameOf(key), out var info))
                            continue;
                        var zones = info.AllZones();
                        if (zones.Contains(zone))
                            continue;
                        info.Zones = new List<string>(zones) { zone };
                        lines.Append(JsonConvert.SerializeObject(info)).Append('\n');
                    }
                    if (lines.Length > 0)
                        File.AppendAllText(Path.Combine(folder, IndexName), lines.ToString());
                }
            }
            catch (Exception)
            {
            }
        }

        public void Touch(string key, string zone) => Touch(new[] { key }, zone);

        // What is kept, by voice and zone (a file in each zone it is used in; files made before
        // the index was kept: one group).
        public List<TtsCacheGroup> Groups()
        {
            var groups = new Dictionary<string, TtsCacheGroup>();
            try
            {
                if (!Directory.Exists(folder))
                    return new List<TtsCacheGroup>();
                Dictionary<string, TtsCacheInfo> known;
                lock (sync)
                    known = new Dictionary<string, TtsCacheInfo>(Index(), StringComparer.OrdinalIgnoreCase);
                foreach (var file in new DirectoryInfo(folder).GetFiles("*.pcm"))
                {
                    known.TryGetValue(file.Name, out var info);
                    string label = info == null ? null : string.IsNullOrEmpty(info.Voice) ? info.ProviderName : $"{info.ProviderName} · {info.Voice}";
                    var zones = info?.AllZones() ?? new List<string>();
                    foreach (var zone in zones.Count > 0 ? zones : new List<string> { null })
                    {
                        string key = $"{info?.Provider}|{label}|{zone}";
                        if (!groups.TryGetValue(key, out var group))
                        {
                            groups[key] = group = new TtsCacheGroup
                            {
                                Provider = info?.Provider ?? "",
                                Label = label,
                                Zone = zone,
                                Paid = info?.Paid ?? false,
                            };
                        }
                        group.Count++;
                        group.Bytes += file.Length;
                        group.Files.Add(file.Name);
                        group.Paid |= info?.Paid ?? false;
                        if (zones.Count > 1)
                            group.Shared++;
                    }
                }
            }
            catch (Exception)
            {
            }
            return groups.Values.OrderBy(g => g.Label == null).ThenBy(g => g.Label).ThenBy(g => g.Zone).ToList();
        }

        // Groups' speech removed: a file goes when no zone it is used in is left; otherwise only
        // the removed zones come off it.  Just counted when not apply (for asking first).
        public (int Deleted, int Kept) Remove(IEnumerable<TtsCacheGroup> groups, bool apply)
        {
            lock (sync)
            {
                var known = Index();
                var removing = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var group in groups)
                {
                    foreach (var file in group.Files)
                    {
                        if (!removing.TryGetValue(file, out var zones))
                            removing[file] = zones = new HashSet<string>();
                        // A group without a zone: the file itself.
                        zones.Add(group.Zone ?? "\0");
                    }
                }
                var delete = new List<string>();
                var detach = new List<(TtsCacheInfo Info, List<string> Left)>();
                foreach (var pair in removing)
                {
                    known.TryGetValue(pair.Key, out var info);
                    var left = info == null || pair.Value.Contains("\0") ? new List<string>() : info.AllZones().Where(z => !pair.Value.Contains(z)).ToList();
                    if (left.Count == 0)
                        delete.Add(pair.Key);
                    else
                        detach.Add((info, left));
                }
                if (apply)
                {
                    foreach (var (info, left) in detach)
                    {
                        info.Zones = left;
                        info.Zone = left[0];
                    }
                    // Saved by Delete (or here, when nothing goes).
                    if (delete.Count > 0)
                        Delete(delete);
                    else if (detach.Count > 0)
                        SaveIndex();
                }
                return (delete.Count, detach.Count);
            }
        }

        public void Delete(IEnumerable<string> files)
        {
            lock (sync)
            {
                var known = Index();
                foreach (var name in files)
                {
                    try
                    {
                        File.Delete(Path.Combine(folder, Path.GetFileName(name)));
                    }
                    catch (Exception)
                    {
                    }
                    known.Remove(name);
                }
                SaveIndex();
            }
        }

        public void Clear()
        {
            lock (sync)
            {
                try
                {
                    if (Directory.Exists(folder))
                        foreach (var f in Directory.GetFiles(folder, "*.pcm"))
                            File.Delete(f);
                }
                catch (Exception)
                {
                }
                Index().Clear();
                SaveIndex();
            }
        }

        public long Size()
        {
            try
            {
                return Directory.Exists(folder) ? new DirectoryInfo(folder).GetFiles("*.pcm").Sum(f => f.Length) : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private void Trim()
        {
            try
            {
                if (!Directory.Exists(folder))
                    return;
                Dictionary<string, TtsCacheInfo> known;
                lock (sync)
                    known = new Dictionary<string, TtsCacheInfo>(Index(), StringComparer.OrdinalIgnoreCase);
                bool Paid(FileInfo f) => known.TryGetValue(f.Name, out var i) && i.Paid;
                // Free voices' speech first (made again at no cost), then the least recently used.
                var files = new DirectoryInfo(folder).GetFiles("*.pcm").OrderBy(f => Paid(f)).ThenBy(f => f.LastAccessTimeUtc).ToList();
                long total = files.Sum(f => f.Length);
                long max = maxBytes();
                var removed = new List<string>();
                foreach (var f in files)
                {
                    if (total <= max)
                        break;
                    total -= f.Length;
                    removed.Add(f.Name);
                }
                if (removed.Count > 0)
                    Delete(removed);
            }
            catch (Exception)
            {
            }
        }
    }
}
