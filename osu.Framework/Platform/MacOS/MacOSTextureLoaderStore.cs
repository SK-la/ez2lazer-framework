// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using osu.Framework.Graphics.Imaging;
using osu.Framework.IO.Stores;
using osu.Framework.Platform.Apple;
using osu.Framework.Platform.Apple.Native;
using osu.Framework.Platform.MacOS.Native;

namespace osu.Framework.Platform.MacOS
{
    internal class MacOSTextureLoaderStore : AppleTextureLoaderStore
    {
        public MacOSTextureLoaderStore(IResourceStore<byte[]> store)
            : base(store)
        {
        }

        protected override unsafe RgbaImage ImageFromStream(Stream stream)
        {
            using (NSAutoreleasePool.Init())
            {
                int length = (int)(stream.Length - stream.Position);
                var nativeData = NSMutableData.FromLength(length);

                var bytesSpan = new Span<byte>(nativeData.MutableBytes, length);
                stream.ReadExactly(bytesSpan);

                using var nsImage = NSImage.InitWithData(nativeData);
                if (nsImage.Handle == IntPtr.Zero)
                    throw new ArgumentException($"An image could not be created from {nameof(stream)}.");

                var cgImage = nsImage.CGImage;
                return ImageFromCGImage(cgImage);
            }
        }
    }
}
