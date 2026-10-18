using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RainbowMage.OverlayPlugin.Tts
{
    // Fork: plays speech on one output that stays open while alerts come (opening an audio device
    // costs tens of milliseconds, which would be added to every alert), closed after a while
    // without any.  Clips are mixed in, so one can be cut off at once for a more important one.
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
        private readonly Timer idleTimer;

        public TtsPlayer(ILogger logger)
        {
            this.logger = logger;
            idleTimer = new Timer(_ => CloseIfIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
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
                        // A device that is gone: the default, rather than silence.
                        endpoint = endpoint ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        var mix = endpoint.AudioClient.MixFormat;
                        mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, Math.Min(2, Math.Max(1, mix.Channels))))
                        {
                            // Silence between clips, so the output keeps running.
                            ReadFully = true,
                        };
                        output = new WasapiOut(endpoint, AudioClientShareMode.Shared, true, LatencyMs);
                        output.PlaybackStopped += OnStopped;
                        output.Init(mixer);
                        output.Play();
                        deviceId = device ?? "";
                    }
                }
                catch (Exception e)
                {
                    logger.Log(LogLevel.Warning, $"TTS: could not open the audio output: {e.Message}");
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
                if (mixer == null)
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

        private void OnStopped(object sender, StoppedEventArgs e)
        {
            // The device went away or failed: opened again on the next alert.
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
                if (output != null && DateTime.UtcNow - lastUse > IdleClose && mixer.MixerInputs.Count() == 0)
                    Close();
            }
        }

        private void Close()
        {
            var o = output;
            output = null;
            mixer = null;
            if (o != null)
            {
                o.PlaybackStopped -= OnStopped;
                try
                {
                    o.Stop();
                    o.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        public void Dispose()
        {
            idleTimer.Dispose();
            lock (sync)
                Close();
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
