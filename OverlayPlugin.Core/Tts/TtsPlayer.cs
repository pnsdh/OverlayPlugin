using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RainbowMage.OverlayPlugin.Tts
{
    // Fork: plays speech on one output that stays open while alerts come (opening an audio device
    // costs tens of milliseconds, which would be added to every alert), closed after a while
    // without any.  Clips are mixed in, so one can be cut off at once for a more important one.
    //
    // An output is never stopped on its own playback thread (NAudio's Stop joins that thread):
    // it is taken out under the lock and stopped on the thread pool.  A clip that goes with a
    // closed output is ended (its finished called), so whoever waits for it is not stuck.
    public class TtsPlayer : IDisposable
    {
        private const int LatencyMs = 40;
        private static readonly TimeSpan IdleClose = TimeSpan.FromMinutes(5);

        private readonly ILogger logger;
        private readonly object sync = new object();
        private WasapiOut output;
        private MixingSampleProvider mixer;
        private string deviceId;
        private DateTime lastUse;
        private DateTime lastOpenError;
        private bool disposed;
        private readonly Timer idleTimer;
        private readonly MMDeviceEnumerator notifications;
        private readonly DefaultDeviceWatcher watcher;

        public TtsPlayer(ILogger logger)
        {
            this.logger = logger;
            idleTimer = new Timer(_ => CloseIfIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            // "Default device" follows Windows' default output when it changes.
            try
            {
                notifications = new MMDeviceEnumerator();
                watcher = new DefaultDeviceWatcher(this);
                notifications.RegisterEndpointNotificationCallback(watcher);
            }
            catch (Exception)
            {
                notifications = null;
            }
        }

        public int MixRate
        {
            get
            {
                lock (sync)
                    return mixer?.WaveFormat.SampleRate ?? 0;
            }
        }

        public static List<(string Id, string Name)> GetDevices()
        {
            var list = new List<(string, string)>();
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                        list.Add((device.ID, device.FriendlyName));
                }
            }
            catch (Exception)
            {
                // No audio service: only the default is offered.
            }
            return list;
        }

        // Opens the output if it is not (ahead of the speech, which is synthesized meanwhile).
        public void Prepare(string device)
        {
            lock (sync)
            {
                if (disposed)
                    return;
                lastUse = DateTime.UtcNow;
                if (output != null && deviceId == (device ?? ""))
                    return;
                Close();
                try
                {
                    using (var enumerator = new MMDeviceEnumerator())
                    {
                        MMDevice endpoint = null;
                        if (!string.IsNullOrEmpty(device))
                        {
                            try
                            {
                                endpoint = enumerator.GetDevice(device);
                                if (endpoint.State != DeviceState.Active)
                                    endpoint = null;
                            }
                            catch (Exception)
                            {
                                endpoint = null;
                            }
                        }
                        // A device that is gone: the default, rather than silence (as the default,
                        // so it follows Windows' default and the chosen one is tried again next time).
                        bool fallback = endpoint == null;
                        endpoint = endpoint ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        var mix = endpoint.AudioClient.MixFormat;
                        var newMixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, Math.Min(2, Math.Max(1, mix.Channels))))
                        {
                            // Silence between clips, so the output keeps running.
                            ReadFully = true,
                        };
                        var newOutput = new WasapiOut(endpoint, AudioClientShareMode.Shared, true, LatencyMs);
                        try
                        {
                            newOutput.PlaybackStopped += OnStopped;
                            newOutput.Init(newMixer);
                            newOutput.Play();
                        }
                        catch
                        {
                            newOutput.PlaybackStopped -= OnStopped;
                            newOutput.Dispose();
                            throw;
                        }
                        output = newOutput;
                        mixer = newMixer;
                        deviceId = fallback ? "" : device ?? "";
                    }
                }
                catch (Exception e)
                {
                    // No device (or a failing one): said once a minute, not for every alert.
                    if (DateTime.UtcNow - lastOpenError > TimeSpan.FromMinutes(1))
                    {
                        lastOpenError = DateTime.UtcNow;
                        logger.Log(LogLevel.Warning, $"TTS: could not open the audio output: {e.Message}");
                    }
                    Close();
                }
            }
        }

        // Starts a clip now; returns what plays it (to stop or wait for), or null without output.
        public TtsPlayback Play(TtsClip clip, float volume, Action finished)
        {
            lock (sync)
            {
                lastUse = DateTime.UtcNow;
                if (mixer == null || disposed)
                    return null;
                var samples = clip.SampleRate == mixer.WaveFormat.SampleRate ? clip.Samples : Resample(clip, mixer.WaveFormat.SampleRate);
                var playback = new TtsPlayback(samples, mixer.WaveFormat, volume, finished);
                mixer.AddMixerInput(playback);
                return playback;
            }
        }

        // To the output's rate, done once per clip before playing (cached clips are kept so).
        public static float[] Resample(TtsClip clip, int rate)
        {
            if (clip.SampleRate == rate || clip.Samples.Length == 0)
                return clip.Samples;
            var resampler = new WdlResampler();
            resampler.SetMode(true, 2, false);
            resampler.SetFilterParms();
            resampler.SetFeedMode(true);
            resampler.SetRates(clip.SampleRate, rate);
            int outCount = (int)((long)clip.Samples.Length * rate / clip.SampleRate) + 64;
            var result = new float[outCount];
            int need = resampler.ResamplePrepare(clip.Samples.Length, 1, out float[] inBuffer, out int inOffset);
            Array.Copy(clip.Samples, 0, inBuffer, inOffset, Math.Min(need, clip.Samples.Length));
            int produced = resampler.ResampleOut(result, 0, clip.Samples.Length, outCount, 1);
            if (produced < outCount)
                Array.Resize(ref result, produced);
            return result;
        }

        // On NAudio's playback thread, after the device failed (unplugged, format changed...): the
        // output is only taken out here; opened again with the next alert.
        private void OnStopped(object sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
                logger.Log(LogLevel.Warning, $"TTS: audio output stopped: {e.Exception.Message}");
            lock (sync)
            {
                if (sender == output)
                    Close();
            }
        }

        private void CloseIfIdle()
        {
            lock (sync)
            {
                if (output != null && DateTime.UtcNow - lastUse > IdleClose && !mixer.MixerInputs.Any())
                    Close();
            }
        }

        // Windows' default output changed: an output on the default is opened again on it.
        private void DefaultChanged()
        {
            lock (sync)
            {
                if (output != null && deviceId == "")
                    Close();
            }
        }

        // Under the lock: the output taken out, its clips ended, and it stopped on the thread pool
        // (never on its own playback thread, which may be the one calling).
        private void Close()
        {
            var o = output;
            var m = mixer;
            output = null;
            mixer = null;
            if (m != null)
            {
                List<TtsPlayback> ended;
                // The mixer's own list, also changed by the playback thread under it.
                lock (m.MixerInputs)
                    ended = m.MixerInputs.OfType<TtsPlayback>().ToList();
                m.RemoveAllMixerInputs();
                foreach (var playback in ended)
                    playback.End();
            }
            if (o != null)
            {
                o.PlaybackStopped -= OnStopped;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        o.Stop();
                        o.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                });
            }
        }

        public void Dispose()
        {
            idleTimer.Dispose();
            try
            {
                if (notifications != null)
                {
                    notifications.UnregisterEndpointNotificationCallback(watcher);
                    notifications.Dispose();
                }
            }
            catch (Exception)
            {
            }
            lock (sync)
            {
                disposed = true;
                Close();
            }
        }

        private class DefaultDeviceWatcher : IMMNotificationClient
        {
            private readonly TtsPlayer player;

            public DefaultDeviceWatcher(TtsPlayer player)
            {
                this.player = player;
            }

            public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
            {
                // Called by Windows on its own thread: the output is closed elsewhere.
                if (flow == DataFlow.Render && role == Role.Multimedia)
                    ThreadPool.QueueUserWorkItem(_ => player.DefaultChanged());
            }

            public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
            public void OnDeviceAdded(string pwstrDeviceId) { }
            public void OnDeviceRemoved(string deviceId) { }
            public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
        }
    }

    // One clip in the mix.  Stop fades it out over a few milliseconds (a cut at full level clicks).
    public class TtsPlayback : ISampleProvider
    {
        private readonly float[] samples;
        private readonly int channels;
        private readonly Action finished;
        private int position;
        private float volume;
        private int fadeLeft = -1;
        private int done;

        public WaveFormat WaveFormat { get; }

        public TtsPlayback(float[] samples, WaveFormat format, float volume, Action finished)
        {
            this.samples = samples;
            WaveFormat = format;
            channels = format.Channels;
            this.volume = volume;
            this.finished = finished;
        }

        public bool Finished => Volatile.Read(ref done) != 0;

        public void Stop()
        {
            if (fadeLeft < 0)
                fadeLeft = WaveFormat.SampleRate / 200;
        }

        // Its output closed: over, without playing the rest.
        public void End()
        {
            position = samples.Length;
            if (Interlocked.Exchange(ref done, 1) == 0)
                finished?.Invoke();
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / channels;
            int written = 0;
            for (int f = 0; f < frames && position < samples.Length; f++)
            {
                float gain = volume;
                if (fadeLeft >= 0)
                {
                    if (fadeLeft == 0)
                    {
                        position = samples.Length;
                        break;
                    }
                    gain *= fadeLeft / (float)(WaveFormat.SampleRate / 200);
                    fadeLeft--;
                }
                float s = samples[position++] * gain;
                for (int c = 0; c < channels; c++)
                    buffer[offset + written++] = s;
            }
            if (position >= samples.Length && Interlocked.Exchange(ref done, 1) == 0)
                finished?.Invoke();
            return written;
        }
    }
}
