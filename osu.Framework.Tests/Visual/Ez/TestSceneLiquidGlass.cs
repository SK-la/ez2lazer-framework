// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Input.Events;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Tests.Visual.Ez
{
    [System.ComponentModel.Description("液态玻璃：可拖动卡片在动态背景上做边缘折射、色散与高光")]
    public partial class TestSceneLiquidGlass : FrameworkTestScene
    {
        private LiquidGlassDrawable bufferedGlass = null!;
        private LiquidGlassDrawable sharedGlass = null!;
        private Box rotating = null!;

        [BackgroundDependencyLoader]
        private void load(TextureStore textures)
        {
            var bufferedCard = createCard(new Vector2(-170, 0), out bufferedGlass);
            var sharedCard = createCard(new Vector2(170, 0), out sharedGlass);

            Child = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Children = new Drawable[]
                {
                    // 承载缓冲：卡片采样 CurrentFrameBuffer，走逐卡片外扩抓取路径。
                    new BufferedContainer(pixelSnapping: true)
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
                            rotating = new Box
                            {
                                Anchor = Anchor.Centre,
                                Origin = Anchor.Centre,
                                Size = new Vector2(220),
                                Colour = new Color4(0.85f, 0.25f, 0.35f, 0.9f),
                                Rotation = 12,
                            },
                            bufferedCard,
                        },
                    },
                    // 没有承载缓冲：走整屏共享快照路径。
                    sharedCard,
                },
            };
        }

        private DraggableCard createCard(Vector2 position, out LiquidGlassDrawable glass)
        {
            glass = new LiquidGlassDrawable
            {
                RelativeSizeAxes = Axes.Both,
                BlurSigma = new Vector2(18),
            };

            glass.Material = new LiquidGlassMaterial
            {
                CornerRadius = 26,
                RefractionDepth = 22,
                RefractionStrength = 7,
                ChromaticStrength = 0.15f,
                EdgeHighlight = 0.4f,
                InnerShadow = 0.2f,
                TintStrength = 0.35f,
                Tint = new Color4(1f, 1f, 1f, 0.08f),
                LightDirection = new Vector2(-0.65f, -0.76f),
            };

            return new DraggableCard
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Position = position,
                Size = new Vector2(280, 170),
                Masking = true,
                CornerRadius = 26,
                Children = new Drawable[]
                {
                    glass,
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = new Color4(1f, 1f, 1f, 0.06f),
                    },
                    new SpriteText
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Text = "拖动我",
                        Font = FrameworkFont.Regular.With(size: 24),
                    },
                },
            };
        }

        [Test]
        public void TestTune()
        {
            AddToggleStep("effect enabled", v =>
            {
                bufferedGlass.EffectEnabled = v;
                sharedGlass.EffectEnabled = v;
            });

            AddSliderStep("blur sigma", 0f, 40f, 18f, v =>
            {
                bufferedGlass.BlurSigma = new Vector2(v);
                sharedGlass.BlurSigma = new Vector2(v);
            });

            AddSliderStep("refraction depth", 1f, 60f, 22f, v => applyMaterial(m =>
            {
                m.RefractionDepth = v;
                return m;
            }));

            AddSliderStep("refraction strength", 0f, 24f, 7f, v => applyMaterial(m =>
            {
                m.RefractionStrength = v;
                return m;
            }));

            AddSliderStep("chromatic", 0f, 0.6f, 0.15f, v => applyMaterial(m =>
            {
                m.ChromaticStrength = v;
                return m;
            }));

            AddSliderStep("edge highlight", 0f, 1f, 0.4f, v => applyMaterial(m =>
            {
                m.EdgeHighlight = v;
                return m;
            }));

            AddStep("spin background", () => rotating.RotateTo(rotating.Rotation + 45, 600, Easing.OutQuint));

            AddAssert("cards cover only part of the screen",
                () => bufferedGlass.ScreenSpaceDrawQuad.AABBFloat.Width < DrawSize.X);
        }

        private void applyMaterial(Func<LiquidGlassMaterial, LiquidGlassMaterial> change)
        {
            bufferedGlass.Material = change(bufferedGlass.Material);
            sharedGlass.Material = change(sharedGlass.Material);
        }

        private partial class DraggableCard : Container
        {
            protected override bool OnDragStart(DragStartEvent e) => true;

            protected override void OnDrag(DragEvent e)
            {
                Position += e.Delta;
            }
        }
    }
}
