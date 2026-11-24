// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Tests.Resources;
using SkiaSharp;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class AnimatedImageLoaderTest
    {
        [Test]
        public void TestGifExpandsMultipleFramesFromEmbeddedAsset()
        {
            using var stream = GifTestResources.OpenSampleFlameGifStream();
            using var result = AnimatedImageLoader.LoadFromStream(stream);

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Frames.Count, Is.GreaterThan(1));
            Assert.That(result.FrameDurationMs.Count, Is.EqualTo(result.Frames.Count));

            int width = result.Frames[0].Width;
            int height = result.Frames[0].Height;
            Assert.That(width, Is.GreaterThan(0));
            Assert.That(height, Is.GreaterThan(0));
            Assert.That(result.Frames.All(f => f.Width == width && f.Height == height), Is.True);
        }

        [Test]
        public void TestGifRespectsMaxFrames()
        {
            using var stream = GifTestResources.OpenSampleFlameGifStream();
            using var result = AnimatedImageLoader.LoadFromStream(stream, 2);

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Frames.Count, Is.EqualTo(2));
            Assert.That(result.FrameDurationMs.Count, Is.EqualTo(2));
            Assert.That(stream.CanRead, Is.True);
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void TestGifAppliesFrameDisposal(byte disposal)
        {
            byte[] encoded = createDisposalGif(disposal);
            using var codecStream = new SKManagedStream(new MemoryStream(encoded), disposeManagedStream: true);
            using var codec = SKCodec.Create(codecStream);
            Assert.That(codec, Is.Not.Null);
            Assert.That(codec!.FrameInfo[1].DisposalMethod, Is.EqualTo(disposal switch
            {
                1 => SKCodecAnimationDisposalMethod.Keep,
                2 => SKCodecAnimationDisposalMethod.RestoreBackgroundColor,
                _ => SKCodecAnimationDisposalMethod.RestorePrevious,
            }));
            Assert.That(codec.FrameInfo[2].RequiredFrame, Is.EqualTo(disposal == 3 ? 0 : 1));

            using var stream = new MemoryStream(encoded);
            using var result = AnimatedImageLoader.LoadFromStream(stream);

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Frames.Count, Is.EqualTo(3));
            Assert.That(result.Frames[1].Data.ToArray(), Is.EqualTo(new[]
            {
                new Rgba32(0, 255, 0), new Rgba32(255, 0, 0), new Rgba32(255, 0, 0),
            }));

            Rgba32 firstPixel = disposal switch
            {
                1 => new Rgba32(0, 255, 0),
                2 => new Rgba32(0, 0, 0, 0),
                _ => new Rgba32(255, 0, 0),
            };
            Assert.That(result.Frames[2].Data.ToArray(), Is.EqualTo(new[]
            {
                firstPixel, new Rgba32(255, 0, 0), new Rgba32(0, 0, 255),
            }));
        }

        [Test]
        public void TestTruncatedGifLeavesCallerStreamOpen()
        {
            using var source = GifTestResources.OpenSampleFlameGifStream();
            byte[] truncated = new byte[32];
            source.ReadExactly(truncated);
            using var stream = new MemoryStream(truncated, writable: false);

            using var result = AnimatedImageLoader.LoadFromStream(stream);

            Assert.That(result, Is.Null);
            Assert.That(stream.Position, Is.Zero);
            Assert.That(stream.ReadByte(), Is.EqualTo(truncated[0]));
        }

        [Test]
        public void TestTextureLoaderStoreLoadAnimatedFrames()
        {
            using var result = GifTestResources.LoadSampleFlameGif();

            Assert.That(result.Frames.Count, Is.GreaterThan(1));
        }

        [Test]
        public void TestPngReturnsSingleFrame()
        {
            var store = new DllResourceStore(typeof(GifTestResources).Assembly);
            using var stream = store.GetStream("Resources/Fonts/Roboto/Roboto-Regular_0.png");
            Assert.That(stream, Is.Not.Null);

            using var encoded = new MemoryStream();
            stream!.CopyTo(encoded);
            byte[] data = encoded.ToArray();

            using (var decodeStream = new MemoryStream(data))
            using (var image = ImageCodec.Decode(decodeStream))
                Assert.That(image.PixelCount, Is.GreaterThan(0));

            using var result = AnimatedImageLoader.LoadFromStream(new MemoryStream(data));
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Frames.Count, Is.EqualTo(1));
        }

        private static byte[] createDisposalGif(byte disposal)
        {
            using var stream = new MemoryStream();
            stream.Write("GIF89a"u8);
            stream.Write(new byte[]
            {
                3, 0, 1, 0, 0x81, 0, 0,
                0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255,
                // An opaque red canvas, followed by a cropped green frame.
                0x21, 0xf9, 4, 5, 1, 0, 0, 0,
                0x2c, 0, 0, 0, 0, 3, 0, 1, 0, 0, 2, 3, 0x0c, 0xc3, 0x14, 0,
                0x21, 0xf9, 4,
            });
            stream.WriteByte((byte)((disposal << 2) | 1));
            stream.Write(new byte[]
            {
                1, 0, 0, 0,
                0x2c, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x54, 0x01, 0,
                // A cropped blue frame exposes how the green frame was disposed.
                0x21, 0xf9, 4, 5, 1, 0, 0, 0,
                0x2c, 2, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x5c, 0x01, 0,
                0x3b,
            });
            return stream.ToArray();
        }
    }
}
