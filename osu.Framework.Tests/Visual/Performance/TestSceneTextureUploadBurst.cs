// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Dummy;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Logging;
using osuTK;
using osuTK.Graphics;
using SixLabors.ImageSharp.PixelFormats;

namespace osu.Framework.Tests.Visual.Performance
{
    /// <summary>
    /// Reproduces a settings/CJK-style burst of thousands of region uploads onto one atlas page,
    /// and asserts the draw thread drains them across frames instead of dumping the whole queue at once.
    /// </summary>
    public partial class TestSceneTextureUploadBurst : FrameworkTestScene
    {
        private const int burst_count = 2800;
        private const int region_size = 8;
        private const int atlas_size = 1024;

        [Resolved]
        private IRenderer renderer { get; set; } = null!;

        private int peakRegions;
        private int drainFrames;
        private Texture? burstTexture;
        private readonly List<string> logMessages = new List<string>();

        protected override void Update()
        {
            base.Update();

            if (renderer is Renderer r)
                peakRegions = Math.Max(peakRegions, r.TextureUploadRegionsThisFrame);
        }

        [Test]
        public void TestBurstDoesNotDumpInOneFrame()
        {
            AddStep("queue burst", () =>
            {
                peakRegions = 0;
                drainFrames = 0;

                burstTexture = renderer.CreateTexture(atlas_size, atlas_size, manualMipmaps: true, initialisationColour: Color4.Black);
                burstTexture.NativeTexture.Label = "BurstFixture";

                Child = new Sprite
                {
                    Texture = burstTexture,
                    Size = new Vector2(256)
                };

                var pixels = new Rgba32[region_size * region_size];
                Array.Fill(pixels, new Rgba32(255, 255, 255, 255));

                int columns = atlas_size / region_size;

                for (int i = 0; i < burst_count; i++)
                {
                    int x = i % columns * region_size;
                    int y = i / columns * region_size;

                    burstTexture.SetData(new SharedPixelUpload(pixels)
                    {
                        Bounds = new RectangleI(x, y, region_size, region_size)
                    });
                }
            });

            AddUntilStep("uploads complete", () =>
            {
                pumpDummyDrain();
                return burstTexture?.UploadComplete == true;
            });

            AddAssert("drained across multiple frames", () =>
                drainFrames >= Math.Max(2, burst_count / Math.Max(1, renderer.MaxUploadsPerFrame)));

            AddAssert("single frame stayed within region budget", () =>
                peakRegions <= renderer.MaxUploadsPerFrame);

            AddAssert("did not dump the whole burst in one frame", () =>
                peakRegions > 0 && peakRegions < burst_count);
        }

        private void pumpDummyDrain()
        {
            if (renderer is not DummyRenderer dummy || burstTexture == null || burstTexture.UploadComplete)
                return;

            dummy.ResetTextureUploadBudget();
            burstTexture.NativeTexture.Upload();
            peakRegions = Math.Max(peakRegions, dummy.TextureUploadRegionsThisFrame);
            drainFrames++;
        }

        [Test]
        public void TestLargeQueueLogIncludesLabelAndSize()
        {
            AddStep("listen for queue logs", () =>
            {
                logMessages.Clear();
                loggerWasEnabled = Logger.Enabled;
                Logger.Enabled = true;
                Logger.NewEntry += onLog;
            });

            AddStep("queue labeled atlas past 100", () =>
            {
                var atlas = new TextureAtlas(renderer, atlas_size, atlas_size, manualMipmaps: true, label: "BurstAtlas");
                var pixels = new Rgba32[region_size * region_size];
                Array.Fill(pixels, new Rgba32(255, 0, 0, 255));

                for (int i = 0; i < 120; i++)
                {
                    var region = atlas.Add(region_size, region_size);
                    Assert.That(region, Is.Not.Null);

                    region!.SetData(new SharedPixelUpload(pixels)
                    {
                        Bounds = new RectangleI(0, 0, region_size, region_size)
                    });
                }
            });

            AddAssert("log names the atlas and size", () =>
            {
                foreach (string message in logMessages)
                {
                    if (message.Contains("BurstAtlas", StringComparison.Ordinal)
                        && message.Contains($"{atlas_size}x{atlas_size}", StringComparison.Ordinal)
                        && message.Contains("upload queue is large", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                return false;
            });

            AddAssert("padding did not multiply into 200 uploads", () =>
            {
                foreach (string message in logMessages)
                {
                    if (message.Contains("BurstAtlas", StringComparison.Ordinal)
                        && message.Contains("large (200)", StringComparison.Ordinal))
                        return false;
                }

                return true;
            });

            AddStep("stop listening", () =>
            {
                Logger.NewEntry -= onLog;
                Logger.Enabled = loggerWasEnabled;
            });
        }

        private bool loggerWasEnabled = true;

        private void onLog(LogEntry entry)
        {
            if (!string.IsNullOrEmpty(entry.Message))
                logMessages.Add(entry.Message);
        }

        protected override void Dispose(bool isDisposing)
        {
            Logger.NewEntry -= onLog;
            Logger.Enabled = loggerWasEnabled;
            base.Dispose(isDisposing);
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
