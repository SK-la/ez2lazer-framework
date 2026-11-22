// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        [TestCase(1, 3, 2, new[] { 0, 1, 2, 3, 4, 5 })]
        [TestCase(2, 3, 2, new[] { 2, 1, 0, 5, 4, 3 })]
        [TestCase(3, 3, 2, new[] { 5, 4, 3, 2, 1, 0 })]
        [TestCase(4, 3, 2, new[] { 3, 4, 5, 0, 1, 2 })]
        [TestCase(5, 2, 3, new[] { 0, 3, 1, 4, 2, 5 })]
        [TestCase(6, 2, 3, new[] { 3, 0, 4, 1, 5, 2 })]
        [TestCase(7, 2, 3, new[] { 5, 2, 4, 1, 3, 0 })]
        [TestCase(8, 2, 3, new[] { 2, 5, 1, 4, 0, 3 })]
        public void TestJpegAppliesExifOrientation(int orientation, int width, int height, int[] pixelIndices)
        {
            using var source = new RgbaImage(3, 2);

            for (int i = 0; i < source.PixelCount; i++)
            {
                byte intensity = (byte)(i * 40);
                source.PixelSpan[i] = new Rgba32(intensity, intensity, intensity);
            }

            using var jpeg = new MemoryStream();
            source.SaveAsJpeg(jpeg, quality: 100);
            jpeg.Position = 0;
            using var baseline = ImageCodec.Decode(jpeg);
            Assert.That(baseline.PixelSpan.ToArray(), Is.Unique);

            byte[] encoded = jpeg.ToArray();
            using var withExif = new MemoryStream();
            withExif.Write(encoded.AsSpan(0, 2));
            withExif.Write(new byte[]
            {
                0xff, 0xe1, 0x00, 0x22, // APP1 marker and length.
                (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
                (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, // Little-endian TIFF header.
                1, 0, // One directory entry.
                0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, // Orientation as a SHORT.
                0, 0, 0, 0, // No next directory.
            });
            withExif.Write(encoded.AsSpan(2));
            withExif.Position = 0;

            using var decoded = ImageCodec.Decode(withExif);
            Assert.That(decoded.Width, Is.EqualTo(width));
            Assert.That(decoded.Height, Is.EqualTo(height));
            for (int i = 0; i < pixelIndices.Length; i++)
                Assert.That(decoded.PixelSpan[i], Is.EqualTo(baseline.PixelSpan[pixelIndices[i]]));
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
        public void TestWebpSeparateAlphaPlanePreservesTransparency()
        {
            using var resources = new DllResourceStore(typeof(StaticImageDecoderTest).Assembly);
            using var stream = resources.GetStream("Resources/Textures/zeta.webp");
            Assert.That(stream, Is.Not.Null);

            using var image = ImageCodec.Decode(stream!);

            Assert.That(image.Width, Is.EqualTo(320));
            Assert.That(image.Height, Is.EqualTo(320));
            Assert.That(image.PixelSpan.ToArray().Any(pixel => pixel.A == 0), Is.True);
            Assert.That(image.PixelSpan.ToArray().Any(pixel => pixel.A is > 0 and < 255), Is.True);
            Assert.That(image.PixelSpan.ToArray().Any(pixel => pixel.A == 255), Is.True);
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
            // Keep enough of the PNG for SKCodec to recognise it and attempt pixel decoding.
            byte[] truncatedPng = readFixture("png-512.bin")[..512];
            const int initial_position = 8;
            using var stream = new MemoryStream();
            stream.Position = initial_position;
            stream.Write(truncatedPng);
            stream.Position = initial_position;

            Assert.That(SkiaStaticImageDecoder.TryDecode(stream, out RgbaImage? image), Is.False);
            Assert.That(image, Is.Null);
            Assert.That(stream.CanRead, Is.True);
            Assert.That(stream.Position, Is.EqualTo(initial_position));
            Assert.That(stream.ReadByte(), Is.EqualTo(truncatedPng[0]));

            using var store = new TrackingTextureLoaderStore(truncatedPng);
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
