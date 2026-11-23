// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Drawing;
using System.Linq;
using BenchmarkDotNet.Attributes;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Dummy;
using osu.Framework.Graphics.Rendering.Vertices;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Platform;
using osu.Framework.Statistics;
using osu.Framework.Utils;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Benchmarks
{
    public partial class BenchmarkSpriteText : GameBenchmark
    {
        private TestGame game = null!;

        [Test]
        [Benchmark]
        public void TestStaticText()
        {
            game.Mode = 0;
            RunSingleFrame();
        }

        [Test]
        [Benchmark]
        public void TestMovingText()
        {
            game.Mode = 1;
            RunSingleFrame();
        }

        [Test]
        [Benchmark]
        public void TestChangingText()
        {
            game.Mode = 2;
            RunSingleFrame();
        }

        protected override Game CreateGame() => game = new TestGame();

        private partial class TestGame : Game
        {
            public int Mode;

            private readonly string text1 = Guid.NewGuid().ToString();
            private readonly string text2 = Guid.NewGuid().ToString();

            private long frame;

            [BackgroundDependencyLoader]
            private void load()
            {
                for (int i = 0; i < 1000; i++)
                {
                    Add(new SpriteText
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Text = "i am some relatively long text",
                    });
                }
            }

            protected override void Update()
            {
                base.Update();

                switch (Mode)
                {
                    case 0:
                        break;

                    case 1:
                        var pos = new Vector2(RNG.NextSingle(100));

                        foreach (var text in Children.OfType<SpriteText>())
                            text.Position = pos;

                        break;

                    case 2:
                        foreach (var text in Children.OfType<SpriteText>())
                            text.Text = frame % 2 == 0 ? text1 : text2;
                        break;
                }

                frame++;
            }
        }
    }

    [MemoryDiagnoser]
    public class BenchmarkGlyphAtlasPageSwitching
    {
        private const int atlas_size = 128;
        private const int glyphs_per_frame = 32;
        private static readonly Vector2 viewport_size = new Vector2(640, 480);
        private static readonly ColourInfo colour = ColourInfo.SingleColour(Color4.White);

        private DummyRenderer renderer = null!;
        private TextureAtlas atlas = null!;
        private CountingBatch batch = null!;
        private DummyShader shader = null!;
        private GlobalStatistic<int> bindFlushes = null!;
        private Texture firstPage = null!;
        private Texture secondPage = null!;

        [GlobalSetup]
        public void SetUp()
        {
            renderer = new DummyRenderer();
            ((IRenderer)renderer).Initialise(new BenchmarkGraphicsSurface());
            atlas = new TextureAtlas(renderer, atlas_size, atlas_size);
            firstPage = atlas.Add(48, 48) ?? throw new InvalidOperationException("First glyph did not fit.");
            secondPage = atlas.Add(48, 48) ?? throw new InvalidOperationException("Second glyph did not fit.");
            if (firstPage.HasSameNativeTexture(secondPage))
                throw new InvalidOperationException("Expected the synthetic glyphs to occupy separate atlas pages.");

            batch = new CountingBatch(renderer);
            shader = new DummyShader();
            bindFlushes = GlobalStatistics.Get<int>(nameof(FlushBatchSource), nameof(FlushBatchSource.BindTexture));
        }

        [GlobalCleanup]
        public void TearDown()
        {
            report(nameof(DrawSinglePage), DrawSinglePage);
            report(nameof(DrawContiguousPages), DrawContiguousPages);
            report(nameof(DrawAlternatingPages), DrawAlternatingPages);
            Console.WriteLine($"Base texture storage estimate: {atlas.PageCount * atlas_size * atlas_size * 4 / 1024} KiB across {atlas.PageCount} RGBA pages.");
            atlas.DisposeResources();
            renderer.BeginFrame(viewport_size);
            renderer.FinishFrame();
            shader.Dispose();
            batch.Dispose();
        }

        [Benchmark(Baseline = true)]
        public void DrawSinglePage() => draw(int.MaxValue);

        [Benchmark]
        public void DrawContiguousPages() => draw(glyphs_per_frame / 2);

        [Benchmark]
        public void DrawAlternatingPages() => draw(1);

        private void draw(int glyphsPerPageRun)
        {
            renderer.BeginFrame(viewport_size);
            renderer.BindShader(shader);
            renderer.SetActiveBatch(batch);

            for (int i = 0; i < glyphs_per_frame; i++)
            {
                Texture texture = (i / glyphsPerPageRun & 1) == 0 ? firstPage : secondPage;
                renderer.DrawQuad(texture, new Quad(i, 0, 1, 1), colour, vertexAction: batch.AddAction);
            }

            renderer.FinishFrame();
        }

        private void report(string scenario, Action drawFrame)
        {
            Array.Clear(FrameStatistics.COUNTERS);
            drawFrame();
            Console.WriteLine($"{scenario}: {FrameStatistics.COUNTERS[(int)StatisticsCounterType.TextureBinds]} binds, "
                              + $"{FrameStatistics.COUNTERS[(int)StatisticsCounterType.DrawCalls]} draw submissions, {bindFlushes.Value} BindTexture flushes");
        }

        private sealed class CountingBatch : IVertexBatch<TexturedVertex2D>
        {
            private readonly DummyRenderer renderer;
            private int vertexCount;

            public CountingBatch(DummyRenderer renderer)
            {
                this.renderer = renderer;
                AddAction = Add;
            }

            public Action<TexturedVertex2D> AddAction { get; }
            public int Size => glyphs_per_frame * 4;

            public void Add(TexturedVertex2D vertex) => vertexCount++;

            public int Draw()
            {
                int count = vertexCount;
                vertexCount = 0;
                if (count > 0)
                    renderer.DrawBatchVertices(PrimitiveTopology.Triangles, 0, count);

                return count;
            }

            public void ResetCounters() => vertexCount = 0;

            public void Dispose()
            {
            }
        }

        private sealed class BenchmarkGraphicsSurface : IGraphicsSurface
        {
            public IntPtr WindowHandle => IntPtr.Zero;
            public GraphicsSurfaceType Type => GraphicsSurfaceType.Vulkan;

            public void Initialise()
            {
            }

            public Size GetDrawableSize() => new Size((int)viewport_size.X, (int)viewport_size.Y);
        }
    }
}
