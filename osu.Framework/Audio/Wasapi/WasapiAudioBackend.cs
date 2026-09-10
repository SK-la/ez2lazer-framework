// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using ManagedBass;

namespace osu.Framework.Audio.Wasapi
{
    /// <summary>
    /// Legacy prototype backend retained for optional hardware-timestamp experiments.
    /// Default Windows playback now goes through <see cref="NAudioWasapiOutput"/> (Bass decode mixer + WasapiPlayer).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class WasapiAudioBackend : IAudioBackend
    {
        private readonly Stopwatch stopwatch = new Stopwatch();
        private readonly Func<int?> globalMixerHandleProvider;
        private int deviceIndex = -1;
        private bool initialized;

        public WasapiAudioBackend(Func<int?>? globalMixerHandleProvider = null)
        {
            this.globalMixerHandleProvider = globalMixerHandleProvider ?? (() => null);
        }

        public string DebugInfo => $"WasapiAudioBackend (initialized={initialized}, deviceIndex={deviceIndex}; playback via NAudioWasapiOutput)";

        public void Initialize(int deviceIndex)
        {
            this.deviceIndex = deviceIndex;
            stopwatch.Restart();
            initialized = true;
        }

        public void UpdateDevice(int deviceIndex)
        {
            this.deviceIndex = deviceIndex;
            stopwatch.Restart();
        }

        public double GetDeviceTimeSeconds()
        {
            try
            {
                int? handle = globalMixerHandleProvider.Invoke();

                if (handle.HasValue && handle.Value != 0)
                {
                    long pos = Bass.ChannelGetPosition(handle.Value);

                    if (pos != -1)
                    {
                        double secs = Bass.ChannelBytes2Seconds(handle.Value, pos);
                        if (!double.IsNaN(secs) && !double.IsInfinity(secs))
                            return secs;
                    }
                }
            }
            catch
            {
                // If any BASS call fails, fall back to stopwatch.
            }

            return stopwatch.Elapsed.TotalSeconds;
        }

        public bool TryGetHardwareTimestamp(out long deviceTimestampNs)
        {
            deviceTimestampNs = 0;
            return false;
        }

        public void Dispose()
        {
            stopwatch.Stop();
            initialized = false;
        }
    }
}
