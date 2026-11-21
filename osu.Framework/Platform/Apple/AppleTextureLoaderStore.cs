// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Platform.Apple.Native;
using osu.Framework.Platform.Apple.Native.Accelerate;

namespace osu.Framework.Platform.Apple
{
    internal abstract class AppleTextureLoaderStore : TextureLoaderStore
    {
        protected AppleTextureLoaderStore(IResourceStore<byte[]> store)
            : base(store)
        {
        }

        internal static unsafe RgbaImage ImageFromCGImage(CGImage cgImage)
        {
            int width = (int)cgImage.Width;
            int height = (int)cgImage.Height;
            var format = new vImage_CGImageFormat
            {
                BitsPerComponent = 8,
                BitsPerPixel = 32,
                ColorSpace = CGColorSpace.CreateDeviceRGB(),
                BitmapInfo = (CGBitmapInfo)CGImageAlphaInfo.Last,
                Decode = null,
                RenderingIntent = CGColorRenderingIntent.Default,
            };

            vImage_Buffer buffer = default;
            nuint alignment = (nuint)vImage.Init(&buffer, (uint)height, (uint)width, 32, vImage_Flags.NoAllocate);
            Debug.Assert(alignment > 0);
            nuint bytesPerRow = buffer.BytesPerRow;
            buffer.Data = (byte*)NativeMemory.AlignedAlloc(bytesPerRow * buffer.Height, alignment);
            var result = vImage.InitWithCGImage(&buffer, &format, null, cgImage.Handle, vImage_Flags.NoAllocate);
            Debug.Assert(result == vImage_Error.NoError);

            var image = new RgbaImage(width, height);
            try
            {
                Span<Rgba32> pixels = image.PixelSpan;
                for (int y = 0; y < height; y++)
                {
                    var row = new ReadOnlySpan<Rgba32>(&buffer.Data[(int)bytesPerRow * y], width);
                    row.CopyTo(pixels.Slice(y * width, width));
                }

                return image;
            }
            catch
            {
                image.Dispose();
                throw;
            }
            finally
            {
                NativeMemory.AlignedFree(buffer.Data);
            }
        }
    }
}
