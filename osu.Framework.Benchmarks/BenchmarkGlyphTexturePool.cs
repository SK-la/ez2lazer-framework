// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Graphics.Veldrid;
using osu.Framework.IO.Stores;
using osu.Framework.Text;
using Veldrid;
using Veldrid.SPIRV;

namespace osu.Framework.Benchmarks
{
    internal static class BenchmarkGlyphTexturePool
    {
        private const int page_count = 4;
        private const int page_size = 128;
        private const int cell_size = 32;
        private const int cells_per_row = page_size / cell_size;
        private const int glyphs_per_page = cells_per_row * cells_per_row;
        private const int frame_size = 512;
        private const int glyph_count = frame_size / cell_size * (frame_size / cell_size);
        private const int vertices_per_glyph = 6;
        private const int warmup_count = 4;
        private const int sample_count = 30;

        private const string vertex_shader = """
                                             #version 450
                                             layout(location = 0) in vec2 Position;
                                             layout(location = 1) in vec2 TexCoord;
                                             layout(location = 2) in vec2 GlyphInfo;
                                             layout(location = 0) out vec2 v_TexCoord;
                                             layout(location = 1) flat out vec2 v_GlyphInfo;
                                             void main()
                                             {
                                                 gl_Position = vec4(Position, 0.5, 1.0);
                                                 v_TexCoord = TexCoord;
                                                 v_GlyphInfo = GlyphInfo;
                                             }
                                             """;

        private const string fragment_shader = """
                                               #version 450
                                               layout(location = 0) in vec2 v_TexCoord;
                                               layout(location = 1) flat in vec2 v_GlyphInfo;
                                               layout(set = 0, binding = 0) uniform COVERAGE_TEXTURE Coverage;
                                               layout(set = 0, binding = 1) uniform sampler CoverageSampler;
                                               layout(set = 1, binding = 0) uniform texture2D Colour;
                                               layout(set = 1, binding = 1) uniform sampler ColourSampler;
                                               layout(location = 0) out vec4 OutputColour;
                                               void main()
                                               {
                                                   if (v_GlyphInfo.y > 0.5)
                                                       OutputColour = texture(sampler2D(Colour, ColourSampler), v_TexCoord);
                                                   else
                                                       OutputColour = vec4(1.0, 1.0, 1.0, SAMPLE_COVERAGE.r);
                                               }
                                               """;

        public static void Run()
        {
            byte[][] pages = rasterizeCoverage();
            Rgba32[] colourPage = createColourPage();
            var offline = new VeldridDevice(frame_size, frame_size);

            try
            {
                run(offline, pages, colourPage);
            }
            finally
            {
                offline.DisposeOffline();
            }
        }

