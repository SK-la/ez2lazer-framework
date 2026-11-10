// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using Veldrid;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class DisplayOutputPolicyTest
    {
        [Test]
        public void Paper_white_uses_configured_value()
        {
            var caps = hdr400();
            Assert.That(DisplayOutputPolicy.ResolveHdrPaperWhiteNits(400, caps), Is.EqualTo(400).Within(0.01));
            Assert.That(DisplayOutputPolicy.ResolveHdrPaperWhiteNits(2000, caps), Is.EqualTo(2000).Within(0.01));
        }

        [Test]
        public void Paper_white_zero_uses_full_frame_luminance()
        {
            Assert.That(DisplayOutputPolicy.ResolveHdrPaperWhiteNits(0, hdr400()), Is.EqualTo(400).Within(0.01));
        }

        [Test]
        public void Paper_white_zero_falls_back_to_peak_then_200()
        {
            var peakOnly = new MonitorDisplayCapabilities
            {
                MonitorName = @"\\.\DISPLAY2",
                BitsPerColor = 10,
                HdrSignal = true,
                MaxLuminance = 1000,
                MinLuminance = 0.02f,
                MaxFullFrameLuminance = 0,
            };

            Assert.That(DisplayOutputPolicy.ResolveHdrPaperWhiteNits(0, peakOnly), Is.EqualTo(1000).Within(0.01));
            Assert.That(DisplayOutputPolicy.ResolveHdrPaperWhiteNits(0, null), Is.EqualTo(200).Within(0.01));
        }

        [TearDown]
        public void RestoreTone() => HostStartupDisplayOutput.SetTone(1, 0);

        [Test]
        public void SetTone_clamps_to_slider_limits()
        {
            HostStartupDisplayOutput.SetTone(4, 20000);
            Assert.That(HostStartupDisplayOutput.SdrExposure, Is.EqualTo(2).Within(0.001));
            Assert.That(HostStartupDisplayOutput.HdrPaperWhiteNits, Is.EqualTo(10000).Within(0.001));

            HostStartupDisplayOutput.SetTone(0.1f, -5);
            Assert.That(HostStartupDisplayOutput.SdrExposure, Is.EqualTo(0.5f).Within(0.001));
            Assert.That(HostStartupDisplayOutput.HdrPaperWhiteNits, Is.EqualTo(0).Within(0.001));
        }

        private static MonitorDisplayCapabilities hdr400() => new MonitorDisplayCapabilities
        {
            MonitorName = @"\\.\DISPLAY2",
            BitsPerColor = 10,
            HdrSignal = true,
            MaxLuminance = 400,
            MinLuminance = 0.02f,
            MaxFullFrameLuminance = 400,
        };
    }
}
