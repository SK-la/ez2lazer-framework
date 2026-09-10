// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Logging;

namespace osu.Framework.Audio.EzLatency
{
    /// <summary>
    /// Shared Arm / ignore-window / RMS threshold logic for acoustic-style detection.
    /// Safe to call from audio pull callbacks when only observing while armed.
    /// </summary>
    internal sealed class DigitalLevelDetector
    {
        private const double ignore_after_arm_ms = 8;
        private const float default_threshold = 0.08f;

        private readonly Func<double> getTimestamp;
        private readonly Action onPollTimeout;
        private readonly Action<double, double, double, double> recordHardware;

        private volatile bool armed;
        private double armedInputTime;
        private float threshold = default_threshold;

        public DigitalLevelDetector(Func<double> getTimestamp, Action onPollTimeout, Action<double, double, double, double> recordHardware)
        {
            this.getTimestamp = getTimestamp;
            this.onPollTimeout = onPollTimeout;
            this.recordHardware = recordHardware;
        }

        public float Threshold
        {
            get => threshold;
            set => threshold = Math.Clamp(value, 0.0001f, 1f);
        }

        public bool IsArmed => armed;

        public void Arm(double inputTimeMs)
        {
            armedInputTime = inputTimeMs;
            armed = true;
        }

        public void Disarm() => armed = false;

        /// <summary>
        /// Soft-timeout poll + optional RMS detect on native PCM.
        /// </summary>
        public void Observe(IntPtr buffer, int bytes, AcousticLevelMath.SampleFormat format)
        {
            poll();

            if (!armed || bytes <= 0 || buffer == IntPtr.Zero || format == AcousticLevelMath.SampleFormat.Unsupported)
                return;

            double now = getTimestamp();
            if (now - armedInputTime < ignore_after_arm_ms)
                return;

            float rms = AcousticLevelMath.ComputeRms(buffer, bytes, format);
            tryRecord(now, rms);
        }

        /// <summary>IEEE float shortcut for ASIO / NAudio float pulls.</summary>
        public void ObserveFloat(IntPtr buffer, int bytes) =>
            Observe(buffer, bytes, AcousticLevelMath.SampleFormat.IeeeFloat32);

        /// <summary>
        /// Soft-timeout poll + optional RMS detect on a managed PCM byte buffer.
        /// </summary>
        public void Observe(byte[] buffer, int bytes, AcousticLevelMath.SampleFormat format)
        {
            poll();

            if (!armed || bytes <= 0 || format == AcousticLevelMath.SampleFormat.Unsupported)
                return;

            double now = getTimestamp();
            if (now - armedInputTime < ignore_after_arm_ms)
                return;

            float rms = AcousticLevelMath.ComputeRms(buffer, bytes, format);
            tryRecord(now, rms);
        }

        private void poll()
        {
            try
            {
                onPollTimeout();
            }
            catch
            {
                // best-effort
            }
        }

        private void tryRecord(double now, float rms)
        {
            if (rms < threshold)
                return;

            armed = false;
            double roundtripMs = Math.Max(0, now - armedInputTime);

            try
            {
                recordHardware(now, roundtripMs, 0, roundtripMs);
            }
            catch (Exception ex)
            {
                Logger.Log($"[EzLatency] digital level record failed: {ex.Message}", name: "ez_runtime", level: LogLevel.Debug);
            }
        }
    }
}
