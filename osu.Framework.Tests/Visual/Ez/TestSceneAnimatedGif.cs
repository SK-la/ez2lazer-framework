// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Animations;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Testing;
using osu.Framework.Tests.Resources;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Tests.Visual.Ez
{
    [System.ComponentModel.Description("Embedded sample GIF with optional continuous rotation and scale pulse")]
    public partial class TestSceneAnimatedGif : FrameworkTestScene
    {
        private const float rotation_speed = 0.15f;
        private const float scale_amplitude = 0.325f;
        private const double scale_pulse_speed = 0.004;

        private TextureAnimation animation = null!;
        private SpriteText status = null!;
        private AnimatedImageFrameLoadResult decodedFrames = null!;
        private int expectedFrameCount;

        private bool continuousRotation;
        private bool continuousScale;
        private float rotationAtStop;
        private float lastRecordedRotation;

        [BackgroundDependencyLoader]
        private void load(IRenderer renderer)
        {
            decodedFrames = GifTestResources.LoadSampleFlameGif();
            expectedFrameCount = decodedFrames.Frames.Count;

            animation = buildAnimation(renderer, decodedFrames);
            animation.Play();

            status = new SpriteText
            {
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre,
                Y = 8,
                Font = FontUsage.Default,
            };

            Child = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.Both,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 8),
                Children = new Drawable[]
                {
                    status,
                    new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Child = new Container
                        {
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            Size = new Vector2(360),
                            Masking = true,
                            BorderColour = Color4.White,
                            BorderThickness = 1,
                            Children = new Drawable[]
                            {
                                new Box
                                {
                                    RelativeSizeAxes = Axes.Both,
                                    Colour = new Color4(20, 20, 24, 255),
                                },
                                animation,
                            },
                        },
                    },
                },
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            AddStep("reset transforms", resetTransforms);
            AddToggleStep("continuous rotation", enabled =>
            {
                continuousRotation = enabled;

                if (enabled)
                    return;

                rotationAtStop = animation.Rotation;
            });
            AddToggleStep("continuous scale", enabled =>
            {
                continuousScale = enabled;

                if (!enabled)
                    animation.Scale = Vector2.One;
            });
        }

        private void resetTransforms()
        {
            continuousRotation = false;
            continuousScale = false;
            animation.Rotation = 0;
            rotationAtStop = 0;
            animation.Scale = Vector2.One;
        }

        private static TextureAnimation buildAnimation(IRenderer renderer, AnimatedImageFrameLoadResult decoded)
        {
            var first = decoded.Frames[0];
            var size = new Vector2(first.Width, first.Height);

            var animation = new TextureAnimation
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                RelativeSizeAxes = Axes.None,
                Size = size,
                Loop = true,
            };

            for (int i = 0; i < decoded.Frames.Count; i++)
            {
                var upload = decoded.Frames[i];
                var texture = renderer.CreateTexture(upload.Width, upload.Height);
                texture.SetData(upload);
                animation.AddFrame(texture, decoded.FrameDurationMs[i]);
            }

            return animation;
        }

        [Test]
        public void TestGifAnimationCycles()
        {
            AddAssert("has multiple frames", () => animation.FrameCount == expectedFrameCount && expectedFrameCount > 1);
            AddUntilStep("sprite has texture", () => animation.ChildrenOfType<Sprite>().Single().Texture?.Available == true);
            AddUntilStep("animation advances", () => animation.CurrentFrameIndex > 0);
        }

        [Test]
        public void TestContinuousRotationToggle()
        {
            resetTransforms();
            lastRecordedRotation = animation.Rotation;

            AddToggleStep("continuous rotation", enabled => continuousRotation = enabled);

            AddAssert("rotation off is stable", () => !continuousRotation);

            AddStep("enable rotation", () => continuousRotation = true);
            AddUntilStep("rotation increases", () => animation.Rotation > lastRecordedRotation + 5);

            lastRecordedRotation = animation.Rotation;
            AddStep("disable rotation", () =>
            {
                continuousRotation = false;
                rotationAtStop = animation.Rotation;
            });
            AddWaitStep("wait", 5);
            AddAssert("rotation frozen", () => Math.Abs(animation.Rotation - rotationAtStop) < 0.01f);
        }

        [Test]
        public void TestContinuousScaleToggle()
        {
            resetTransforms();

            AddToggleStep("continuous scale", enabled => continuousScale = enabled);

            AddStep("enable scale pulse", () => continuousScale = true);
            AddUntilStep("scale deviates from 1", () => Math.Abs(animation.Scale.X - 1) > 0.05f);

            AddStep("disable scale pulse", () =>
            {
                continuousScale = false;
                animation.Scale = Vector2.One;
            });
            AddAssert("scale reset", () => animation.Scale == Vector2.One);
        }

        protected override void Dispose(bool isDisposing)
        {
            if (isDisposing)
                decodedFrames.Dispose();

            base.Dispose(isDisposing);
        }

        protected override void Update()
        {
            base.Update();

            if (continuousRotation)
                animation.Rotation += (float)(Clock.ElapsedFrameTime * rotation_speed);

            if (continuousScale)
            {
                float scale = 1f + scale_amplitude * (float)Math.Sin(Clock.CurrentTime * scale_pulse_speed);
                animation.Scale = new Vector2(scale);
            }

            status.Text = $"frame {animation.CurrentFrameIndex + 1}/{animation.FrameCount}"
                          + $"  rotate {(continuousRotation ? "on" : "off")}"
                          + $"  scale {(continuousScale ? "on" : "off")}"
                          + $"  {animation.Rotation:0}° {animation.Scale.X:0.00}x";
        }
    }
}
