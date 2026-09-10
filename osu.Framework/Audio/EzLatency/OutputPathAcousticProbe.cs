// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Framework.Audio.EzLatency
{
    /// <summary>
    /// Product In→Acou sensor: RMS on PCM as it is pulled into the active output driver
    /// (NAudio / BassWasapi / ASIO), not WASAPI loopback and not a microphone.
    /// </summary>
    public sealed class OutputPathAcousticProbe
    {
        private readonly DigitalLevelDetector detector;

        public OutputPathAcousticProbe(Func<double> getTimestamp, Action onPollTimeout, Action<double, double, double, double> recordHardware)
        {
            detector = new DigitalLevelDetector(getTimestamp, onPollTimeout, recordHardware);
        }

        public float Threshold
        {
            get => detector.Threshold;
            set => detector.Threshold = value;
        }

        public bool IsArmed => detector.IsArmed;

        public void Arm(double inputTimeMs) => detector.Arm(inputTimeMs);

        public void Disarm() => detector.Disarm();

        /// <summary>Observe native PCM from a driver pull callback.</summary>
        public void Observe(IntPtr buffer, int bytes, AcousticLevelMath.SampleFormat format) =>
            detector.Observe(buffer, bytes, format);

        /// <summary>Observe IEEE-float interleaved PCM from a driver pull callback.</summary>
        public void ObserveFloat(IntPtr buffer, int bytes) => detector.ObserveFloat(buffer, bytes);

        /// <summary>Observe managed PCM (e.g. NAudio <c>BassMixerWaveProvider</c>).</summary>
        public void Observe(byte[] buffer, int bytes, AcousticLevelMath.SampleFormat format) =>
            detector.Observe(buffer, bytes, format);
    }
}
