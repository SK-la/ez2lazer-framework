// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.InteropServices;
using osuTK;
using osuTK.Graphics;
using osuTK.Graphics.ES30;

namespace osu.Framework.Graphics.Rendering.Vertices
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct GlyphArrayVertex2D : IEquatable<GlyphArrayVertex2D>, IVertex
    {
        [VertexMember(2, VertexAttribPointerType.Float)]
        public Vector2 Position;

        [VertexMember(4, VertexAttribPointerType.Float)]
        public Color4 Colour;

        [VertexMember(2, VertexAttribPointerType.Float)]
        public Vector2 TexturePosition;

        [VertexMember(4, VertexAttribPointerType.Float)]
        public Vector4 TextureRect;

        [VertexMember(2, VertexAttribPointerType.Float)]
        public Vector2 BlendRange;

        [VertexMember(1, VertexAttribPointerType.Float)]
        private readonly float backbufferDrawDepth;

        [VertexMember(1, VertexAttribPointerType.Float)]
        public float GlyphLayer;

        [VertexMember(1, VertexAttribPointerType.Float)]
        public float UseLegacyTexture;

        public GlyphArrayVertex2D(IRenderer renderer,
                                  Vector2 position,
                                  Vector2 texturePosition,
                                  Vector4 textureRect,
                                  Vector2 blendRange,
                                  Color4 colour,
                                  float glyphLayer,
                                  bool useLegacyTexture)
        {
            Position = position;
            Colour = colour;
            TexturePosition = texturePosition;
            TextureRect = textureRect;
            BlendRange = blendRange;
            backbufferDrawDepth = renderer.BackbufferDepth;
            GlyphLayer = glyphLayer;
            UseLegacyTexture = useLegacyTexture ? 1 : 0;
        }

        public readonly bool Equals(GlyphArrayVertex2D other) =>
            Position.Equals(other.Position)
            && Colour.Equals(other.Colour)
            && TexturePosition.Equals(other.TexturePosition)
            && TextureRect.Equals(other.TextureRect)
            && BlendRange.Equals(other.BlendRange)
            && backbufferDrawDepth == other.backbufferDrawDepth
            && GlyphLayer == other.GlyphLayer
            && UseLegacyTexture == other.UseLegacyTexture;

        public override readonly bool Equals(object? obj) => obj is GlyphArrayVertex2D other && Equals(other);

        public override readonly int GetHashCode() =>
            HashCode.Combine(HashCode.Combine(Position, Colour, TexturePosition, TextureRect), BlendRange, GlyphLayer, UseLegacyTexture);
    }
}
