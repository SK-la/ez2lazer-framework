// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Linq;
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

    [MemoryDiagnoser]
    public class BenchmarkFontAwesomeMigration
    {
        private const string font_asset_prefix = "Fonts/FontAwesome5/FontAwesome-";

        private NamespacedResourceStore<byte[]> frameworkResources = null!;
        private NamespacedResourceStore<byte[]> benchmarkResources = null!;
        private TemporaryNativeStorage cacheStorage = null!;
        private FontStore warmBitmapStore = null!;
        private FontStore warmOutlineStore = null!;
        private OutlineFont warmOutlineFont = null!;
        private OutlineGlyphStore warmOutlineGlyphStore = null!;
        private int[] codepoints = null!;
        private string assetName = null!;
        private string bitmapFontName = null!;
        private string outlineFontName = null!;

        [Params("Solid", "Regular", "Brands")]
        public string Weight { get; set; } = null!;

        public static void RunOnce(string[] args)
        {
            if (args.Length != 3 || args[0] is not ("Solid" or "Regular" or "Brands") || args[1] is not ("bmfont-cold" or "outline-cold" or "bmfont-warm" or "outline-warm")
                || !int.TryParse(args[2], out int batches) || batches < 1)
                throw new ArgumentException("Usage: --fontawesome-migration <Solid|Regular|Brands> <bmfont-cold|outline-cold|bmfont-warm|outline-warm> <batches>");

            var benchmark = new BenchmarkFontAwesomeMigration { Weight = args[0] };
            benchmark.SetUp();

            try
            {
                Action run = args[1] switch
                {
                    "bmfont-cold" => benchmark.ColdBmFontBatch,
                    "outline-cold" => benchmark.ColdOutlineBatch,
                    "bmfont-warm" => benchmark.WarmBmFontLookupBatch,
                    "outline-warm" => benchmark.WarmOutlineLookupBatch,
                    _ => throw new ArgumentOutOfRangeException(nameof(args))
                };

                run();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                long startedAt = Stopwatch.GetTimestamp();

                for (int i = 0; i < batches; i++)
                    run();

                double elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds / batches;
                long allocatedBytes = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / batches;
                Console.WriteLine(FormattableString.Invariant(
                    $"FontAwesome {args[0]} {args[1]}: {benchmark.codepoints.Length} glyphs, {elapsedMs:F3} ms/batch, {allocatedBytes:N0} managed bytes/batch, batches={batches}."));
            }
            finally
            {
                benchmark.TearDown();
            }
        }

        [GlobalSetup]
        public void SetUp()
        {
            frameworkResources = new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(Game).Assembly), "Resources");
            benchmarkResources = new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(Program).Assembly), "Resources");
            cacheStorage = new TemporaryNativeStorage("fontawesome-migration-" + Guid.NewGuid());
            assetName = font_asset_prefix + Weight;
            bitmapFontName = "FontAwesome-" + Weight;

            Type iconType = typeof(FontAwesome).GetNestedType(Weight)!;
            var icons = iconType.GetProperties(BindingFlags.Public | BindingFlags.Static)
                                .Where(property => property.PropertyType == typeof(IconUsage))
                                .Select(property => (IconUsage)property.GetValue(null)!)
                                .ToArray();

            using var probeFont = new OutlineGlyphStore(benchmarkResources, assetName + "-5.15.4");
            probeFont.LoadFontAsync().GetAwaiter().GetResult();
            codepoints = icons.Select(icon => (int)icon.Icon).Where(probeFont.HasGlyph).Distinct().Order().ToArray();

            var bitmapGlyphs = new RawCachingGlyphStore(frameworkResources, assetName) { CacheStorage = cacheStorage };
            bitmapGlyphs.LoadFontAsync().GetAwaiter().GetResult();
            warmBitmapStore = new FontStore(new DummyRenderer(), bitmapGlyphs, scaleAdjust: 100, minFilterMode: TextureFilteringMode.Linear);

            warmOutlineFont = new OutlineFont(benchmarkResources, assetName + "-5.15.4") { Resolution = 100 };
            warmOutlineFont.LoadAsync().GetAwaiter().GetResult();
            outlineFontName = bitmapFontName;
            warmOutlineGlyphStore = new OutlineGlyphStore(warmOutlineFont, nameOverride: outlineFontName);
            warmOutlineGlyphStore.LoadFontAsync().GetAwaiter().GetResult();
            warmOutlineStore = new FontStore(new DummyRenderer(), warmOutlineGlyphStore, scaleAdjust: 100, minFilterMode: TextureFilteringMode.Linear);

            foreach (int codepoint in codepoints)
            {
                if (!bitmapGlyphs.HasGlyph(codepoint))
                    throw new InvalidOperationException($"The bundled BMFont is missing U+{codepoint:X}.");

                if (warmBitmapStore.Get(bitmapFontName, codepoint) == null || warmOutlineStore.Get(outlineFontName, codepoint) == null)
                    throw new InvalidOperationException($"The glyph stores could not resolve U+{codepoint:X} under their SpriteIcon font names.");
            }

            Console.WriteLine($"FontAwesome {Weight}: {codepoints.Length} shared icon glyphs; BMFont={bitmapGlyphs.FontName}; outline={warmOutlineGlyphStore.FontName}.");
        }

        [GlobalCleanup]
        public void TearDown()
        {
            warmBitmapStore.Dispose();
            warmOutlineStore.Dispose();
            warmOutlineGlyphStore.Dispose();
            warmOutlineFont.Dispose();
            cacheStorage.Dispose();
            frameworkResources.Dispose();
            benchmarkResources.Dispose();
        }

        [Benchmark(Baseline = true)]
        public void ColdBmFontBatch()
        {
            using var storage = new TemporaryNativeStorage("fontawesome-bmfont-" + Guid.NewGuid());
            using var glyphs = new RawCachingGlyphStore(frameworkResources, assetName) { CacheStorage = storage };
            using var store = new FontStore(new DummyRenderer(), glyphs, scaleAdjust: 100, minFilterMode: TextureFilteringMode.Linear);
            glyphs.LoadFontAsync().GetAwaiter().GetResult();

            foreach (int codepoint in codepoints)
                store.Get(bitmapFontName, codepoint);
        }

        [Benchmark]
        public void ColdOutlineBatch()
        {
            using var font = new OutlineFont(benchmarkResources, assetName + "-5.15.4") { Resolution = 100 };
            font.LoadAsync().GetAwaiter().GetResult();
            using var glyphs = new OutlineGlyphStore(font, nameOverride: outlineFontName);
            using var store = new FontStore(new DummyRenderer(), glyphs, scaleAdjust: 100, minFilterMode: TextureFilteringMode.Linear);
            glyphs.LoadFontAsync().GetAwaiter().GetResult();

            foreach (int codepoint in codepoints)
                store.Get(outlineFontName, codepoint);
        }

        [Benchmark]
        public void WarmBmFontLookupBatch()
        {
            foreach (int codepoint in codepoints)
                warmBitmapStore.Get(bitmapFontName, codepoint);
        }

        [Benchmark]
        public void WarmOutlineLookupBatch()
        {
            foreach (int codepoint in codepoints)
                warmOutlineStore.Get(outlineFontName, codepoint);
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
