// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Dummy;
using osu.Framework.Graphics.Rendering.Vertices;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Platform;
using osu.Framework.Statistics;
using osu.Framework.Testing;
using osu.Framework.Utils;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Benchmarks
{
    internal static partial class BenchmarkCjkSpriteText
    {
        private const int warmup_frames = 180;
        private const int first_use_frames = 60;
        private const int sample_frames = 360;
        private const int row_count = 28;
        private const int row_height = 28;

        public static void Run(string[] args)
        {
            if (args.Length is < 1 or > 3)
                throw new ArgumentException("Usage: --sprite-text-cjk <osu.Game.Resources.dll> [preloaded glyph count] [UTF-8 titles file]");

            using var resources = new ResourceStore<byte[]>(new DllResourceStore(Assembly.LoadFrom(Path.GetFullPath(args[0]))));
            int preloadCount = args.Length >= 2 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
            ArgumentOutOfRangeException.ThrowIfNegative(preloadCount);
            string[] titles = args.Length == 3
                ? File.ReadAllLines(args[2]).Where(title => !string.IsNullOrWhiteSpace(title)).ToArray()
                : new[]
                {
                    "星空のメロディー  夜空の彼方", "风起云涌  万物生长", "달빛 아래  별이 빛나는 밤", "終末の旅  静かな街",
                    "青空と夏の記憶", "繁星落在孤岛  纯白之境", "은하수 여행  기억의 조각", "紅蓮の光  水鏡の世界",
                    "海阔天空  追逐梦想", "花鳥風月  春の足音", "새벽의 노래  희망의 빛", "風の向こうへ  銀河鉄道",
                };

            if (titles.Length == 0)
                throw new ArgumentException("The titles file must contain at least one title.");

            using var storage = new TemporaryNativeStorage($"cjk-sprite-text-{Guid.NewGuid():N}");
            using var host = new BenchmarkHost(storage.GetFullPath(string.Empty));
            host.Run(new TitleListGame(host, resources, titles, preloadCount));
        }

        private sealed class BenchmarkHost : OffscreenGameHost
        {
            private readonly GlobalStatistic<int> bindFlushes = GlobalStatistics.Get<int>(nameof(FlushBatchSource), nameof(FlushBatchSource.BindTexture));
            private readonly GlobalStatistic<long> rgbaBytes = GlobalStatistics.Get<long>("Native", "VeldridTexture");
            private readonly GlobalStatistic<long> arrayBytes = GlobalStatistics.Get<long>("Native", "VeldridGlyphTextureArray");
            private readonly int[] flushSamples = new int[sample_frames];
            private int measuredFrames;
            private int framesToMeasure;
            private long peakTextureBytes;

            public BenchmarkHost(string path)
                : base("cjk-sprite-text", 960, 720, path)
            {
            }

            public void StartSample(int frames)
            {
                measuredFrames = 0;
                framesToMeasure = frames;
                StartRecording(frames);
            }

            protected override void DrawFrame()
            {
                Array.Clear(FrameStatistics.COUNTERS);
                base.DrawFrame();
                peakTextureBytes = Math.Max(peakTextureBytes, rgbaBytes.Value + arrayBytes.Value);

                if (measuredFrames < framesToMeasure)
                    flushSamples[measuredFrames++] = bindFlushes.Value;
            }

            public void Report(string scenario, OffscreenFrameRecording recording)
            {
                double[] frameTimes = new double[recording.Frames];
                for (int i = 0; i < frameTimes.Length; i++)
                    frameTimes[i] = recording.UpdateTimes[i] + recording.DrawTimes[i] + recording.GpuTimes[i];

                Array.Sort(frameTimes);
                Console.WriteLine(FormattableString.Invariant(
                    $"{scenario} frame p50/p95/p99 {percentile(0.50):F3}/{percentile(0.95):F3}/{percentile(0.99):F3} ms; managed bytes/frame {recording.AllocatedBytes.Average():F0}."));

                foreach (string counter in new[] { "DrawCalls", "TextureBinds", "ShaderBinds" })
                {
                    int index = Array.IndexOf(recording.CounterNames, counter);
                    Console.WriteLine(FormattableString.Invariant($"{counter}/frame {recording.Counters[index].Average():F2}."));
                }

                Console.WriteLine(FormattableString.Invariant(
                    $"Texture-bind flushes/frame {flushSamples.Take(recording.Frames).Average():F2}; peak tracked texture payload {peakTextureBytes:N0} bytes (includes fallback textures)."));

                double percentile(double fraction) => frameTimes[(int)Math.Ceiling(fraction * frameTimes.Length) - 1];
            }
        }

        private sealed partial class TitleListGame : Game
        {
            private readonly BenchmarkHost host;
            private readonly ResourceStore<byte[]> resources;
            private readonly string[] titles;
            private readonly int preloadCount;
            private readonly Container rows = new Container { RelativeSizeAxes = Axes.Both };
            private bool firstUseRecorded;
            private bool scrollRecordingStarted;
            private int frame;

            public TitleListGame(BenchmarkHost host, ResourceStore<byte[]> resources, string[] titles, int preloadCount)
            {
                this.host = host;
                this.resources = resources;
                this.titles = titles;
                this.preloadCount = preloadCount;
            }

            [BackgroundDependencyLoader]
            private void load()
            {
                long start = Stopwatch.GetTimestamp();
                long allocatedAtStart = GC.GetTotalAllocatedBytes();
                var cjk = new RawCachingGlyphStore(resources, "Fonts/Noto/Noto-CJK-Basic");
                var hangul = new RawCachingGlyphStore(resources, "Fonts/Noto/Noto-Hangul");
                var basic = new RawCachingGlyphStore(resources, "Fonts/Noto/Noto-Basic");
                Fonts.AddTextureSource(cjk);
                Fonts.AddTextureSource(hangul);
                Fonts.AddTextureSource(basic);
                if (!cjk.HasGlyph('一') || !hangul.HasGlyph('가') || !basic.HasGlyph('é'))
                    throw new InvalidOperationException("The bundled Noto CJK, Hangul, or Basic glyph fixture is incomplete.");

                bool useArray = Fonts.UsesGlyphTextureArray;
                char[] cjkCharacters = cjk.GetAvailableResources()
                                          .Where(name => name.Length == cjk.FontName.Length + 2)
                                          .Select(name => name[^1])
                                          .Where(character => character >= '\u4e00' && character <= '\u9fff')
                                          .Distinct()
                                          .Order()
                                          .ToArray();
                char[] preloadedCharacters = cjkCharacters.Take(preloadCount).ToArray();

                if (preloadedCharacters.Length != preloadCount)
                    throw new InvalidOperationException($"Noto CJK contains {preloadedCharacters.Length} BMP Han glyphs, fewer than the requested {preloadCount}.");

                foreach (char character in preloadedCharacters)
                    Fonts.GetForSpriteText(string.Empty, character, useArray);

                string[] workloadTitles = titles;

                if (preloadedCharacters.Length >= 32)
                {
                    // Use the same glyph sequence in both processes regardless of how the renderer packs it.
                    string spreadTitle = new string(Enumerable.Range(0, 32).Select(i => preloadedCharacters[i * (preloadedCharacters.Length - 1) / 31]).ToArray());
                    workloadTitles = titles.Append(spreadTitle).ToArray();
                }

                int[] codepoints = workloadTitles.SelectMany(title => title.EnumerateRunes().ToArray()).Select(rune => rune.Value).Where(codepoint => codepoint > 127).Distinct().ToArray();

                foreach (int codepoint in codepoints)
                {
                    if (!cjk.HasGlyph(codepoint) && !hangul.HasGlyph(codepoint) && !basic.HasGlyph(codepoint))
                        throw new InvalidOperationException($"Noto fixtures do not cover U+{codepoint:X}.");
                }

                var textures = codepoints.Select(codepoint => Fonts.GetForSpriteText(string.Empty, codepoint, useArray)!.Texture).ToArray();
                int textureCount = textures.Select(texture => texture.NativeTexture).Distinct().Count();
                int layerCount = textures.OfType<GlyphAtlasRegion>().Select(region => region.Layer).Distinct().Count();
                Console.WriteLine(
                    $"{host.DeviceDescription}; array={useArray}; preload={preloadedCharacters.Length} glyphs; {workloadTitles.Length} workload strings / {codepoints.Length} unique non-ASCII scalars; {textureCount} native textures and {layerCount} array layers for visible glyphs.");

                Add(rows);

                for (int i = 0; i < row_count; i++)
                {
                    rows.Add(new SpriteText { Text = workloadTitles[i % workloadTitles.Length], Font = FrameworkFont.Regular.With(size: 21), X = 24, Y = i * row_height });
                }

                Console.WriteLine(FormattableString.Invariant(
                    $"Font-store setup, glyph preload and row construction: {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F1} ms, {GC.GetTotalAllocatedBytes() - allocatedAtStart:N0} managed bytes."));
                host.StartSample(first_use_frames);
            }

            protected override void Update()
            {
                base.Update();

                if (!firstUseRecorded && host.TakeRecording() is OffscreenFrameRecording firstUseRecording)
                {
                    host.Report("First-use", firstUseRecording);
                    firstUseRecorded = true;
                }

                if (frame >= warmup_frames && !scrollRecordingStarted)
                {
                    int arrayRows = rows.Children.OfType<SpriteText>().Count(text => text.UsesGlyphTextureArrayLayout);
                    Console.WriteLine($"Array layouts: {arrayRows}/{row_count}; pool fallbacks: {Fonts.GlyphTextureArrayFallbackCount}.");
                    host.StartSample(sample_frames);
                    scrollRecordingStarted = true;
                }

                if (scrollRecordingStarted)
                    rows.Y = -(frame % row_height);

                frame++;

                if (host.TakeRecording() is OffscreenFrameRecording recording)
                {
                    host.Report(scrollRecordingStarted ? "Warm scroll" : "First-use", recording);
                    firstUseRecorded = true;

                    if (!scrollRecordingStarted)
                    {
                        frame = warmup_frames;
                        return;
                    }

                    host.Exit();
                }
            }
        }
    }

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
