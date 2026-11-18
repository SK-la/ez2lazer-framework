// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Tests.Resources;

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

            using var result = AnimatedImageLoader.LoadFromStream(stream!);
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Frames.Count, Is.EqualTo(1));
        }
    }
}
