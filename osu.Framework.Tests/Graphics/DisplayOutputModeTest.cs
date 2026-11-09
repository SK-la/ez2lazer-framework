// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Platform;
using Veldrid;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class DisplayOutputModeTest
    {
        [TestCase(DisplayOutputMode.Standard, GraphicsSurfaceType.Direct3D11, SwapchainDisplayMode.Standard8Bit)]
        [TestCase(DisplayOutputMode.Native10Bit, GraphicsSurfaceType.Direct3D11, SwapchainDisplayMode.Native10BitSdr)]
        [TestCase(DisplayOutputMode.Hdr10, GraphicsSurfaceType.Direct3D11, SwapchainDisplayMode.Hdr10)]
        [TestCase(DisplayOutputMode.Native10Bit, GraphicsSurfaceType.OpenGL, SwapchainDisplayMode.Standard8Bit)]
        [TestCase(DisplayOutputMode.Hdr10, GraphicsSurfaceType.Vulkan, SwapchainDisplayMode.Standard8Bit)]
        public void Maps_to_swapchain_mode(DisplayOutputMode hostMode, GraphicsSurfaceType surface, SwapchainDisplayMode expected)
        {
            Assert.That(hostMode.ToSwapchainDisplayMode(surface, logFallback: false), Is.EqualTo(expected));
        }
    }
}
