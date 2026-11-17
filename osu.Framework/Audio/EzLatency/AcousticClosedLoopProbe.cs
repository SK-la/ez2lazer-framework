// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.Versioning;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using osu.Framework.Audio.Windows;
using osu.Framework.Logging;

namespace osu.Framework.Audio.EzLatency
{
    /// <summary>
    /// Bridges the game's current WASAPI render device into a loopback capture stream and
    /// treats RMS rising above a threshold as the acoustic playback point.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class AcousticClosedLoopProbe : IDisposable
    {
        private const double ignore_after_arm_ms = 8;
        private const float default_threshold = 0.02f;

        private readonly Lock sync = new Lock();
        private readonly Func<double> getTimestamp;
        private readonly Action onPollTimeout;
        private readonly Action<double, double, double, double> recordHardware;

        private WasapiRecorder? capture;
        private MMDevice? device;
        private string? boundDriverId;
        private volatile bool armed;
        private double armedInputTime;
        private float threshold = default_threshold;
        private volatile float peakRms;
        private volatile float currentRms;
        private bool disposed;
        private Timer? pollTimer;

        public AcousticClosedLoopProbe(Func<double> getTimestamp, Action onPollTimeout, Action<double, double, double, double> recordHardware)
        {
            this.getTimestamp = getTimestamp;
            this.onPollTimeout = onPollTimeout;
            this.recordHardware = recordHardware;
        }

        public bool IsRunning
        {
            get
            {
                lock (sync)
                    return capture != null;
            }
        }

        public float Threshold
        {
            get => threshold;
            set => threshold = Math.Clamp(value, 0.0001f, 1f);
        }

        /// <summary>Highest RMS observed on the current capture (0–1 linear).</summary>
        public float PeakRms => peakRms;

        /// <summary>RMS of the latest loopback buffer (0–1 linear).</summary>
        public float CurrentRms => currentRms;

        /// <summary>
        /// Opens (or reopens) loopback on the render endpoint matching <paramref name="bassDriverId"/>.
        /// </summary>
        public bool TryStart(string? bassDriverId)
        {
            if (disposed)
                return false;

            lock (sync)
            {
                if (capture != null && string.Equals(boundDriverId, bassDriverId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            stopCapture();

            MMDevice? opened = null;
            WasapiRecorder? started = null;
            bool attached = false;

            try
            {
                opened = WindowsAudioFormatQuery.TryOpenPlaybackDevice(bassDriverId);

                if (opened == null)
                {
                    Logger.Log("[EzLatency] acoustic loopback: no MMDevice for current output.", name: "ez_runtime", level: LogLevel.Debug);
                    return false;
                }

                started = new WasapiRecorderBuilder()
                          .WithDevice(opened)
                          .WithLoopbackCapture()
                          .WithBufferLength(20)
                          .Build();
                started.DataAvailable += onDataAvailable;
                started.RecordingStopped += onRecordingStopped;

                lock (sync)
                {
                    if (disposed)
                    {
                        started.Dispose();
                        opened.Dispose();
                        return false;
                    }

                    device = opened;
                    capture = started;
                    boundDriverId = bassDriverId ?? opened.ID;
                    peakRms = 0;
                    currentRms = 0;
                    attached = true;
                    pollTimer?.Dispose();
                    pollTimer = new Timer(_ =>
                    {
                        try
                        {
                            onPollTimeout();
                        }
                        catch
                        {
                        }
                    }, null, 50, 50);
                }

                started.StartRecording();
                Logger.Log($"[EzLatency] acoustic loopback started on {opened.FriendlyName}", name: "ez_runtime", level: LogLevel.Debug);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[EzLatency] acoustic loopback start failed: {ex.Message}", name: "ez_runtime", level: LogLevel.Debug);

                if (attached)
                {
                    stopCapture();
                }
                else
                {
                    try
                    {
                        started?.Dispose();
                    }
                    catch
                    {
                    }

                    try
                    {
                        opened?.Dispose();
                    }
                    catch
                    {
                    }
                }

                return false;
            }
        }

        public void Stop() => stopCapture();

        public void Arm(double inputTimeMs)
        {
            armedInputTime = inputTimeMs;
            armed = true;
        }

        public void Disarm() => armed = false;

        private void onDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
        {
            try
            {
                onPollTimeout();
            }
            catch
            {
                // best-effort
            }

            var cap = capture;

            bool silent = (flags & AudioClientBufferFlags.Silent) != 0;

            if (cap == null || buffer.IsEmpty || silent)
            {
                if (cap != null && silent)
                    currentRms = 0;

                return;
            }

            float rms = AcousticLevelMath.ComputeRms(buffer, toSampleFormat(cap.WaveFormat));
            currentRms = rms;

            if (rms > peakRms)
                peakRms = rms;

            if (!armed)
                return;

            double now = getTimestamp();
            if (now - armedInputTime < ignore_after_arm_ms)
                return;

            if (rms < threshold)
                return;

            armed = false;
            double roundtripMs = Math.Max(0, now - armedInputTime);

            try
            {
                // DriverTime = detect clock; OutputHardwareTime / LatencyDifference = round-trip ms.
                recordHardware(now, roundtripMs, 0, roundtripMs);
            }
            catch (Exception ex)
            {
                Logger.Log($"[EzLatency] acoustic record failed: {ex.Message}", name: "ez_runtime", level: LogLevel.Debug);
            }
        }

        private void onRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
                Logger.Log($"[EzLatency] acoustic loopback stopped: {e.Exception.Message}", name: "ez_runtime", level: LogLevel.Debug);
        }

        private static AcousticLevelMath.SampleFormat toSampleFormat(WaveFormat format)
        {
            if (format.Encoding == WaveFormatEncoding.IeeeFloat || format.BitsPerSample == 32)
                return AcousticLevelMath.SampleFormat.IeeeFloat32;

            if (format.BitsPerSample == 16)
                return AcousticLevelMath.SampleFormat.Pcm16;

            return AcousticLevelMath.SampleFormat.Unsupported;
        }

        private void stopCapture()
        {
            WasapiRecorder? captureToStop;
            MMDevice? deviceToDispose;
            Timer? timerToDispose;

            lock (sync)
            {
                armed = false;
                timerToDispose = pollTimer;
                pollTimer = null;
                captureToStop = capture;
                capture = null;
                deviceToDispose = device;
                device = null;
                boundDriverId = null;
                currentRms = 0;

                if (captureToStop != null)
                {
                    captureToStop.DataAvailable -= onDataAvailable;
                    captureToStop.RecordingStopped -= onRecordingStopped;
                }
            }

            timerToDispose?.Dispose();

            if (captureToStop != null)
            {
                try
                {
                    if (captureToStop.CaptureState != CaptureState.Stopped)
                        captureToStop.StopRecording();
                }
                catch (Exception ex)
                {
                    Logger.Log($"[EzLatency] acoustic loopback stop failed: {ex.Message}", name: "ez_runtime", level: LogLevel.Debug);
                }

                captureToStop.Dispose();
            }

            try
            {
                deviceToDispose?.Dispose();
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            Stop();
        }
    }
}
