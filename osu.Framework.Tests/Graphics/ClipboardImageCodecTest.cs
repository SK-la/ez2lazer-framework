// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using NUnit.Framework;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Platform;
using osu.Framework.Platform.Apple;
using osu.Framework.Platform.Apple.Native;
using osu.Framework.Platform.MacOS.Native;
using osu.Framework.Platform.Windows;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class ClipboardImageCodecTest
    {
        [Test]
        public void TestDibRoundTripPreservesRgbaPixels()
        {
            using var source = createImage();
            byte[] dib = DibImageCodec.Encode(source);

            Assert.That(BitConverter.ToUInt32(dib), Is.EqualTo(124));

            using var decoded = DibImageCodec.Decode(dib);
            assertPixelsEqual(source, decoded);
        }

        [Test]
        public void TestPngRoundTripPreservesRgbaPixels()
        {
            using var source = createImage();
            using var encoded = new MemoryStream();
            ImageCodec.Encode(source, encoded, ImageFileFormat.Png);
            encoded.Position = 0;
            using var decoded = ImageCodec.Decode(encoded);

            assertPixelsEqual(source, decoded);
        }

        [Test]
        public void TestHeadlessClipboardCopiesImageOwnership()
        {
            var source = createImage();
            var clipboard = new HeadlessClipboard();

            try
            {
                Assert.That(clipboard.SetImage(source), Is.True);
                source.Dispose();

                using var retrieved = clipboard.GetImage();
                Assert.That(retrieved, Is.Not.Null);
                Assert.That(retrieved![0, 0], Is.EqualTo(new Rgba32(12, 34, 56, 255)));
                Assert.That(retrieved[1, 0], Is.EqualTo(new Rgba32(78, 90, 123, 128)));

                retrieved[0, 0] = new Rgba32(0, 0, 0, 0);
                using var retrievedAgain = clipboard.GetImage();
                Assert.That(retrievedAgain![0, 0], Is.EqualTo(new Rgba32(12, 34, 56, 255)));
            }
            finally
            {
                source.Dispose();
            }
        }

        [Test]
        public void TestTiffRoundTripPreservesStraightAlpha()
        {
            using var source = createImage();
            using var stream = new MemoryStream(TiffImageCodec.Encode(source));
            using var decoded = TiffImageCodec.Decode(stream);

            assertPixelsEqual(source, decoded);
            Assert.That(stream.CanRead, Is.True);
        }

        [Test]
        public void TestMacOsReadsTiffClipboardImage()
        {
            if (!OperatingSystem.IsMacOS())
                Assert.Ignore("ImageIO clipboard TIFF codec is only available on macOS.");

            using var source = createImage();

            using (NSAutoreleasePool.Init())
            {
                var data = NSData.FromBytes(TiffImageCodec.Encode(source));
                using var image = NSImage.InitWithData(data);
                Assert.That(image.Handle, Is.Not.EqualTo(IntPtr.Zero));
                using var decoded = AppleTextureLoaderStore.ImageFromCGImage(image.CGImage);
                assertPixelsEqual(source, decoded);
            }
        }

        private static RgbaImage createImage()
        {
            var image = new RgbaImage(2, 1);
            image[0, 0] = new Rgba32(12, 34, 56, 255);
            image[1, 0] = new Rgba32(78, 90, 123, 128);
            return image;
        }

        private static void assertPixelsEqual(RgbaImage expected, RgbaImage actual)
        {
            Assert.That(actual.Width, Is.EqualTo(expected.Width));
            Assert.That(actual.Height, Is.EqualTo(expected.Height));
            Assert.That(actual.PixelSpan.ToArray(), Is.EqualTo(expected.PixelSpan.ToArray()));
        }
    }
}
