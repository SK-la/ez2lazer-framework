// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Graphics.Textures;
using osu.Framework.Tests.Resources;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using ImageSharpRgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class GifFrameCompositorTest
    {
        [Test]
        public void TestLocalFramesTransparencyAndDisposal()
        {
            using var stream = new MemoryStream(createGif());
            using var result = loadLegacyGif(stream, null);

            Assert.That(result, Is.Not.Null);
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
            using var result = loadLegacyGif(stream, 2);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Frames.Count, Is.EqualTo(2));
            Assert.That(result.FrameDurationMs, Is.EqualTo(new[] { 30, 40 }));
            Assert.That(result.Frames[1].Data[0], Is.EqualTo(new Rgba32(255, 0, 0, 255)));
            Assert.That(result.Frames[1].Data[1], Is.EqualTo(new Rgba32(0, 255, 0, 255)));
        }

        [Test]
        public void TestSkiaMatchesLegacyDisposalAndTiming()
        {
            byte[] encoded = createGif();
            using var stream = new MemoryStream(encoded);
            using var legacy = loadLegacyGif(stream, null);
            var skia = decodeWithSkia(encoded, 4);

            Assert.That(legacy, Is.Not.Null);
            Assert.That(skia.Frames.Length, Is.EqualTo(legacy.Frames.Count));
            Assert.That(skia.Durations, Is.EqualTo(legacy.FrameDurationMs));

            for (int i = 0; i < skia.Frames.Length; i++)
                Assert.That(skia.Frames[i], Is.EqualTo(legacy.Frames[i].Data.ToArray()), $"Frame {i} differed.");
        }

        [Test]
        public void TestSkiaMatchesLegacyMaxFramesPrefix()
        {
            byte[] encoded = createGif();
            using var stream = new MemoryStream(encoded);
            using var legacy = loadLegacyGif(stream, 2);
            var skia = decodeWithSkia(encoded, 2);

            Assert.That(legacy, Is.Not.Null);
            Assert.That(skia.Frames.Length, Is.EqualTo(legacy.Frames.Count));
            Assert.That(skia.Durations, Is.EqualTo(legacy.FrameDurationMs));

            for (int i = 0; i < skia.Frames.Length; i++)
                Assert.That(skia.Frames[i], Is.EqualTo(legacy.Frames[i].Data.ToArray()), $"Frame {i} differed.");
        }

        [Test]
        public void TestSkiaMatchesLegacySampleAnimation()
        {
            using var source = GifTestResources.OpenSampleFlameGifStream();
            using var copy = new MemoryStream();
            source.CopyTo(copy);
            byte[] encoded = copy.ToArray();

            using var legacyStream = new MemoryStream(encoded);
            using var legacy = loadLegacyGif(legacyStream, null);
            var skia = decodeWithSkia(encoded, int.MaxValue);

            Assert.That(legacy, Is.Not.Null);
            Assert.That(skia.Frames.Length, Is.EqualTo(legacy.Frames.Count));
            Assert.That(skia.Durations, Is.EqualTo(legacy.FrameDurationMs));

            for (int i = 0; i < skia.Frames.Length; i++)
                Assert.That(skia.Frames[i], Is.EqualTo(legacy.Frames[i].Data.ToArray()), $"Frame {i} differed.");
        }

        private static (Rgba32[][] Frames, double[] Durations) decodeWithSkia(byte[] encoded, int maxFrames)
        {
            using var stream = new MemoryStream(encoded);
            using var result = SkiaAnimatedImageDecoder.TryDecode(stream, maxFrames)
                               ?? throw new InvalidDataException("Skia could not decode the GIF.");
            var frames = new Rgba32[result.Frames.Count][];

            for (int i = 0; i < frames.Length; i++)
                frames[i] = result.Frames[i].Data.ToArray();

            return (frames, result.FrameDurationMs.ToArray());
        }

        private static AnimatedImageFrameLoadResult loadLegacyGif(Stream stream, int? maxFrames)
        {
            var options = new DecoderOptions
            {
                Configuration = TextureUpload.DECODER_OPTIONS.Configuration,
                MaxFrames = maxFrames.HasValue ? (uint)Math.Max(1, maxFrames.Value) : uint.MaxValue,
            };

            using var source = Image.Load<ImageSharpRgba32>(options, stream);

            if (source.Frames.Count <= 1)
            {
                var upload = new TextureUpload(source.Clone());
                double delay = Math.Max(source.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay * 10, 1000.0 / 60.0);
                return new AnimatedImageFrameLoadResult(new[] { upload }, new[] { delay });
            }

            var (images, durations) = GifFrameCompositor.Compose(source, maxFrames);
            var uploads = new TextureUpload[images.Count];

            for (int i = 0; i < uploads.Length; i++)
                uploads[i] = new TextureUpload(images[i]);

            return new AnimatedImageFrameLoadResult(uploads, durations);
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
