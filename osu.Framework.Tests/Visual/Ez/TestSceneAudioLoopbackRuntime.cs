// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using ManagedBass;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.EzLatency;
using osu.Framework.Audio.Sample;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Threading;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Tests.Visual.Ez
{
    [System.ComponentModel.Description("Switch output device, auto-fire samples, record trigger-to-loopback time")]
    public partial class TestSceneAudioLoopbackRuntime : FrameworkTestScene
    {
        private const string default_item = "Default";
        private const string exclusive_suffix = " (WASAPI Exclusive)";
        private const string asio_suffix = " (ASIO)";
        private const int burst_count = 5;
        private const double first_hit_delay_ms = 400;
        private const double hit_gap_ms = 1600;
        private const double hit_timeout_ms = 1400;
        private const float heard_rms = 0.002f;

        public override bool AutomaticallyRunFirstStep => false;

        [Resolved]
        private AudioManager audioManager { get; set; } = null!;

        [Resolved]
        private ISampleStore sampleStore { get; set; } = null!;

        private Sample? sample;
        private BasicDropdown<string> deviceDropdown = null!;
        private SpriteText statusText = null!;
        private SpriteText loopbackText = null!;
        private SpriteText statsText = null!;
        private SpriteText resultsText = null!;
        private Box meterFill = null!;
        private AcousticClosedLoopProbe? loopback;
        private readonly List<ScheduledDelegate> pendingHits = new List<ScheduledDelegate>();
        private readonly List<double> hitMs = new List<double>();
        private readonly List<string> resultLines = new List<string>();
        private readonly Dictionary<int, bool> hitHeard = new Dictionary<int, bool>();
        private int fireSeq;
        private int armedId;
        private bool applyingDevice;

        [BackgroundDependencyLoader]
        private void load()
        {
            sample = sampleStore.Get("long.mp3") ?? sampleStore.Get("sample-track.mp3");
            AudioThread.AllowRealWindowsAudioOutput = true;

            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = new Color4(0.08f, 0.09f, 0.11f, 1f),
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Direction = FillDirection.Vertical,
                    Padding = new MarginPadding(20),
                    Spacing = new Vector2(0, 10),
                    Children = new Drawable[]
                    {
                        new SpriteText
                        {
                            Text = "Audio loopback runtime",
                            Font = FrameworkFont.Condensed.With(size: 24),
                        },
                        new SpriteText
                        {
                            Text = "Switch device, then Test / Retest.\nAuto-fires samples and records trigger to loopback.",
                            Font = FrameworkFont.Condensed.With(size: 14),
                            Colour = Color4.LightGray,
                        },
                        deviceDropdown = new BasicDropdown<string>
                        {
                            Width = 720,
                            Depth = -1,
                        },
                        new FillFlowContainer
                        {
                            AutoSizeAxes = Axes.Both,
                            Direction = FillDirection.Horizontal,
                            Spacing = new Vector2(8),
                            Children = new Drawable[]
                            {
                                createActionButton("Test", () => runBurst(clear: false), 120),
                                createActionButton("Retest", () => runBurst(clear: true), 120),
                                createActionButton("Refresh devices", refreshDeviceList, 180),
                            }
                        },
                        statusText = new SpriteText
                        {
                            Font = FrameworkFont.Condensed.With(size: 14),
                        },
                        loopbackText = new SpriteText
                        {
                            Font = FrameworkFont.Condensed.With(size: 14),
                        },
                        new Container
                        {
                            RelativeSizeAxes = Axes.X,
                            Height = 18,
                            Children = new Drawable[]
                            {
                                new Box
                                {
                                    RelativeSizeAxes = Axes.Both,
                                    Colour = new Color4(0.16f, 0.17f, 0.2f, 1f),
                                },
                                meterFill = new Box
                                {
                                    RelativeSizeAxes = Axes.Both,
                                    Width = 0,
                                    Colour = new Color4(0.25f, 0.75f, 0.4f, 1f),
                                },
                            }
                        },
                        statsText = new SpriteText
                        {
                            Font = FrameworkFont.Condensed.With(size: 16),
                            Text = "hits: 0",
                        },
                        new BasicScrollContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            Height = 260,
                            Child = resultsText = new SpriteText
                            {
                                Font = FrameworkFont.Condensed.With(size: 14),
                            }
                        },
                    }
                }
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            audioManager.OnNewDevice += onDeviceListChanged;
            audioManager.OnLostDevice += onDeviceListChanged;
            audioManager.OnWindowsOutputRuntimeChanged += onRuntimeChanged;
            audioManager.AudioDevice.ValueChanged += onAudioDeviceBindableChanged;
            deviceDropdown.Current.ValueChanged += onDropdownBindableChanged;

            refreshDeviceList();
            bindLoopbackToCurrentDevice();
            refreshStatus();
        }

        protected override void Update()
        {
            base.Update();

            if (!OperatingSystem.IsWindows() || loopback == null)
            {
                meterFill.Width = 0;
                return;
            }

            float rms = loopback.CurrentRms;
            meterFill.Width = Math.Clamp((float)Math.Sqrt(Math.Clamp(rms, 0f, 1f)), 0f, 1f);
            loopbackText.Text = loopback.IsRunning
                ? $"loopback: curr={rms:0.0000} peak={loopback.PeakRms:0.0000}"
                : "loopback: off";
        }

        protected override void Dispose(bool isDisposing)
        {
            audioManager.OnNewDevice -= onDeviceListChanged;
            audioManager.OnLostDevice -= onDeviceListChanged;
            audioManager.OnWindowsOutputRuntimeChanged -= onRuntimeChanged;
            audioManager.AudioDevice.ValueChanged -= onAudioDeviceBindableChanged;
            deviceDropdown.Current.ValueChanged -= onDropdownBindableChanged;
            cancelPendingHits();
            stopLoopback();
            AudioThread.AllowRealWindowsAudioOutput = false;
            base.Dispose(isDisposing);
        }

        [Test]
        public void TestDeviceListPopulates()
        {
            AddStep("refresh devices", refreshDeviceList);
            AddAssert("dropdown has devices", () => deviceDropdown.Items.Any());
        }

        private void onDeviceListChanged(string _) => Scheduler.AddOnce(refreshDeviceList);

        private void onRuntimeChanged() => Scheduler.AddOnce(bindLoopbackToCurrentDevice);

        private void onAudioDeviceBindableChanged(ValueChangedEvent<string> _) => onDeviceSelectionChanged();

        private void onDropdownBindableChanged(ValueChangedEvent<string> e) => onDropdownChanged(e.NewValue);

        private void onDeviceSelectionChanged()
        {
            if (applyingDevice)
                return;

            Scheduler.AddOnce(() =>
            {
                applyingDevice = true;
                deviceDropdown.Current.Value = toDropdownItem(audioManager.AudioDevice.Value);
                applyingDevice = false;
                refreshStatus();
            });
        }

        private void onDropdownChanged(string? item)
        {
            if (applyingDevice || item == null)
                return;

            applyingDevice = true;
            audioManager.AudioDevice.Value = fromDropdownItem(item);
            applyingDevice = false;
            refreshStatus();
        }

        private void refreshDeviceList()
        {
            var items = new List<string> { default_item };
            items.AddRange(audioManager.AudioDeviceNames.Distinct());

            applyingDevice = true;
            deviceDropdown.Items = items;

            string selected = toDropdownItem(audioManager.AudioDevice.Value);

            if (!items.Contains(selected))
                selected = default_item;

            deviceDropdown.Current.Value = selected;
            applyingDevice = false;
            refreshStatus();
        }

        private void runBurst(bool clear)
        {
            cancelPendingHits();

            if (clear)
            {
                hitMs.Clear();
                resultLines.Clear();
                hitHeard.Clear();
                fireSeq = 0;
                armedId = 0;
            }

            refreshResults();
            refreshStats();

            if (!loopbackIsRunning())
                bindLoopbackToCurrentDevice();

            if (sample == null)
            {
                appendResult("sample missing");
                return;
            }

            for (int i = 0; i < burst_count; i++)
            {
                var delayed = Scheduler.AddDelayed(fireOnce, first_hit_delay_ms + i * hit_gap_ms);

                pendingHits.Add(delayed);
            }
        }

        private void fireOnce()
        {
            if (sample == null)
                return;

            int id = ++fireSeq;
            armedId = id;

            if (OperatingSystem.IsWindows())
                armLoopback();

            sample.Play();

            var timeout = Scheduler.AddDelayed(() => completeHit(id, null), hit_timeout_ms);

            pendingHits.Add(timeout);
        }

        private void onLoopbackDetected(double roundtripMs)
        {
            Scheduler.Add(() => completeHit(armedId, roundtripMs));
        }

        private void completeHit(int id, double? roundtripMs)
        {
            if (id <= 0)
                return;

            if (roundtripMs is double ms)
            {
                if (hitHeard.TryGetValue(id, out bool heard) && heard)
                    return;

                bool wasTimeout = hitHeard.ContainsKey(id);
                hitHeard[id] = true;
                hitMs.Add(ms);

                string line = $"#{id}  {ms:F1} ms";

                if (wasTimeout)
                    replaceResult($"#{id}  timeout", line);
                else
                    appendResult(line);

                refreshStats();
                return;
            }

            if (hitHeard.ContainsKey(id))
                return;

            hitHeard[id] = false;
            appendResult($"#{id}  timeout");
            refreshStats();
        }

        private void bindLoopbackToCurrentDevice()
        {
            stopLoopback();
            refreshStatus();

            if (!OperatingSystem.IsWindows())
            {
                loopbackText.Text = "loopback: Windows only";
                return;
            }

            startLoopback();
        }

        [SupportedOSPlatform("windows")]
        private void startLoopback()
        {
            loopback = new AcousticClosedLoopProbe(
                () => Environment.TickCount64,
                () => { },
                (_, outputHardwareTime, _, _) => onLoopbackDetected(outputHardwareTime))
            {
                Threshold = heard_rms
            };

            string? driverId = tryResolveDriverId(audioManager.AudioDevice.Value);
            bool started = loopback.TryStart(driverId);
            loopbackText.Text = started
                ? $"loopback: on\n{audioManager.AudioDevice.Value}"
                : "loopback: unavailable";

            if (!started)
            {
                loopback.Dispose();
                loopback = null;
            }
        }

        [SupportedOSPlatform("windows")]
        private void armLoopback() => loopback?.Arm(Environment.TickCount64);

        private bool loopbackIsRunning() => OperatingSystem.IsWindows() && loopbackRunningWindows();

        [SupportedOSPlatform("windows")]
        private bool loopbackRunningWindows() => loopback?.IsRunning == true;

        private void stopLoopback()
        {
            if (OperatingSystem.IsWindows())
                disposeLoopbackWindows();

            loopback = null;
        }

        [SupportedOSPlatform("windows")]
        private void disposeLoopbackWindows() => loopback?.Dispose();

        private void cancelPendingHits()
        {
            foreach (var hit in pendingHits)
                hit.Cancel();

            pendingHits.Clear();
        }

        private void appendResult(string line)
        {
            resultLines.Add(line);
            refreshResults();
        }

        private void replaceResult(string from, string to)
        {
            int index = resultLines.LastIndexOf(from);

            if (index >= 0)
                resultLines[index] = to;
            else
                resultLines.Add(to);

            refreshResults();
        }

        private void refreshResults() => resultsText.Text = string.Join('\n', resultLines);

        private void refreshStats()
        {
            if (hitMs.Count == 0)
            {
                statsText.Text = $"hits: 0 / {fireSeq}";
                return;
            }

            statsText.Text =
                $"hits: {hitMs.Count}/{fireSeq}  avg={hitMs.Average():F1} ms  min={hitMs.Min():F1}  max={hitMs.Max():F1}";
        }

        private void refreshStatus()
        {
            statusText.Text = audioManager.GetOutputDeviceStatusNote();
        }

        private static string toDropdownItem(string selection) =>
            string.IsNullOrEmpty(selection) ? default_item : selection;

        private static string fromDropdownItem(string item) =>
            item == default_item ? string.Empty : item;

        private static string? tryResolveDriverId(string selection)
        {
            string name = selection;

            if (name.EndsWith(exclusive_suffix, StringComparison.Ordinal))
                name = name[..^exclusive_suffix.Length];
            else if (name.EndsWith(asio_suffix, StringComparison.Ordinal))
                name = name[..^asio_suffix.Length];

            if (string.IsNullOrEmpty(name) || name == default_item)
                return null;

            for (int i = 1; i < Bass.DeviceCount; i++)
            {
                if (!Bass.GetDeviceInfo(i, out var info) || !info.IsEnabled)
                    continue;

                if (!string.Equals(info.Name, name, StringComparison.Ordinal))
                    continue;

                return string.IsNullOrEmpty(info.Driver) ? null : info.Driver;
            }

            return null;
        }

        private static BasicButton createActionButton(string text, Action action, float width) => new BasicButton
        {
            Size = new Vector2(width, 36),
            Text = text,
            BackgroundColour = new Color4(0.2f, 0.45f, 0.75f, 1f),
            Action = action,
        };
    }
}
