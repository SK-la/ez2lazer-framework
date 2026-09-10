// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Framework.Audio.EzLatency
{
    /// <summary>
    /// Platform-agnostic RMS helpers for acoustic closed-loop detection (unit-testable).
    /// </summary>
    internal static class AcousticLevelMath
    {
        internal enum SampleFormat
        {
            IeeeFloat32,
            Pcm16,
            Unsupported
        }

        /// <summary>RMS of interleaved PCM samples.</summary>
        internal static float ComputeRms(byte[] buffer, int bytesRecorded, SampleFormat format)
        {
            if (format == SampleFormat.IeeeFloat32)
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

            if (format == SampleFormat.Pcm16)
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
    }
}
