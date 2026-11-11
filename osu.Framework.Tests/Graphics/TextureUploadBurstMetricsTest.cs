// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using NUnit.Framework;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering.Dummy;
using osu.Framework.Graphics.Textures;
using SixLabors.ImageSharp.PixelFormats;

namespace osu.Framework.Tests.Graphics
{
    /// <summary>
    /// Headless timings for a settings/CJK-style upload burst: total drain time, per-frame peak, and
    /// whether one Upload() dumps the whole queue (the hitch) versus budgeted drain.
    /// </summary>
    [TestFixture]
    public class TextureUploadBurstMetricsTest
    {
        private const int burst_count = 2800;
        private const int atlas_glyphs = 400;
        private const int region_size = 8;
        private const int atlas_size = 1024;

        [Test]
        public void BudgetedDrainSplitsHitchAndReportsDuration()
        {
            var renderer = new DummyRenderer();
            var pixels = opaquePixels();

            var dumpQueued = queueRawBurst(renderer, pixels);
            BurstResult dump = drain(renderer, dumpQueued.Texture, dumpAll: true) with { EnqueueMs = dumpQueued.EnqueueMs };

            var budgetedQueued = queueRawBurst(renderer, pixels);
            BurstResult budgeted = drain(renderer, budgetedQueued.Texture, dumpAll: false) with { EnqueueMs = budgetedQueued.EnqueueMs };

            report("dump", dump);
            report("budgeted", budgeted);

            Assert.That(dump.Queued, Is.EqualTo(burst_count));
            Assert.That(dump.DrainFrames, Is.EqualTo(1), "unbounded Upload should empty the queue in one call (the hitch)");
            Assert.That(dump.PeakRegions, Is.EqualTo(burst_count));

            Assert.That(budgeted.Queued, Is.EqualTo(burst_count));
            Assert.That(budgeted.DrainFrames, Is.GreaterThanOrEqualTo(burst_count / renderer.MaxUploadsPerFrame));
            Assert.That(budgeted.PeakRegions, Is.LessThanOrEqualTo(renderer.MaxUploadsPerFrame));
            Assert.That(budgeted.PeakRegions, Is.LessThan(burst_count));
            Assert.That(budgeted.PeakFrameMs, Is.LessThan(dump.DrainMs).Or.LessThan(8),
                $"budgeted peak frame {budgeted.PeakFrameMs:F3}ms should not match a full dump {dump.DrainMs:F3}ms");
            Assert.That(budgeted.EnqueueMs + budgeted.DrainMs, Is.LessThan(5000));
        }

        [Test]
        public void AtlasPaddingDoesNotMultiplyQueueOrHitchDrain()
        {
            var renderer = new DummyRenderer();
            var pixels = opaquePixels();

            BurstResult atlas = queueAndDrainAtlas(renderer, pixels, atlas_glyphs);

            report("atlas", atlas);

            Assert.That(atlas.Queued, Is.GreaterThanOrEqualTo(atlas_glyphs));
            Assert.That(atlas.Queued, Is.LessThan(atlas_glyphs * 3),
                $"padding still multiplying uploads: queued={atlas.Queued} for {atlas_glyphs} glyphs");
            Assert.That(atlas.PeakRegions, Is.LessThanOrEqualTo(renderer.MaxUploadsPerFrame));
            Assert.That(atlas.DrainFrames, Is.GreaterThanOrEqualTo(2));
            Assert.That(atlas.PeakFrameMs, Is.LessThan(8));
            Assert.That(atlas.EnqueueMs + atlas.DrainMs, Is.LessThan(5000));
        }

        private static BurstResult queueAndDrainAtlas(DummyRenderer renderer, Rgba32[] pixels, int glyphCount)
        {
            var enqueueWatch = Stopwatch.StartNew();
            var atlas = new TextureAtlas(renderer, atlas_size, atlas_size, manualMipmaps: true, label: "BurstMetrics");
            Texture? last = null;

            for (int i = 0; i < glyphCount; i++)
            {
                var region = atlas.Add(region_size, region_size);
                Assert.That(region, Is.Not.Null);
                last = region;

                region!.SetData(new SharedPixelUpload(pixels)
                {
                    Bounds = new RectangleI(0, 0, region_size, region_size)
                });
            }

            double enqueueMs = enqueueWatch.Elapsed.TotalMilliseconds;
            Assert.That(last, Is.Not.Null);

            BurstResult result = drain(renderer, last!, dumpAll: false);
            return result with { EnqueueMs = enqueueMs };
        }

