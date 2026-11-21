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
                    {
                        resultImage.Dispose();
                        return false;
                    }

                    image = resultImage;
                    decoded = true;
                    return true;
                }
                catch
                {
                    resultImage.Dispose();
                    throw;
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
