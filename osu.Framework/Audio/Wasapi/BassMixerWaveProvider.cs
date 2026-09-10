// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.InteropServices;
using ManagedBass;
using NAudio.Wave;
using NAudioWaveFormat = NAudio.Wave.WaveFormat;
using osu.Framework.Audio.EzLatency;

namespace osu.Framework.Audio.Wasapi
{
    /// <summary>
    /// Pulls IEEE-float PCM from a BASS decode mixer into an NAudio <see cref="IWaveProvider"/>.
    /// Used by the default Windows output path (NAudio writes the device; BASS only mixes).
    /// </summary>
    internal sealed class BassMixerWaveProvider : IWaveProvider
    {
        private readonly Func<int?> mixerHandleProvider;

        public BassMixerWaveProvider(NAudioWaveFormat waveFormat, Func<int?> mixerHandleProvider)
        {
            WaveFormat = waveFormat ?? throw new ArgumentNullException(nameof(waveFormat));
            this.mixerHandleProvider = mixerHandleProvider ?? throw new ArgumentNullException(nameof(mixerHandleProvider));
        }

        public NAudioWaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty)
                return 0;

            int? mixer = mixerHandleProvider();

            if (mixer is not > 0)
            {
                buffer.Clear();
                return buffer.Length;
            }

            byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(buffer.Length);
            var handle = GCHandle.Alloc(rented, GCHandleType.Pinned);

            try
            {
                int read = Bass.ChannelGetData(mixer.Value, handle.AddrOfPinnedObject(), buffer.Length | (int)DataFlags.Float);

                if (read < 0)
                {
                    buffer.Clear();
                    return buffer.Length;
                }

                if (read > 0)
                    EzLatencyManager.GLOBAL.ObserveOutputPath(rented, read, AcousticLevelMath.SampleFormat.IeeeFloat32);

                rented.AsSpan(0, Math.Min(read, buffer.Length)).CopyTo(buffer);
                if (read < buffer.Length)
                    buffer[read..].Clear();

                // Always report a full buffer so WASAPI keeps the stream alive during silence.
                return buffer.Length;
            }
            finally
            {
                handle.Free();
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
