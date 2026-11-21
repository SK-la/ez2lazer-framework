// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class StaticImageDecoderTest
    {
        [TestCase("png-512.bin", 512, 512)]
        [TestCase("png-1024.bin", 1024, 576)]
        [TestCase("jpeg-1024.bin", 1024, 576)]
        [TestCase("bmp-1024.bin", 1024, 576)]
        [TestCase("webp-1024.bin", 1024, 576)]
        public void TestSkiaDecodesSupportedFormats(string name, int width, int height)
        {
            byte[] encoded = readFixture(name);
            using var stream = new MemoryStream(encoded, writable: false);

            Assert.That(SkiaStaticImageDecoder.TryDecode(stream, out RgbaImage? image), Is.True);
            using (image)
            {
                Assert.That(image!.Width, Is.EqualTo(width));
                Assert.That(image.Height, Is.EqualTo(height));
                Assert.That(image.PixelSpan.Length, Is.EqualTo(width * height));
            }
        }

        [Test]
        public void TestPngEncodingAndDecodingPreservesTransparentRgba()
        {
            using var source = new RgbaImage(2, 1);
            source[0, 0] = new Rgba32(12, 34, 56, 0);
            source[1, 0] = new Rgba32(78, 90, 123, 167);
            using var stream = new MemoryStream();
            ImageCodec.Encode(source, stream, ImageFileFormat.Png);
            stream.Position = 0;

            using var decoded = ImageCodec.Decode(stream);

            Assert.That(decoded.PixelSpan.ToArray(), Is.EqualTo(source.PixelSpan.ToArray()));
        }

        [Test]
        public void TestTiffRemainsUnsupportedBySkia()
        {
            using var stream = new MemoryStream(readFixture("sample.tiff"), writable: false);

            Assert.That(SkiaStaticImageDecoder.TryDecode(stream, out RgbaImage? image), Is.False);
            Assert.That(image, Is.Null);
            Assert.That(stream.Position, Is.EqualTo(0));
        }

        [Test]
        public void TestTextureLoaderStoreUsesSkiaForStaticImages()
        {
            using var source = new RgbaImage(2, 1);
            source[0, 0] = new Rgba32(12, 34, 56, 0);
            source[1, 0] = new Rgba32(78, 90, 123, 167);
            using var png = new MemoryStream();
            ImageCodec.Encode(source, png, ImageFileFormat.Png);
            using var pngStore = new TrackingTextureLoaderStore(png.ToArray());
            using var pngUpload = pngStore.Get("texture.png");

            Assert.That(pngUpload, Is.Not.Null);
            Assert.That(pngStore.UsedFallbackDecoder, Is.False);
            Assert.That(pngUpload!.Data.ToArray(), Is.EqualTo(source.PixelSpan.ToArray()));

            using var jpeg = new MemoryStream(readFixture("jpeg-1024.bin"), writable: false);
            using var jpegStore = new TrackingTextureLoaderStore(toByteArray(jpeg));
            using var jpegUpload = jpegStore.Get("texture.jpg");

            Assert.That(jpegUpload, Is.Not.Null);
            Assert.That(jpegStore.UsedFallbackDecoder, Is.False);
        }

        [Test]
        public void TestSkiaFailureResetsStreamForFallback()
        {
            byte[] invalidPng = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a };
            using var store = new TrackingTextureLoaderStore(invalidPng);

            Assert.That(store.Get("invalid.png"), Is.Null);
            Assert.That(store.UsedFallbackDecoder, Is.True);
        }

        private static byte[] readFixture(string name)
        {
            using var store = new DllResourceStore(typeof(StaticImageDecoderTest).Assembly);
            using var stream = store.GetStream($"Resources/Textures/Decoding/{name}")
                               ?? throw new FileNotFoundException($"Static image fixture '{name}' was not found.");
            return toByteArray(stream);
        }

        private static byte[] toByteArray(Stream stream)
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        private sealed class TrackingTextureLoaderStore : TextureLoaderStore
        {
            public bool UsedFallbackDecoder { get; private set; }

            public TrackingTextureLoaderStore(byte[] data)
                : base(new ByteArrayResourceStore(data))
            {
            }

            protected override RgbaImage ImageFromStream(Stream stream)
            {
                UsedFallbackDecoder = true;
                return base.ImageFromStream(stream);
            }
        }

        private sealed class ByteArrayResourceStore : IResourceStore<byte[]>
        {
            private readonly byte[] data;

            public ByteArrayResourceStore(byte[] data)
            {
                this.data = data;
            }

            public byte[] Get(string name) => data;
            public Task<byte[]> GetAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(data);
            public Stream GetStream(string name) => new MemoryStream(data, false);
            public IEnumerable<string> GetAvailableResources() => new[] { "texture" };

            public void Dispose()
            {
            }
        }
    }
}
