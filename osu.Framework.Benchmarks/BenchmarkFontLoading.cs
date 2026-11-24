// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using NUnit.Framework;
using osu.Framework.Extensions;
using osu.Framework.Graphics.Rendering.Dummy;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Testing;
using osu.Framework.Text;

namespace osu.Framework.Benchmarks
{
    public class BenchmarkFontLoading : BenchmarkTest
    {
        private NamespacedResourceStore<byte[]> baseResources = null!;
        private NamespacedResourceStore<byte[]> testResources = null!;
        private TemporaryNativeStorage sharedTemp = null!;

        public override void SetUp()
        {
            baseResources = new NamespacedResourceStore<byte[]>(new DllResourceStore(@"osu.Framework.dll"), @"Resources");
            testResources = new NamespacedResourceStore<byte[]>(new DllResourceStore(@"osu.Framework.Benchmarks.dll"), @"Resources");
            sharedTemp = new TemporaryNativeStorage("fontstore-test-" + Guid.NewGuid());
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            sharedTemp.Dispose();
        }

        [Params(1, 10, 100, 1000, 10000)]
        public int FetchCount;

        private const string font_name = @"Fonts/FontAwesome5/FontAwesome-Solid";

        [Benchmark]
        public void BenchmarkRawCachingReuse()
        {
            using (var store = new RawCachingGlyphStore(baseResources, font_name))
            {
                store.CacheStorage = sharedTemp;
                runFor(store);
            }
        }

        [Benchmark(Baseline = true)]
        public void BenchmarkRawCaching()
        {
            using (var temp = new TemporaryNativeStorage("fontstore-test" + Guid.NewGuid()))
            using (var store = new RawCachingGlyphStore(baseResources, font_name))
            {
                store.CacheStorage = temp;
                runFor(store);
            }
        }

        [Benchmark]
        public void BenchmarkOutline()
        {
            using (var store = new OutlineGlyphStore(testResources, font_name))
                runFor(store);
        }

        [Benchmark]
        public void BenchmarkTimedExpiry()
        {
            using (var store = new TimedExpiryGlyphStore(baseResources, font_name))
                runFor(store);
        }

        [Benchmark]
        public void BenchmarkTimedExpiryMemoryPooling()
        {
            using (var store = new TimedExpiryGlyphStore(baseResources, font_name))
                runFor(store);
        }

        [Benchmark]
        public void BenchmarkNoCache()
        {
            if (FetchCount > 100) // gets too slow.
                throw new NotImplementedException();

            using (var store = new GlyphStore(baseResources, font_name))
                runFor(store);
        }

        private void runFor(IGlyphStore store)
        {
            store.LoadFontAsync().WaitSafely();

            var props = typeof(FontAwesome.Solid).GetProperties(BindingFlags.Public | BindingFlags.Static);

            int remainingCount = FetchCount;

            while (true)
            {
                foreach (var p in props)
                {
                    object? propValue = p.GetValue(null);
                    Debug.Assert(propValue != null);

                    var icon = (IconUsage)propValue;

                    using (_ = (store as IResourceStore<TextureUpload>)!.Get(icon.Icon.ToString()))
                    {
                    }

                    if (remainingCount-- == 0)
                        return;
                }
            }
        }
    }

    public class BenchmarkOutlineFontCosts : BenchmarkTest
    {
        private const string asset_name = @"Fonts/Roboto/Roboto";
        private const string font_name = "BenchmarkRoboto";

        private NamespacedResourceStore<byte[]> resources = null!;
        private DummyRenderer renderer = null!;
        private OutlineFont warmFont = null!;
        private OutlineGlyphStore warmGlyphStore = null!;
        private FontStore warmFontStore = null!;
        private uint glyphIndex;

        public override void SetUp()
        {
            base.SetUp();

            resources = new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(Game).Assembly), @"Resources");
            renderer = new DummyRenderer();
            warmFont = new OutlineFont(resources, asset_name);
            warmFont.LoadAsync().WaitSafely();
            glyphIndex = warmFont.GetGlyphIndex('A');

            warmGlyphStore = new OutlineGlyphStore(warmFont, nameOverride: font_name);
            warmFontStore = new FontStore(renderer, null, 100, TextureFilteringMode.Linear);
            warmFontStore.AddTextureSource(warmGlyphStore);
            warmGlyphStore.LoadFontAsync().WaitSafely();
            _ = warmFontStore.Get(font_name, 'A');
        }

        [GlobalCleanup]
        public void TearDown()
        {
            warmFontStore.Dispose();
            warmGlyphStore.Dispose();
            warmFont.Dispose();
        }

        [Benchmark]
        public void ColdFontLoad()
        {
            using var font = new OutlineFont(resources, asset_name);
            font.LoadAsync().WaitSafely();
        }

        [Benchmark]
        public void ColdFontLoadAndFirstGlyph()
        {
            using var font = new OutlineFont(resources, asset_name);
            font.LoadAsync().WaitSafely();
            uint index = font.GetGlyphIndex('A');
            using TextureUpload upload = font.RasterizeGlyph(index, null)!;
        }

        [Benchmark]
        public void WarmFontStoreCacheHit() => warmFontStore.Get(font_name, 'A');

        [Benchmark]
        public void WarmGlyphRasterization()
        {
            using TextureUpload upload = warmFont.RasterizeGlyph(glyphIndex, null)!;
        }
    }
}
