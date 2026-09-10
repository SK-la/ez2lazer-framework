// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using ManagedBass;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Audio.EzLatency;
using osu.Framework.Audio.Sample;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Framework.Logging;
using osuTK;
using osuTK.Graphics;
using osuTK.Input;

namespace osu.Framework.Tests.Visual.Ez
{
    /// <summary>
    /// Interactive / automated acoustic closed-loop check (current-output WASAPI loopback on Windows).
    /// Keys: Z = single, X = rapid burst, C = simultaneous multi-hit. Buttons mirror the same actions.
    /// </summary>
    public partial class TestSceneAcousticClosedLoop : FrameworkTestScene
    {
        private const string log_name = "ez_runtime";

        [Resolved]
        private ISampleStore sampleStore { get; set; }

        private readonly EzLatencyManager latency = EzLatencyManager.GLOBAL;
        private readonly List<EzLatencyRecord> sessionRecords = new List<EzLatencyRecord>();

        private Sample sample;
        private SpriteText statusText;
        private SpriteText lastHitText;
        private Action<EzLatencyRecord> measurementHandler;
        private int hitsArmed;

        [BackgroundDependencyLoader]
        private void load()
        {
            sample = sampleStore.Get("long.mp3") ?? sampleStore.Get("sample-track.mp3");

            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = new Color4(0.08f, 0.09f, 0.11f, 1f),
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Padding = new MarginPadding(20),
                    Spacing = new Vector2(0, 10),
                    Children = new Drawable[]
                    {
                        new SpriteText
                        {
                            Text = "Acoustic closed-loop (Ez)",
                            Font = FrameworkFont.Condensed.With(size: 24),
                        },
                        statusText = new SpriteText
                        {
                            Text = "idle",
                            Font = FrameworkFont.Condensed.With(size: 16),
                        },
                        lastHitText = new SpriteText
                        {
                            Text = "last: —",
                            Font = FrameworkFont.Condensed.With(size: 16),
                        },
                        new SpriteText
                        {
                            Text = "Z = single   |   X = rapid×8   |   C = chord×3   |   or click buttons",
                            Font = FrameworkFont.Condensed.With(size: 14),
                            Colour = Color4.LightGray,
                        },
                        new FillFlowContainer
                        {
                            AutoSizeAxes = Axes.Both,
                            Direction = FillDirection.Horizontal,
                            Spacing = new Vector2(8),
                            Children = new Drawable[]
                            {
                                createActionButton("Single (Z)", fireSingle),
                                createActionButton("Rapid (X)", fireRapid),
                                createActionButton("Chord (C)", fireChord),
                                createActionButton("Reset stats", resetSession),
                            }
                        },
                    }
                },
            };
        }

        protected override bool OnKeyDown(KeyDownEvent e)
        {
            if (e.Repeat)
                return false;

            switch (e.Key)
            {
                case Key.Z:
                    fireSingle();
                    return true;

                case Key.X:
                    fireRapid();
                    return true;

                case Key.C:
                    fireChord();
                    return true;
            }

            return base.OnKeyDown(e);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            measurementHandler = onMeasurement;
            latency.OnNewRecord += measurementHandler;
            prepareSession();
            refreshStatus();
        }

        protected override void Update()
        {
            base.Update();

            // Belt-and-suspenders: soft-emit when awaiting acoustic even if capture callbacks stall.
            if (latency.Enabled.Value)
                latency.PollPendingTimeout();
        }

        protected override void Dispose(bool isDisposing)
        {
            if (measurementHandler != null)
                latency.OnNewRecord -= measurementHandler;

            latency.Enabled.Value = false;
            base.Dispose(isDisposing);
        }

        [Test]
        public void TestSingleHit()
        {
            AddStep("prepare", prepareSession);
            int before = 0;
            AddStep("snapshot count", () => before = latency.RecordCount);
            AddStep("single hit", fireSingle);
            AddUntilStep("record emitted", () => latency.RecordCount > before);
            AddAssert("has software playback stamp", () => sessionRecords.Exists(r => r.PlaybackTime > 0));
        }

        [Test]
        public void TestRapidHits()
        {
            AddStep("prepare", prepareSession);

            for (int i = 0; i < 8; i++)
            {
                int before = 0;
                int index = i;
                AddStep($"snapshot {index}", () => before = latency.RecordCount);
                AddStep($"rapid hit {index}", () => fireHit(index));
                AddUntilStep($"recorded {index}", () => latency.RecordCount > before);
            }

            AddAssert("got multiple records", () => sessionRecords.Count >= 5);
        }

        [Test]
        public void TestSimultaneousChord()
        {
            AddStep("prepare", prepareSession);
            int before = 0;
            AddStep("snapshot count", () => before = latency.RecordCount);
            AddStep("chord (3 keys)", fireChord);
            // Single-slot analyzer keeps the last armed key; still expect ≥1 completed measurement.
            AddUntilStep("at least one record", () => latency.RecordCount > before);
            AddAssert("session saw a hit", () => sessionRecords.Count >= 1);
        }

        private void prepareSession()
        {
            Assert.That(sample, Is.Not.Null, "Test sample long.mp3 / sample-track.mp3 missing");

            latency.ClearStatistics();
            sessionRecords.Clear();
            hitsArmed = 0;

            // Keep automated runs snappy when loopback is unavailable.
            latency.SetAcousticAwaitTimeoutMs(400);
            latency.SetAcousticThreshold(0.01f);

            bindOutputDevice();
            latency.Enabled.Value = true;
            refreshStatus();
        }

        private void resetSession()
        {
            prepareSession();
            lastHitText.Text = "last: —";
            Logger.Log("[EzOsuLatency][TestScene] stats reset", name: log_name, level: LogLevel.Debug);
        }

        private void bindOutputDevice()
        {
            try
            {
                int device = Bass.CurrentDevice;
                if (device >= 0 && Bass.GetDeviceInfo(device, out var info))
                    latency.NotifyOutputDeviceChanged(info.Driver);
                else
                    latency.NotifyOutputDeviceChanged(null);
            }
            catch (Exception ex)
            {
                Logger.Log($"[EzOsuLatency][TestScene] bind output failed: {ex.Message}", name: log_name, level: LogLevel.Debug);
            }
        }

        private void fireSingle() => fireHit(0);

        private void fireRapid()
        {
            // Burst of sequential hits with tiny gaps so each slot can complete (or soft-timeout).
            for (int i = 0; i < 8; i++)
            {
                int key = i;
                Scheduler.AddDelayed(() => fireHit(key), i * 450);
            }
        }

        private void fireChord()
        {
            // Three near-simultaneous arms + plays (analyzer single-slot: last key wins for acoustic pairing).
            fireHit(10);
            fireHit(11);
            fireHit(12);
        }

        private void fireHit(object key)
        {
            if (sample == null)
                return;

            hitsArmed++;
            latency.RecordInputEvent(key);
            sample.Play();
            refreshStatus();
        }

        private void onMeasurement(EzLatencyRecord record)
        {
            sessionRecords.Add(record);

            double soft = record.PlaybackTime > 0 && record.InputTime > 0
                ? record.PlaybackTime - record.InputTime
                : record.MeasuredMs;

            string acoustic = record.LatencyDifference > 0 && record.HardwareData.IsValid
                ? $"{record.LatencyDifference:F2}ms"
                : "n/a";

            string line = $"soft={soft:F2}ms acoustic={acoustic} note={record.Note} key={record.InputData.KeyValue}";
            lastHitText.Text = $"last: {line}";
            Logger.Log($"[EzOsuLatency][TestScene] {line}", name: log_name, level: LogLevel.Debug);
            refreshStatus();
        }

        private void refreshStatus()
        {
            statusText?.Text = $"enabled={latency.Enabled.Value} probe={(latency.AcousticProbeRunning ? "on" : "off")} "
                               + $"records={latency.RecordCount} armedHits={hitsArmed} session={sessionRecords.Count}";
        }

        private static BasicButton createActionButton(string text, Action action) => new BasicButton
        {
            Size = new Vector2(140, 36),
            Text = text,
            BackgroundColour = new Color4(0.2f, 0.45f, 0.75f, 1f),
            Action = action,
        };
    }
}
