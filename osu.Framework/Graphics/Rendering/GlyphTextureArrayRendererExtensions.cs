// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering.Vertices;
using osu.Framework.Graphics.Textures;
using osu.Framework.Statistics;
using osuTK;

namespace osu.Framework.Graphics.Rendering
{
    internal static class GlyphTextureArrayRendererExtensions
    {
        public static void DrawGlyphArrayQuad(this IRenderer renderer,
                                              IVertexBatch<GlyphArrayVertex2D> batch,
                                              Texture texture,
                                              int layer,
                                              bool useLegacyTexture,
                                              Quad vertexQuad,
                                              ColourInfo drawColour,
                                              Vector2 inflationPercentage)
        {
            int textureUnit = useLegacyTexture ? 1 : 0;

            if (!texture.Bind(textureUnit, WrapMode.None, WrapMode.None))
                return;

            RectangleF texRect = texture.GetTextureRect();
            Vector2 inflationAmount = new Vector2(inflationPercentage.X * texRect.Width, inflationPercentage.Y * texRect.Height);
            RectangleF coordRect = texRect;
            RectangleF inflatedCoordRect = coordRect.Inflate(inflationAmount);

            batch.Add(new GlyphArrayVertex2D(renderer, vertexQuad.BottomLeft,
                new Vector2(inflatedCoordRect.Left, inflatedCoordRect.Bottom),
                new Vector4(texRect.Left, texRect.Top, texRect.Right, texRect.Bottom),
                inflationAmount, drawColour.BottomLeft.SRGB, layer, useLegacyTexture));
            batch.Add(new GlyphArrayVertex2D(renderer, vertexQuad.BottomRight,
                new Vector2(inflatedCoordRect.Right, inflatedCoordRect.Bottom),
                new Vector4(texRect.Left, texRect.Top, texRect.Right, texRect.Bottom),
                inflationAmount, drawColour.BottomRight.SRGB, layer, useLegacyTexture));
            batch.Add(new GlyphArrayVertex2D(renderer, vertexQuad.TopRight,
                new Vector2(inflatedCoordRect.Right, inflatedCoordRect.Top),
                new Vector4(texRect.Left, texRect.Top, texRect.Right, texRect.Bottom),
                inflationAmount, drawColour.TopRight.SRGB, layer, useLegacyTexture));
            batch.Add(new GlyphArrayVertex2D(renderer, vertexQuad.TopLeft,
                new Vector2(inflatedCoordRect.Left, inflatedCoordRect.Top),
                new Vector4(texRect.Left, texRect.Top, texRect.Right, texRect.Bottom),
                inflationAmount, drawColour.TopLeft.SRGB, layer, useLegacyTexture));

            long area = (long)vertexQuad.Area;
            if (area > 0)
                FrameStatistics.Add(StatisticsCounterType.Pixels, area);
        }
    }
}
