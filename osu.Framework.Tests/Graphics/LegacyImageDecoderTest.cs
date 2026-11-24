// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using NUnit.Framework;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class LegacyImageDecoderTest
    {
        [Test]
        public void TestTextureUploadPreservesTgaRgbaContract()
        {
            using var stream = new MemoryStream(createTga());
            using var upload = new TextureUpload(stream);

            Assert.That(upload.Width, Is.EqualTo(2));
            Assert.That(upload.Height, Is.EqualTo(1));
            Assert.That(upload.Data.ToArray(), Is.EqualTo(new[]
            {
                new Rgba32(10, 20, 30, 255),
                new Rgba32(40, 50, 60, 255),
            }));
            Assert.That(stream.CanRead, Is.True);
        }

        [Test]
        public void TestTextureLoaderStorePreservesQoiRgbaContract()
        {
            using var store = new TextureLoaderStore(new ByteArrayResourceStore(createQoi()));
            using var upload = store.Get("image.qoi");

            Assert.That(upload, Is.Not.Null);
            Assert.That(upload!.Width, Is.EqualTo(1));
            Assert.That(upload.Height, Is.EqualTo(1));
            Assert.That(upload.Data.ToArray(), Is.EqualTo(new[] { new Rgba32(10, 20, 30, 40) }));
        }

        [TestCase(0)]
        [TestCase(3)]
        public void TestTextureUploadPreservesQoiRgbaContract(int initialPosition)
        {
            using var stream = new MemoryStream();
            stream.Write(new byte[initialPosition]);
            stream.Write(createQoi());
            stream.Position = initialPosition;

            using var upload = new TextureUpload(stream);

            Assert.That(upload.Width, Is.EqualTo(1));
            Assert.That(upload.Height, Is.EqualTo(1));
            Assert.That(upload.Data.ToArray(), Is.EqualTo(new[] { new Rgba32(10, 20, 30, 40) }));
            Assert.That(stream.CanRead, Is.True);
        }

        [Test]
        public void TestTruncatedQoiLeavesNoImageAndRestoresStreamPosition()
        {
            using var stream = new MemoryStream();
            stream.Write(new byte[3]);
            stream.Write(createQoi().AsSpan(0, 16));
            stream.Position = 3;

            bool decoded = LegacyImageDecoder.TryDecodeStbi(stream, out RgbaImage? image);

            using (image)
            {
                Assert.That(decoded, Is.False);
                Assert.That(image, Is.Null);
            }

            Assert.That(stream.Position, Is.EqualTo(3));
            Assert.That(stream.CanRead, Is.True);
        }

        [Test]
        public void TestDecodeTgaIcon()
        {
            using var stream = new MemoryStream(createTga());
            using var image = ImageCodec.DecodeIcon(stream);

            Assert.That(image.Width, Is.EqualTo(2));
            Assert.That(image.Height, Is.EqualTo(1));
            Assert.That(image.PixelSpan.ToArray(), Is.EqualTo(new[] { new Rgba32(10, 20, 30), new Rgba32(40, 50, 60) }));
        }

        [Test]
        public void TestDecodeQoiIcon()
        {
            using var stream = new MemoryStream(createQoi());
            using var image = ImageCodec.DecodeIcon(stream);

            Assert.That(image.Width, Is.EqualTo(1));
            Assert.That(image.Height, Is.EqualTo(1));
            Assert.That(image.PixelSpan.ToArray(), Is.EqualTo(new[] { new Rgba32(10, 20, 30, 40) }));
        }

        [TestCaseSource(nameof(netpbmCases))]
        public void TestDecodeIconSupportsNetpbm(byte[] encoded, Rgba32[] expectedPixels)
        {
            using var stream = new MemoryStream(encoded, writable: false);
            using var image = ImageCodec.DecodeIcon(stream);

            Assert.That(image.Width, Is.EqualTo(expectedPixels.Length));
            Assert.That(image.Height, Is.EqualTo(1));
            Assert.That(image.PixelSpan.ToArray(), Is.EqualTo(expectedPixels));
            Assert.That(stream.Position, Is.EqualTo(0));
        }

        [Test]
        public void TestDecodeIconUsesTiffDecoder()
        {
            using var resources = new DllResourceStore(typeof(LegacyImageDecoderTest).Assembly);
            using var stream = resources.GetStream("Resources/Textures/Decoding/sample.tiff");
            Assert.That(stream, Is.Not.Null);

            using var image = ImageCodec.DecodeIcon(stream!);

            Assert.That(image.Width, Is.GreaterThan(0));
            Assert.That(image.Height, Is.GreaterThan(0));
        }

        [Test]
        public void TestDecodeTextureLeavesUnsupportedInputAtItsStartingPosition()
        {
            using var stream = new MemoryStream(new byte[24], writable: false);
            stream.Position = 3;

            bool unsupported = false;

            try
            {
                ImageCodec.DecodeTexture(stream);
            }
            catch (NotSupportedException)
            {
                unsupported = true;
            }

            Assert.That(unsupported, Is.True);
            Assert.That(stream.Position, Is.EqualTo(3));
            Assert.That(stream.CanRead, Is.True);
        }

        private static TestCaseData[] netpbmCases => new[]
        {
            new TestCaseData(encodeAsciiNetpbm("P1\n2 1\n0 1\n"), new[] { new Rgba32(255, 255, 255), new Rgba32(0, 0, 0) })
                .SetName("P1"),
            new TestCaseData(encodeAsciiNetpbm("P2\n2 1\n255\n0 255\n"), new[] { new Rgba32(0, 0, 0), new Rgba32(255, 255, 255) })
                .SetName("P2"),
            new TestCaseData(encodeAsciiNetpbm("P3\n2 1\n255\n10 20 30 40 50 60\n"), new[] { new Rgba32(10, 20, 30), new Rgba32(40, 50, 60) })
                .SetName("P3"),
            new TestCaseData(encodeBinaryNetpbm("P4\n2 1\n", new byte[] { 0b01000000 }), new[] { new Rgba32(255, 255, 255), new Rgba32(0, 0, 0) })
                .SetName("P4"),
            new TestCaseData(encodeBinaryNetpbm("P5\n2 1\n255\n", new byte[] { 0, 255 }), new[] { new Rgba32(0, 0, 0), new Rgba32(255, 255, 255) })
                .SetName("P5"),
            new TestCaseData(encodeBinaryNetpbm("P6\n2 1\n255\n", new byte[] { 10, 20, 30, 40, 50, 60 }), new[] { new Rgba32(10, 20, 30), new Rgba32(40, 50, 60) })
                .SetName("P6"),
        };

        private static byte[] encodeAsciiNetpbm(string header) => System.Text.Encoding.ASCII.GetBytes(header);

        private static byte[] encodeBinaryNetpbm(string header, byte[] pixels)
        {
            byte[] headerBytes = System.Text.Encoding.ASCII.GetBytes(header);
            byte[] encoded = new byte[headerBytes.Length + pixels.Length];
            headerBytes.CopyTo(encoded, 0);
            pixels.CopyTo(encoded, headerBytes.Length);
            return encoded;
        }

        private static byte[] createTga()
        {
            byte[] encoded = new byte[24];
            encoded[2] = 2;
            encoded[12] = 2;
            encoded[14] = 1;
            encoded[16] = 24;
            encoded[17] = 0x20;
            encoded[18] = 30;
            encoded[19] = 20;
            encoded[20] = 10;
            encoded[21] = 60;
            encoded[22] = 50;
            encoded[23] = 40;
            return encoded;
        }

        private static byte[] createQoi()
        {
            byte[] encoded = new byte[27];
            "qoif"u8.CopyTo(encoded);
            encoded[7] = 1;
            encoded[11] = 1;
            encoded[12] = 4;
            encoded[14] = 0xff;
            encoded[15] = 10;
            encoded[16] = 20;
            encoded[17] = 30;
            encoded[18] = 40;
            encoded[26] = 1;
            return encoded;
        }

        private sealed class ByteArrayResourceStore : IResourceStore<byte[]>
        {
            private readonly byte[] data;

            public ByteArrayResourceStore(byte[] data)
            {
                this.data = data;
            }

            public byte[] Get(string name) => data;
            public System.Threading.Tasks.Task<byte[]> GetAsync(string name, System.Threading.CancellationToken cancellationToken = default) => System.Threading.Tasks.Task.FromResult(data);
            public Stream GetStream(string name) => new MemoryStream(data, writable: false);
            public System.Collections.Generic.IEnumerable<string> GetAvailableResources() => new[] { "image" };

            public void Dispose()
            {
            }
        }
    }
}
