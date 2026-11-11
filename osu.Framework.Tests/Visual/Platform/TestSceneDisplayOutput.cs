// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Veldrid;
using osu.Framework.Platform;
using osu.Framework.Utils;
using osuTK;
using Veldrid;

namespace osu.Framework.Tests.Visual.Platform
{
    /// <summary>
    /// Visual check for swapchain display encoding (10-bit SDR / HDR10 on D3D11).
    /// Launch framework visual tests with <c>EZ_FRAMEWORK_DISPLAY_OUTPUT=Native10Bit</c> (or Hdr10) and Direct3D 11 renderer.
    /// </summary>
    [Ignore("Requires a GPU-backed renderer (not headless).")]
    public partial class TestSceneDisplayOutput : FrameworkTestScene
    {
        [Resolved]
        private GameHost host { get; set; } = null!;

        private SpriteText statusText = null!;

        [SetUp]
        public void SetUp() => Schedule(() =>
        {
            Child = new FillFlowContainer
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 12),
                Children = new Drawable[]
                {
                    statusText = new SpriteText
                    {
                        Font = FrameworkFont.Regular.With(size: 20),
                        Text = "Initialising…",
                    },
                    new SpriteText
                    {
                        Font = FrameworkFont.Regular.With(size: 16),
                        Text = "Grey vertical ramp (inspect banding). Native10Bit should show smoother steps on a 10-bit display.",
                    },
                    new Container
                    {
                        Size = new Vector2(512, 256),
                        Children = new Drawable[]
                        {
                            new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.Both,
                                Direction = FillDirection.Vertical,
                                ChildrenEnumerable = Enumerable.Range(0, 256).Select(i => new Box
                                {
                                    RelativeSizeAxes = Axes.X,
                                    Height = 1,
                                    Colour = new Colour4(
                                        (byte)(Interpolation.Lerp(13, 242, i / 255f)),
                                        (byte)(Interpolation.Lerp(13, 242, i / 255f)),
                                        (byte)(Interpolation.Lerp(13, 242, i / 255f)),
                                        255),
                                }),
                            },
                        },
                    },
                },
            };

            refreshStatus();
        });

        protected override void Update()
        {
            base.Update();
            refreshStatus();
        }

        private void refreshStatus()
        {
            string swapInfo = "Swapchain: n/a";

            if (host.Renderer is VeldridRenderer veldrid && host.Window.GraphicsSurface.Type == GraphicsSurfaceType.Direct3D11)
            {
                Swapchain sc = veldrid.Device.MainSwapchain;
                swapInfo = $"Swapchain request={HostStartupDisplayOutput.Mode.ToSwapchainDisplayMode(GraphicsSurfaceType.Direct3D11, logFallback: false)}, "
                           + $"effective={sc.EffectiveDisplayMode}, surface={sc.SurfaceColorPixelFormat?.ToString() ?? "unknown"}";
            }

            statusText.Text = $"Host DisplayOutputMode={HostStartupDisplayOutput.Mode}\n"
                              + $"Renderer: {host.ResolvedRenderer} ({host.Renderer.GetType().Name} / {host.Window.GraphicsSurface.Type})\n"
                              + swapInfo;
        }
    }
}
