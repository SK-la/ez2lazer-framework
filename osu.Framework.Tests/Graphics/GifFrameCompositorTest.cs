// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using System.Text;
using NUnit.Framework;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Graphics.Textures;
using osu.Framework.Tests.Resources;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class GifFrameCompositorTest
    {
        [Test]
        public void TestLocalFramesTransparencyAndDisposal()
        {
            using var stream = new MemoryStream(createGif());
            using var result = AnimatedImageLoader.LoadFromStream(stream)
                               ?? throw new AssertionException("The GIF fixture could not be decoded.");
            Assert.That(result.Frames.Count, Is.EqualTo(4));
            Assert.That(result.Frames[0].Width, Is.EqualTo(3));
            Assert.That(result.Frames[0].Height, Is.EqualTo(1));
            Assert.That(result.FrameDurationMs, Is.EqualTo(new[] { 30, 40, 20, 20 }));

            Assert.That(result.Frames[0].Data[0], Is.EqualTo(new Rgba32(255, 0, 0, 255)));
            Assert.That(result.Frames[0].Data[1].A, Is.EqualTo(0));

            Assert.That(result.Frames[1].Data[0], Is.EqualTo(new Rgba32(255, 0, 0, 255)));
            Assert.That(result.Frames[1].Data[1], Is.EqualTo(new Rgba32(0, 255, 0, 255)));

            Assert.That(result.Frames[2].Data[0], Is.EqualTo(new Rgba32(255, 0, 0, 255)));
            Assert.That(result.Frames[2].Data[1].A, Is.EqualTo(0));
            Assert.That(result.Frames[2].Data[2], Is.EqualTo(new Rgba32(0, 0, 255, 255)));

            Assert.That(result.Frames[3].Data[0], Is.EqualTo(new Rgba32(255, 0, 0, 255)));
            Assert.That(result.Frames[3].Data[1].A, Is.EqualTo(0));
            Assert.That(result.Frames[3].Data[2].A, Is.EqualTo(0));
        }

        [Test]
        public void TestMaxFramesRetainsCompositedPrefixAndDurations()
        {
            using var stream = new MemoryStream(createGif());
            using var result = AnimatedImageLoader.LoadFromStream(stream, 2)
                               ?? throw new AssertionException("The capped GIF fixture could not be decoded.");
            Assert.That(result.Frames.Count, Is.EqualTo(2));
            Assert.That(result.FrameDurationMs, Is.EqualTo(new[] { 30, 40 }));
            Assert.That(result.Frames[1].Data[0], Is.EqualTo(new Rgba32(255, 0, 0, 255)));
            Assert.That(result.Frames[1].Data[1], Is.EqualTo(new Rgba32(0, 255, 0, 255)));
        }

        [Test]
        public void TestSampleAnimationUsesSkiaCanvasFrames()
        {
            using var source = GifTestResources.OpenSampleFlameGifStream();
            using var result = AnimatedImageLoader.LoadFromStream(source);

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Frames.Count, Is.EqualTo(5));
            Assert.That(result.FrameDurationMs, Is.EqualTo(new[] { 50, 50, 50, 50, 50 }));

            foreach (TextureUpload frame in result.Frames)
            {
                Assert.That(frame.Width, Is.EqualTo(230));
                Assert.That(frame.Height, Is.EqualTo(109));
                Assert.That(frame.Data.Length, Is.EqualTo(230 * 109));
            }
        }

        private static byte[] createGif()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.ASCII, true);

            writer.Write(Encoding.ASCII.GetBytes("GIF89a"));
            writer.Write((ushort)3);
            writer.Write((ushort)1);
            writer.Write((byte)0x81);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write(new byte[]
            {
                0, 0, 0,
                255, 0, 0,
                0, 255, 0,
                0, 0, 255,
            });

            writeFrame(writer, 0, 1, 1, 3);
            writeFrame(writer, 1, 2, 3, 4);
            writeFrame(writer, 2, 3, 2, 0);
            writeFrame(writer, 2, 0, 1, 1);

            writer.Write((byte)0x3b);
            return stream.ToArray();
        }

        private static void writeFrame(BinaryWriter writer, int left, byte colorIndex, int disposal, int delay)
        {
            writer.Write((byte)0x21);
            writer.Write((byte)0xf9);
            writer.Write((byte)4);
            writer.Write((byte)((disposal << 2) | 1));
            writer.Write((ushort)delay);
            writer.Write((byte)0);
            writer.Write((byte)0);

            writer.Write((byte)0x2c);
            writer.Write((ushort)left);
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write((byte)0);

            int packedCodes = 4 | (colorIndex << 3) | (5 << 6);
            writer.Write((byte)2);
            writer.Write((byte)2);
            writer.Write((byte)packedCodes);
            writer.Write((byte)(packedCodes >> 8));
            writer.Write((byte)0);
        }
    }
}
