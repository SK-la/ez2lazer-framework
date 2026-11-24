// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using TiffLibrary;
using TiffLibrary.PixelFormats;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class TiffImageCodecTest
    {
        [TestCase("color", 16, 167, 247, 84, 4, 29, 2, 36, 96)]
        [TestCase("gray", 85, 85, 85, 7, 7, 7, 6, 6, 6)]
        public void TestImageCodecDecodesEmbeddedTiff(string name,
                                                      byte firstRed,
                                                      byte firstGreen,
                                                      byte firstBlue,
                                                      byte middleRed,
                                                      byte middleGreen,
                                                      byte middleBlue,
                                                      byte lastRed,
                                                      byte lastGreen,
                                                      byte lastBlue)
        {
            byte[] encoded = readFixture(name);
            using var stream = new MemoryStream(encoded, writable: false);

            using var image = ImageCodec.Decode(stream);

            Assert.That(image.Width, Is.EqualTo(160));
            Assert.That(image.Height, Is.EqualTo(120));
            Assert.That(image[0, 0], Is.EqualTo(new Rgba32(firstRed, firstGreen, firstBlue)));
            Assert.That(image[80, 60], Is.EqualTo(new Rgba32(middleRed, middleGreen, middleBlue)));
            Assert.That(image[159, 119], Is.EqualTo(new Rgba32(lastRed, lastGreen, lastBlue)));
            Assert.That(stream.CanRead, Is.True);
        }

        [Test]
        public void TestTextureLoaderFindsTiffWithoutExtension()
        {
            using var resources = new DllResourceStore(typeof(TiffImageCodecTest).Assembly);
            using var store = new TextureLoaderStore(resources);
            using var upload = store.Get("Resources/Textures/bricks-color");

            Assert.That(upload, Is.Not.Null);
            Assert.That(upload!.Width, Is.EqualTo(160));
            Assert.That(upload.Height, Is.EqualTo(120));
            Assert.That(upload.Data[60 * upload.Width + 80], Is.EqualTo(new Rgba32(84, 4, 29)));
        }

        [Test]
        public void TestTextureUploadAndIconDecodeTiff()
        {
            byte[] encoded = readFixture("color");
            using var uploadStream = new MemoryStream(encoded, writable: false);
            using var upload = new TextureUpload(uploadStream);

            Assert.That(upload.Width, Is.EqualTo(160));
            Assert.That(upload.Height, Is.EqualTo(120));
            Assert.That(upload.Data[60 * upload.Width + 80], Is.EqualTo(new Rgba32(84, 4, 29)));
            Assert.That(uploadStream.CanRead, Is.True);

            using var iconStream = new MemoryStream(encoded, writable: false);
            using var icon = ImageCodec.DecodeIcon(iconStream);

            Assert.That(icon[80, 60], Is.EqualTo(new Rgba32(84, 4, 29)));
            Assert.That(iconStream.Position, Is.EqualTo(0));
            Assert.That(iconStream.CanRead, Is.True);
        }

        [Test]
        public void TestPublicTiffEncodingPreservesStraightAlphaOnNonSeekStream()
        {
            using var source = new RgbaImage(2, 2);
            source[0, 0] = new Rgba32(12, 34, 56, 0);
            source[1, 0] = new Rgba32(78, 90, 123, 1);
            source[0, 1] = new Rgba32(111, 222, 33, 128);
            source[1, 1] = new Rgba32(240, 17, 98, 255);

            using var backing = new MemoryStream();
            using var destination = new NonSeekableStream(backing, leaveOpen: true);
            ImageCodec.Encode(source, destination, ImageFileFormat.Tiff);

            Assert.That(destination.CanWrite, Is.True);
            Assert.That(backing.Length, Is.GreaterThan(0));

            byte[] encodedBytes = backing.ToArray();
            assertSinglePage(encodedBytes);
            using var encoded = new MemoryStream(encodedBytes, writable: false);
            using var decoded = ImageCodec.Decode(encoded);

            Assert.That(decoded.PixelSpan.ToArray(), Is.EqualTo(source.PixelSpan.ToArray()));
        }

        [TestCase(TiffCompression.NoCompression)]
        [TestCase(TiffCompression.Deflate)]
        [TestCase(TiffCompression.Lzw)]
        [TestCase(TiffCompression.PackBits)]
        public void TestTiffDecodeSupportsCommonCompression(TiffCompression compression)
        {
            using var source = new RgbaImage(2, 2);
            source[0, 0] = new Rgba32(12, 34, 56, 0);
            source[1, 0] = new Rgba32(78, 90, 123, 1);
            source[0, 1] = new Rgba32(111, 222, 33, 128);
            source[1, 1] = new Rgba32(240, 17, 98, 255);
            using var encoded = new MemoryStream(encodeFixtureTiff(source, compression), writable: false);

            using var decoded = ImageCodec.Decode(encoded);

            Assert.That(decoded.PixelSpan.ToArray(), Is.EqualTo(source.PixelSpan.ToArray()));
        }

        [Test]
        public void TestTiffDecodeSupportsNonSeekInputAndBigEndian()
        {
            using var backing = new MemoryStream(createBigEndianTiff(), writable: false);
            using var stream = new NonSeekableStream(backing, leaveOpen: true);

            using var image = ImageCodec.Decode(stream);

            Assert.That(image.Width, Is.EqualTo(1));
            Assert.That(image.Height, Is.EqualTo(1));
            Assert.That(image[0, 0], Is.EqualTo(new Rgba32(12, 34, 56, 78)));
            Assert.That(stream.CanRead, Is.True);
        }

        [Test]
        public void TestTiffDecodeReadsFirstPage()
        {
            using var stream = new MemoryStream(createTwoPageTiff(), writable: false);

            using var image = ImageCodec.Decode(stream);

            Assert.That(image.Width, Is.EqualTo(1));
            Assert.That(image.Height, Is.EqualTo(1));
            Assert.That(image[0, 0], Is.EqualTo(new Rgba32(12, 34, 56, 78)));
        }

        [TestCase(4096, 1)]
        [TestCase(1, 4096)]
        public void TestTiffDecodeAcceptsDimensionLimit(int width, int height)
        {
            using var source = new RgbaImage(width, height);
            using var stream = new MemoryStream(TiffImageCodec.Encode(source), writable: false);
            using var image = ImageCodec.Decode(stream);

            Assert.That(image.Width, Is.EqualTo(width));
            Assert.That(image.Height, Is.EqualTo(height));
        }

        [TestCase(4097, 1)]
        [TestCase(1, 4097)]
        public void TestTiffDecodeRejectsDimensionsOverLimit(int width, int height)
        {
            using var source = new RgbaImage(width, height);
            byte[] encoded = TiffImageCodec.Encode(source);

            Assert.Throws<InvalidDataException>(() =>
            {
                using var stream = new MemoryStream(encoded, writable: false);
                using var image = ImageCodec.Decode(stream);
            });

            Assert.Throws<NotSupportedException>(() =>
            {
                using var stream = new MemoryStream(encoded, writable: false);
                using var upload = new TextureUpload(stream);
            });
        }

        [Test]
        public void TestFailedTiffDecodeRestoresNonZeroStreamPosition()
        {
            using var stream = new MemoryStream(new byte[] { 1, 2, (byte)'I', (byte)'I', 42, 0, 8, 0 });
            stream.Position = 2;
            bool failed = false;

            try
            {
                using var image = ImageCodec.Decode(stream);
            }
            catch (Exception)
            {
                failed = true;
            }

            Assert.That(failed, Is.True);
            Assert.That(stream.Position, Is.EqualTo(2));
            Assert.That(stream.CanRead, Is.True);
        }

        private static byte[] readFixture(string name)
        {
            using var resources = new DllResourceStore(typeof(TiffImageCodecTest).Assembly);
            using var stream = resources.GetStream($"Resources/Textures/bricks-{name}.tiff")
                               ?? throw new FileNotFoundException($"TIFF fixture '{name}' was not found.");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        private static byte[] createBigEndianTiff()
        {
            const int entry_count = 11;
            const int bits_offset = 8 + 2 + entry_count * 12 + 4;
            const int pixel_offset = bits_offset + 8;
            byte[] bytes = new byte[pixel_offset + 4];
            bytes[0] = (byte)'M';
            bytes[1] = (byte)'M';
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 42);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 8);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8), entry_count);

            int entryOffset = 10;
            writeEntry(256, 4, 1, 1);
            writeEntry(257, 4, 1, 1);
            writeEntry(258, 3, 4, bits_offset);
            writeEntry(259, 3, 1, 1);
            writeEntry(262, 3, 1, 2);
            writeEntry(273, 4, 1, pixel_offset);
            writeEntry(277, 3, 1, 4);
            writeEntry(278, 4, 1, 1);
            writeEntry(279, 4, 1, 4);
            writeEntry(284, 3, 1, 1);
            writeEntry(338, 3, 1, 2);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entryOffset), 0);

            for (int i = 0; i < 4; i++)
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(bits_offset + i * 2), 8);

            bytes[pixel_offset] = 12;
            bytes[pixel_offset + 1] = 34;
            bytes[pixel_offset + 2] = 56;
            bytes[pixel_offset + 3] = 78;
            return bytes;

            void writeEntry(ushort tag, ushort type, uint count, uint value)
            {
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(entryOffset), tag);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(entryOffset + 2), type);
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entryOffset + 4), count);

                if (type == 3 && count == 1)
                    BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(entryOffset + 8), (ushort)value);
                else
                    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entryOffset + 8), value);

                entryOffset += 12;
            }
        }

        private static byte[] createTwoPageTiff()
        {
            const int entry_count = 11;
            const int ifd_size = 2 + entry_count * 12 + 4;
            const int first_ifd_offset = 8;
            const int second_ifd_offset = first_ifd_offset + ifd_size;
            const int bits_offset = second_ifd_offset + ifd_size;
            const int first_pixel_offset = bits_offset + 8;
            const int second_pixel_offset = first_pixel_offset + 4;
            byte[] bytes = new byte[second_pixel_offset + 4];
            bytes[0] = (byte)'I';
            bytes[1] = (byte)'I';
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 42);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), first_ifd_offset);

            writePage(first_ifd_offset, first_pixel_offset, second_ifd_offset);
            writePage(second_ifd_offset, second_pixel_offset, 0);

            for (int i = 0; i < 4; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bits_offset + i * 2), 8);

            bytes[first_pixel_offset] = 12;
            bytes[first_pixel_offset + 1] = 34;
            bytes[first_pixel_offset + 2] = 56;
            bytes[first_pixel_offset + 3] = 78;
            bytes[second_pixel_offset] = 210;
            bytes[second_pixel_offset + 1] = 120;
            bytes[second_pixel_offset + 2] = 30;
            bytes[second_pixel_offset + 3] = 255;
            return bytes;

            void writePage(int ifdOffset, int pixelOffset, int nextIfdOffset)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(ifdOffset), entry_count);
                int entryOffset = ifdOffset + 2;

                writeEntry(256, 4, 1, 1);
                writeEntry(257, 4, 1, 1);
                writeEntry(258, 3, 4, bits_offset);
                writeEntry(259, 3, 1, 1);
                writeEntry(262, 3, 1, 2);
                writeEntry(273, 4, 1, pixelOffset);
                writeEntry(277, 3, 1, 4);
                writeEntry(278, 4, 1, 1);
                writeEntry(279, 4, 1, 4);
                writeEntry(284, 3, 1, 1);
                writeEntry(338, 3, 1, 2);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entryOffset), (uint)nextIfdOffset);

                void writeEntry(ushort tag, ushort type, uint count, int value)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(entryOffset), tag);
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(entryOffset + 2), type);
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entryOffset + 4), count);

                    if (type == 3 && count == 1)
                        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(entryOffset + 8), (ushort)value);
                    else
                        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entryOffset + 8), (uint)value);

                    entryOffset += 12;
                }
            }
        }

        private static byte[] encodeFixtureTiff(RgbaImage image, TiffCompression compression)
        {
            using var stream = new MemoryStream();
            using var pixels = MemoryPool<TiffRgba32>.Shared.Rent(image.PixelCount);
            MemoryMarshal.Cast<Rgba32, TiffRgba32>(image.PixelSpan).CopyTo(pixels.Memory.Span);
            var buffer = new TiffMemoryPixelBuffer<TiffRgba32>(pixels.Memory, image.Width, image.Height, writable: false);
            var encoder = new TiffImageEncoderBuilder
            {
                PhotometricInterpretation = TiffPhotometricInterpretation.RGB,
                Compression = compression,
                EnableTransparencyForRgb = true,
            }.Build<TiffRgba32>();

            using var writer = TiffFileWriter.OpenAsync(stream, leaveOpen: true).GetAwaiter().GetResult();
            using var directory = writer.CreateImageFileDirectory();
            encoder.EncodeAsync(directory, buffer).GetAwaiter().GetResult();
            writer.SetFirstImageFileDirectoryOffset(directory.FlushAsync().GetAwaiter().GetResult());
            writer.FlushAsync().GetAwaiter().GetResult();
            return stream.ToArray();
        }

        private static void assertSinglePage(byte[] encoded)
        {
            Assert.That(encoded.Length, Is.GreaterThan(8));
            bool littleEndian = encoded[0] == 'I' && encoded[1] == 'I';
            uint directoryOffset = littleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(4))
                : BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(4));
            ushort entryCount = littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan((int)directoryOffset))
                : BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan((int)directoryOffset));
            int nextDirectoryOffset = checked((int)directoryOffset + 2 + entryCount * 12);
            uint nextDirectory = littleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(nextDirectoryOffset))
                : BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(nextDirectoryOffset));

            Assert.That(nextDirectory, Is.Zero);
        }

        private sealed class NonSeekableStream : Stream
        {
            private readonly Stream inner;
            private readonly bool leaveOpen;

            public NonSeekableStream(Stream inner, bool leaveOpen)
            {
                this.inner = inner;
                this.leaveOpen = leaveOpen;
            }

            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => inner.CanWrite;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() => inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
            public override int Read(Span<byte> buffer) => inner.Read(buffer);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
            public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

            protected override void Dispose(bool disposing)
            {
                if (disposing && !leaveOpen)
                    inner.Dispose();

                base.Dispose(disposing);
            }
        }
    }
}
