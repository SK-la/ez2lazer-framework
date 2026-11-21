// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using SkiaSharp;

namespace osu.Framework.Graphics.Imaging
{
    internal static unsafe class SkiaStaticImageDecoder
    {
        public static bool IsGif(Stream stream)
        {
            if (!stream.CanRead || !stream.CanSeek)
                return false;

            long initialPosition = stream.Position;
            Span<byte> header = stackalloc byte[6];
            int read = 0;

            try
            {
                while (read < header.Length)
                {
                    int count = stream.Read(header[read..]);
                    if (count == 0)
                        break;

                    read += count;
                }

                return read == header.Length && (header.SequenceEqual("GIF87a"u8) || header.SequenceEqual("GIF89a"u8));
            }
            finally
            {
                stream.Position = initialPosition;
            }
        }

        public static bool TryDecode(Stream stream, [NotNullWhen(true)] out RgbaImage? image)
        {
            image = null;

            if (!stream.CanRead || !stream.CanSeek)
                return false;

            long initialPosition = stream.Position;
            if (!hasSupportedFormat(stream, initialPosition))
                return false;

            bool decoded = false;

            try
            {
                using var skStream = new SKManagedStream(stream, disposeManagedStream: false);
                using var codec = SKCodec.Create(skStream);
                if (codec == null)
                    return false;

                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                var resultImage = new RgbaImage(info.Width, info.Height);

                try
                {
                    SKCodecResult result;

                    fixed (Rgba32* pixels = resultImage.PixelSpan)
                        result = codec.GetPixels(info, (IntPtr)pixels, info.RowBytes, new SKCodecOptions(0, -1));

                    if (result != SKCodecResult.Success)
                        return false;

                    image = applyEncodedOrigin(resultImage, codec.EncodedOrigin);
                    decoded = true;
                    return true;
                }
                finally
                {
                    if (image != resultImage)
                        resultImage.Dispose();
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
            {
                return false;
            }
            finally
            {
                if (!decoded)
                    stream.Position = initialPosition;
            }
        }

        private static RgbaImage applyEncodedOrigin(RgbaImage source, SKEncodedOrigin origin)
        {
            if (origin is < SKEncodedOrigin.TopRight or > SKEncodedOrigin.LeftBottom)
                return source;

            int width = source.Width;
            int height = source.Height;
            bool transpose = origin >= SKEncodedOrigin.LeftTop;
            var result = new RgbaImage(transpose ? height : width, transpose ? width : height);

            // Destination index of the first source pixel and the strides along the source axes.
            (int offset, int xStep, int yStep) = origin switch
            {
                SKEncodedOrigin.TopRight => (width - 1, -1, width),
                SKEncodedOrigin.BottomRight => (source.PixelCount - 1, -1, -width),
                SKEncodedOrigin.BottomLeft => ((height - 1) * width, 1, -width),
                SKEncodedOrigin.LeftTop => (0, height, 1),
                SKEncodedOrigin.RightTop => (height - 1, height, -1),
                SKEncodedOrigin.RightBottom => (source.PixelCount - 1, -height, -1),
                SKEncodedOrigin.LeftBottom => ((width - 1) * height, -height, 1),
                _ => (0, 1, width),
            };

            ReadOnlySpan<Rgba32> sourcePixels = source.PixelSpan;
            Span<Rgba32> destinationPixels = result.PixelSpan;

            for (int y = 0; y < height; y++)
            {
                int destinationIndex = offset + y * yStep;

                foreach (Rgba32 pixel in sourcePixels.Slice(y * width, width))
                {
                    destinationPixels[destinationIndex] = pixel;
                    destinationIndex += xStep;
                }
            }

            return result;
        }

        private static bool hasSupportedFormat(Stream stream, long initialPosition)
        {
            Span<byte> header = stackalloc byte[12];
            int read = 0;

            try
            {
                while (read < header.Length)
                {
                    int count = stream.Read(header[read..]);
                    if (count == 0)
                        break;

                    read += count;
                }

                if (read < header.Length)
                    return false;

                header = header[..read];

                bool png = header.Length >= 8
                           && header[0] == 0x89
                           && header[1] == 'P'
                           && header[2] == 'N'
                           && header[3] == 'G'
                           && header[4] == 0x0d
                           && header[5] == 0x0a
                           && header[6] == 0x1a
                           && header[7] == 0x0a;
                bool jpeg = header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff;
                bool gif = header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8);
                bool bmp = header.Length >= 2 && header[0] == 'B' && header[1] == 'M';
                bool webp = header.Length >= 12
                            && header[..4].SequenceEqual("RIFF"u8)
                            && header[8..12].SequenceEqual("WEBP"u8);

                return png || jpeg || gif || bmp || webp;
            }
            finally
            {
                stream.Position = initialPosition;
            }
        }
    }
}
