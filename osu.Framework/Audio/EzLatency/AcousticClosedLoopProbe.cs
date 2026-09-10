// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
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
    public sealed class AcousticClosedLoopProbe : IDisposable
    {
        private const double ignore_after_arm_ms = 8;
        private const float default_threshold = 0.02f;

        private readonly object sync = new object();
        private readonly Func<double> getTimestamp;
        private readonly Action onPollTimeout;
        private readonly Action<double, double, double, double> recordHardware;

#pragma warning disable CS0618 // WasapiLoopbackCapture is obsolete in NAudio 3 but still the simplest system-wide loopback API.
        private WasapiLoopbackCapture? capture;
#pragma warning restore CS0618
        private MMDevice? device;
        private string? boundDriverId;
        private volatile bool armed;
        private double armedInputTime;
        private float threshold = default_threshold;
        private bool disposed;

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

        /// <summary>
        /// Opens (or reopens) loopback on the render endpoint matching <paramref name="bassDriverId"/>.
        /// </summary>
        public bool TryStart(string? bassDriverId)
        {
            if (disposed)
                return false;

            if (!OperatingSystem.IsWindows())
                return false;

            lock (sync)
            {
                if (capture != null && string.Equals(boundDriverId, bassDriverId, StringComparison.OrdinalIgnoreCase))
                    return true;

                stopCaptureLocked();

                try
                {
                    device = WindowsAudioFormatQuery.TryOpenPlaybackDevice(bassDriverId);

                    if (device == null)
                    {
                        Logger.Log("[EzLatency] acoustic loopback: no MMDevice for current output.", name: "audio", level: LogLevel.Debug);
                        return false;
                    }

#pragma warning disable CS0618
                    capture = new WasapiLoopbackCapture(device);
#pragma warning restore CS0618
                    capture.DataAvailable += onDataAvailable;
                    capture.RecordingStopped += onRecordingStopped;
                    capture.StartRecording();
                    boundDriverId = bassDriverId ?? device.ID;

                    Logger.Log($"[EzLatency] acoustic loopback started on {device.FriendlyName}", name: "audio", level: LogLevel.Debug);
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[EzLatency] acoustic loopback start failed: {ex.Message}", name: "audio", level: LogLevel.Debug);
                    stopCaptureLocked();
                    return false;
                }
            }
        }

        public void Stop()
        {
            lock (sync)
                stopCaptureLocked();
        }

        public void Arm(double inputTimeMs)
        {
            armedInputTime = inputTimeMs;
            armed = true;
        }

        public void Disarm() => armed = false;

        private void onDataAvailable(object? sender, WaveInEventArgs e)
        {
            try
            {
                onPollTimeout();
            }
            catch
            {
                // best-effort
            }

            if (!armed || e.BytesRecorded <= 0 || capture == null)
                return;

            double now = getTimestamp();
            if (now - armedInputTime < ignore_after_arm_ms)
                return;

            float rms = computeRms(e.Buffer, e.BytesRecorded, capture.WaveFormat);
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
                Logger.Log($"[EzLatency] acoustic record failed: {ex.Message}", name: "audio", level: LogLevel.Debug);
            }
        }

        private void onRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
                Logger.Log($"[EzLatency] acoustic loopback stopped: {e.Exception.Message}", name: "audio", level: LogLevel.Debug);
        }

        private static float computeRms(byte[] buffer, int bytesRecorded, WaveFormat format)
        {
            if (format.Encoding == WaveFormatEncoding.IeeeFloat || format.BitsPerSample == 32)
            {
                int samples = bytesRecorded / 4;
                if (samples <= 0)
                    return 0;

                double sum = 0;
                for (int i = 0; i < samples; i++)
                {
                    float sample = BitConverter.ToSingle(buffer, i * 4);
                    sum += sample * sample;
                }

                return (float)Math.Sqrt(sum / samples);
            }

            if (format.BitsPerSample == 16)
            {
                int samples = bytesRecorded / 2;
                if (samples <= 0)
                    return 0;

                double sum = 0;
                for (int i = 0; i < samples; i++)
                {
                    short sample = BitConverter.ToInt16(buffer, i * 2);
                    float n = sample / 32768f;
                    sum += n * n;
                }

                return (float)Math.Sqrt(sum / samples);
            }

            return 0;
        }

        private void stopCaptureLocked()
        {
            armed = false;

            if (capture != null)
            {
                try
                {
                    capture.DataAvailable -= onDataAvailable;
                    capture.RecordingStopped -= onRecordingStopped;

                    if (capture.CaptureState != CaptureState.Stopped)
                        capture.StopRecording();
                }
                catch (Exception ex)
                {
                    Logger.Log($"[EzLatency] acoustic loopback stop failed: {ex.Message}", name: "audio", level: LogLevel.Debug);
                }

                capture.Dispose();
                capture = null;
            }

            device?.Dispose();
            device = null;
            boundDriverId = null;
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
