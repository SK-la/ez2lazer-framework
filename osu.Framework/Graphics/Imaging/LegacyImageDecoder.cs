// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using StbiSharp;

namespace osu.Framework.Graphics.Imaging
{
    internal static class LegacyImageDecoder
    {
        public static RgbaImage DecodeTexture(Stream stream)
        {
            if (!stream.CanRead)
                throw new ArgumentException("The image stream must be readable.", nameof(stream));

            if (!stream.CanSeek)
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                copy.Position = 0;
                return DecodeTexture(copy);
            }

            if (TryDecodeStbi(stream, out RgbaImage? image))
                return image;

            throw new NotSupportedException("The legacy decoder does not support this image format or could not decode the image.");
        }

        public static bool TryDecodeStbi(Stream stream, [NotNullWhen(true)] out RgbaImage? image)
        {
            image = null;

            if (!stream.CanRead || !stream.CanSeek)
                return false;

            long initialPosition = stream.Position;
            long remainingLength = stream.Length - initialPosition;

            if (remainingLength <= 0 || remainingLength > int.MaxValue)
                return false;

            byte[] encoded = ArrayPool<byte>.Shared.Rent((int)remainingLength);
            bool decoded = false;

            try
            {
                stream.ReadExactly(encoded.AsSpan(0, (int)remainingLength));
                decoded = TryDecodeStbi(encoded.AsSpan(0, (int)remainingLength), out image);
                return decoded;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
            {
                image?.Dispose();
                image = null;
                return false;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(encoded);

                if (!decoded)
                    stream.Position = initialPosition;
            }
        }

        public static bool TryDecodeStbi(ReadOnlySpan<byte> encoded, [NotNullWhen(true)] out RgbaImage? image)
        {
            image = null;

            try
            {
                using var decoded = Stbi.LoadFromMemory(encoded, 4);
                int width = decoded.Width;
                int height = decoded.Height;
                int pixelCount = checked(width * height);
                ReadOnlySpan<Rgba32> pixels = MemoryMarshal.Cast<byte, Rgba32>(decoded.Data);

                if (width <= 0 || height <= 0 || pixels.Length != pixelCount)
                    return false;

                image = new RgbaImage(width, height, pixels);
                return true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
            {
                image?.Dispose();
                image = null;
                return false;
            }
        }
    }
}
