// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
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
    public class GlyphTextureArrayRenderingTest
    {
        private const int target_size = 64;
        private const int glyph_size = 16;
        private const int glyph_region_size = 8;
        private const int layer_count = 2;

        [TestCase(0u, byte.MaxValue)]
        [TestCase(1u, (byte)0)]
        public void TestGlyphArrayLayerSample(uint layer, byte coverage)
        {
            if (!OperatingSystem.IsWindows())
                Assert.Ignore("This integration test exercises the immediate D3D11 renderer.");

            bool wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;

            var renderer = new VeldridRenderer();
            VeldridGlyphTextureArray? glyphTexture = null;
            byte[] coveredGlyph = new byte[glyph_region_size * glyph_region_size];
            Array.Fill(coveredGlyph, byte.MaxValue);
            byte[] transparentGlyph = new byte[glyph_region_size * glyph_region_size];
            IFrameBuffer? target = null;
            ShaderManager? shaders = null;

            try
            {
                renderer.InitialiseOffline(target_size, target_size);
                glyphTexture = new VeldridGlyphTextureArray(renderer, glyph_size, glyph_size, layer_count);
                var glyphAtlas = new Texture(glyphTexture, WrapMode.None, WrapMode.None);
                var glyph = new GlyphAtlasRegion(glyphAtlas, new RectangleI(4, 4, glyph_region_size, glyph_region_size), (int)layer);
                glyphTexture.SetRegionData(0, 4, 4, glyph_region_size, glyph_region_size, coveredGlyph);
                glyphTexture.SetRegionData(1, 4, 4, glyph_region_size, glyph_region_size, transparentGlyph);
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
            if (!OperatingSystem.IsWindows())
                Assert.Ignore("This integration test exercises the immediate D3D11 renderer.");

            bool wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;

            var renderer = new VeldridRenderer();
            var stores = new List<OutlineGlyphStore>();
            var resources = new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(Game).Assembly), "Resources");
            var font = new OutlineFont(resources, "Fonts/Roboto/Roboto") { Resolution = 100 };
            var fontStore = new FontStore(renderer, useAtlas: false);

            try
            {
                renderer.InitialiseOffline(target_size, target_size);
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
                renderer.DisposeOffline();
                ThreadSafety.IsDrawThread = wasDrawThread;
            }
        }
    }
}
