// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Rendering.Dummy;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class TextureUploadBackdropSampleTest
    {
        [Test]
        public void TestEmptyUploadHasNoSample()
        {
            using (var upload = new TextureUpload())
                Assert.That(upload.TrySampleOpaquePixel(out _), Is.False);
        }

        [Test]
        public void TestTransparentPixelIsSkipped()
        {
            using (var upload = new TextureUpload(imageWithPixel(0, 0, 0, 0)))
                Assert.That(upload.TrySampleOpaquePixel(out _), Is.False);
        }

        [Test]
        public void TestOpaquePixelIsRead()
        {
            using (var upload = new TextureUpload(imageWithPixel(255, 128, 0, 255)))
            {
                Assert.That(upload.TrySampleOpaquePixel(out Colour4 colour), Is.True);
                Assert.That(colour.R, Is.EqualTo(1f).Within(0.001f));
                Assert.That(colour.G, Is.EqualTo(128 / 255f).Within(0.001f));
                Assert.That(colour.B, Is.EqualTo(0f));
                Assert.That(colour.A, Is.EqualTo(1f));
            }
        }

        [Test]
        public void TestStoreKeepsSampleFromUpload()
        {
            using (var store = new TextureStore(new DummyRenderer()))
            {
                store.AddTextureSource(new OnePixelStore(imageWithPixel(10, 20, 30, 255)));

                Texture texture = store.Get("pixel");

                Assert.That(texture, Is.Not.Null);
                Assert.That(texture.HasBackdropSample, Is.True);
                Assert.That(texture.BackdropSample.R, Is.EqualTo(10 / 255f).Within(0.001f));
                Assert.That(texture.BackdropSample.G, Is.EqualTo(20 / 255f).Within(0.001f));
                Assert.That(texture.BackdropSample.B, Is.EqualTo(30 / 255f).Within(0.001f));
            }
        }

        private static Image<Rgba32> imageWithPixel(byte r, byte g, byte b, byte a)
        {
            var image = new Image<Rgba32>(1, 1);
            image[0, 0] = new Rgba32(r, g, b, a);
            return image;
        }

        private class OnePixelStore : IResourceStore<TextureUpload>
        {
            private readonly Image<Rgba32> image;

            public OnePixelStore(Image<Rgba32> image)
            {
                this.image = image;
            }

            public TextureUpload Get(string name) => name == "pixel" ? new TextureUpload(image) : null!;

            public Task<TextureUpload> GetAsync(string name, CancellationToken cancellationToken = default)
                => Task.FromResult(Get(name));

            public Stream GetStream(string name) => null!;

            public IEnumerable<string> GetAvailableResources() => new[] { "pixel" };

            public void Dispose()
            {
            }
        }
    }
}
