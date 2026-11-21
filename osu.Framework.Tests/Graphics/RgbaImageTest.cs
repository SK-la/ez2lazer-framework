// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers;
using System.Runtime.InteropServices;
using NUnit.Framework;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Graphics.Textures;
using ImageSharpImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
using ImageSharpRgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class RgbaImageTest
    {
        [Test]
        public void TestRgba32ByteLayout()
        {
            Rgba32[] pixel = { new Rgba32(0x12, 0x34, 0x56, 0x78) };

            Assert.That(MemoryMarshal.AsBytes(pixel.AsSpan()).ToArray(), Is.EqualTo(new byte[] { 0x12, 0x34, 0x56, 0x78 }));
        }

        [Test]
        public void TestFloatConversionMatchesImageSharpRgba32()
        {
            for (int i = 0; i < 255; i++)
            {
                float value = (i + 0.5f) / 255;
                var expected = new ImageSharpRgba32(value, value, value, value);
                var actual = new Rgba32(value, value, value, value);

                Assert.That(actual, Is.EqualTo(new Rgba32(expected.R, expected.G, expected.B, expected.A)), $"Channel conversion differed at {value}.");
            }
        }

        [Test]
        public void TestImageMemoryAndCoordinateBounds()
        {
            var image = new RgbaImage(3, 2);
            var pixel = new Rgba32(12, 34, 56, 78);

            Assert.That(image.PixelSpan.Length, Is.EqualTo(6));
            Assert.That(image.PixelMemory.Length, Is.EqualTo(6));
            Assert.That(image.PixelSpan.ToArray(), Is.EqualTo(new Rgba32[6]));

            image[2, 1] = pixel;
            Assert.That(image.GetReadOnlyPixelRowSpan(1)[2], Is.EqualTo(pixel));
            assertIndexOutOfRange(image, 3, 0);
            assertIndexOutOfRange(image, -1, 0);

            image.Dispose();
            image.Dispose();
        }

        private static void assertIndexOutOfRange(RgbaImage image, int x, int y)
        {
            try
            {
                _ = image[x, y];
            }
            catch (ArgumentOutOfRangeException)
            {
                return;
            }

            Assert.Fail("Expected pixel coordinates to be outside the image bounds.");
        }

        [Test]
        public void TestImageSharpUploadBridgePreservesRgbaBytes()
        {
            var image = new ImageSharpImage(2, 1);
            image[0, 0] = new ImageSharpRgba32(12, 34, 56, 0);
            image[1, 0] = new ImageSharpRgba32(78, 90, 123, 255);

            using var upload = new TextureUpload(image);

            Assert.That(upload.Width, Is.EqualTo(2));
            Assert.That(upload.Height, Is.EqualTo(1));
            Assert.That(upload.Data.ToArray(), Is.EqualTo(new[] { new Rgba32(12, 34, 56, 0), new Rgba32(78, 90, 123, 255) }));
            Assert.That(MemoryMarshal.AsBytes(upload.Data).ToArray(), Is.EqualTo(new byte[] { 12, 34, 56, 0, 78, 90, 123, 255 }));
        }

        [Test]
        public void TestRgbaImageUploadExposesPixels()
        {
            var image = new RgbaImage(2, 1);
            image.PixelSpan[0] = new Rgba32(12, 34, 56, 78);
            image.PixelSpan[1] = new Rgba32(90, 123, 145, 167);

            using var upload = new TextureUpload(image);

            Assert.That(upload.Data.Length, Is.EqualTo(2));
            Assert.That(upload.Data[0], Is.EqualTo(new Rgba32(12, 34, 56, 78)));
            Assert.That(upload.Data[1], Is.EqualTo(new Rgba32(90, 123, 145, 167)));
        }

        [Test]
        public void TestArrayPoolUploadExposesRequestedPixelCount()
        {
            using var upload = new ArrayPoolTextureUpload(3, 2, ArrayPool<Rgba32>.Create());

            Assert.That(upload.RawData.Length, Is.EqualTo(6));
            Assert.That(upload.Data.Length, Is.EqualTo(6));
        }

        [Test]
        public void TestMemoryAllocatorUploadExposesRequestedPixelCount()
        {
            using var upload = new MemoryAllocatorTextureUpload(3, 2);

            Assert.That(upload.RawData.Length, Is.EqualTo(6));
            Assert.That(upload.Data.Length, Is.EqualTo(6));
        }
    }
}
