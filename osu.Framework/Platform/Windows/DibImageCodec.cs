// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers.Binary;
using System.IO;
using osu.Framework.Graphics.Imaging;

namespace osu.Framework.Platform.Windows
{
    internal static class DibImageCodec
    {
        private const int bitmap_file_header_length = 14;
        private const int bitmap_v5_header_length = 124;

        internal static RgbaImage Decode(ReadOnlySpan<byte> dib)
        {
            if (dib.Length < 12)
                throw new InvalidDataException("The clipboard bitmap header is incomplete.");

            uint headerLength = BinaryPrimitives.ReadUInt32LittleEndian(dib);
            uint colorCount;
            int paletteEntryLength;
            ushort bitsPerPixel;
            int maskLength = 0;

            if (headerLength == 12)
            {
                bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(dib[10..]);
                colorCount = bitsPerPixel <= 8 ? 1u << bitsPerPixel : 0;
                paletteEntryLength = 3;
            }
            else
            {
                if (headerLength < 40 || headerLength > dib.Length)
                    throw new InvalidDataException("The clipboard bitmap header is invalid.");

                bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
                uint compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
                colorCount = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
                if (colorCount == 0 && bitsPerPixel <= 8)
                    colorCount = 1u << bitsPerPixel;

                paletteEntryLength = 4;
                if (headerLength == 40)
                    maskLength = compression == 3 ? 12 : compression == 6 ? 16 : 0;
            }

            int pixelOffset = checked((int)headerLength + maskLength + checked((int)colorCount * paletteEntryLength));
            if (pixelOffset > dib.Length)
                throw new InvalidDataException("The clipboard bitmap palette is incomplete.");

            byte[] bitmap = new byte[checked(bitmap_file_header_length + dib.Length)];
            bitmap[0] = (byte)'B';
            bitmap[1] = (byte)'M';
            BinaryPrimitives.WriteUInt32LittleEndian(bitmap.AsSpan(2), (uint)bitmap.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bitmap.AsSpan(10), checked((uint)(bitmap_file_header_length + pixelOffset)));
            dib.CopyTo(bitmap.AsSpan(bitmap_file_header_length));

            using var stream = new MemoryStream(bitmap, writable: false);
            return ImageCodec.Decode(stream);
        }

        internal static byte[] Encode(RgbaImage image)
        {
            int imageBytes = checked(image.PixelCount * 4);
            byte[] dib = new byte[checked(bitmap_v5_header_length + imageBytes)];
            BinaryPrimitives.WriteUInt32LittleEndian(dib, bitmap_v5_header_length);
            BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), image.Width);
            BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), -image.Height);
            BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 32);
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(16), 3);
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(20), (uint)imageBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(40), 0x00ff0000);
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(44), 0x0000ff00);
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(48), 0x000000ff);
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(52), 0xff000000);
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(56), 0x73524742);
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(108), 4);

            Span<byte> destination = dib.AsSpan(bitmap_v5_header_length);
            ReadOnlySpan<Rgba32> pixels = image.PixelSpan;
            for (int i = 0; i < pixels.Length; i++)
            {
                Rgba32 pixel = pixels[i];
                destination[i * 4] = pixel.B;
                destination[i * 4 + 1] = pixel.G;
                destination[i * 4 + 2] = pixel.R;
                destination[i * 4 + 3] = pixel.A;
            }

            return dib;
        }
    }
}