        private static void run(VeldridDevice offline, byte[][] pages, Rgba32[] colourPage)
        {
            GraphicsDevice device = offline.Device;
            ResourceFactory factory = offline.Factory;
            Framebuffer target = offline.DefaultFramebuffer;
            Console.WriteLine($"Offscreen GPU: {device.BackendType} ({device.DeviceName})");

            using var sampler = factory.CreateSampler(new SamplerDescription
            {
                AddressModeU = SamplerAddressMode.Clamp,
                AddressModeV = SamplerAddressMode.Clamp,
                AddressModeW = SamplerAddressMode.Clamp,
                Filter = SamplerFilter.MinPointMagPointMipPoint,
            });
            using var coverageLayout = createLayout(factory, "Coverage", "CoverageSampler");
            using var colourLayout = createLayout(factory, "Colour", "ColourSampler");
            using var colourTexture = createTexture(factory, PixelFormat.R8G8B8A8UNorm, 1);
            using var colourSet = factory.CreateResourceSet(new ResourceSetDescription(colourLayout, colourTexture, sampler));
            device.UpdateTexture<Rgba32>(colourTexture, colourPage.AsSpan(), 0, 0, 0, page_size, page_size, 1, 0, 0);
            device.WaitForIdle();

            using var arrayTexture = createTexture(factory, PixelFormat.R8UNorm, page_count);
            using var arraySet = factory.CreateResourceSet(new ResourceSetDescription(coverageLayout, arrayTexture, sampler));
            var pageTextures = new Texture[page_count];
            var pageSets = new ResourceSet[page_count];

            try
            {
                for (int i = 0; i < page_count; i++)
                {
                    pageTextures[i] = createTexture(factory, PixelFormat.R8UNorm, 1);
                    pageSets[i] = factory.CreateResourceSet(new ResourceSetDescription(coverageLayout, pageTextures[i], sampler));
                }

                uploadCoverage(device, pages, pageTextures, arrayTexture);

                using var pageShaders = new GlyphShaders(factory, array: false);
                using var arrayShaders = new GlyphShaders(factory, array: true);
                using var pagePipeline = createPipeline(factory, target, pageShaders.Shaders, coverageLayout, colourLayout);
                using var arrayPipeline = createPipeline(factory, target, arrayShaders.Shaders, coverageLayout, colourLayout);
                using var commands = factory.CreateCommandList();

                testLayout(1);
                testLayout(8);

                const int coverage_bytes = page_count * page_size * page_size;
                const int colour_bytes = page_size * page_size * 4;
                Console.WriteLine(
                    $"Texture payload without mips: {coverage_bytes:N0} B for {page_count} R8 pages/layers plus {colour_bytes:N0} B for the shared RGBA page. The same coverage pages in RGBA would use {coverage_bytes * 4:N0} B.");

                void testLayout(int glyphsPerPageRun)
                {
                    var vertices = new GlyphVertex[glyph_count * vertices_per_glyph];
                    int[] pageIndices = new int[glyph_count];
                    var expected = new Rgba32[frame_size * frame_size];
                    buildGrid(device, pages, colourPage, glyphsPerPageRun, vertices, pageIndices, expected);

                    uint vertexBytes = (uint)(vertices.Length * Marshal.SizeOf<GlyphVertex>());
                    using var vertexBuffer = factory.CreateBuffer(new BufferDescription(vertexBytes, BufferUsage.VertexBuffer));
                    device.UpdateBuffer<GlyphVertex>(vertexBuffer, 0, vertices.AsSpan());

                    int pageDraws = draw(false);
                    using var pageImage = offline.TakeScreenshot();
                    draw(true);
                    using var arrayImage = offline.TakeScreenshot();

                    if (!pageImage.PixelSpan.SequenceEqual(arrayImage.PixelSpan))
                        throw new InvalidOperationException("Separate atlas pages and the texture array produced different pixels.");
                    if (!matchesReference(arrayImage, expected, flipVertical: false) && !matchesReference(arrayImage, expected, flipVertical: true))
                        throw new InvalidOperationException("Glyph coverage, layer selection, or RGBA colour differs from the source pixels.");

                    const int layer_probe_glyph = 1;
                    const int probe_vertex = layer_probe_glyph * vertices_per_glyph;
                    float expectedLayer = vertices[probe_vertex].GlyphInfo.X;
                    vertices[probe_vertex].GlyphInfo.X = (expectedLayer + 1) % page_count;
                    device.UpdateBuffer(vertexBuffer, 0, vertices.AsSpan());
                    draw(true);

                    using (var wrongLayerImage = offline.TakeScreenshot())
                    {
                        if (matchesReference(wrongLayerImage, expected, flipVertical: false)
                            || matchesReference(wrongLayerImage, expected, flipVertical: true))
                            throw new InvalidOperationException("The pixel check did not detect a deliberately incorrect array layer.");
                    }

                    vertices[probe_vertex].GlyphInfo.X = expectedLayer;
                    device.UpdateBuffer(vertexBuffer, 0, vertices.AsSpan());
                    Console.WriteLine("Pixel check rejected the deliberately incorrect array layer.");

                    for (int i = 0; i < warmup_count; i++)
                    {
                        draw(false);
                        draw(true);
                    }

                    var pageSamples = new Sample[sample_count];
                    var arraySamples = new Sample[sample_count];

                    for (int i = 0; i < sample_count; i++)
                    {
                        if ((i & 1) == 0)
                        {
                            pageSamples[i] = measure(false);
                            arraySamples[i] = measure(true);
                        }
                        else
                        {
                            arraySamples[i] = measure(true);
                            pageSamples[i] = measure(false);
                        }
                    }

                    Console.WriteLine($"{glyph_count} glyphs / {glyphsPerPageRun} per page run. Readback matched both the alternate path and source pixels.");
                    report("Separate R8 pages", pageDraws, pageSamples);
                    report("R8 texture array", 1, arraySamples);

                    Sample measure(bool array)
                    {
                        long start = Stopwatch.GetTimestamp();
                        drawWithTimestamp(array, out long submitted);
                        return new Sample(Stopwatch.GetElapsedTime(start, submitted).TotalMilliseconds, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    }

                    int draw(bool array) => drawWithTimestamp(array, out _);

                    int drawWithTimestamp(bool array, out long submitted)
                    {
                        commands.Begin();
                        commands.SetFramebuffer(target);
                        commands.SetViewport(0, new Viewport(0, 0, frame_size, frame_size, 0, 1));
                        commands.ClearColorTarget(0, new RgbaFloat(0, 0, 0, 0));
                        commands.SetPipeline(array ? arrayPipeline : pagePipeline);
                        commands.SetVertexBuffer(0, vertexBuffer);
                        commands.SetGraphicsResourceSet(1, colourSet);

                        int draws = 0;

                        if (array)
                        {
                            commands.SetGraphicsResourceSet(0, arraySet);
                            commands.Draw(glyph_count * vertices_per_glyph);
                            draws = 1;
                        }
                        else
                        {
                            int first = 0;

                            while (first < pageIndices.Length)
                            {
                                int page = pageIndices[first];
                                int end = first + 1;
                                while (end < pageIndices.Length && pageIndices[end] == page)
                                    end++;

                                commands.SetGraphicsResourceSet(0, pageSets[page]);
                                commands.Draw((uint)((end - first) * vertices_per_glyph), 1, (uint)(first * vertices_per_glyph), 0);
                                draws++;
                                first = end;
                            }
                        }

                        commands.End();
                        device.SubmitCommands(commands);
                        submitted = Stopwatch.GetTimestamp();
                        device.WaitForIdle();
                        return draws;
                    }
                }
            }
            finally
            {
                device.WaitForIdle();

                for (int i = page_count - 1; i >= 0; i--)
                {
                    pageSets[i].Dispose();
                    pageTextures[i].Dispose();
                }
            }
        }

        private static void report(string label, int draws, Sample[] samples)
        {
            Array.Sort(samples, static (a, b) => a.SubmitMs.CompareTo(b.SubmitMs));
            double medianSubmit = samples[samples.Length / 2].SubmitMs;
            Array.Sort(samples, static (a, b) => a.CompleteMs.CompareTo(b.CompleteMs));
            Console.WriteLine(
                $"  {label}: {draws} coverage binds/draws; median record+submit {medianSubmit:F3} ms, median submit+wait including recording {samples[samples.Length / 2].CompleteMs:F3} ms.");
        }

        private static void uploadCoverage(GraphicsDevice device, byte[][] pages, Texture[] pageTextures, Texture arrayTexture)
        {
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < page_count; i++)
                device.UpdateTexture<byte>(pageTextures[i], pages[i].AsSpan(), 0, 0, 0, page_size, page_size, 1, 0, 0);
            device.WaitForIdle();
            double separateTime = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            start = Stopwatch.GetTimestamp();
            for (int i = 0; i < page_count; i++)
                device.UpdateTexture<byte>(arrayTexture, pages[i].AsSpan(), 0, 0, 0, page_size, page_size, 1, 0, (uint)i);
            device.WaitForIdle();
            Console.WriteLine($"First coverage upload + wait: separate pages {separateTime:F3} ms, texture array {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F3} ms.");
        }

