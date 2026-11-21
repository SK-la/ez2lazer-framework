// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using SkiaSharp;

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

                throw new NotSupportedException("Skia does not support this image format or could not decode the image.");
            }
            finally
            {
                copy?.Dispose();
            }
        }

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
