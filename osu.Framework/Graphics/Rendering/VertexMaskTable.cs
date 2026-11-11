// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Shaders.Types;
using osuTK;

namespace osu.Framework.Graphics.Rendering
{
    /// <summary>
    /// One rounded-rect mask in <c>g_VertexMasks</c>. Five <c>vec4</c>s, matching the shader struct.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 80)]
    internal struct VertexMaskRecord
    {
        public UniformVector4 Column0;
        public UniformVector4 Column1;
        public UniformVector4 Column2;
        public UniformVector4 Rect;
        public UniformVector4 Parameters;

        public static VertexMaskRecord FromMaskingInfo(in MaskingInfo info)
        {
            Matrix3 matrix = info.ToMaskingSpace;
            RectangleF rect = info.MaskingRect;

            return new VertexMaskRecord
            {
                Column0 = new Vector4(matrix.Row0.X, matrix.Row0.Y, matrix.Row0.Z, 0),
                Column1 = new Vector4(matrix.Row1.X, matrix.Row1.Y, matrix.Row1.Z, 0),
                Column2 = new Vector4(matrix.Row2.X, matrix.Row2.Y, matrix.Row2.Z, 0),
                Rect = new Vector4(rect.Left, rect.Top, rect.Right, rect.Bottom),
                Parameters = new Vector4(info.CornerRadius, info.CornerExponent, info.BlendRange, info.AlphaExponent),
            };
        }
    }

    /// <summary>
    /// Masks for one encoded batch. A vertex stores a 1-based index into this table.
    /// 128 records stay under the 16KB uniform-block minimum. Field order matches <c>g_VertexMaskRecords</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct VertexMaskTable : IEquatable<VertexMaskTable>
    {
        public const int CAPACITY = 128;
        public const int RECORD_SIZE = 80;

        public VertexMaskRecord M0;
        public VertexMaskRecord M1;
        public VertexMaskRecord M2;
        public VertexMaskRecord M3;
        public VertexMaskRecord M4;
        public VertexMaskRecord M5;
        public VertexMaskRecord M6;
        public VertexMaskRecord M7;
        public VertexMaskRecord M8;
        public VertexMaskRecord M9;
        public VertexMaskRecord M10;
        public VertexMaskRecord M11;
        public VertexMaskRecord M12;
        public VertexMaskRecord M13;
        public VertexMaskRecord M14;
        public VertexMaskRecord M15;
        public VertexMaskRecord M16;
        public VertexMaskRecord M17;
        public VertexMaskRecord M18;
        public VertexMaskRecord M19;
        public VertexMaskRecord M20;
        public VertexMaskRecord M21;
        public VertexMaskRecord M22;
        public VertexMaskRecord M23;
        public VertexMaskRecord M24;
        public VertexMaskRecord M25;
        public VertexMaskRecord M26;
        public VertexMaskRecord M27;
        public VertexMaskRecord M28;
        public VertexMaskRecord M29;
        public VertexMaskRecord M30;
        public VertexMaskRecord M31;
        public VertexMaskRecord M32;
        public VertexMaskRecord M33;
        public VertexMaskRecord M34;
        public VertexMaskRecord M35;
        public VertexMaskRecord M36;
        public VertexMaskRecord M37;
        public VertexMaskRecord M38;
        public VertexMaskRecord M39;
        public VertexMaskRecord M40;
        public VertexMaskRecord M41;
        public VertexMaskRecord M42;
        public VertexMaskRecord M43;
        public VertexMaskRecord M44;
        public VertexMaskRecord M45;
        public VertexMaskRecord M46;
        public VertexMaskRecord M47;
        public VertexMaskRecord M48;
        public VertexMaskRecord M49;
        public VertexMaskRecord M50;
        public VertexMaskRecord M51;
        public VertexMaskRecord M52;
        public VertexMaskRecord M53;
        public VertexMaskRecord M54;
        public VertexMaskRecord M55;
        public VertexMaskRecord M56;
        public VertexMaskRecord M57;
        public VertexMaskRecord M58;
        public VertexMaskRecord M59;
        public VertexMaskRecord M60;
        public VertexMaskRecord M61;
        public VertexMaskRecord M62;
        public VertexMaskRecord M63;
        public VertexMaskRecord M64;
        public VertexMaskRecord M65;
        public VertexMaskRecord M66;
        public VertexMaskRecord M67;
        public VertexMaskRecord M68;
        public VertexMaskRecord M69;
        public VertexMaskRecord M70;
        public VertexMaskRecord M71;
        public VertexMaskRecord M72;
        public VertexMaskRecord M73;
        public VertexMaskRecord M74;
        public VertexMaskRecord M75;
        public VertexMaskRecord M76;
        public VertexMaskRecord M77;
        public VertexMaskRecord M78;
        public VertexMaskRecord M79;
        public VertexMaskRecord M80;
        public VertexMaskRecord M81;
        public VertexMaskRecord M82;
        public VertexMaskRecord M83;
        public VertexMaskRecord M84;
        public VertexMaskRecord M85;
        public VertexMaskRecord M86;
        public VertexMaskRecord M87;
        public VertexMaskRecord M88;
        public VertexMaskRecord M89;
        public VertexMaskRecord M90;
        public VertexMaskRecord M91;
        public VertexMaskRecord M92;
        public VertexMaskRecord M93;
        public VertexMaskRecord M94;
        public VertexMaskRecord M95;
        public VertexMaskRecord M96;
        public VertexMaskRecord M97;
        public VertexMaskRecord M98;
        public VertexMaskRecord M99;
        public VertexMaskRecord M100;
        public VertexMaskRecord M101;
        public VertexMaskRecord M102;
        public VertexMaskRecord M103;
        public VertexMaskRecord M104;
        public VertexMaskRecord M105;
        public VertexMaskRecord M106;
        public VertexMaskRecord M107;
        public VertexMaskRecord M108;
        public VertexMaskRecord M109;
        public VertexMaskRecord M110;
        public VertexMaskRecord M111;
        public VertexMaskRecord M112;
        public VertexMaskRecord M113;
        public VertexMaskRecord M114;
        public VertexMaskRecord M115;
        public VertexMaskRecord M116;
        public VertexMaskRecord M117;
        public VertexMaskRecord M118;
        public VertexMaskRecord M119;
        public VertexMaskRecord M120;
        public VertexMaskRecord M121;
        public VertexMaskRecord M122;
        public VertexMaskRecord M123;
        public VertexMaskRecord M124;
        public VertexMaskRecord M125;
        public VertexMaskRecord M126;
        public VertexMaskRecord M127;

        static VertexMaskTable()
        {
            Debug.Assert(Marshal.SizeOf<VertexMaskRecord>() == RECORD_SIZE);
            Debug.Assert(Marshal.SizeOf<VertexMaskTable>() == CAPACITY * RECORD_SIZE);
        }

        public void Write(int index, in VertexMaskRecord record)
        {
            Unsafe.Add(ref Unsafe.As<VertexMaskTable, VertexMaskRecord>(ref this), index) = record;
        }

        public bool Equals(VertexMaskTable other)
        {
            ReadOnlySpan<byte> left = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<VertexMaskTable, byte>(ref this), CAPACITY * RECORD_SIZE);
            ReadOnlySpan<byte> right = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<VertexMaskTable, byte>(ref other), CAPACITY * RECORD_SIZE);
            return left.SequenceEqual(right);
        }

        public override bool Equals(object? obj) => obj is VertexMaskTable other && Equals(other);

        public override int GetHashCode() => Unsafe.As<VertexMaskTable, int>(ref this);
    }
}