        private static (Texture Texture, double EnqueueMs) queueRawBurst(DummyRenderer renderer, Rgba32[] pixels)
        {
            var enqueueWatch = Stopwatch.StartNew();
            var texture = renderer.CreateTexture(atlas_size, atlas_size, manualMipmaps: true);
            texture.NativeTexture.Label = "BurstMetricsRaw";

            const int columns = atlas_size / region_size;

            for (int i = 0; i < burst_count; i++)
            {
                int x = i % columns * region_size;
                int y = i / columns * region_size;
                texture.SetData(new SharedPixelUpload(pixels)
                {
                    Bounds = new RectangleI(x, y, region_size, region_size)
                });
            }

            return (texture, enqueueWatch.Elapsed.TotalMilliseconds);
        }

        private static BurstResult drain(DummyRenderer renderer, Texture texture, bool dumpAll)
        {
            var native = (DummyNativeTexture)texture.NativeTexture;
            int queued = native.PendingUploadCount;

            int savedMaxUploads = renderer.MaxUploadsPerFrame;
            int savedMaxPixels = renderer.MaxPixelsUploadedPerFrame;
            bool savedBypass = texture.BypassTextureUploadQueueing;

            if (dumpAll)
            {
                texture.BypassTextureUploadQueueing = true;
                renderer.MaxUploadsPerFrame = int.MaxValue;
                renderer.MaxPixelsUploadedPerFrame = int.MaxValue;
            }

            int frames = 0;
            int peakRegions = 0;
            double peakFrameMs = 0;
            var drainWatch = Stopwatch.StartNew();

            do
            {
                renderer.ResetTextureUploadBudget();
                var frameWatch = Stopwatch.StartNew();
                native.Upload();
                double frameMs = frameWatch.Elapsed.TotalMilliseconds;
                peakFrameMs = Math.Max(peakFrameMs, frameMs);
                peakRegions = Math.Max(peakRegions, renderer.TextureUploadRegionsThisFrame);
                frames++;
            } while (!dumpAll && !native.UploadComplete);

            renderer.MaxUploadsPerFrame = savedMaxUploads;
            renderer.MaxPixelsUploadedPerFrame = savedMaxPixels;
            texture.BypassTextureUploadQueueing = savedBypass;

            return new BurstResult
            {
                Queued = queued,
                DrainFrames = frames,
                PeakRegions = peakRegions,
                DrainMs = drainWatch.Elapsed.TotalMilliseconds,
                PeakFrameMs = peakFrameMs
            };
        }

        private static void report(string name, BurstResult result)
        {
            string line =
                $"TextureUploadBurst {name}: queued={result.Queued} enqueueMs={result.EnqueueMs:F2} "
                + $"drainMs={result.DrainMs:F2} frames={result.DrainFrames} peakFrameMs={result.PeakFrameMs:F3} peakRegions={result.PeakRegions}";

            TestContext.Out.WriteLine(line);
        }

        private static Rgba32[] opaquePixels()
        {
            var pixels = new Rgba32[region_size * region_size];
            Array.Fill(pixels, new Rgba32(255, 255, 255, 255));
            return pixels;
        }

        private readonly struct BurstResult
        {
            public int Queued { get; init; }
            public int DrainFrames { get; init; }
            public int PeakRegions { get; init; }
            public double EnqueueMs { get; init; }
            public double DrainMs { get; init; }
            public double PeakFrameMs { get; init; }
        }

        private class SharedPixelUpload : ITextureUpload
        {
            private readonly Rgba32[] pixels;

            public SharedPixelUpload(Rgba32[] pixels)
            {
                this.pixels = pixels;
            }

            public ReadOnlySpan<Rgba32> Data => pixels;

            public int Level => 0;

            public RectangleI Bounds { get; set; }

            public void Dispose()
            {
            }
        }
    }
}
