// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Tests.Visual.Drawables
{
    [System.ComponentModel.Description("大 logo 场景：圆形裁切里的毛玻璃直接画到后缓冲，圈外与菜单展开后都不应出现黑块")]
    public partial class TestSceneAcrylicBackdropLogo : FrameworkTestScene
    {
        private AcrylicBackdropDrawable acrylic = null!;
        private CircularContainer logoMask = null!;
        private FillFlowContainer menu = null!;

        [BackgroundDependencyLoader]
        private void load(TextureStore textures)
        {
            // 刻意不套 BufferedContainer：真实主菜单里开启毛玻璃后就不再 acquire 全屏捕获，
            // 毛玻璃直接画到交换链，走共用后缓冲快路径。套了承载缓冲只会测到回退路径。
            Child = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Children = new Drawable[]
                {
                    new Sprite
                    {
                        RelativeSizeAxes = Axes.Both,
                        FillMode = FillMode.Fill,
                        Texture = textures.Get("sample-texture"),
                    },
                    // 大 logo：圆形裁切，尺寸只占屏幕一小块，和主菜单 logo 一样。
                    logoMask = new CircularContainer
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Size = new Vector2(340),
                        Masking = true,
                        BorderThickness = 4,
                        BorderColour = new Color4(1f, 1f, 1f, 0.6f),
                        Children = new Drawable[]
                        {
                            acrylic = new AcrylicBackdropDrawable
                            {
                                RelativeSizeAxes = Axes.Both,
                                BlurSigma = new Vector2(16),
                            },
                            new Box
                            {
                                RelativeSizeAxes = Axes.Both,
                                Colour = new Color4(1f, 1f, 1f, 0.15f),
                            },
                        },
                    },
                    menu = new FillFlowContainer
                    {
                        Anchor = Anchor.BottomCentre,
                        Origin = Anchor.BottomCentre,
                        Alpha = 0,
                        Spacing = new Vector2(16),
                        AutoSizeAxes = Axes.Both,
                        Margin = new MarginPadding { Bottom = 60 },
                        Children = new Drawable[]
                        {
                            new AcrylicChip(),
                            new AcrylicChip(),
                            new AcrylicChip(),
                        },
                    },
                },
            };
        }

        [Test]
        public void TestBigLogo()
        {
            AddToggleStep("effect enabled", v => acrylic.EffectEnabled = v);
            AddSliderStep("blur sigma", 0f, 40f, 16f, v => acrylic.BlurSigma = new Vector2(v));
            AddSliderStep("logo size", 120f, 640f, 340f, v => logoMask.Size = new Vector2(v));
            AddStep("open menu", () => menu.FadeTo(1, 300, Easing.OutQuint));
            AddStep("close menu", () => menu.FadeTo(0, 300, Easing.OutQuint));

            // 卡片只占屏幕一小块：圈外到方形边界之间必须是背景，不是黑块，也不是整屏糊过来的模糊。
            AddAssert("logo covers only part of the screen", () => logoMask.ScreenSpaceDrawQuad.AABBFloat.Width < DrawSize.X);
        }

        /// <summary>
        /// 主菜单按钮那种小毛玻璃块：多个小块同时存在时最容易把整屏画花。
        /// </summary>
        private partial class AcrylicChip : Container
        {
            public AcrylicChip()
            {
                Size = new Vector2(140, 90);
                Masking = true;
                CornerRadius = 12;

                InternalChildren = new Drawable[]
                {
                    new AcrylicBackdropDrawable
                    {
                        RelativeSizeAxes = Axes.Both,
                    },
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = new Color4(1f, 1f, 1f, 0.15f),
                    },
                };
            }
        }
    }
}
