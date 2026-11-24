// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Testing;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class SvgImageCodecTest
    {
        private static readonly Rgba32 embedded_pixel = new Rgba32(12, 34, 56);

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
            catch (NotSupportedException)
            {
                unsupported = true;
            }

            Assert.That(unsupported, Is.True);
            Assert.That(stream.Position, Is.EqualTo(0));
            Assert.That(stream.CanRead, Is.True);
        }

        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(false, 2)]
        [TestCase(true, 2)]
        public void TestNestedSvgCannotLoadLocalImages(bool base64, int depth)
        {
            using var storage = new TemporaryNativeStorage(nameof(TestNestedSvgCannotLoadLocalImages));
            byte[] png = createEmbeddedPng();
            using (var file = storage.GetStream("external.png", FileAccess.Write))
                file.Write(png);

            string svg = createImageSvg(new Uri(storage.GetFullPath("external.png")).AbsoluteUri);
            for (int i = 0; i < depth; i++)
                svg = createImageSvg(createSvgDataUri(svg, base64));

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svg));
            using var image = ImageCodec.Decode(stream);

            Assert.That(image.PixelSpan.ToArray(), Has.None.EqualTo(embedded_pixel));
            Assert.That(stream.Position, Is.EqualTo(0));
            Assert.That(stream.CanRead, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task TestNestedSvgCannotRequestRemoteImages(bool base64)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task<bool> server = respondToImageRequest(listener, createEmbeddedPng(), cancellation.Token);
            bool requested;

            try
            {
                string reference = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/external.png";
                string svg = createImageSvg(createSvgDataUri(createImageSvg(reference), base64));
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svg));
                using var image = ImageCodec.Decode(stream);

                Assert.That(image.PixelSpan.ToArray(), Has.None.EqualTo(embedded_pixel));
            }
            finally
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                requested = await server.ConfigureAwait(false);
            }

            Assert.That(requested, Is.False);
        }

        [Test]
        public void TestSvgCanLoadEmbeddedImages()
        {
            string reference = "data:image/png;base64," + Convert.ToBase64String(createEmbeddedPng());
            string svg = createImageSvg(reference);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svg));
            using var image = ImageCodec.Decode(stream);

            Assert.That(image[5, 5], Is.EqualTo(embedded_pixel));
        }

        private static string createImageSvg(string reference)
            => $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><image width=\"10\" height=\"10\" href=\"{reference}\"/></svg>";

        private static string createSvgDataUri(string svg, bool base64)
            => base64 ? "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg)) : "data:image/svg+xml," + Uri.EscapeDataString(svg);

        private static byte[] createEmbeddedPng()
        {
            using var image = new RgbaImage(1, 1);
            image[0, 0] = embedded_pixel;
            using var stream = new MemoryStream();
            ImageCodec.Encode(image, stream, ImageFileFormat.Png);
            return stream.ToArray();
        }

        private static async Task<bool> respondToImageRequest(TcpListener listener, byte[] png, CancellationToken cancellationToken)
        {
            bool requested = false;

            try
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                requested = true;
                using var response = client.GetStream();
                using var reader = new StreamReader(response, Encoding.ASCII, leaveOpen: true);

                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)))
                {
                }

                await response.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {png.Length}\r\nConnection: close\r\n\r\n"), cancellationToken)
                              .ConfigureAwait(false);
                await response.WriteAsync(png, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            return requested;
        }
    }
}
