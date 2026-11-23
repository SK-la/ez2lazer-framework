// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Testing;
using osu.Framework.Testing.Drawables.Steps;
using osu.Framework.Tests.Visual.Ez;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Tests.Ez
{
    [TestFixture]
    [NonParallelizable]
    public partial class WeatherEffectRenderingTest
    {
        [Test]
        [Explicit("Requires a desktop GPU. Select this test explicitly; use OSU_GRAPHICS_RENDERER and OSU_GRAPHICS_SURFACE to select a backend.")]
        [Apartment(ApartmentState.STA)]
        [SuppressMessage("ReSharper", "AccessToDisposedClosure")]
        public void TestRenderWeather()
        {
            string backend = $"{FrameworkEnvironment.PreferredGraphicsRenderer ?? "default"}-{FrameworkEnvironment.PreferredGraphicsSurface}";
            string output = Path.Combine(TestContext.CurrentContext.WorkDirectory, "weather-screenshots", backend);
            Directory.CreateDirectory(output);
            using var host = Host.GetSuitableDesktopHost("weather-rendering-tests", new HostOptions { FriendlyGameName = "Weather rendering verification" });
            var game = new RenderingGame(output);
            host.ExceptionThrown += e =>
            {
                game.Failure = ExceptionDispatchInfo.Capture(e);
                host.Exit();
                return true;
            };
            host.Run(game);

            foreach (string screenshot in Directory.EnumerateFiles(output, "*.png"))
                TestContext.AddTestAttachment(screenshot);

            game.Failure?.Throw();
            Assert.That(game.Completed, Is.True, "Rendering verification did not complete.");
        }

        private partial class RenderingGame : TestGame
        {
            public ExceptionDispatchInfo? Failure;
            public bool Completed;
            private readonly string output;
            private WeatherEffectContainer weather = null!;
            private Container clipped = null!;
            private FrameworkConfigManager configuration = null!;

            public RenderingGame(string output)
            {
                this.output = output;
            }

            [BackgroundDependencyLoader]
            private void load(FrameworkConfigManager config)
            {
                configuration = config;
                // The UI screenshot uses the repository's bitmap fixtures, independently of native outline font support.
                AddFont(Resources, "Fonts/Roboto/Roboto-Regular");
                AddFont(Resources, "Fonts/RobotoCondensed/RobotoCondensed-Regular");
                config.SetValue(FrameworkSetting.WindowMode, WindowMode.Windowed);
                config.SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(900, 650));
                Add(new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(0.12f, 0.08f, 0.2f, 1) });
                Add(new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Size = new Vector2(0.45f, 0.8f),
                    RelativePositionAxes = Axes.Both,
                    Position = new Vector2(0.03f, 0.08f),
                    Masking = true,
                    CornerRadius = 36,
                    Child = new WeatherTestBackground { Animate = false },
                });
                Add(clipped = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Size = new Vector2(0.45f, 0.8f),
                    RelativePositionAxes = Axes.Both,
                    Position = new Vector2(0.52f, 0.08f),
                    Masking = true,
                    CornerRadius = 36,
                    Child = weather = new WeatherEffectContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        PreviewTime = 5,
                        Child = new WeatherTestBackground { Animate = false },
                    },
                });
                // A foreground marker must survive both active and bypass rendering paths.
                Add(new Box
                {
                    RelativePositionAxes = Axes.Both,
                    Position = new Vector2(0.7f, 0.45f),
                    Size = new Vector2(32),
                    Colour = Color4.Magenta,
                });
            }

            protected override void LoadComplete()
            {
                base.LoadComplete();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await verify().ConfigureAwait(false);
                        Completed = true;
                    }
                    catch (Exception e)
                    {
                        Failure = ExceptionDispatchInfo.Capture(e);
                    }
                    finally
                    {
                        Host.Exit();
                    }
                });
            }

            private async Task verify()
            {
                // Warm the initial framebuffer/driver pipelines before taking the reference image.
                using var warmup = await capture("warmup", () => weather.Effect = WeatherEffect.Underwater).ConfigureAwait(false);
                using var baseline = await capture("00-original", () => weather.Effect = WeatherEffect.None).ConfigureAwait(false);

                foreach (var effect in new[] { WeatherEffect.DesertHeat, WeatherEffect.Underwater, WeatherEffect.OvercastRain, WeatherEffect.UnderwaterLightCurtain })
                {
                    using var image = await capture(effect.ToString(), () => weather.Effect = effect).ConfigureAwait(false);
                    Assert.That(difference(baseline, image, 0.54f, 0.12f, 0.4f, 0.68f), Is.GreaterThan(0.2), $"{effect} did not change the scene.");
                    Assert.That(difference(baseline, image, 0.05f, 0.12f, 0.4f, 0.68f), Is.LessThan(0.05), "Weather escaped its subtree.");
                    Assert.That(image[(int)(image.Width * 0.7f) + 10, (int)(image.Height * 0.45f) + 10], Is.EqualTo(baseline[(int)(image.Width * 0.7f) + 10, (int)(image.Height * 0.45f) + 10]),
                        "Weather covered the foreground.");
                    Assert.That(image[(int)(image.Width * 0.52f) + 1, (int)(image.Height * 0.08f) + 1], Is.EqualTo(baseline[(int)(image.Width * 0.52f) + 1, (int)(image.Height * 0.08f) + 1]),
                        "Weather escaped rounded clipping.");

                    using var changedTime = await capture($"{effect}-later", () => weather.PreviewTime = 6).ConfigureAwait(false);
                    Assert.That(difference(image, changedTime, 0.54f, 0.12f, 0.4f, 0.68f), Is.GreaterThan(0.1), "Weather did not animate.");
                    using var repeat = await capture($"{effect}-repeat", () => weather.PreviewTime = 5).ConfigureAwait(false);
                    Assert.That(difference(image, repeat, 0.54f, 0.12f, 0.4f, 0.68f), Is.LessThan(0.05), "Fixed preview time was not deterministic.");
                    using var bypass = await capture($"{effect}-zero", () => weather.Intensity = 0).ConfigureAwait(false);
                    Assert.That(difference(baseline, bypass, 0, 0, 1, 1), Is.LessThan(0.05), "Zero intensity did not restore the original.");
                    await onUpdate(() => weather.Intensity = 1).ConfigureAwait(false);
                }

                using var outside = await capture("scope-inactive", () => weather.Scope = WeatherEffectScope.Gameplay).ConfigureAwait(false);
                Assert.That(difference(baseline, outside, 0, 0, 1, 1), Is.LessThan(0.05));
                using var inside = await capture("scope-gameplay", () => weather.IsGameplay = true).ConfigureAwait(false);
                Assert.That(difference(baseline, inside, 0.54f, 0.12f, 0.4f, 0.68f), Is.GreaterThan(0.2));

                await onUpdate(() =>
                {
                    weather.Scope = WeatherEffectScope.Global;
                    weather.Clear();
                    weather.Add(new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(0.02f, 0.06f, 0.12f, 1) });
                }).ConfigureAwait(false);
                using var deepWater = await capture("curtain-dark-water", () => weather.Effect = WeatherEffect.None).ConfigureAwait(false);
                using var curtain = await capture("curtain-front-view", () => weather.Effect = WeatherEffect.UnderwaterLightCurtain).ConfigureAwait(false);
                double upperLight = difference(deepWater, curtain, 0.56f, 0.16f, 0.36f, 0.14f);
                double lowerLight = difference(deepWater, curtain, 0.56f, 0.64f, 0.36f, 0.14f);
                Assert.That(upperLight, Is.GreaterThan(0.5), "The curtain did not illuminate the upper water.");
                Assert.That(upperLight, Is.GreaterThan(lowerLight * 2), "The curtain did not fade downwards from the surface.");

                foreach (var (name, settings) in new[]
                         {
                             ("density", UnderwaterLightCurtainSettings.Default with { Scale = 2 }),
                             ("falloff", UnderwaterLightCurtainSettings.Default with { Falloff = 0.5f }),
                             ("sway", UnderwaterLightCurtainSettings.Default with { Sway = 0.9f }),
                         })
                {
                    using var tuned = await capture($"curtain-{name}", () => weather.UnderwaterLightCurtain = settings).ConfigureAwait(false);
                    Assert.That(difference(curtain, tuned, 0.56f, 0.16f, 0.36f, 0.6f), Is.GreaterThan(0.1), $"Curtain {name} did not change the light.");
                }

                using var wideCurtain = await capture("curtain-wide-front-view", () =>
                {
                    weather.UnderwaterLightCurtain = UnderwaterLightCurtainSettings.Default;
                    clipped.Size = new Vector2(0.94f, 0.8f);
                    clipped.X = 0.03f;
                }).ConfigureAwait(false);
                foreach (double time in new[] { 9.0, 17.0 })
                {
                    using var movingVeil = await capture($"curtain-wide-time-{time:0}", () => weather.PreviewTime = time).ConfigureAwait(false);
                    Assert.That(difference(wideCurtain, movingVeil, 0.06f, 0.16f, 0.86f, 0.4f), Is.GreaterThan(0.5), "The layered veils did not move across the wide scene.");
                }

                // Neutral shader parameters must preserve geometry and colour, including the container's own tint and alpha.
                await onUpdate(() =>
                {
                    clipped.Size = new Vector2(0.45f, 0.8f);
                    clipped.X = 0.52f;
                    weather.PreviewTime = 5;
                    weather.Clear();
                    weather.Add(new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(0.4f, 0.6f, 0.8f, 1) });
                    weather.Scope = WeatherEffectScope.Global;
                    weather.Colour = new Color4(0.7f, 0.85f, 0.9f, 1);
                    weather.Alpha = 0.6f;
                }).ConfigureAwait(false);
                using var tinted = await capture("tinted-direct", () => weather.Effect = WeatherEffect.None).ConfigureAwait(false);

                foreach (var effect in new[] { WeatherEffect.DesertHeat, WeatherEffect.Underwater, WeatherEffect.OvercastRain, WeatherEffect.UnderwaterLightCurtain })
                {
                    using var neutral = await capture($"{effect}-neutral-tinted", () =>
                    {
                        weather.Effect = effect;
                        weather.DesertHeat = weather.DesertHeat with { Amplitude = 0, Tint = 0 };
                        weather.Underwater = weather.Underwater with { Brightness = 0, Tint = 0 };
                        weather.OvercastRain = weather.OvercastRain with { Density = 0, Cloudiness = 0 };
                        weather.UnderwaterLightCurtain = weather.UnderwaterLightCurtain with { Brightness = 0 };
                    }).ConfigureAwait(false);
                    // Compare a single opaque child; grouping overlapping translucent children has different semantics.
                    Assert.That(difference(tinted, neutral, 0.83f, 0.68f, 0.08f, 0.08f), Is.LessThan(1), "Container colour/alpha was applied twice or neutral weather changed the scene.");
                }

                await onUpdate(() =>
                {
                    weather.Colour = Color4.White;
                    weather.Alpha = 1;
                    weather.Underwater = UnderwaterSettings.Default;
                    weather.UnderwaterLightCurtain = UnderwaterLightCurtainSettings.Default;
                }).ConfigureAwait(false);

                // Both water effects must preserve empty pixels over a transparent child subtree.
                foreach (var effect in new[] { WeatherEffect.Underwater, WeatherEffect.UnderwaterLightCurtain })
                {
                    using var transparent = await capture($"{effect}-transparent", () =>
                    {
                        weather.Clear();
                        weather.Effect = effect;
                        weather.Add(new Box { Size = new Vector2(40), Position = new Vector2(80), Colour = Color4.White });
                    }).ConfigureAwait(false);
                    Assert.That(transparent[(int)(transparent.Width * 0.9f), (int)(transparent.Height * 0.75f)], Is.EqualTo(baseline[1, 1]), "Weather filled transparent pixels.");
                }

                using var resized = await capture("resized", () =>
                {
                    weather.Clear();
                    weather.Add(new WeatherTestBackground { Animate = false });
                    clipped.Size = new Vector2(0.35f, 0.6f);
                    clipped.Rotation = 5;
                }).ConfigureAwait(false);
                Assert.That(resized.Width, Is.GreaterThan(0));

                using var nested = await capture("nested-buffer", () =>
                {
                    clipped.Remove(weather, false);
                    clipped.Add(new BufferedContainer { RelativeSizeAxes = Axes.Both, Child = weather });
                }).ConfigureAwait(false);
                Assert.That(difference(resized, nested, 0, 0, 1, 1), Is.LessThan(1), "Nesting inside a buffered container changed the weather.");

                using var windowResize = await capture("window-resized", () => configuration.SetValue(FrameworkSetting.WindowedSize, new System.Drawing.Size(1000, 720))).ConfigureAwait(false);
                Assert.That(windowResize.Width, Is.Not.EqualTo(resized.Width), "The window was not resized.");
                Assert.That(windowResize.Height, Is.Not.EqualTo(resized.Height));

                var sceneLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                TestSceneWeatherEffects laboratory = null!;
                await onUpdate(() =>
                {
                    Clear();
                    var browser = new TestBrowser();
                    browser.TestTypes.Clear();
                    browser.TestTypes.Add(typeof(TestSceneWeatherEffects));
                    browser.OnLoadComplete += _ => browser.CurrentTest.OnLoadComplete += loaded =>
                    {
                        laboratory = (TestSceneWeatherEffects)loaded;
                        sceneLoaded.SetResult();
                    };
                    Add(browser);
                }).ConfigureAwait(false);
                await sceneLoaded.Task.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                using var controls = await capture("debug-controls", () => { }).ConfigureAwait(false);
                int textPixels = 0;

                for (int y = 75; y < controls.Height; y++)
                {
                    for (int x = 210; x < 370; x++)
                    {
                        Rgba32 pixel = controls[x, y];
                        if (pixel.R > 220 && pixel.G > 220 && pixel.B > 220)
                            textPixels++;
                    }
                }

                Assert.That(textPixels, Is.GreaterThan(50), "Debug parameter labels were not rendered.");
                using var preview = await capture("debug-light-curtain", () =>
                {
                    laboratory.ShowLightCurtainPreview();
                    laboratory.Children.OfType<WeatherEffectContainer>().Single().PreviewTime = 5;
                    // Common, heat and caustic controls occupy the first nine sliders.
                    var firstCurtainSlider = laboratory.StepsContainer.OfType<StepSlider<float>>().ElementAt(9);
                    var scroll = firstCurtainSlider.FindClosestParent<ScrollContainer<Drawable>>();
                    Assert.That(scroll, Is.Not.Null, "The curtain parameter panel must be scrollable.");
                    scroll!.ScrollTo(firstCurtainSlider, animated: false);
                }).ConfigureAwait(false);
                Assert.That(preview.Width, Is.EqualTo(controls.Width));
                await onUpdate(() =>
                {
                    // The comparison capture left the time fixed. Check that the wide preview plays even after a pause.
                    var speedSlider = laboratory.StepsContainer.OfType<StepSlider<float>>().ElementAt(1);
                    speedSlider.Current.Value = 0;
                }).ConfigureAwait(false);
                using var livePreview = await capture("debug-light-curtain-wide", laboratory.ShowWideLightCurtainPreview).ConfigureAwait(false);
                await Task.Delay(1100).ConfigureAwait(false);
                using var livePreviewLater = await capture("debug-light-curtain-wide-later", () => { }).ConfigureAwait(false);
                Assert.That(difference(livePreview, livePreviewLater, 0.4f, 0.18f, 0.58f, 0.43f), Is.GreaterThan(0.5),
                    "The live preview did not animate its light sheets after a pause.");
            }

            private Task onUpdate(Action action)
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Schedule(() =>
                {
                    try
                    {
                        action();
                        // Allow several update/draw cycles before requesting a readback.
                        Scheduler.AddDelayed(completion.SetResult, 120);
                    }
                    catch (Exception e)
                    {
                        completion.SetException(e);
                    }
                });
                return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }

            private async Task<RgbaImage> capture(string name, Action change)
            {
                await onUpdate(change).ConfigureAwait(false);
                if (name == "debug-controls")
                    await Task.Delay(1000).ConfigureAwait(false);
                using var previous = await Host.TakeScreenshotAsync().WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                var image = await Host.TakeScreenshotAsync().WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                using (var destination = File.Create(Path.Combine(output, $"{name}.png")))
                    image.SaveAsPng(destination);
                return image;
            }

            private static double difference(RgbaImage a, RgbaImage b, float left, float top, float width, float height)
            {
                long sum = 0;
                int count = 0;

                for (int y = (int)(a.Height * top); y < (int)(a.Height * (top + height)); y++)
                {
                    for (int x = (int)(a.Width * left); x < (int)(a.Width * (left + width)); x++)
                    {
                        Rgba32 p = a[x, y];
                        Rgba32 q = b[x, y];
                        sum += Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B);
                        count += 3;
                    }
                }

                return sum / (double)count;
            }
        }
    }
}
