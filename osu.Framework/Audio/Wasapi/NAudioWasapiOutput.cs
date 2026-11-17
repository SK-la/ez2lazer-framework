// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.Versioning;
using System.Threading;
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
        private string? playbackDriverId;
        private bool ownsMixer;

        public int? MixerHandle { get; private set; }

        /// <summary>WASAPI endpoint id currently owned by this output, if running.</summary>
        public string? BoundEndpointId { get; private set; }

        public int SampleRateHz { get; private set; }
        public int RequestedLatencyMs { get; private set; }
        public int ActualLatencyMs { get; private set; }
        public bool LowLatencyActive { get; private set; }

        public bool IsRunning => player != null && MixerHandle is int runningHandle && runningHandle != 0;

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
            playbackDriverId = string.IsNullOrEmpty(bassInfo.Driver) ? null : bassInfo.Driver;
            string? endpointId = WindowsAudioFormatQuery.TryResolvePlaybackEndpointId(playbackDriverId);

            device = WindowsAudioFormatQuery.TryOpenPlaybackDevice(playbackDriverId);

            if (device == null)
            {
                Logger.Log(
                    $"NAudio default output: could not open MMDevice for bassDevice={bassDeviceId} (\"{bassInfo.Name}\", driver={playbackDriverId ?? "empty"}).",
                    name: "audio",
                    level: LogLevel.Important);
                return null;
            }

            var deviceFormat = WindowsAudioFormatQuery.TryGetDeviceFormat(device);
            int sampleRate = deviceFormat is { SampleRate: > 0 } ? deviceFormat.SampleRate : AudioOutputDefaults.DEFAULT_SAMPLE_RATE;
            int channels = deviceFormat is { Channels: > 0 } ? deviceFormat.Channels : 2;

            var sourceFormat = NAudioWaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

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
                Exception? lastError = null;
                var attempts = new (bool eventSync, int latencyMs)[]
                {
                    (true, AudioOutputDefaults.DEFAULT_NAUDIO_LATENCY_MS),
                    (false, AudioOutputDefaults.DEFAULT_NAUDIO_LATENCY_MS),
                    (true, 50),
                    (false, 50),
                };

                bool started = false;
                int startedLatencyMs = 0;

                for (int i = 0; i < attempts.Length; i++)
                {
                    if (i > 0 && !reopenPlaybackDevice())
                        break;

                    if (tryStartPlayer(provider, attempts[i].eventSync, attempts[i].latencyMs, out lastError))
                    {
                        started = true;
                        startedLatencyMs = attempts[i].latencyMs;
                        break;
                    }

                    // 0x800700AA: this endpoint was just released (default and the named device are often the same DAC).
                    if (lastError?.HResult == unchecked((int)0x800700AA)
                        || lastError?.Message.Contains("800700AA", StringComparison.OrdinalIgnoreCase) == true)
                        Thread.Sleep(250);
                }

                if (!started)
                {
                    Logger.Log($"NAudio default output failed to start: {lastError?.Message}", name: "audio", level: LogLevel.Error);
                    Stop();
                    return null;
                }

                SampleRateHz = sourceFormat.SampleRate;
                RequestedLatencyMs = startedLatencyMs;
                ActualLatencyMs = player!.LatencyMilliseconds;
                LowLatencyActive = player.LowLatencyActive;

                string lowLatencyNote = LowLatencyActive
                    ? "lowLatency=true"
                    : $"lowLatency=false ({player.LowLatencyUnavailableReason ?? "standard shared mode"})";

                Logger.Log(
                    $"NAudio default output started: bassDevice={bassDeviceId} (\"{bassInfo.Name}\"), wasapi=\"{device.FriendlyName}\", endpoint={BoundEndpointId}, {sourceFormat.SampleRate}Hz/{sourceFormat.Channels}ch float, requestedLatency={RequestedLatencyMs}ms, actualLatency={ActualLatencyMs}ms, {lowLatencyNote}, mmcss={AudioOutputDefaults.DEFAULT_NAUDIO_MMCSS_TASK} (requested)"
                    + (playbackDriverId == null ? " (via Windows default endpoint; BASS Driver empty)" : string.Empty),
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

        private bool reopenPlaybackDevice()
        {
            disposePlayer();

            try
            {
                device?.Dispose();
            }
            catch
            {
            }

            device = WindowsAudioFormatQuery.TryOpenPlaybackDevice(playbackDriverId);
            return device != null;
        }

        private bool tryStartPlayer(IWaveProvider provider, bool eventSync, int latencyMs, out Exception? error)
        {
            error = null;
            disposePlayer();

            try
            {
                var builder = new WasapiPlayerBuilder()
                              .WithDevice(device!)
                              .WithSharedMode();

                builder = eventSync ? builder.WithEventSync() : builder.WithPollingSync();
                builder = builder
                          .WithMmcssThreadPriority(AudioOutputDefaults.DEFAULT_NAUDIO_MMCSS_TASK)
                          .WithLatency(latencyMs);

                player = builder.Build();
                player.Init(provider);
                player.Play();
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                disposePlayer();
                return false;
            }
        }

        private void disposePlayer()
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
        }

        public void Stop()
        {
            disposePlayer();
            SampleRateHz = 0;
            RequestedLatencyMs = 0;
            ActualLatencyMs = 0;
            LowLatencyActive = false;
            BoundEndpointId = null;

            if (ownsMixer && MixerHandle is int handle && handle != 0)
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
            playbackDriverId = null;
        }

        public void Dispose() => Stop();
    }
}
