// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Video;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class VideoBackdropSampleTest
    {
        [Test]
        public void TestUnsetPackedSampleIsEmpty()
        {
            Assert.That(VideoDecoder.TryUnpackBackdropSample(0, out _), Is.False);
        }

        [Test]
        public void TestBlackSampleIsDistinctFromUnset()
        {
            int packed = VideoDecoder.PackBackdropSample(0, 0, 0);

            Assert.That(VideoDecoder.TryUnpackBackdropSample(packed, out Colour4 colour), Is.True);
            Assert.That(colour.R, Is.EqualTo(0f));
            Assert.That(colour.G, Is.EqualTo(0f));
            Assert.That(colour.B, Is.EqualTo(0f));
            Assert.That(colour.A, Is.EqualTo(1f));
        }

        [Test]
        public void TestLimitedRangeBlackAndRed()
        {
            VideoDecoder.ConvertYuvToRgb(16, 128, 128, out int blackR, out int blackG, out int blackB);
            Assert.That(blackR, Is.EqualTo(0));
            Assert.That(blackG, Is.EqualTo(0));
            Assert.That(blackB, Is.EqualTo(0));

            VideoDecoder.ConvertYuvToRgb(16, 128, 240, out int redR, out int redG, out int redB);
            Assert.That(redR, Is.EqualTo(179));
            Assert.That(redG, Is.EqualTo(0));
            Assert.That(redB, Is.EqualTo(0));
        }

        [Test]
        public void TestSampleIntervalSkipsUntilASecondOrASeek()
        {
            Assert.That(VideoDecoder.ShouldTakeBackdropSample(0, -1), Is.True);
            Assert.That(VideoDecoder.ShouldTakeBackdropSample(500, 1000), Is.False);
            Assert.That(VideoDecoder.ShouldTakeBackdropSample(1000, 1000), Is.True);
            Assert.That(VideoDecoder.ShouldTakeBackdropSample(1000, 10000), Is.True);
        }

        [Test]
        public void TestSamplePointStaysFixedUntilReset()
        {
            int x = -1;
            int y = -1;

            VideoDecoder.ResolveBackdropSamplePoint(ref x, ref y, 320, 180, 3);
            int fixedX = x;
            int fixedY = y;

            VideoDecoder.ResolveBackdropSamplePoint(ref x, ref y, 320, 180, 99);

            Assert.That(x, Is.EqualTo(fixedX));
            Assert.That(y, Is.EqualTo(fixedY));
            Assert.That(x, Is.GreaterThanOrEqualTo(0).And.LessThan(320));
            Assert.That(y, Is.GreaterThanOrEqualTo(0).And.LessThan(180));

            int nextX = -1;
            int nextY = -1;
            VideoDecoder.ResolveBackdropSamplePoint(ref nextX, ref nextY, 320, 180, 4);

            Assert.That(nextX != fixedX || nextY != fixedY, Is.True);
        }
    }
}
