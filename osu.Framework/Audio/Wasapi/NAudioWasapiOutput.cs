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
        private bool ownsMixer;

        public int? MixerHandle { get; private set; }

        /// <summary>WASAPI endpoint id currently owned by this output, if running.</summary>
        public string? BoundEndpointId { get; private set; }

        public int SampleRateHz { get; private set; }
        public int RequestedLatencyMs { get; private set; }
        public int ActualLatencyMs { get; private set; }
        public bool LowLatencyActive { get; private set; }

        public bool IsRunning => player != null && MixerHandle is > 0;

        /// <summary>
        /// Creates a decode mixer and starts NAudio playback that pulls from it.
        /// </summary>
        /// <param name="bassDeviceId">Currently selected BASS playback device (for driver/id matching).</param>
        /// <returns>Mixer handle on success; null on failure (caller should fall back to classic BASS output).</returns>
        public int? Start(int bassDeviceId)
        {
            Stop();

            if (bassDeviceId <= 0)
            {
                Logger.Log($"NAudio default output: refusing BASS device {bassDeviceId}.", name: "audio", level: LogLevel.Important);
                return null;
            }

            if (!Bass.GetDeviceInfo(bassDeviceId, out var bassInfo))
            {
                Logger.Log($"NAudio default output: BASS.GetDeviceInfo({bassDeviceId}) failed.", name: "audio", level: LogLevel.Important);
                return null;
            }

            // BASS's synthetic "Default" device often reports an empty Driver id. That is not a
            // failure — open the Windows default render endpoint instead of aborting to classic BASS.
            string? driverId = string.IsNullOrEmpty(bassInfo.Driver) ? null : bassInfo.Driver;
            string? endpointId = WindowsAudioFormatQuery.TryResolvePlaybackEndpointId(driverId);

            device = WindowsAudioFormatQuery.TryOpenPlaybackDevice(driverId);

            if (device == null)
            {
                Logger.Log(
                    $"NAudio default output: could not open MMDevice for bassDevice={bassDeviceId} (\"{bassInfo.Name}\", driver={driverId ?? "empty"}).",
                    name: "audio",
                    level: LogLevel.Important);
                return null;
            }

            NAudioWaveFormat? mixFormat = (driverId != null
                                              ? WindowsAudioFormatQuery.TryGetMixFormatForDriver(driverId)
                                              : null)
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

            MixerHandle = handle;
            ownsMixer = true;
            BoundEndpointId = endpointId ?? device.ID;

            var provider = new BassMixerWaveProvider(sourceFormat, () => MixerHandle);

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
                    $"NAudio default output started: bassDevice={bassDeviceId} (\"{bassInfo.Name}\"), wasapi=\"{device.FriendlyName}\", endpoint={BoundEndpointId}, {sourceFormat.SampleRate}Hz/{sourceFormat.Channels}ch float, requestedLatency={RequestedLatencyMs}ms, actualLatency={ActualLatencyMs}ms, lowLatency={LowLatencyActive}"
                    + (driverId == null ? " (via Windows default endpoint; BASS Driver empty)" : string.Empty),
                    name: "audio", level: LogLevel.Important);

                // Return the local handle — never re-read MixerHandle (Stop from another path could null it).
                return handle;
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
            BoundEndpointId = null;

            if (ownsMixer && MixerHandle is int handle and > 0)
            {
                try
                {
                    Bass.StreamFree(handle);
                }
                catch
                {
                }
            }

            MixerHandle = null;
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
