// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
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

            if (mode == "exclusive")
            {
                TestContext.WriteLine("loopback: skipped (exclusive owns the endpoint)");
            }

            using var loopback = mode == "exclusive" ? null : new LoopbackListen(tryResolveDriverId(selection));
            loopback?.Arm();

            track.Restart();

            bool progressing = waitUntil(() => track.IsRunning && track.CurrentTime > 0, 2000, null);
            Thread.Sleep(playback_ms);
            bool heard = loopback?.Heard ?? false;
            float peak = loopback?.PeakRms ?? 0;

            track.Stop();
            loopback?.Stop();
            Thread.Sleep(100);

            if (!progressing)
            {
                failures.Add($"{selection} ({mode}): track did not start (IsRunning={track.IsRunning}, t={track.CurrentTime:0.000})");
                return;
            }

            if (loopback == null)
                return;

            if (!loopback.Started)
                TestContext.WriteLine($"loopback: unavailable ({loopback.StartError ?? "no capture"})");
            else if (!heard)
                TestContext.WriteLine($"loopback: below threshold (peakRms={peak:0.0000}, recorded, not a failure)");
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

        private static bool waitUntil(Func<bool> condition, int timeoutMs, string? failMessage)
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
            private volatile bool heard;

            public LoopbackListen(string? bassDriverId)
            {
                probe = new AcousticClosedLoopProbe(
                    () => Environment.TickCount64,
                    () => { },
                    (_, _, _, _) => heard = true)
                {
                    Threshold = 0.01f
                };

                Started = probe.TryStart(bassDriverId);
                StartError = Started ? null : "TryStart returned false";
            }

            public bool Started { get; }
            public string? StartError { get; }
            public bool Heard => heard;
            public float PeakRms => probe.PeakRms;

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
