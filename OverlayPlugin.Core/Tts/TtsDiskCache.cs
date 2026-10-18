using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace RainbowMage.OverlayPlugin.Tts
{
    // Fork: speech from the network kept on disk (16-bit, a file per text), so an alert heard once
    // is never fetched (or paid for) again, across ACT restarts.  The least recently used files go
    // first when it is over its size.
    public class TtsDiskCache
    {
        private readonly string folder;
        private readonly Func<long> maxBytes;
        private int writes;

        public TtsDiskCache(string folder, Func<long> maxBytes)
        {
            this.folder = folder;
            this.maxBytes = maxBytes;
            ThreadPool.QueueUserWorkItem(_ => Trim());
        }

        public string Folder => folder;

        private string PathOf(string key)
        {
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                return Path.Combine(folder, string.Concat(hash.Select(b => b.ToString("x2"))) + ".pcm");
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

        public void Put(string key, TtsClip clip)
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
                var path = PathOf(key);
                var temp = path + ".tmp";
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temp, path);
                if (Interlocked.Increment(ref writes) % 50 == 0)
                    Trim();
            }
            catch (Exception)
            {
                // A full disk or a locked file: not kept, said all the same.
            }
        }

        public void Clear()
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
                var files = new DirectoryInfo(folder).GetFiles("*.pcm").OrderBy(f => f.LastAccessTimeUtc).ToList();
                long total = files.Sum(f => f.Length);
                long max = maxBytes();
                foreach (var f in files)
                {
                    if (total <= max)
                        break;
                    total -= f.Length;
                    f.Delete();
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
