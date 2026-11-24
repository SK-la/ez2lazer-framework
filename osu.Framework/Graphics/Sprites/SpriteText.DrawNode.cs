// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Vertices;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Graphics.Textures;
using osu.Framework.Graphics.Veldrid;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Graphics.Sprites
{
    public partial class SpriteText
    {
        internal class SpriteTextDrawNode : TexturedShaderDrawNode
        {
            protected new SpriteText Source => (SpriteText)base.Source;

            private bool shadow;
            private ColourInfo shadowColour;
            private Vector2 shadowOffset;

            private List<ScreenSpaceCharacterPart>? parts;
            private IShader? glyphArrayShader;
            private bool useGlyphTextureArray;

            public SpriteTextDrawNode(SpriteText source)
                : base(source)
            {
            }

            public override void ApplyState()
            {
                base.ApplyState();

                updateScreenSpaceCharacters();
                useGlyphTextureArray = Source.UsesGlyphTextureArrayLayout && Source.UsesDefaultTextureShader;
                glyphArrayShader = Source.GlyphArrayTextureShader;
                shadow = Source.Shadow;

                if (shadow)
                {
                    shadowColour = Source.ShadowColour;
                    shadowOffset = Source.premultipliedShadowOffset;
                }
            }

            protected override void Draw(IRenderer renderer)
            {
                Debug.Assert(parts != null);

                base.Draw(renderer);

                if (tryDrawGlyphArray(renderer))
                    return;

                BindTextureShader(renderer);

                var avgColour = (Color4)DrawColourInfo.Colour.AverageColour;
                float shadowAlpha = MathF.Pow(Math.Max(Math.Max(avgColour.R, avgColour.G), avgColour.B), 2);

                //adjust shadow alpha based on highest component intensity to avoid muddy display of darker text.
                //squared result for quadratic fall-off seems to give the best result.
                var finalShadowColour = DrawColourInfo.Colour;
                finalShadowColour.ApplyChild(shadowColour.MultiplyAlpha(shadowAlpha));

                for (int i = 0; i < parts.Count; i++)
                {
                    ColourInfo glyphColour = parts[i].HasColour
                        ? ColourInfo.SingleColour(new Color4(1f, 1f, 1f, ((Color4)DrawColourInfo.Colour.AverageColour).A))
                        : DrawColourInfo.Colour;

                    if (shadow && !parts[i].HasColour)
                    {
                        var shadowQuad = parts[i].DrawQuad;

                        renderer.DrawQuad(parts[i].Texture,
                            new Quad(
                                shadowQuad.TopLeft + shadowOffset,
                                shadowQuad.TopRight + shadowOffset,
                                shadowQuad.BottomLeft + shadowOffset,
                                shadowQuad.BottomRight + shadowOffset),
                            finalShadowColour, inflationPercentage: parts[i].InflationPercentage);
                    }

                    renderer.DrawQuad(parts[i].Texture, parts[i].DrawQuad, glyphColour, inflationPercentage: parts[i].InflationPercentage);
                }

                UnbindTextureShader(renderer);
            }

            private bool tryDrawGlyphArray(IRenderer renderer)
            {
                if (!useGlyphTextureArray || renderer is not VeldridRenderer concrete || glyphArrayShader == null || parts == null || parts.Count == 0)
                    return false;

                GlyphAtlasRegion? firstArrayGlyph = null;

                foreach (ScreenSpaceCharacterPart part in parts)
                {
                    if (part.Texture is GlyphAtlasRegion { IsArrayGlyph: true } region)
                        firstArrayGlyph ??= region;
                }

                if (firstArrayGlyph == null)
                    return false;

                glyphArrayShader.Bind();
                BindUniformResources(glyphArrayShader, renderer);
                firstArrayGlyph.ArrayTexture.Bind(0, WrapMode.None, WrapMode.None);
                renderer.WhitePixel.Bind(1, WrapMode.None, WrapMode.None);
                drawGlyphArrayParts(renderer, concrete.GlyphArrayBatch);
                glyphArrayShader.Unbind();
                return true;
            }

            private void drawGlyphArrayParts(IRenderer renderer, IVertexBatch<GlyphArrayVertex2D> batch)
            {
                var averageColour = (Color4)DrawColourInfo.Colour.AverageColour;
                float shadowAlpha = MathF.Pow(Math.Max(Math.Max(averageColour.R, averageColour.G), averageColour.B), 2);
                ColourInfo finalShadowColour = DrawColourInfo.Colour;
                finalShadowColour.ApplyChild(shadowColour.MultiplyAlpha(shadowAlpha));

                foreach (ScreenSpaceCharacterPart part in parts!)
                {
                    bool isArrayGlyph = part.Texture is GlyphAtlasRegion { IsArrayGlyph: true };
                    var glyph = part.Texture as GlyphAtlasRegion;
                    ColourInfo glyphColour = part.HasColour
                        ? ColourInfo.SingleColour(new Color4(1f, 1f, 1f, averageColour.A))
                        : DrawColourInfo.Colour;

                    if (shadow && !part.HasColour)
                    {
                        Quad shadowQuad = part.DrawQuad;
                        renderer.DrawGlyphArrayQuad(batch, part.Texture, glyph?.Layer ?? 0, !isArrayGlyph,
                            new Quad(
                                shadowQuad.TopLeft + shadowOffset,
                                shadowQuad.TopRight + shadowOffset,
                                shadowQuad.BottomLeft + shadowOffset,
                                shadowQuad.BottomRight + shadowOffset),
                            finalShadowColour, part.InflationPercentage);
                    }

                    renderer.DrawGlyphArrayQuad(batch, part.Texture, glyph?.Layer ?? 0, !isArrayGlyph,
                        part.DrawQuad, glyphColour, part.InflationPercentage);
                }
            }

            /// <summary>
            /// The characters in screen space. These are ready to be drawn.
            /// </summary>
            private void updateScreenSpaceCharacters()
            {
                int partCount = Source.characters.Count;

                if (parts == null)
                    parts = new List<ScreenSpaceCharacterPart>(partCount);
                else
                {
                    parts.Clear();
                    parts.EnsureCapacity(partCount);
                }

                Vector2 inflationAmount = DrawInfo.MatrixInverse.ExtractScale().Xy;

                foreach (var character in Source.characters)
                {
                    parts.Add(new ScreenSpaceCharacterPart
                    {
                        DrawQuad = Source.ToScreenSpace(character.DrawRectangle.Inflate(inflationAmount)),
                        InflationPercentage = new Vector2(
                            character.DrawRectangle.Size.X == 0 ? 0 : inflationAmount.X / character.DrawRectangle.Size.X,
                            character.DrawRectangle.Size.Y == 0 ? 0 : inflationAmount.Y / character.DrawRectangle.Size.Y),
                        Texture = character.Texture,
                        HasColour = character.HasColour
                    });
                }
            }
        }

        /// <summary>
        /// A character of a <see cref="SpriteText"/> provided with screen space draw coordinates.
        /// </summary>
        internal struct ScreenSpaceCharacterPart
        {
            /// <summary>
            /// The screen-space quad for the character to be drawn in.
            /// </summary>
            public Quad DrawQuad;

            /// <summary>
            /// Extra padding for the character's texture.
            /// </summary>
            public Vector2 InflationPercentage;

            /// <summary>
            /// The texture to draw the character with.
            /// </summary>
            public Texture Texture;

            /// <summary>
            /// Whether the glyph already contains colour and should not be tinted.
            /// </summary>
            public bool HasColour;
        }
    }
}
