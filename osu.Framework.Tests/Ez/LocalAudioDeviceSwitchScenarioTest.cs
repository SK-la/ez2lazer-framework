// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using JetBrains.Annotations;
using ManagedBass;
using NUnit.Framework;
using osu.Framework.Audio;
using osu.Framework.Audio.EzLatency;
using osu.Framework.IO.Stores;
using osu.Framework.Threading;

namespace osu.Framework.Tests.Ez
{
    [TestFixture]
    [Explicit("Plays through local audio devices.")]
    [SupportedOSPlatform("windows")]
    public class LocalAudioDeviceSwitchScenarioTest
    {
        private const string exclusive_suffix = " (WASAPI Exclusive)";
        private const int switch_timeout_ms = 8000;
        private const int playback_ms = 500;
        private const int meter_tick_ms = 100;
        private const int meter_width = 16;
        private const float silence_rms = 0.0001f;
        private const float heard_rms = 0.01f;

        [Test]
        public void SharedThenExclusivePerPlaybackDevice()
        {
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                Assert.Ignore("Windows output scenario");

            AudioThread.AllowRealWindowsAudioOutput = true;
            AudioThread.PreloadBass();

            var audioThread = new AudioThread();
            var store = new NamespacedResourceStore<byte[]>(new DllResourceStore(@"osu.Framework.Tests.dll"), @"Resources");
            var failures = new List<string>();
            var switchFailures = new List<string>();

            try
            {
                using var audioManager = new AudioManager(audioThread, store, store, null);
                audioThread.Start();
                audioManager.Volume.Value = 1;

                audioManager.OnAudioOutputSwitchFailed += info => switchFailures.Add($"{info.Selection}: {info.Message}");

                waitUntil(() => audioManager.AudioDeviceNames.Any(), 5000, "No playback devices enumerated");

                var devices = audioManager.AudioDeviceNames
                                          .Where(name => !name.EndsWith(" (ASIO)", StringComparison.Ordinal)
                                                         && !name.EndsWith(exclusive_suffix, StringComparison.Ordinal))
                                          .Distinct()
                                          .ToList();

                if (devices.Count == 0)
                    Assert.Ignore("No WASAPI playback devices");

                foreach (string device in devices)
                {
                    runMode(audioManager, device, "shared", failures, switchFailures);
                    runMode(audioManager, device + exclusive_suffix, "exclusive", failures, switchFailures);
                    runMode(audioManager, device, "shared", failures, switchFailures);
                }
            }
            finally
            {
                audioThread.Exit();
                AudioThread.AllowRealWindowsAudioOutput = false;
            }

            Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
        }

