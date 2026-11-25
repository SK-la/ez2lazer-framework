// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Graphics.Rendering.Dummy;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Testing;
using osu.Framework.Text;

namespace osu.Framework.Tests.IO
{
    [TestFixture]
    public class FontStoreTest
    {
        private ResourceStore<byte[]> fontResourceStore;
        private TemporaryNativeStorage storage;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            storage = new TemporaryNativeStorage("fontstore-test");
            fontResourceStore = new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(FontStoreTest).Assembly), "Resources.Fonts.Roboto");
        }

        [Test]
        public void TestNestedScaleAdjust()
        {
            using (var fontStore = new FontStore(new DummyRenderer(), new RawCachingGlyphStore(fontResourceStore, "Roboto-Regular") { CacheStorage = storage }, scaleAdjust: 100))
            using (var nestedFontStore = new FontStore(new DummyRenderer(), new RawCachingGlyphStore(fontResourceStore, "Roboto-Bold") { CacheStorage = storage }, 10))
            {
                fontStore.AddStore(nestedFontStore);

                var normalGlyph = (TexturedCharacterGlyph)fontStore.Get("Roboto-Regular", 'a');
                Assert.That(normalGlyph, Is.Not.Null);

                var boldGlyph = (TexturedCharacterGlyph)fontStore.Get("Roboto-Bold", 'a');
                Assert.That(boldGlyph, Is.Not.Null);

                Assert.That(normalGlyph.Scale, Is.EqualTo(1f / 100));
                Assert.That(boldGlyph.Scale, Is.EqualTo(1f / 10));
            }
        }

        [Test]
        public async Task TestRawCachingCoverageMatchesTextureAlpha()
        {
            using var glyphStore = new RawCachingGlyphStore(fontResourceStore, "Roboto-Regular");
            glyphStore.CacheStorage = storage;
            await glyphStore.LoadFontAsync().ConfigureAwait(false);

            using TextureUpload rgba = await ((IResourceStore<TextureUpload>)glyphStore).GetAsync("Roboto-Regular/A", CancellationToken.None).ConfigureAwait(false);
            GlyphCoverageUpload coverage = glyphStore.GetCoverage('A')!;

            Assert.That(coverage.Width, Is.EqualTo(rgba.Width));
            Assert.That(coverage.Height, Is.EqualTo(rgba.Height));
            Assert.That(coverage.Data.Length, Is.EqualTo(rgba.Data.Length));

            for (int i = 0; i < coverage.Data.Length; i++)
                Assert.That(coverage.Data[i], Is.EqualTo(rgba.Data[i].A), $"Coverage mismatch at pixel {i}.");
        }

        [TestCase(typeof(FontAwesome.Solid))]
        [TestCase(typeof(FontAwesome.Regular))]
        [TestCase(typeof(FontAwesome.Brands))]
        public async Task TestFontAwesomeGlyphCoverageMatchesIconMappings(Type weight)
        {
            using var resources = new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(Game).Assembly), "Resources");
            using var glyphStore = new GlyphStore(resources, $"Fonts/FontAwesome5/FontAwesome-{weight.Name}");
            await glyphStore.LoadFontAsync().ConfigureAwait(false);

            var properties = weight.GetProperties(BindingFlags.Public | BindingFlags.Static)
                                   .Where(property => property.PropertyType == typeof(IconUsage))
                                   .ToArray();
            Assert.That(properties, Is.Not.Empty);

            var unresolved = properties.Where(property => property.GetValue(null) is not IconUsage icon
                                                          || icon.FontName != glyphStore.FontName
                                                          || !glyphStore.HasGlyph(icon.Icon))
                                       .Select(property => property.Name)
                                       .ToArray();

            Assert.That(unresolved, Is.Empty, $"{weight.Name} icons not resolved by the bundled font: {string.Join(", ", unresolved)}");
        }

        [Test]
        public async Task TestBudgetedGlyphPreloadDeduplicatesAndHonoursCancellation()
        {
            using var glyphStore = new RawCachingGlyphStore(fontResourceStore, "Roboto-Regular");
            glyphStore.CacheStorage = storage;
            await glyphStore.LoadFontAsync().ConfigureAwait(false);
            using var fontStore = new FontStore(new DummyRenderer(), glyphStore, scaleAdjust: 100);

            int preloaded = await fontStore.PreloadGlyphsAsync("Roboto-Regular", new[] { (int)'A', 'A', 'B' }, TimeSpan.FromMilliseconds(1)).ConfigureAwait(false);
            Assert.That(preloaded, Is.EqualTo(2));

            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync().ConfigureAwait(false);
            Task preloadTask = fontStore.PreloadGlyphsAsync("Roboto-Regular", new[] { (int)'C' }, TimeSpan.FromMilliseconds(1), cancellation.Token);
            Assert.ThrowsAsync<TaskCanceledException>(async () => await preloadTask.ConfigureAwait(false));
        }

        [Test]
        public void TestGlyphFoundAfterDelayedFontLoad()
        {
            var glyphs = new DelayedGlyphStore();
            using var fontStore = new FontStore(new DummyRenderer(), glyphs, 100);

            Assert.That(fontStore.Get("Delayed", 'A'), Is.Null);

            glyphs.Release();

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            ITexturedCharacterGlyph glyph = null;

            while (DateTime.UtcNow < deadline && glyph == null)
            {
                glyph = fontStore.Get("Delayed", 'A');

                if (glyph == null)
                    Thread.Sleep(10);
            }

            Assert.That(glyph, Is.Not.Null);
        }

        [Test]
        public void TestNoCrashOnMissingResources()
        {
            using (var glyphStore = new RawCachingGlyphStore(fontResourceStore, "DoesntExist"))
            {
                glyphStore.CacheStorage = storage;

                using (var fontStore = new FontStore(new DummyRenderer(), glyphStore, 100))
                {
                    Assert.That(glyphStore.Get('a'), Is.Null);

                    Assert.That(fontStore.Get("DoesntExist", 'a'), Is.Null);
                    Assert.That(fontStore.Get("OtherAttempt", 'a'), Is.Null);
                }
            }
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            storage.Dispose();
        }

        private sealed class DelayedGlyphStore : IGlyphStore, IResourceStore<TextureUpload>
        {
            private readonly TaskCompletionSource<bool> loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public string FontName => "Delayed";

            public float? Baseline => 0;

            public void Release() => loaded.TrySetResult(true);

            public Task LoadFontAsync() => loaded.Task;

            public bool HasGlyph(char c) => HasGlyph((int)c);

            public bool HasGlyph(int codepoint) => loaded.Task.IsCompletedSuccessfully && codepoint == 'A';

            public CharacterGlyph Get(char character) => Get((int)character);

            public CharacterGlyph Get(int codepoint) => HasGlyph(codepoint) ? new CharacterGlyph(codepoint, 0, 0, 1, 0, this) : null;

            public int GetKerning(char left, char right) => 0;

            public int GetKerning(int leftCodepoint, int rightCodepoint) => 0;

            private TextureUpload getTexture(string name) => loaded.Task.IsCompletedSuccessfully && name == "Delayed/A"
                ? new TextureUpload(new RgbaImage(1, 1))
                : null;

            TextureUpload IResourceStore<TextureUpload>.Get(string name) => getTexture(name);

            Task<TextureUpload> IResourceStore<TextureUpload>.GetAsync(string name, CancellationToken cancellationToken) => Task.FromResult(getTexture(name));

            CharacterGlyph IResourceStore<CharacterGlyph>.Get(string name) => name == "A" ? Get('A') : null;

            Task<CharacterGlyph> IResourceStore<CharacterGlyph>.GetAsync(string name, CancellationToken cancellationToken) =>
                Task.FromResult(((IResourceStore<CharacterGlyph>)this).Get(name));

            public Stream GetStream(string name) => null;

            public IEnumerable<string> GetAvailableResources() => Array.Empty<string>();

            public void Dispose()
            {
            }
        }
    }
}
