// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using osu.Framework.Graphics.Primitives;
using Sdcb.SimdCodecs.Pnm;
using Sdcb.SimdCodecs.Qoi;
using SkiaSharp;
using StbImageSharp;

namespace osu.Framework.Graphics.Imaging
{
    public enum ImageFileFormat
    {
        Png,
        Jpeg,
        Bmp,
        Gif,
        WebP,
        Tiff
    }

    public static unsafe class ImageCodec
    {
        public static RgbaImage Decode(Stream stream)
            => decode(stream, null);

        /// <summary>
        /// Decodes an image stream, using <paramref name="svgTargetSize"/> as the maximum output dimensions for SVG input.
        /// </summary>
        public static RgbaImage Decode(Stream stream, Vector2I svgTargetSize)
            => decode(stream, svgTargetSize);

        private static RgbaImage decode(Stream stream, Vector2I? svgTargetSize)
        {
            Stream input = stream;
            MemoryStream? copy = null;

            if (!stream.CanSeek)
            {
                copy = new MemoryStream();
                stream.CopyTo(copy);
                copy.Position = 0;
                input = copy;
            }

            try
            {
                if (SkiaStaticImageDecoder.TryDecode(input, out RgbaImage? image))
                    return image;

                if (SvgImageDecoder.TryDecode(input, svgTargetSize, out RgbaImage? svgImage))
                    return svgImage;

                throw new NotSupportedException("Skia does not support this image format or could not decode the image.");
            }
            finally
            {
                copy?.Dispose();
            }
        }

        internal static RgbaImage DecodeTexture(Stream stream)
            => DecodeTexture(stream, null);

        internal static RgbaImage DecodeTexture(Stream stream, Vector2I? svgTargetSize)
        {
            if (!stream.CanSeek)
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                copy.Position = 0;
                return DecodeTexture(copy, svgTargetSize);
            }

            long initialPosition = stream.Position;

            if (SkiaStaticImageDecoder.TryDecode(stream, out RgbaImage? image))
                return image;

            if (SvgImageDecoder.TryDecode(stream, svgTargetSize, out RgbaImage? svgImage))
                return svgImage;

            stream.Position = initialPosition;
            return LegacyImageDecoder.DecodeTexture(stream);
        }

        internal static RgbaImage DecodeIcon(Stream stream)
        {
            long initialPosition = stream.CanSeek ? stream.Position : 0;

            try
            {
                using var input = new MemoryStream();
                stream.CopyTo(input);
                input.Position = 0;

                if (SkiaStaticImageDecoder.TryDecode(input, out RgbaImage? image))
                    return image;

                if (SvgImageDecoder.TryDecode(input, null, out RgbaImage? svgImage))
                    return svgImage;

                ReadOnlySpan<byte> bytes = input.GetBuffer().AsSpan(0, (int)input.Length);

                if (isTiff(bytes))
                {
                    input.Position = 0;
                    return TiffImageCodec.Decode(input);
                }

                if (isNetpbm(bytes))
                {
                    var info = PnmDecoder.ReadInfo(bytes);
                    var result = new RgbaImage(info.Width, info.Height);

                    try
                    {
                        PnmDecoder.DecodeTo(bytes, MemoryMarshal.AsBytes(result.PixelSpan));
                        return result;
                    }
                    catch
                    {
                        result.Dispose();
                        throw;
                    }
                }

                if (isQoi(bytes))
                {
                    var info = QoiDecoder.ReadInfo(bytes);
                    var result = new RgbaImage(info.Width, info.Height);

                    try
                    {
                        QoiDecoder.DecodeTo(bytes, MemoryMarshal.AsBytes(result.PixelSpan));
                        return result;
                    }
                    catch
                    {
                        result.Dispose();
                        throw;
                    }
                }

                if (isTga(bytes))
                {
                    input.Position = 0;
                    var result = ImageResult.FromStream(input, ColorComponents.RedGreenBlueAlpha);
                    return new RgbaImage(result.Width, result.Height, MemoryMarshal.Cast<byte, Rgba32>(result.Data));
                }

                throw new NotSupportedException("No supported image decoder could decode the icon.");
            }
            finally
            {
                if (stream.CanSeek)
                    stream.Position = initialPosition;
            }
        }

        private static bool isTiff(ReadOnlySpan<byte> bytes)
            => bytes.Length >= 4
               && ((bytes[0] == 'I' && bytes[1] == 'I' && bytes[2] == 42 && bytes[3] == 0)
                   || (bytes[0] == 'M' && bytes[1] == 'M' && bytes[2] == 0 && bytes[3] == 42));

        private static bool isNetpbm(ReadOnlySpan<byte> bytes)
            => bytes.Length >= 3 && bytes[0] == (byte)'P' && bytes[1] is >= (byte)'1' and <= (byte)'6' && isWhitespace(bytes[2]);

        private static bool isQoi(ReadOnlySpan<byte> bytes)
            => bytes.Length >= 4 && bytes[..4].SequenceEqual("qoif"u8);

        private static bool isTga(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 18 || bytes[1] > 1)
                return false;

            byte imageType = bytes[2];
            bool indexed = imageType is 1 or 9;
            bool trueColour = imageType is 2 or 10;
            bool grayscale = imageType is 3 or 11;

            if ((!indexed && !trueColour && !grayscale)
                || (indexed && bytes[1] != 1)
                || (trueColour && bytes[16] is not (15 or 16 or 24 or 32))
                || (grayscale && bytes[16] is not (8 or 16))
                || (indexed && bytes[16] is not (8 or 16)))
                return false;

            int width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]);
            int height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);
            int colourMapStart = 18 + bytes[0];
            int colourMapLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[5..]);
            int colourMapBits = bytes[7];
            int colourMapBytes = bytes[1] == 1 ? colourMapLength * ((colourMapBits + 7) / 8) : 0;

            return width > 0
                   && height > 0
                   && (bytes[1] == 0 || colourMapBits is 15 or 16 or 24 or 32)
                   && colourMapStart + colourMapBytes < bytes.Length;
        }

        private static bool isWhitespace(byte value) => value == (byte)' ' || value == (byte)'\t' || value == (byte)'\r' || value == (byte)'\n';

        public static void Encode(RgbaImage image, Stream destination, ImageFileFormat format, int? quality = null)
        {
            SKEncodedImageFormat encodedFormat = format switch
            {
                ImageFileFormat.Png => SKEncodedImageFormat.Png,
                ImageFileFormat.Jpeg => SKEncodedImageFormat.Jpeg,
                ImageFileFormat.Bmp => SKEncodedImageFormat.Bmp,
                ImageFileFormat.WebP => SKEncodedImageFormat.Webp,
                ImageFileFormat.Gif => throw new NotSupportedException("Skia does not encode GIF images."),
                ImageFileFormat.Tiff => throw new NotSupportedException("Skia does not encode TIFF images."),
                _ => throw new ArgumentOutOfRangeException(nameof(format))
            };

            var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

            fixed (Rgba32* pixels = image.PixelSpan)
            {
                using (var skImage = SKImage.FromPixels(info, (IntPtr)pixels, info.RowBytes))
                using (var encoded = skImage?.Encode(encodedFormat, quality ?? 90)
                                     ?? throw new InvalidOperationException($"Skia could not encode the image as {format}."))
                    encoded.SaveTo(destination);
            }
        }
    }
}
