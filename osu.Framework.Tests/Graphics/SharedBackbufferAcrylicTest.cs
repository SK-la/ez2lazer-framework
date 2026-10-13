// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Utils;
using osuTK;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class SharedBackbufferAcrylicTest
    {
        private static readonly Vector2 screen_size = new Vector2(1920, 1080);

        // The shared blur runs at half resolution.
        private static readonly Vector2 texture_size = new Vector2(960, 540);

        [Test]
        public void TestFullScreenRectangleCoversWholeTexture()
        {
            RectangleF coords = SharedBackbufferAcrylic.ComputeTextureCoords(new RectangleF(0, 0, screen_size.X, screen_size.Y), screen_size, texture_size);

            Assert.Multiple(() =>
            {
                Assert.That(coords.Left, Is.EqualTo(0).Within(Precision.FLOAT_EPSILON));
                Assert.That(coords.Top, Is.EqualTo(0).Within(Precision.FLOAT_EPSILON));
                Assert.That(coords.Right, Is.EqualTo(texture_size.X).Within(Precision.FLOAT_EPSILON));
                Assert.That(coords.Bottom, Is.EqualTo(texture_size.Y).Within(Precision.FLOAT_EPSILON));
            });
        }

        [Test]
        public void TestRectangleSmallerThanScreenSamplesSubRegion()
        {
            // A big-logo-sized backdrop in the middle of the screen must sample only its own square of the blur
            // texture. Covering the whole texture here draws the whole screen instead, over every other drawable.
            var logoRect = new RectangleF(790, 370, 340, 340);

            RectangleF coords = SharedBackbufferAcrylic.ComputeTextureCoords(logoRect, screen_size, texture_size);

            Assert.Multiple(() =>
            {
                Assert.That(coords.Left, Is.EqualTo(395).Within(Precision.FLOAT_EPSILON));
                Assert.That(coords.Top, Is.EqualTo(185).Within(Precision.FLOAT_EPSILON));
                Assert.That(coords.Width, Is.EqualTo(170).Within(Precision.FLOAT_EPSILON));
                Assert.That(coords.Height, Is.EqualTo(170).Within(Precision.FLOAT_EPSILON));
            });
        }

        [Test]
        public void TestTextureCoordsStayInsideTexture()
        {
            var halfRect = new RectangleF(screen_size.X / 2, 0, screen_size.X / 2, screen_size.Y / 2);

            RectangleF coords = SharedBackbufferAcrylic.ComputeTextureCoords(halfRect, screen_size, texture_size);

            Assert.That(coords.Right, Is.LessThanOrEqualTo(texture_size.X + Precision.FLOAT_EPSILON));
            Assert.That(coords.Bottom, Is.LessThanOrEqualTo(texture_size.Y + Precision.FLOAT_EPSILON));
        }
    }
}
