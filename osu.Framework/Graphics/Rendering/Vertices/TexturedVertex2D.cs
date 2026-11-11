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
    public struct TexturedVertex2D : IEquatable<TexturedVertex2D>, IVertex
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

        /// <summary>
        /// 1 when <see cref="MaskingRect"/> / <see cref="MaskParameters"/> / the mask matrix columns describe this
        /// vertex's mask. 0 keeps the existing uniform mask, which manual vertex writers and border/hollow masks use.
        /// </summary>
        [VertexMember(1, VertexAttribPointerType.Float)]
        public float UseVertexMask;

        // Same three vectors uploaded as g_ToMaskingSpace (std140 reads them as columns).
        [VertexMember(3, VertexAttribPointerType.Float)]
        public Vector3 MaskColumn0;

        [VertexMember(3, VertexAttribPointerType.Float)]
        public Vector3 MaskColumn1;

        [VertexMember(3, VertexAttribPointerType.Float)]
        public Vector3 MaskColumn2;

        /// <summary>Left, top, right, bottom in masking space. Matches g_MaskingRect.</summary>
        [VertexMember(4, VertexAttribPointerType.Float)]
        public Vector4 MaskingRect;

        /// <summary>CornerRadius, CornerExponent, BlendRange, AlphaExponent.</summary>
        [VertexMember(4, VertexAttribPointerType.Float)]
        public Vector4 MaskParameters;

        [Obsolete("Initialise this type with an IRenderer instead", true)]
        public TexturedVertex2D()
        {
            this = default; // explicitly initialise all members to default values
        }

        public TexturedVertex2D(IRenderer renderer)
        {
            this = default; // explicitly initialise all members to default values
            backbufferDrawDepth = renderer.BackbufferDepth;
        }

        public readonly bool Equals(TexturedVertex2D other) =>
            Position.Equals(other.Position)
            && TexturePosition.Equals(other.TexturePosition)
            && Colour.Equals(other.Colour)
            && TextureRect.Equals(other.TextureRect)
            && BlendRange.Equals(other.BlendRange)
            && backbufferDrawDepth == other.backbufferDrawDepth
            && UseVertexMask.Equals(other.UseVertexMask)
            && MaskColumn0.Equals(other.MaskColumn0)
            && MaskColumn1.Equals(other.MaskColumn1)
            && MaskColumn2.Equals(other.MaskColumn2)
            && MaskingRect.Equals(other.MaskingRect)
            && MaskParameters.Equals(other.MaskParameters);
    }
}
