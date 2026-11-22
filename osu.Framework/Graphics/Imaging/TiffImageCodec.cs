// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using TiffLibrary;
using TiffLibrary.PixelFormats;

namespace osu.Framework.Graphics.Imaging
{
    internal static class TiffImageCodec
    {
        public static bool IsTiff(Stream stream)
        {
            if (!stream.CanRead || !stream.CanSeek)
                return false;

            long initialPosition = stream.Position;
            Span<byte> header = stackalloc byte[4];
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
            }
            finally
            {
                stream.Position = initialPosition;
            }

            return isTiff(header[..read]);
        }

        public static RgbaImage Decode(Stream stream)
        {
            long initialPosition = stream.CanSeek ? stream.Position : 0;
            bool decoded = false;
            MemoryStream? copy = null;

            try
            {
                Stream input = stream;

                if (!stream.CanSeek || stream.Position != 0)
                {
                    copy = new MemoryStream();
                    stream.CopyTo(copy);
                    copy.Position = 0;
                    input = copy;
                }

                RgbaImage image = decode(input);
                decoded = true;
                return image;
            }
            finally
            {
                copy?.Dispose();

                if (!decoded && stream.CanSeek)
                    stream.Position = initialPosition;
            }
        }

        public static byte[] Encode(RgbaImage image)
        {
            using var stream = new MemoryStream();
            using var pixels = MemoryPool<TiffRgba32>.Shared.Rent(image.PixelCount);
            MemoryMarshal.Cast<Rgba32, TiffRgba32>(image.PixelSpan).CopyTo(pixels.Memory.Span);
            var buffer = new TiffMemoryPixelBuffer<TiffRgba32>(pixels.Memory, image.Width, image.Height, writable: false);
            var encoder = new TiffImageEncoderBuilder
            {
                PhotometricInterpretation = TiffPhotometricInterpretation.RGB,
                Compression = TiffCompression.Deflate,
                EnableTransparencyForRgb = true,
            }.Build<TiffRgba32>();

            using var writer = TiffFileWriter.OpenAsync(stream, leaveOpen: true).GetAwaiter().GetResult();
            using var directory = writer.CreateImageFileDirectory();
            encoder.EncodeAsync(directory, buffer).GetAwaiter().GetResult();
            writer.SetFirstImageFileDirectoryOffset(directory.FlushAsync().GetAwaiter().GetResult());
            writer.FlushAsync().GetAwaiter().GetResult();
            return stream.ToArray();
        }

        private static RgbaImage decode(Stream stream)
        {
            using var reader = TiffFileReader.Open(stream, leaveOpen: true);
            var decoder = reader.CreateImageDecoder();
            using var pixels = MemoryPool<TiffRgba32>.Shared.Rent(checked(decoder.Width * decoder.Height));
            var buffer = new TiffMemoryPixelBuffer<TiffRgba32>(pixels.Memory, decoder.Width, decoder.Height, writable: true);
            decoder.DecodeAsync(buffer).GetAwaiter().GetResult();
            return new RgbaImage(decoder.Width, decoder.Height, MemoryMarshal.Cast<TiffRgba32, Rgba32>(buffer.GetSpan()));
        }

        private static bool isTiff(ReadOnlySpan<byte> bytes)
            => bytes.Length >= 4
               && ((bytes[0] == 'I' && bytes[1] == 'I' && bytes[2] == 42 && bytes[3] == 0)
                   || (bytes[0] == 'M' && bytes[1] == 'M' && bytes[2] == 0 && bytes[3] == 42));
    }
}
