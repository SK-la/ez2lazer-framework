// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class SvgImageCodecTest
    {
        [TestCase(32)]
        [TestCase(128)]
        [TestCase(512)]
        public void TestDanSvgRasterizesAtTargetSize(int size)
        {
            using var resources = new DllResourceStore(typeof(SvgImageCodecTest).Assembly);
            using var stream = resources.GetStream("Resources/Textures/ln-azimuth.svg");
            Assert.That(stream, Is.Not.Null);

            using var image = ImageCodec.Decode(stream!, new Vector2I(size));

            Assert.That(image.Width, Is.EqualTo(size));
            Assert.That(image.Height, Is.EqualTo(size));
            Assert.That(image.PixelSpan.ToArray().Any(pixel => pixel.A != 0), Is.True);
            Assert.That(image.PixelSpan.ToArray().Any(pixel => pixel.A is > 0 and < 255), Is.True);
        }

        [Test]
        public void TestTextureLoaderStoreUsesConfiguredSvgSize()
        {
            using var store = new TextureLoaderStore(new DllResourceStore(typeof(SvgImageCodecTest).Assembly), new Vector2I(128));
            using var upload = store.Get("Resources/Textures/ln-azimuth.svg");

            Assert.That(upload, Is.Not.Null);
            Assert.That(upload!.Width, Is.EqualTo(128));
            Assert.That(upload.Height, Is.EqualTo(128));
        }

        [Test]
        public void TestTextureLoaderStoreFindsSvgWithoutExtension()
        {
            using var store = new TextureLoaderStore(new DllResourceStore(typeof(SvgImageCodecTest).Assembly), new Vector2I(32));
            using var upload = store.Get("Resources/Textures/ln-azimuth");

            Assert.That(upload, Is.Not.Null);
            Assert.That(upload!.Width, Is.EqualTo(32));
            Assert.That(upload.Height, Is.EqualTo(32));
        }

        [Test]
        public void TestDecodeSvgRestoresNonZeroStreamPosition()
        {
            using var resources = new DllResourceStore(typeof(SvgImageCodecTest).Assembly);
            using var svg = resources.GetStream("Resources/Textures/ln-azimuth.svg");
            Assert.That(svg, Is.Not.Null);

            using var stream = new MemoryStream();
            stream.Write(new byte[] { 1, 2, 3, 4 });
            svg!.CopyTo(stream);
            stream.Position = 4;

            using var image = ImageCodec.Decode(stream, new Vector2I(32));

            Assert.That(stream.Position, Is.EqualTo(4));
            Assert.That(stream.CanRead, Is.True);
            Assert.That(image.Width, Is.EqualTo(32));
        }

        [TestCase(100, 100, 100, 50)]
        [TestCase(100, 40, 80, 40)]
        public void TestSvgViewBoxPreservesAspectRatioAndOrigin(int targetWidth, int targetHeight, int expectedWidth, int expectedHeight)
        {
            const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"-10 20 20 10\">"
                               + "<rect x=\"-10\" y=\"20\" width=\"20\" height=\"10\" fill=\"#ff0000\"/>"
                               + "<rect x=\"-10\" y=\"20\" width=\"10\" height=\"10\" fill=\"#00ff00\"/>"
                               + "</svg>";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svg));

            using var image = ImageCodec.Decode(stream, new Vector2I(targetWidth, targetHeight));

            Assert.That(image.Width, Is.EqualTo(expectedWidth));
            Assert.That(image.Height, Is.EqualTo(expectedHeight));
            Assert.That(image[10, 25], Is.EqualTo(new Rgba32(0, 255, 0)));
            Assert.That(image[75, 25], Is.EqualTo(new Rgba32(255, 0, 0)));
        }

        [Test]
        public void TestSvgWithoutTargetSizeUsesIntrinsicViewBoxSize()
        {
            const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"-10 20 20 10\"><rect width=\"20\" height=\"10\" fill=\"#fff\"/></svg>";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svg));

            using var image = ImageCodec.Decode(stream);

            Assert.That(image.Width, Is.EqualTo(20));
            Assert.That(image.Height, Is.EqualTo(10));
        }

        [Test]
        public void TestSvgWithoutTargetSizeClampsIntrinsicDimensionsPreservingAspectRatio()
        {
            const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"8192\" height=\"4\"><rect width=\"8192\" height=\"4\" fill=\"#fff\"/></svg>";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svg));

            using var image = ImageCodec.Decode(stream);

            Assert.That(image.Width, Is.EqualTo(4096));
            Assert.That(image.Height, Is.EqualTo(2));
        }

        [Test]
        public void TestSvgRejectsExternalResources()
        {
            const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><image href=\"https://example.com/image.png\"/></svg>";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svg));

            bool unsupported = false;

            try
            {
                using var image = ImageCodec.Decode(stream);
            }
            catch (System.NotSupportedException)
            {
                unsupported = true;
            }

            Assert.That(unsupported, Is.True);
            Assert.That(stream.Position, Is.EqualTo(0));
            Assert.That(stream.CanRead, Is.True);
        }
    }
}
