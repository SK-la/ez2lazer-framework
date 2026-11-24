// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using osu.Framework.Development;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Graphics.Textures;
using osu.Framework.Graphics.Veldrid;
using osu.Framework.Graphics.Veldrid.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Text;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    [NonParallelizable]
    public class GlyphTextureArrayRenderingTest
    {
        private const int target_size = 64;
        private const int glyph_size = 16;
        private const int glyph_region_size = 8;
        private const int layer_count = 2;

        [Test]
        public void TestGlyphArrayUsesEnvironmentSettingCapturedAtFirstLookup()
        {
            const string environment_variable = "EZ2LAZER_GLYPH_TEXTURE_ARRAY";
            string? previousSetting = Environment.GetEnvironmentVariable(environment_variable);
            bool expectedEnabled = previousSetting != "0";
            bool wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;

            var renderer = new VeldridRenderer();
            FontStore? firstFontStore = null;
            FontStore? secondFontStore = null;

            try
            {
                firstFontStore = new FontStore(renderer, useAtlas: false);

                if (!tryInitialiseRenderer(renderer, requireGlyphArraySupport: false))
                    return;

                expectedEnabled &= GlyphTexturePool.SupportsArrayRendering(renderer);
                Assert.That(firstFontStore.UsesGlyphTextureArray, Is.EqualTo(expectedEnabled));

                Environment.SetEnvironmentVariable(environment_variable, expectedEnabled ? "0" : null);
                secondFontStore = new FontStore(renderer, useAtlas: false);
                Assert.That(secondFontStore.UsesGlyphTextureArray, Is.EqualTo(expectedEnabled));
            }
            finally
            {
                Environment.SetEnvironmentVariable(environment_variable, previousSetting);
                secondFontStore?.Dispose();
                firstFontStore?.Dispose();

                if (renderer.IsInitialised)
                    renderer.DisposeOffline();

                ThreadSafety.IsDrawThread = wasDrawThread;
            }
        }

        [TestCase(0u, byte.MaxValue)]
        [TestCase(0u, (byte)127)]
        [TestCase(1u, (byte)0)]
        public void TestGlyphArrayLayerSample(uint layer, byte coverage)
        {
            bool wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;

            var renderer = new VeldridRenderer();
            VeldridGlyphTextureArray? glyphTexture = null;
            byte[] glyphCoverage = new byte[glyph_region_size * glyph_region_size];
            Array.Fill(glyphCoverage, coverage);
            IFrameBuffer? target = null;
            ShaderManager? shaders = null;

            try
            {
                if (!tryInitialiseRenderer(renderer))
                    return;

                glyphTexture = new VeldridGlyphTextureArray(renderer, glyph_size, glyph_size, layer_count);
                var glyphAtlas = new Texture(glyphTexture, WrapMode.None, WrapMode.None);
                var glyph = new GlyphAtlasRegion(glyphAtlas, new RectangleI(4, 4, glyph_region_size, glyph_region_size), (int)layer);
                glyphTexture.SetRegionData(layer, 4, 4, glyph_region_size, glyph_region_size, glyphCoverage);
                renderer.BeginFrame(new Vector2(target_size));

                shaders = new ShaderManager(renderer, new NamespacedResourceStore<byte[]>(
                    new DllResourceStore(typeof(Game).Assembly), "Resources/Shaders"));
                IShader shader = shaders.Load(VertexShaderDescriptor.GLYPH_TEXTURE_ARRAY, FragmentShaderDescriptor.GLYPH_TEXTURE_ARRAY);
                target = renderer.CreateFrameBuffer();
                target.Size = new Vector2(target_size);

                target.Bind();
                renderer.PushViewport(new RectangleI(0, 0, target_size, target_size));
                renderer.PushDepthInfo(new DepthInfo(false, false));
                renderer.Clear(new ClearInfo(Color4.Black));
                renderer.SetBlend(BlendingParameters.None);
                shader.Bind();
                renderer.SetActiveBatch(renderer.GlyphArrayBatch);

                // The shader requires the legacy sampler even when all vertices use the glyph array.
                Assert.That(renderer.WhitePixel.Bind(1, WrapMode.None, WrapMode.None), Is.True);
                renderer.DrawGlyphArrayQuad(renderer.GlyphArrayBatch, glyph, glyph.Layer, useLegacyTexture: false,
                    new Quad(0, 0, target_size, target_size),
                    ColourInfo.SingleColour(Color4.White), Vector2.Zero);

                shader.Unbind();
                renderer.SetActiveBatch(renderer.DefaultQuadBatch);
                renderer.PopDepthInfo();
                renderer.PopViewport();
                target.Unbind();
                renderer.FinishFrame();
                renderer.WaitForGpu();

                using RgbaImage? image = ((IRenderer)renderer).ExtractFrameBufferData(target);
                Assert.That(image, Is.Not.Null);

                Rgba32 sample = image![target_size / 2, target_size / 2];
                Assert.That(sample, Is.EqualTo(new Rgba32(255, 255, 255, coverage)));
            }
            finally
            {
                shaders?.Dispose();
                target?.Dispose();
                glyphTexture?.Dispose();

                if (renderer.IsInitialised)
                    renderer.DisposeOffline();

                ThreadSafety.IsDrawThread = wasDrawThread;
            }
        }

        [Test]
        public void TestOutlineGlyphPoolUsesMultipleLayersWithinCapacity()
        {
            bool wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;

            var renderer = new VeldridRenderer();
            var stores = new List<OutlineGlyphStore>();
            var resources = new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(Game).Assembly), "Resources");
            var font = new OutlineFont(resources, "Fonts/Roboto/Roboto") { Resolution = 100 };
            var fontStore = new FontStore(renderer, useAtlas: false);

            try
            {
                if (!tryInitialiseRenderer(renderer))
                    return;

                fontStore.EnableGlyphTextureArrayForVisualTest();
                font.LoadAsync().GetAwaiter().GetResult();

                const int alias_count = 16;
                const string glyphs = "!\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~";

                for (int i = 0; i < alias_count; i++)
                {
                    var store = new OutlineGlyphStore(font, nameOverride: $"GlyphPool{i:D2}");
                    stores.Add(store);
                    fontStore.AddTextureSource(store);
                }

                stores[^1].LoadFontAsync().GetAwaiter().GetResult();

                int glyphCount = 0;
                int highestLayer = -1;

                foreach (OutlineGlyphStore store in stores)
                {
                    foreach (char character in glyphs)
                    {
                        var glyph = fontStore.GetForSpriteText(store.FontName, character, useGlyphTextureArray: true) as TexturedCharacterGlyph;
                        Assert.That(glyph, Is.Not.Null);
                        Assert.That(glyph!.Texture, Is.TypeOf<GlyphAtlasRegion>());

                        highestLayer = Math.Max(highestLayer, ((GlyphAtlasRegion)glyph.Texture).Layer);
                        glyphCount++;
                    }
                }

                Assert.That(glyphCount, Is.EqualTo(alias_count * glyphs.Length));
                Assert.That(highestLayer, Is.GreaterThan(0), "The workload should cross at least one array layer.");
                Assert.That(fontStore.GlyphTextureArrayFallbackCount, Is.Zero, "The configured workload should fit the fixed layer capacity.");
            }
            finally
            {
                fontStore.Dispose();
                font.Dispose();

                if (renderer.IsInitialised)
                    renderer.DisposeOffline();

                ThreadSafety.IsDrawThread = wasDrawThread;
            }
        }

        [Test]
        public void TestBitmapGlyphStoreUsesArrayCoverage()
        {
            bool wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;

            var renderer = new VeldridRenderer();
            GlyphStore? glyphStore = null;
            FontStore? fontStore = null;

            try
            {
                if (!tryInitialiseRenderer(renderer))
                    return;

                fontStore = new FontStore(renderer, useAtlas: false);
                fontStore.EnableGlyphTextureArrayForVisualTest();

                var resources = new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(GlyphTextureArrayRenderingTest).Assembly), "Resources");
                glyphStore = new GlyphStore(resources, "Fonts/Roboto/Roboto-Regular");
                glyphStore.LoadFontAsync().GetAwaiter().GetResult();
                fontStore.AddTextureSource(glyphStore);

                var ordinaryGlyph = fontStore.GetForSpriteText(null, 'a', useGlyphTextureArray: false) as TexturedCharacterGlyph;
                var arrayGlyph = fontStore.GetForSpriteText(null, 'a', useGlyphTextureArray: true) as TexturedCharacterGlyph;

                Assert.That(ordinaryGlyph, Is.Not.Null);
                Assert.That(arrayGlyph, Is.Not.Null);
                Assert.That(arrayGlyph!.Texture, Is.TypeOf<GlyphAtlasRegion>());
                Assert.That(arrayGlyph.XOffset, Is.EqualTo(ordinaryGlyph!.XOffset));
                Assert.That(arrayGlyph.YOffset, Is.EqualTo(ordinaryGlyph.YOffset));
                Assert.That(arrayGlyph.XAdvance, Is.EqualTo(ordinaryGlyph.XAdvance));
                Assert.That(arrayGlyph.Baseline, Is.EqualTo(ordinaryGlyph.Baseline));

                using TextureUpload upload = glyphStore.Get("Roboto-Regular/a");
                Rgba32[] pixels = upload.Data.ToArray();
                Assert.That(Array.Exists(pixels, static pixel => pixel.A > 0 && pixel.R != pixel.G), Is.False);
                Assert.That(Array.Exists(pixels, static pixel => pixel.A == 0 && pixel.R != pixel.G), Is.True);
            }
            finally
            {
                fontStore?.Dispose();
                glyphStore?.Dispose();

                if (renderer.IsInitialised)
                    renderer.DisposeOffline();

                ThreadSafety.IsDrawThread = wasDrawThread;
            }
        }

        [Test]
        public void TestColourBitmapGlyphStoreUsesLegacyTexture()
        {
            bool wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;

            var renderer = new VeldridRenderer();
            TintedGlyphStore? glyphStore = null;
            FontStore? fontStore = null;

            try
            {
                if (!tryInitialiseRenderer(renderer))
                    return;

                fontStore = new FontStore(renderer, useAtlas: false);
                fontStore.EnableGlyphTextureArrayForVisualTest();

                var resources = new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(GlyphTextureArrayRenderingTest).Assembly), "Resources");
                glyphStore = new TintedGlyphStore(resources, "Fonts/Roboto/Roboto-Regular");
                glyphStore.LoadFontAsync().GetAwaiter().GetResult();
                fontStore.AddTextureSource(glyphStore);

                var glyph = fontStore.GetForSpriteText(null, 'a', useGlyphTextureArray: true) as TexturedCharacterGlyph;

                Assert.That(glyph, Is.Not.Null);
                Assert.That(glyph!.Texture, Is.TypeOf<Texture>());
            }
            finally
            {
                fontStore?.Dispose();
                glyphStore?.Dispose();

                if (renderer.IsInitialised)
                    renderer.DisposeOffline();

                ThreadSafety.IsDrawThread = wasDrawThread;
            }
        }

        [Test]
        public void TestReleasedGlyphRegionsAreReusedWithoutOverwritingLiveGlyphs()
        {
            bool wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;

            var renderer = new VeldridRenderer();
            GlyphTexturePool? glyphPool = null;
            TextureUpload? smallUpload = null;
            TextureUpload? largeUpload = null;

            try
            {
                if (!tryInitialiseRenderer(renderer))
                    return;

                glyphPool = GlyphTexturePool.CreateForTesting(renderer, 32, 1);
                smallUpload = new TextureUpload(new RgbaImage(glyph_size / 2, glyph_size / 2, new Rgba32(255, 255, 255)));
                largeUpload = new TextureUpload(new RgbaImage(24, 24, new Rgba32(255, 255, 255)));

                var glyphs = new List<GlyphRegionReference>();
                for (int i = 0; i < 4; i++)
                    glyphs.Add(addGlyphReference(glyphPool, smallUpload));

                Assert.That(glyphPool.TryAdd(smallUpload, out _), Is.False, "A live glyph region must not be reused.");
                Assert.That(glyphs[0].HoldsGlyph, Is.True);
                Assert.That(isGlyphAlive(glyphs[0]), Is.True);

                foreach (GlyphRegionReference glyph in glyphs)
                    glyph.Release();

                collectFullGarbage();

                foreach (GlyphRegionReference glyph in glyphs)
                    Assert.That(isGlyphAlive(glyph), Is.False);

                glyphPool.ReclaimUnusedRegions();

                Assert.That(glyphPool.TryAdd(largeUpload, out _), Is.True, "Adjacent released regions should merge for a larger glyph.");
            }
            finally
            {
                smallUpload?.Dispose();
                largeUpload?.Dispose();
                glyphPool?.Dispose();

                if (renderer.IsInitialised)
                    renderer.DisposeOffline();

                ThreadSafety.IsDrawThread = wasDrawThread;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static GlyphRegionReference addGlyphReference(GlyphTexturePool glyphPool, TextureUpload upload)
        {
            Assert.That(glyphPool.TryAdd(upload, out GlyphAtlasRegion? glyph), Is.True);
            return new GlyphRegionReference(glyph!);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool isGlyphAlive(GlyphRegionReference glyphReference)
            => glyphReference.WeakRegion.TryGetTarget(out _);

        private static bool tryInitialiseRenderer(VeldridRenderer renderer, bool requireGlyphArraySupport = true)
        {
            try
            {
                renderer.InitialiseOffline(target_size, target_size);

                if (requireGlyphArraySupport && !GlyphTexturePool.SupportsArrayRendering(renderer))
                {
                    Assert.Ignore($"Glyph texture arrays are not enabled for the {renderer.SurfaceType} renderer.");
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                Assert.Ignore($"No immediate Veldrid graphics device is available: {exception.Message}");
                return false;
            }
        }

        private static void collectFullGarbage()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private sealed class TintedGlyphStore : GlyphStore
        {
            public TintedGlyphStore(ResourceStore<byte[]> store, string assetName)
                : base(store, assetName)
            {
            }

            protected override TextureUpload GetPageImage(int page)
            {
                using TextureUpload original = base.GetPageImage(page);
                var tintedImage = new RgbaImage(original.Width, original.Height);

                for (int y = 0; y < original.Height; y++)
                {
                    ReadOnlySpan<Rgba32> sourceRow = original.Data.Slice(y * original.Width, original.Width);
                    Span<Rgba32> targetRow = tintedImage.GetPixelRowSpan(y);

                    for (int x = 0; x < original.Width; x++)
                        targetRow[x] = new Rgba32(byte.MaxValue, 0, 0, sourceRow[x].A);
                }

                return new TextureUpload(tintedImage);
            }
        }

        private sealed class GlyphRegionReference
        {
            private GlyphAtlasRegion? region;

            public bool HoldsGlyph => region != null;

            public WeakReference<GlyphAtlasRegion> WeakRegion { get; }

            public GlyphRegionReference(GlyphAtlasRegion region)
            {
                this.region = region;
                WeakRegion = new WeakReference<GlyphAtlasRegion>(region);
            }

            public void Release() => region = null;
        }
    }
}
