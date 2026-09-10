// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.Versioning;
using ManagedBass;
using ManagedBass.Mix;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using osu.Framework.Audio.Windows;
using osu.Framework.Logging;
using NAudioWaveFormat = NAudio.Wave.WaveFormat;

namespace osu.Framework.Audio.Wasapi
{
    /// <summary>
    /// Default Windows output: BASS decode mixer + NAudio shared WASAPI writer.
    /// Experimental audio continues to use BassWasapi; this path only serves <c>AudioOutputMode.Default</c>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class NAudioWasapiOutput : IDisposable
    {
        private MMDevice? device;
        private WasapiPlayer? player;
        private int? mixerHandle;
        private bool ownsMixer;

        public int? MixerHandle => mixerHandle;

        public int SampleRateHz { get; private set; }
        public int RequestedLatencyMs { get; private set; }
        public int ActualLatencyMs { get; private set; }
        public bool LowLatencyActive { get; private set; }

        /// <summary>
        /// Creates a decode mixer and starts NAudio playback that pulls from it.
        /// </summary>
        /// <param name="bassDeviceId">Currently selected BASS playback device (for driver/id matching).</param>
        /// <returns>Mixer handle on success; null on failure (caller should fall back to classic BASS output).</returns>
        public int? Start(int bassDeviceId)
        {
            Stop();

            if (bassDeviceId <= 0)
                return null;

            if (!Bass.GetDeviceInfo(bassDeviceId, out var bassInfo) || string.IsNullOrEmpty(bassInfo.Driver))
            {
                Logger.Log($"NAudio default output: BASS device {bassDeviceId} has no driver id.", name: "audio", level: LogLevel.Debug);
                return null;
            }

            device = WindowsAudioFormatQuery.TryOpenPlaybackDevice(bassInfo.Driver);

            if (device == null)
            {
                Logger.Log("NAudio default output: could not open matching MMDevice.", name: "audio", level: LogLevel.Important);
                return null;
            }

            NAudioWaveFormat? mixFormat = WindowsAudioFormatQuery.TryGetMixFormatForDriver(bassInfo.Driver)
                                          ?? WindowsAudioFormatQuery.TryGetDefaultPlaybackMixFormat();

            if (mixFormat == null || mixFormat.SampleRate <= 0 || mixFormat.Channels <= 0)
            {
                Logger.Log("NAudio default output: mix format unavailable.", name: "audio", level: LogLevel.Important);
                Stop();
                return null;
            }

            // Feed IEEE float into WASAPI shared; NAudio resamples/converts to the device mix format as needed.
            var sourceFormat = NAudioWaveFormat.CreateIeeeFloatWaveFormat(mixFormat.SampleRate, mixFormat.Channels);

            int handle = BassMix.CreateMixerStream(sourceFormat.SampleRate, sourceFormat.Channels,
                BassFlags.MixerNonStop | BassFlags.Decode | BassFlags.Float);

            if (handle == 0)
            {
                Logger.Log($"NAudio default output: CreateMixerStream failed: {Bass.LastError}", name: "audio", level: LogLevel.Error);
                Stop();
                return null;
            }

            mixerHandle = handle;
            ownsMixer = true;

            var provider = new BassMixerWaveProvider(sourceFormat, () => mixerHandle);

            try
            {
                var builder = new WasapiPlayerBuilder()
                    .WithDevice(device)
                    .WithSharedMode()
                    .WithEventSync()
                    .WithLowLatency(true)
                    .WithLatency(AudioOutputDefaults.DEFAULT_NAUDIO_LATENCY_MS);

                player = builder.Build();
                player.Init(provider);
                player.Play();

                SampleRateHz = sourceFormat.SampleRate;
                RequestedLatencyMs = AudioOutputDefaults.DEFAULT_NAUDIO_LATENCY_MS;
                ActualLatencyMs = player.LatencyMilliseconds;
                LowLatencyActive = player.LowLatencyActive;

                Logger.Log(
                    $"NAudio default output started: device=\"{device.FriendlyName}\", {sourceFormat.SampleRate}Hz/{sourceFormat.Channels}ch float, requestedLatency={RequestedLatencyMs}ms, actualLatency={ActualLatencyMs}ms, lowLatency={LowLatencyActive}",
                    name: "audio", level: LogLevel.Verbose);

                return mixerHandle;
            }
            catch (Exception ex)
            {
                Logger.Log($"NAudio default output failed to start: {ex.Message}", name: "audio", level: LogLevel.Error);
                Stop();
                return null;
            }
        }

        public void Stop()
        {
            try
            {
                player?.Stop();
            }
            catch
            {
            }

            try
            {
                player?.Dispose();
            }
            catch
            {
            }

            player = null;
            SampleRateHz = 0;
            RequestedLatencyMs = 0;
            ActualLatencyMs = 0;
            LowLatencyActive = false;

            if (ownsMixer && mixerHandle is int handle and > 0)
            {
                try
                {
                    Bass.StreamFree(handle);
                }
                catch
                {
                }
            }

            mixerHandle = null;
            ownsMixer = false;

            try
            {
                device?.Dispose();
            }
            catch
            {
            }

            device = null;
        }

        public void Dispose() => Stop();
    }
}
