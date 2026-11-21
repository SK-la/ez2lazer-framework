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
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Webp;
using ImageSharpEncoder = SixLabors.ImageSharp.Formats.IImageEncoder;
using ImageSharpImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
using ImageSharpRgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class StaticImageDecoderTest
    {
        [Test]
        public void TestPngRgbaOrderAndTransparency()
        {
            using var source = createImage(new ImageSharpRgba32(12, 34, 56, 0), new ImageSharpRgba32(78, 90, 123, 167));
            byte[] encoded = encode(source, new PngEncoder());

            using var stream = new MemoryStream(encoded);
            Assert.That(SkiaStaticImageDecoder.TryDecode(stream, out RgbaImage? image), Is.True);

            using (image)
            {
                Assert.That(image!.Width, Is.EqualTo(2));
                Assert.That(image.Height, Is.EqualTo(1));
                Assert.That(image.PixelSpan.ToArray(), Is.EqualTo(new[] { new Rgba32(12, 34, 56, 0), new Rgba32(78, 90, 123, 167) }));
            }

            using var upload = new TextureUpload(new MemoryStream(encoded));
            Assert.That(upload.Data.ToArray(), Is.EqualTo(new[] { new Rgba32(12, 34, 56, 0), new Rgba32(78, 90, 123, 167) }));
        }

        [Test]
        public void TestBmpRgbaOrder()
        {
            using var source = createImage(new ImageSharpRgba32(12, 34, 56, 255), new ImageSharpRgba32(78, 90, 123, 255));
            using var stream = new MemoryStream(encode(source, new BmpEncoder()));

            Assert.That(SkiaStaticImageDecoder.TryDecode(stream, out RgbaImage? image), Is.True);
            using (image)
                Assert.That(image!.PixelSpan.ToArray(), Is.EqualTo(new[] { new Rgba32(12, 34, 56, 255), new Rgba32(78, 90, 123, 255) }));
        }

        [Test]
        public void TestLosslessWebpRgbaOrderAndTransparency()
        {
            using var source = createImage(new ImageSharpRgba32(12, 34, 56, 0), new ImageSharpRgba32(78, 90, 123, 167));
            var encoder = new WebpEncoder { FileFormat = WebpFileFormatType.Lossless };
            using var stream = new MemoryStream(encode(source, encoder));

            Assert.That(SkiaStaticImageDecoder.TryDecode(stream, out RgbaImage? image), Is.True);
            using (image)
                Assert.That(image!.PixelSpan.ToArray(), Is.EqualTo(new[] { new Rgba32(12, 34, 56, 0), new Rgba32(78, 90, 123, 167) }));
        }

        [Test]
        public void TestOtherFormatsStayOnLegacyPath()
        {
            using var source = createImage(new ImageSharpRgba32(12, 34, 56, 255), new ImageSharpRgba32(78, 90, 123, 255));
            byte[][] unsupported =
            {
                encode(source, new JpegEncoder()),
                encode(source, new GifEncoder()),
                encode(source, new TiffEncoder()),
            };

            foreach (byte[] bytes in unsupported)
            {
                using var stream = new MemoryStream(bytes);

                Assert.That(SkiaStaticImageDecoder.TryDecode(stream, out RgbaImage? image), Is.False);
                Assert.That(image, Is.Null);
                Assert.That(stream.Position, Is.EqualTo(0));
            }
        }

        [Test]
        public void TestTextureLoaderStoreUsesSkiaAndPreservesLegacyFallback()
        {
            using var source = createImage(new ImageSharpRgba32(12, 34, 56, 0), new ImageSharpRgba32(78, 90, 123, 167));
            byte[] png = encode(source, new PngEncoder());

            using var skiaStore = new TrackingTextureLoaderStore(png);
            using var skiaUpload = skiaStore.Get("texture.png");

            Assert.That(skiaUpload, Is.Not.Null);
            Assert.That(skiaStore.UsedLegacyDecoder, Is.False);
            Assert.That(skiaUpload!.Data.ToArray(), Is.EqualTo(new[] { new Rgba32(12, 34, 56, 0), new Rgba32(78, 90, 123, 167) }));

            byte[] jpeg = encode(source, new JpegEncoder());
            using var legacyStore = new TrackingTextureLoaderStore(jpeg);
            using var legacyUpload = legacyStore.Get("texture.jpg");

            Assert.That(legacyUpload, Is.Not.Null);
            Assert.That(legacyStore.UsedLegacyDecoder, Is.True);
        }

        [Test]
        public void TestSkiaFailureResetsStreamForLegacyFallback()
        {
            byte[] invalidPng = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a };
            using var store = new TrackingTextureLoaderStore(invalidPng);

            Assert.That(store.Get("invalid.png"), Is.Null);
            Assert.That(store.UsedLegacyDecoder, Is.True);
        }

        private static ImageSharpImage createImage(ImageSharpRgba32 first, ImageSharpRgba32 second)
        {
            var image = new ImageSharpImage(2, 1);
            image[0, 0] = first;
            image[1, 0] = second;
            return image;
        }

        private static byte[] encode(ImageSharpImage image, ImageSharpEncoder encoder)
        {
            using var stream = new MemoryStream();
            image.Save(stream, encoder);
            return stream.ToArray();
        }

        private sealed class TrackingTextureLoaderStore : TextureLoaderStore
        {
            public bool UsedLegacyDecoder { get; private set; }

            public TrackingTextureLoaderStore(byte[] data)
                : base(new ByteArrayResourceStore(data))
            {
            }

            protected override Image<TPixel> ImageFromStream<TPixel>(Stream stream)
            {
                UsedLegacyDecoder = true;
                return base.ImageFromStream<TPixel>(stream);
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