        private static Texture createTexture(ResourceFactory factory, PixelFormat format, uint layers)
            => factory.CreateTexture(TextureDescription.Texture2D(page_size, page_size, 1, layers, format, TextureUsage.Sampled));

        private static ResourceLayout createLayout(ResourceFactory factory, string textureName, string samplerName)
            => factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription(textureName, ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription(samplerName, ResourceKind.Sampler, ShaderStages.Fragment)));

        private static Pipeline createPipeline(ResourceFactory factory, Framebuffer target, Shader[] shaders, ResourceLayout coverage, ResourceLayout colour)
        {
            var vertices = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("TexCoord", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("GlyphInfo", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2));
            return factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
            {
                BlendState = new BlendStateDescription(new RgbaFloat(0, 0, 0, 0), new BlendAttachmentDescription
                {
                    BlendEnabled = false,
                    ColorWriteMask = ColorWriteMask.All,
                }),
                DepthStencilState = new DepthStencilStateDescription(false, false, ComparisonKind.Always),
                RasterizerState = RasterizerStateDescription.CULL_NONE,
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ShaderSet = new ShaderSetDescription(new[] { vertices }, shaders),
                ResourceLayouts = new[] { coverage, colour },
                Outputs = target.OutputDescription,
            });
        }

        private static byte[][] rasterizeCoverage()
        {
            const string glyphs = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!?";
            if (glyphs.Length != page_count * glyphs_per_page)
                throw new InvalidOperationException("The glyph source must fill every atlas page exactly.");

            using var resources = new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(Game).Assembly), "Resources");
            using var font = new OutlineFont(resources, "Fonts/Roboto/Roboto") { Resolution = cell_size };
            font.LoadAsync().GetAwaiter().GetResult();
            var variation = font.DecodeFontVariation(new FontVariation { NamedInstance = "Roboto-Regular" });
            byte[][] pages = new byte[page_count][];
            for (int i = 0; i < pages.Length; i++)
                pages[i] = new byte[page_size * page_size];

            for (int pageIndex = 0; pageIndex < page_count; pageIndex++)
            {
                for (int glyphNumber = 0; glyphNumber < glyphs_per_page; glyphNumber++)
                {
                    char character = glyphs[pageIndex * glyphs_per_page + glyphNumber];
                    uint glyphIndex = font.GetGlyphIndex(character);
                    if (glyphIndex == 0)
                        throw new InvalidOperationException($"Missing source glyph: {character}.");

                    using var glyph = font.RasterizeGlyph(glyphIndex, variation)
                                      ?? throw new InvalidOperationException($"Failed to render source glyph: {character}.");
                    if (glyph.Width > cell_size || glyph.Height > cell_size)
                        throw new InvalidOperationException($"Source glyph exceeds its {cell_size}px cell: {character}.");

                    byte[] page = pages[pageIndex];
                    (int x, int y) = getCellPosition(glyphNumber, cells_per_row);
                    bool visible = false;

                    for (int row = 0; row < glyph.Height; row++)
                    {
                        for (int column = 0; column < glyph.Width; column++)
                        {
                            byte alpha = glyph.Data[row * glyph.Width + column].A;
                            page[(y + row) * page_size + x + column] = alpha;
                            visible |= alpha != 0;
                        }
                    }

                    if (!visible)
                        throw new InvalidOperationException($"Source glyph has no visible pixels: {character}.");
                }
            }

            return pages;
        }

        private static Rgba32[] createColourPage()
        {
            var page = new Rgba32[page_size * page_size];

            for (int y = 3; y < cell_size - 3; y++)
            {
                for (int x = 3; x < cell_size - 3; x++)
                    page[y * page_size + x] = new Rgba32((byte)(x * 7), (byte)(y * 7), 203, (byte)(x < cell_size / 2 ? 96 : 255));
            }

            return page;
        }

        private static void buildGrid(GraphicsDevice device, byte[][] pages, Rgba32[] colourPage, int glyphsPerPageRun, GlyphVertex[] vertices, int[] pageIndices, Rgba32[] expected)
        {
            for (int i = 0; i < pageIndices.Length; i++)
            {
                int page = i / glyphsPerPageRun % page_count;
                pageIndices[i] = page;
                bool hasColour = i % 13 == 0;
                int glyph = hasColour ? 0 : i % glyphs_per_page;
                (int u, int v) = getCellPosition(glyph % glyphs_per_page, cells_per_row);
                (int x, int y) = getCellPosition(i, frame_size / cell_size);
                int start = i * vertices_per_glyph;
                var glyphInfo = new Vector2(page, hasColour ? 1 : 0);
                GlyphVertex topLeft = makeVertex(0, 0);
                GlyphVertex topRight = makeVertex(cell_size, 0);
                GlyphVertex bottomLeft = makeVertex(0, cell_size);
                GlyphVertex bottomRight = makeVertex(cell_size, cell_size);
                vertices[start] = topLeft;
                vertices[start + 1] = bottomLeft;
                vertices[start + 2] = topRight;
                vertices[start + 3] = topRight;
                vertices[start + 4] = bottomLeft;
                vertices[start + 5] = bottomRight;

                for (int row = 0; row < cell_size; row++)
                {
                    for (int column = 0; column < cell_size; column++)
                    {
                        int source = (v + row) * page_size + u + column;
                        expected[(y + row) * frame_size + x + column] = hasColour ? colourPage[source] : new Rgba32(255, 255, 255, pages[page][source]);
                    }
                }

                GlyphVertex makeVertex(int offsetX, int offsetY)
                {
                    float ndcX = 2f * (x + offsetX) / frame_size - 1;
                    float ndcY = 1 - 2f * (y + offsetY) / frame_size;
                    if (device.IsClipSpaceYInverted)
                        ndcY = -ndcY;

                    return new GlyphVertex(new Vector2(ndcX, ndcY), new Vector2((u + offsetX) / (float)page_size, (v + offsetY) / (float)page_size), glyphInfo);
                }
            }
        }

        private static (int X, int Y) getCellPosition(int index, int columns) => (index % columns * cell_size, index / columns * cell_size);

        private static bool matchesReference(RgbaImage actual, Rgba32[] expected, bool flipVertical)
        {
            for (int row = 0; row < frame_size; row++)
            {
                if (!actual.GetReadOnlyPixelRowSpan(flipVertical ? frame_size - 1 - row : row).SequenceEqual(expected.AsSpan(row * frame_size, frame_size)))
                    return false;
            }

            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GlyphVertex
        {
            public Vector2 Position;
            public Vector2 TexCoord;
            public Vector2 GlyphInfo;

            public GlyphVertex(Vector2 position, Vector2 texCoord, Vector2 glyphInfo)
            {
                Position = position;
                TexCoord = texCoord;
                GlyphInfo = glyphInfo;
            }
        }

        private readonly record struct Sample(double SubmitMs, double CompleteMs);

        private sealed class GlyphShaders : IDisposable
        {
            public Shader[] Shaders { get; }

            public GlyphShaders(ResourceFactory factory, bool array)
            {
                string fragment = fragment_shader.Replace("COVERAGE_TEXTURE", array ? "texture2DArray" : "texture2D", StringComparison.Ordinal)
                                                 .Replace("SAMPLE_COVERAGE", array
                                                     ? "texture(sampler2DArray(Coverage, CoverageSampler), vec3(v_TexCoord, v_GlyphInfo.x))"
                                                     : "texture(sampler2D(Coverage, CoverageSampler), v_TexCoord)", StringComparison.Ordinal);
                Shaders = factory.CreateFromSpirv(
                    new ShaderDescription(ShaderStages.Vertex, System.Text.Encoding.UTF8.GetBytes(vertex_shader), "main"),
                    new ShaderDescription(ShaderStages.Fragment, System.Text.Encoding.UTF8.GetBytes(fragment), "main"));
            }

            public void Dispose()
            {
                foreach (Shader shader in Shaders)
                    shader.Dispose();
            }
        }
    }
}
