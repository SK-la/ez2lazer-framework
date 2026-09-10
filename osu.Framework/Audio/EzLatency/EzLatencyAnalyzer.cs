// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using osu.Framework.Logging;

namespace osu.Framework.Audio.EzLatency
{
#nullable disable

    /// <summary>
    /// Records input/judge/playback timing data faithfully without filtering or discarding.
    /// Lock-free single-slot design — never blocks the input or audio thread.
    /// </summary>
    public class EzLatencyAnalyzer
    {
        private readonly Stopwatch stopwatch;
        public bool Enabled { get; set; }
        public event Action<EzLatencyRecord> OnNewRecord;

        private EzLatencyInputData currentInputData;
        private EzLatencyHardwareData currentHardwareData;
        private double recordStartTime;
        private const double timeout_ms = 5000;

        public EzLatencyAnalyzer()
        {
            stopwatch = Stopwatch.StartNew();
        }

        public void RecordInputData(double inputTime, object keyValue = null)
        {
            if (!Enabled) return;

            if (currentInputData.InputTime > 0)
            {
                // Same physical Mania key often double-fires: PassThrough KeyDown (Key enum)
                // then Column.OnPressed (column int). Only coalesce that pair / exact duplicates —
                // different columns or keys within 2ms must open a new slot (chords / 10K).
                if (inputTime - currentInputData.InputTime < 2.0 && tryCoalesceSamePhysicalPress(keyValue))
                    return;

                currentInputData = default;
                currentHardwareData = default;
            }

            currentInputData.InputTime = inputTime;
            currentInputData.KeyValue = keyValue;
            recordStartTime = stopwatch.Elapsed.TotalMilliseconds;
        }

        /// <summary>
        /// Returns true when <paramref name="keyValue"/> is the same physical press as the armed slot
        /// (Key→column upgrade, or identical id), and updates <see cref="currentInputData.KeyValue"/> if needed.
        /// </summary>
        private bool tryCoalesceSamePhysicalPress(object keyValue)
        {
            // Framework KeyDown then Mania column index for the same press.
            if (currentInputData.KeyValue is Enum && keyValue is int)
            {
                currentInputData.KeyValue = keyValue;
                return true;
            }

            // Exact duplicate (repeated KeyDown / repeated column).
            if (Equals(currentInputData.KeyValue, keyValue))
                return true;

            return false;
        }

        public void RecordJudgeData(double judgeTime)
        {
            if (!Enabled) return;

            currentInputData.JudgeTime = judgeTime;
            checkTimeout();
        }

        public void RecordPlaybackData(double playbackTime)
        {
            if (!Enabled) return;

            // Ignore stray sample/track Play() calls until an input has armed the slot.
            if (currentInputData.InputTime <= 0)
                return;

            currentInputData.PlaybackTime = playbackTime;
            tryEmitRecord();
        }

        public void RecordHardwareData(double driverTime, double outputHardwareTime, double inputHardwareTime, double latencyDifference)
        {
            if (!Enabled) return;

            currentHardwareData = new EzLatencyHardwareData
            {
                DriverTime = driverTime,
                OutputHardwareTime = outputHardwareTime,
                InputHardwareTime = inputHardwareTime,
                LatencyDifference = latencyDifference
            };

            tryEmitRecord();
        }

        private void tryEmitRecord()
        {
            if (!currentInputData.IsValid)
            {
                checkTimeout();
                return;
            }

            // Snapshot the raw data before clearing state (prevents re-entrancy without discarding).
            var inputData = currentInputData;
            var hwData = currentHardwareData;

            double measuredMs = inputData.PlaybackTime > 0
                ? inputData.PlaybackTime - inputData.InputTime
                : inputData.JudgeTime > 0
                    ? inputData.JudgeTime - inputData.InputTime
                    : 0;

            var record = new EzLatencyRecord
            {
                Timestamp = DateTimeOffset.Now,
                InputTime = inputData.InputTime,
                JudgeTime = inputData.JudgeTime,
                PlaybackTime = inputData.PlaybackTime,
                DriverTime = hwData.DriverTime,
                OutputHardwareTime = hwData.OutputHardwareTime,
                InputHardwareTime = hwData.InputHardwareTime,
                LatencyDifference = hwData.LatencyDifference,
                MeasuredMs = measuredMs,
                Note = hwData.IsValid ? "complete-latency-measurement" : "best-effort-no-hw",
                InputData = inputData,
                HardwareData = hwData
            };

            // Reset state before dispatching so re-entrant calls (from Logger or callback) start fresh.
            ClearCurrentData();

            try
            {
                OnNewRecord?.Invoke(record);
                EzLatencyService.Instance.PushRecord(record);
            }
            catch (Exception ex)
            {
                Logger.Log($"EzLatencyAnalyzer: tryEmitRecord failed: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
            }
        }

        public double GetCurrentTimestamp() => stopwatch.Elapsed.TotalMilliseconds;

        private void checkTimeout()
        {
            if (recordStartTime <= 0)
                return;

            double elapsed = stopwatch.Elapsed.TotalMilliseconds - recordStartTime;

            if (elapsed > timeout_ms)
            {
                ClearCurrentData();
            }
        }

        public void ClearCurrentData()
        {
            currentInputData = default;
            currentHardwareData = default;
            recordStartTime = 0;
        }
    }
}
