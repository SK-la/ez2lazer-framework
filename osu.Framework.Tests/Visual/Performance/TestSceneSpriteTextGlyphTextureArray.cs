// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Testing;
using osu.Framework.Text;
using osuTK;

namespace osu.Framework.Tests.Visual.Performance
{
    public partial class TestSceneSpriteTextGlyphTextureArray : PerformanceTestScene
    {
        private const int font_alias_count = 16;

        private static readonly string glyph_sample = new string(Enumerable.Range(33, 94).Select(i => (char)i).ToArray());

        [Resolved]
        private FontStore fonts { get; set; } = null!;

        private FillFlowContainer rows = null!;
        private OutlineFont outlineFont = null!;
        private readonly List<OutlineGlyphStore> glyphStores = new List<OutlineGlyphStore>();
        private SpriteText poolStatistics = null!;

        [BackgroundDependencyLoader]
        private void load()
        {
            fonts.EnableGlyphTextureArrayForVisualTest();

            var resources = new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(Game).Assembly), "Resources");
            outlineFont = new OutlineFont(resources, "Fonts/Roboto/Roboto") { Resolution = 100 };

            for (int i = 0; i < font_alias_count; i++)
            {
                var glyphStore = new OutlineGlyphStore(outlineFont, nameOverride: getFontName(i));
                glyphStores.Add(glyphStore);
                fonts.AddTextureSource(glyphStore);
            }
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            Add(new BasicScrollContainer
            {
                RelativeSizeAxes = Axes.Both,
                Child = rows = new FillFlowContainer
                {
                    Anchor = Anchor.TopLeft,
                    AutoSizeAxes = Axes.Y,
                    RelativeSizeAxes = Axes.X,
                    Direction = FillDirection.Vertical,
                    Spacing = new Vector2(0, 4),
                },
            });

            poolStatistics = new SpriteText
            {
                Font = FrameworkFont.Regular.With(size: 16),
                RelativeSizeAxes = Axes.X,
                AllowMultiline = false,
            };
            rows.Add(poolStatistics);

            for (int i = 0; i < font_alias_count; i++)
            {
                rows.Add(new SpriteText
                {
                    Font = new FontUsage(getFontName(i), 18),
                    Text = $"Title {i:D3}  {glyph_sample}",
                    RelativeSizeAxes = Axes.X,
                    AllowMultiline = false,
                });
            }

            rows.Add(new SpriteText
            {
                Font = FrameworkFont.Regular.With(size: 20),
                Text = $"Legacy font fallback  {FontAwesome.Solid.Music.Icon}",
                RelativeSizeAxes = Axes.X,
                AllowMultiline = false,
                Shadow = true,
            });
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            if (LoadState < LoadState.Loaded)
                return;

            bool arrayEnabled = fonts.UsesGlyphTextureArray;
            AddLabel($"Glyph texture array test path active: {arrayEnabled}");
            AddLabel("Production defaults to glyph arrays on immediate D3D11 and Metal. Set EZ2LAZER_GLYPH_TEXTURE_ARRAY=0 before launch to disable. This scene force-enables the array path.");
            AddLabel("Compare repeated UI text while idle and while scrolling.");
            AddLabel("The bundled outline fixture is Roboto Latin. This scene does not measure CJK font coverage or fallback shaping.");

            AddUntilStep("All Roboto alias glyphs are available", () => fonts.GetForSpriteText(getFontName(font_alias_count - 1), '~', useGlyphTextureArray: arrayEnabled) != null);

            AddAssert("Array mode preserves glyph metrics and reports pool usage", () =>
            {
                int arrayGlyphs = 0;
                int legacyGlyphs = 0;
                int highestLayer = -1;
                bool sameMetrics = true;

                foreach (OutlineGlyphStore glyphStore in glyphStores)
                {
                    foreach (char character in glyph_sample)
                    {
                        CharacterGlyph ordinary = glyphStore.Get(character)!;
                        ITexturedCharacterGlyph selected = fonts.GetForSpriteText(glyphStore.FontName, character, useGlyphTextureArray: arrayEnabled)!;

                        sameMetrics &= ordinary.XOffset.Equals(selected.XOffset)
                                       && ordinary.YOffset.Equals(selected.YOffset)
                                       && ordinary.XAdvance.Equals(selected.XAdvance)
                                       && ordinary.Baseline.Equals(selected.Baseline);

                        if (selected.Texture is GlyphAtlasRegion region)
                        {
                            arrayGlyphs++;
                            highestLayer = Math.Max(highestLayer, region.Layer);
                        }
                        else
                        {
                            legacyGlyphs++;
                        }
                    }
                }

                poolStatistics.Text = $"R8 glyphs: {arrayGlyphs:N0}, highest layer: {highestLayer}, legacy fallback: {legacyGlyphs:N0}, pool fallback: {fonts.GlyphTextureArrayFallbackCount:N0}";

                if (!arrayEnabled)
                    return sameMetrics && arrayGlyphs == 0;

                return sameMetrics && arrayGlyphs > 0 && highestLayer > 0 && fonts.GlyphTextureArrayFallbackCount == 0;
            });

            AddAssert("Colour fallback stays on a legacy texture", () =>
                fonts.GetForSpriteText(null, FontAwesome.Solid.Music.Icon, useGlyphTextureArray: arrayEnabled)?.Texture is not GlyphAtlasRegion);
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            if (isDisposing)
            {
                foreach (OutlineGlyphStore glyphStore in glyphStores)
                {
                    fonts.RemoveTextureStore(glyphStore);
                    glyphStore.Dispose();
                }

                outlineFont.Dispose();
            }
        }

        private static string getFontName(int index) => $"GlyphPool{index:D3}";
    }
}
