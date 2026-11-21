// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;

namespace osu.Framework.Graphics.Imaging
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Rgba32 : IEquatable<Rgba32>
    {
        public byte R;
        public byte G;
        public byte B;
        public byte A;

        public Rgba32(byte r, byte g, byte b, byte a = byte.MaxValue)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public Rgba32(float r, float g, float b, float a = 1)
            : this(toByte(r), toByte(g), toByte(b), toByte(a))
        {
        }

        public Rgba32(Vector4 value)
            : this(value.X, value.Y, value.Z, value.W)
        {
        }

        public Vector4 ToVector4() => new Vector4(R / 255f, G / 255f, B / 255f, A / 255f);

        public bool Equals(Rgba32 other) => R == other.R && G == other.G && B == other.B && A == other.A;
        public override bool Equals(object? obj) => obj is Rgba32 other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(R, G, B, A);
        public static bool operator ==(Rgba32 left, Rgba32 right) => left.Equals(right);
        public static bool operator !=(Rgba32 left, Rgba32 right) => !left.Equals(right);

        private static byte toByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);
    }

    public sealed class RgbaImage : IDisposable
    {
        private readonly IMemoryOwner<Rgba32> pixelOwner;
        private bool disposed;

        public int Width { get; }
        public int Height { get; }
        public int PixelCount { get; }

        public Span<Rgba32> PixelSpan => getPixelMemory().Span;
        public Memory<Rgba32> PixelMemory => getPixelMemory();

        public RgbaImage(int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(width <= 0 ? nameof(width) : nameof(height));

            Width = width;
            Height = height;
            PixelCount = checked(width * height);
            pixelOwner = MemoryPool<Rgba32>.Shared.Rent(PixelCount);
            pixelOwner.Memory.Span[..PixelCount].Clear();
        }

        public RgbaImage(int width, int height, Rgba32 fill)
            : this(width, height)
        {
            PixelSpan.Fill(fill);
        }

        public RgbaImage(int width, int height, ReadOnlySpan<Rgba32> source)
            : this(width, height)
        {
            if (source.Length != PixelCount)
            {
                Dispose();
                throw new ArgumentException("Pixel data length does not match image dimensions.", nameof(source));
            }

            source.CopyTo(PixelSpan);
        }

        public Rgba32 this[int x, int y]
        {
            get => PixelSpan[getPixelIndex(x, y)];
            set => PixelSpan[getPixelIndex(x, y)] = value;
        }

        public Span<Rgba32> GetPixelRowSpan(int y)
        {
            if ((uint)y >= (uint)Height)
                throw new ArgumentOutOfRangeException(nameof(y));

            return PixelSpan.Slice(y * Width, Width);
        }

        public ReadOnlySpan<Rgba32> GetReadOnlyPixelRowSpan(int y) => GetPixelRowSpan(y);

        public void Dispose()
        {
            if (disposed)
                return;

            pixelOwner.Dispose();
            disposed = true;
        }

        private int getPixelIndex(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
                throw new ArgumentOutOfRangeException(x < 0 || x >= Width ? nameof(x) : nameof(y));

            return y * Width + x;
        }

        private Memory<Rgba32> getPixelMemory()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return pixelOwner.Memory[..PixelCount];
        }
    }
}