        private static void runMode(AudioManager audioManager, string selection, string mode, List<string> failures, List<string> switchFailures)
        {
            switchFailures.Clear();
            TestContext.WriteLine($"--- {selection} ({mode}) ---");

            int runtimeChanges = 0;
            void onRuntimeChanged() => Interlocked.Increment(ref runtimeChanges);
            audioManager.OnWindowsOutputRuntimeChanged += onRuntimeChanged;

            try
            {
                audioManager.AudioDevice.Value = selection;

                if (!waitUntil(() => runtimeChanges > 0 && (outputIsRunning(audioManager) || switchFailures.Count > 0), switch_timeout_ms, null)
                    || switchFailures.Count > 0
                    || !outputIsRunning(audioManager)
                    || audioManager.AudioDevice.Value != selection)
                {
                    string status = audioManager.GetOutputDeviceStatusNote();
                    string extra = switchFailures.Count > 0 ? " | " + string.Join("; ", switchFailures) : string.Empty;
                    failures.Add($"{selection} ({mode}): output not running. {status}{extra}");
                    TestContext.WriteLine($"FAIL status: {status}");
                    return;
                }
            }
            finally
            {
                audioManager.OnWindowsOutputRuntimeChanged -= onRuntimeChanged;
            }

            TestContext.WriteLine(audioManager.GetOutputDeviceStatusNote().ReplaceLineEndings(" | "));

            var track = audioManager.Tracks.Get("Tracks.sample-track.mp3");

            if (track == null)
            {
                failures.Add($"{selection} ({mode}): test track missing");
                return;
            }

            using var loopback = mode == "exclusive" ? null : new LoopbackListen(tryResolveDriverId(selection));

            if (mode == "exclusive")
                TestContext.WriteLine("loopback: skipped (exclusive owns the endpoint)");
            else if (loopback is { Started: false })
                TestContext.WriteLine($"loopback: unavailable ({loopback.StartError ?? "no capture"})");

            loopback?.Arm();
            track.Restart();

            bool progressing = waitUntil(() => track.IsRunning && track.CurrentTime > 0, 2000, null);

            if (loopback is { Started: true })
                watchLoopbackMeter(loopback, playback_ms);
            else
                Thread.Sleep(playback_ms);

            float peak = loopback?.PeakRms ?? 0;

            track.Stop();
            loopback?.Stop();
            Thread.Sleep(100);

            if (!progressing)
            {
                failures.Add($"{selection} ({mode}): track did not start (IsRunning={track.IsRunning}, t={track.CurrentTime:0.000})");
                return;
            }

            if (loopback is not { Started: true })
                return;

            TestContext.WriteLine($"loopback meter: [{formatMeter(peak)}] peakRms={peak:0.0000}");

            if (peak < silence_rms)
                failures.Add($"{selection} ({mode}): no loopback sound (peakRms={peak:0.0000})");
            else if (peak < heard_rms)
                TestContext.WriteLine($"loopback: below threshold (peakRms={peak:0.0000})");
            else
                TestContext.WriteLine($"loopback: heard peakRms={peak:0.0000}");
        }

        private static bool outputIsRunning(AudioManager audioManager)
        {
            string note = audioManager.GetOutputDeviceStatusNote();
            return note.Contains("Output: running", StringComparison.Ordinal);
        }

        private static string? tryResolveDriverId(string selection)
        {
            string name = selection.EndsWith(exclusive_suffix, StringComparison.Ordinal)
                ? selection[..^exclusive_suffix.Length]
                : selection;

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

        private static void watchLoopbackMeter(LoopbackListen loopback, int durationMs)
        {
            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < durationMs)
            {
                float curr = loopback.CurrentRms;
                TestContext.WriteLine($"loopback meter: [{formatMeter(curr)}] curr={curr:0.0000} peak={loopback.PeakRms:0.0000}");
                Thread.Sleep(meter_tick_ms);
            }
        }

        private static string formatMeter(float rms)
        {
            float shown = Math.Clamp(rms, 0f, 1f);
            int filled = Math.Clamp((int)Math.Round(Math.Sqrt(shown) * meter_width), 0, meter_width);
            return new string('#', filled) + new string('-', meter_width - filled);
        }

        private static bool waitUntil([InstantHandle] Func<bool> condition, int timeoutMs, string? failMessage)
        {
            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition())
                    return true;

                Thread.Sleep(50);
            }

            if (failMessage != null)
                Assert.Fail(failMessage);

            return false;
        }

        [SupportedOSPlatform("windows")]
        private sealed class LoopbackListen : IDisposable
        {
            private readonly AcousticClosedLoopProbe probe;

            public LoopbackListen(string? bassDriverId)
            {
                probe = new AcousticClosedLoopProbe(
                    () => Environment.TickCount64,
                    () => { },
                    (_, _, _, _) => { })
                {
                    Threshold = heard_rms
                };

                Started = probe.TryStart(bassDriverId);
                StartError = Started ? null : "TryStart returned false";
            }

            public bool Started { get; }
            public string? StartError { get; }
            public float PeakRms => probe.PeakRms;
            public float CurrentRms => probe.CurrentRms;

            public void Arm()
            {
                if (Started)
                    probe.Arm(Environment.TickCount64);
            }

            public void Stop() => probe.Stop();

            public void Dispose() => probe.Dispose();
        }
    }
}
