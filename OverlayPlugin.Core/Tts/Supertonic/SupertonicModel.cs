using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace RainbowMage.OverlayPlugin.Tts.Supertonic
{
    // Fork: Supertonic's files, downloaded only when the user asks (about 470 MB: the model from
    // Hugging Face at a fixed revision, ONNX Runtime's native library from Microsoft's release),
    // each checked against its SHA-256 here, kept in the local (not roaming) app data folder.
    public static class SupertonicModel
    {
        private const string Revision = "3cadd1ee6394adea1bd021217a0e650ede09a323";
        private static readonly string[] modelBases =
        {
            "https://huggingface.co/Supertone/supertonic-3/resolve/" + Revision + "/",
        };
        private const string RuntimeUrl = "https://github.com/microsoft/onnxruntime/releases/download/v1.20.1/onnxruntime-win-x64-1.20.1.zip";
        private const string RuntimeZipSha = "78d447051e48bd2e1e778bba378bec4ece11191c9e538cf7b2c4a4565e8f5581";
        private const long RuntimeZipSize = 65585496;
        private const string RuntimeEntry = "onnxruntime-win-x64-1.20.1/lib/onnxruntime.dll";

        public const string LicenseUrl = "https://huggingface.co/Supertone/supertonic-3/blob/" + Revision + "/LICENSE";

        public static readonly string[] Voices = { "F1", "F2", "F3", "F4", "F5", "M1", "M2", "M3", "M4", "M5" };

        // Path in the repository, size, SHA-256.
        private static readonly (string Path, long Size, string Sha)[] files =
        {
            ("onnx/duration_predictor.onnx", 3700147, "c3eb91414d5ff8a7a239b7fe9e34e7e2bf8a8140d8375ffb14718b1c639325db"),
            ("onnx/text_encoder.onnx", 36416150, "c7befd5ea8c3119769e8a6c1486c4edc6a3bc8365c67621c881bbb774b9902ff"),
            ("onnx/vector_estimator.onnx", 256534781, "883ac868ea0275ef0e991524dc64f16b3c0376efd7c320af6b53f5b780d7c61c"),
            ("onnx/vocoder.onnx", 101424195, "085de76dd8e8d5836d6ca66826601f615939218f90e519f70ee8a36ed2a4c4ba"),
            ("onnx/tts.json", 8253, "42078d3aef1cd43ab43021f3c54f47d2d75ceb4e75f627f118890128b06a0d09"),
            ("onnx/unicode_indexer.json", 277676, "9bf7346e43883a81f8645c81224f786d43c5b57f3641f6e7671a7d6c493cb24f"),
            ("voice_styles/F1.json", 292046, "bbdec6ee00231c2c742ad05483df5334cab3b52fda3ba38e6a07059c4563dbc2"),
            ("voice_styles/F2.json", 292423, "7c722c6a72707b1a77f035d67f0d1351ba187738e06f7683e8c72b1df3477fc6"),
            ("voice_styles/F3.json", 290794, "12f6ef2573baa2defa1128069cb59f203e3ab67c92af77b42df8a0e3a2f7c6ab"),
            ("voice_styles/F4.json", 291808, "c2fa764c1225a76dfc3e2c73e8aa4f70d9ee48793860eb34c295fff01c2e032b"),
            ("voice_styles/F5.json", 291479, "45966e73316415626cf41a7d1c6f3b4c70dbc1ba2bee5c1978ef0ce33244fc8d"),
            ("voice_styles/M1.json", 291748, "e35604687f5d23694b8e91593a93eec0e4eca6c0b02bb8ed69139ab2ea6b0a5b"),
            ("voice_styles/M2.json", 292055, "b76cbf62bac707c710cf0ae5aba5e31eea1a6339a9734bfae33ab98499534a50"),
            ("voice_styles/M3.json", 290198, "ea1ac35ccb91b0d7ecad533a2fbd0eec10c91513d8951e3b25fbba99954e159b"),
            ("voice_styles/M4.json", 291522, "ca8eefad4fcd989c9379032ff3e50738adc547eeb5e221b82593a6d7b3bac303"),
            ("voice_styles/M5.json", 291469, "dd22b92740314321f8ae11c5e87f8dd60d060f15dd3a632b5adf77f471f77af2"),
            ("LICENSE", 15007, "0d944a9110fed9a9602d60e0423a272903e7bd21ab060490774efc77c2275e9f"),
        };

        public static long DownloadSize => files.Sum(f => f.Size) + RuntimeZipSize;

        public static string Folder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OverlayPlugin", "tts", "supertonic-3");

        private static string RuntimePath => Path.Combine(Folder, "runtime", "onnxruntime.dll");

        // Written when a download ended with every file checked against its hash (and removed
        // first by a delete): only then is it ready.
        private static string VerifiedPath => Path.Combine(Folder, "verified-" + Revision.Substring(0, 12));

        public static bool Ready =>
            File.Exists(VerifiedPath) && File.Exists(RuntimePath) &&
            files.All(f => File.Exists(Path.Combine(Folder, f.Path)) && new FileInfo(Path.Combine(Folder, f.Path)).Length == f.Size);

        public static long SizeOnDisk()
        {
            try
            {
                return Directory.Exists(Folder) ? new DirectoryInfo(Folder).GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // Downloads what is missing (a file already there and whole is kept), reporting bytes done
        // of all.  A file that does not match its hash is deleted and the download fails.
        public static async Task Download(IProgress<(long Done, long Total)> progress, CancellationToken cancel)
        {
            Directory.CreateDirectory(Folder);
            long total = DownloadSize;
            long done = 0;
            void Add(long n)
            {
                done += n;
                progress?.Report((done, total));
            }

            if (File.Exists(VerifiedPath))
                File.Delete(VerifiedPath);
            foreach (var f in files)
            {
                var path = Path.Combine(Folder, f.Path);
                // Kept from before (a download stopped midway): only if it is the right file.
                if (File.Exists(path) && new FileInfo(path).Length == f.Size && await Task.Run(() => Sha256(path), cancel).ConfigureAwait(false) == f.Sha)
                {
                    Add(f.Size);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                Exception last = null;
                foreach (var b in modelBases)
                {
                    try
                    {
                        await Fetch(b + f.Path, path, f.Size, f.Sha, Add, cancel).ConfigureAwait(false);
                        last = null;
                        break;
                    }
                    catch (Exception e) when (!(e is OperationCanceledException))
                    {
                        last = e;
                    }
                }
                if (last != null)
                    throw last;
            }

            if (!File.Exists(RuntimePath))
            {
                var zip = Path.Combine(Folder, "onnxruntime.zip");
                await Fetch(RuntimeUrl, zip, RuntimeZipSize, RuntimeZipSha, Add, cancel).ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(RuntimePath));
                using (var archive = ZipFile.OpenRead(zip))
                {
                    var entry = archive.GetEntry(RuntimeEntry) ?? throw new InvalidDataException("onnxruntime.dll not in the release");
                    entry.ExtractToFile(RuntimePath + ".tmp", true);
                }
                File.Delete(zip);
                File.Move(RuntimePath + ".tmp", RuntimePath);
            }
            else
            {
                Add(RuntimeZipSize);
            }
            File.WriteAllText(VerifiedPath, DateTime.UtcNow.ToString("o"));
        }

        private static string Sha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create())
                return string.Concat(hash.ComputeHash(stream).Select(x => x.ToString("x2")));
        }

        private static async Task Fetch(string url, string path, long size, string sha, Action<long> progress, CancellationToken cancel)
        {
            var temp = path + ".part";
            long counted = 0;
            try
            {
                HttpResponseMessage answered;
                // A server that takes the connection but never answers: given up after 30 seconds.
                using (var headers = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    headers.CancelAfter(TimeSpan.FromSeconds(30));
                    try
                    {
                        answered = await TtsHttp.LongClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                    {
                        throw new IOException("no answer for 30 seconds");
                    }
                }
                using (var response = answered)
                // A read stuck on a dead connection does not see the token: the response is closed
                // under it (on Stop, or when nothing comes for 30 seconds).
                using (var stall = new CancellationTokenSource())
                using (cancel.Register(() => response.Dispose()))
                using (stall.Token.Register(() => response.Dispose()))
                {
                    response.EnsureSuccessStatusCode();
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = File.Create(temp))
                    using (var hash = SHA256.Create())
                    {
                        var buffer = new byte[1 << 16];
                        int read;
                        while (true)
                        {
                            stall.CancelAfter(TimeSpan.FromSeconds(30));
                            try
                            {
                                read = await input.ReadAsync(buffer, 0, buffer.Length, cancel).ConfigureAwait(false);
                            }
                            catch (Exception) when (cancel.IsCancellationRequested)
                            {
                                throw new OperationCanceledException(cancel);
                            }
                            catch (Exception) when (stall.IsCancellationRequested)
                            {
                                throw new IOException("no data for 30 seconds");
                            }
                            if (read <= 0)
                                break;
                            await output.WriteAsync(buffer, 0, read, cancel).ConfigureAwait(false);
                            hash.TransformBlock(buffer, 0, read, null, 0);
                            counted += read;
                            progress(read);
                        }
                        hash.TransformFinalBlock(buffer, 0, 0);
                        var got = string.Concat(hash.Hash.Select(x => x.ToString("x2")));
                        if (counted != size || got != sha)
                            throw new InvalidDataException($"{Path.GetFileName(path)}: not the expected file (size {counted}, sha256 {got})");
                    }
                }
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temp, path);
            }
            catch
            {
                progress(-counted);
                try
                {
                    File.Delete(temp);
                }
                catch (Exception)
                {
                }
                throw;
            }
        }

        // Everything removed; the runtime library, if ACT has it loaded, stays until ACT restarts
        // (removed then, see CleanUp).
        public static void Delete()
        {
            if (!Directory.Exists(Folder))
                return;
            if (File.Exists(VerifiedPath))
                File.Delete(VerifiedPath);
            foreach (var file in Directory.GetFiles(Folder, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception) when (runtimeLoaded && string.Equals(Path.GetFullPath(file), Path.GetFullPath(RuntimePath), StringComparison.OrdinalIgnoreCase))
                {
                }
            }
            try
            {
                Directory.Delete(Folder, true);
            }
            catch (Exception)
            {
                // The loaded library's folder.
            }
        }

        // At start: what a delete could not remove (the library in use then) goes now.
        public static void CleanUp()
        {
            try
            {
                if (Directory.Exists(Folder) && !File.Exists(VerifiedPath) && !Directory.EnumerateFiles(Folder, "*.onnx", SearchOption.AllDirectories).Any() && !runtimeLoaded)
                    Directory.Delete(Folder, true);
            }
            catch (Exception)
            {
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);

        private static bool runtimeLoaded;

        // ONNX Runtime's managed part finds "onnxruntime.dll" by name: loaded from the download
        // first, the name resolves to it.
        public static void LoadRuntime()
        {
            if (runtimeLoaded)
                return;
            if (LoadLibrary(RuntimePath) == IntPtr.Zero)
                throw new DllNotFoundException($"could not load {RuntimePath} (error {Marshal.GetLastWin32Error()})");
            runtimeLoaded = true;
        }
    }
}
