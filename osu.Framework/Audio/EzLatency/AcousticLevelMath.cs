// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Framework.Audio.EzLatency
{
    /// <summary>
    /// Platform-agnostic RMS helpers for acoustic closed-loop detection (unit-testable).
    /// </summary>
    public static class AcousticLevelMath
    {
        public enum SampleFormat
        {
            IeeeFloat32,
            Pcm16,
            Unsupported
        }

        /// <summary>RMS of interleaved PCM at <paramref name="buffer"/>.</summary>
        public static unsafe float ComputeRms(IntPtr buffer, int bytes, SampleFormat format)
        {
            if (bytes <= 0 || buffer == IntPtr.Zero || format == SampleFormat.Unsupported)
                return 0;

            if (format == SampleFormat.IeeeFloat32)
            {
                int samples = bytes / 4;
                if (samples <= 0)
                    return 0;

                float* ptr = (float*)buffer;
                double sum = 0;

                for (int i = 0; i < samples; i++)
                {
                    float sample = ptr[i];
                    sum += sample * sample;
                }

                return (float)Math.Sqrt(sum / samples);
            }

            if (format == SampleFormat.Pcm16)
            {
                int samples = bytes / 2;
                if (samples <= 0)
                    return 0;

                short* ptr = (short*)buffer;
                double sum = 0;

                for (int i = 0; i < samples; i++)
                {
                    float n = ptr[i] / 32768f;
                    sum += n * n;
                }

                return (float)Math.Sqrt(sum / samples);
            }

            return 0;
        }

        /// <summary>RMS of interleaved IEEE float32 samples at <paramref name="buffer"/>.</summary>
        public static float ComputeRmsFloat(IntPtr buffer, int bytes) =>
            ComputeRms(buffer, bytes, SampleFormat.IeeeFloat32);

        /// <summary>RMS of interleaved PCM samples.</summary>
        public static float ComputeRms(byte[] buffer, int bytesRecorded, SampleFormat format)
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
