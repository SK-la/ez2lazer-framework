// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using Android.Graphics;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;

namespace osu.Framework.Android.Graphics.Textures
{
    internal class AndroidTextureLoaderStore : TextureLoaderStore
    {
        public AndroidTextureLoaderStore(IResourceStore<byte[]> store)
            : base(store)
        {
        }

        protected override RgbaImage ImageFromStream(Stream stream)
        {
            if (LegacyImageDecoder.TryDecodeStbi(stream, out RgbaImage? image))
                return image;

            using var bitmap = BitmapFactory.DecodeStream(stream);
            if (bitmap == null)
                throw new ArgumentException($"An image could not be created from {nameof(stream)}.");

            int width = bitmap.Width;
            int height = bitmap.Height;
            int[] pixels = new int[checked(width * height)];
            bitmap.GetPixels(pixels, 0, width, 0, 0, width, height);
            var result = new RgbaImage(width, height);

            try
            {
                Span<Rgba32> destination = result.PixelSpan;
                for (int i = 0; i < pixels.Length; i++)
                {
                    uint pixel = (uint)pixels[i];
                    destination[i] = new Rgba32((byte)(pixel >> 16), (byte)(pixel >> 8), (byte)pixel, (byte)(pixel >> 24));
                }

                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }
    }
}
