// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace osu.Framework.Audio.EzLatency
{
#nullable disable

    public class EzLatencyStatistics
    {
        public bool HasData => RecordCount > 0;
        public int RecordCount { get; set; }

        public double AvgInputToJudge { get; set; }
        public double MinInputToJudge { get; set; }
        public double MaxInputToJudge { get; set; }

        public double AvgInputToPlayback { get; set; }
        public double MinInputToPlayback { get; set; }
        public double MaxInputToPlayback { get; set; }

        public double AvgPlaybackToJudge { get; set; }
        public double MinPlaybackToJudge { get; set; }
        public double MaxPlaybackToJudge { get; set; }

        public double AvgHardwareLatency { get; set; }

        /// <summary>Acoustic loopback round-trip samples (Input → level threshold).</summary>
        public int AcousticRecordCount { get; set; }

        public double AvgAcousticRoundtrip { get; set; }
        public double MinAcousticRoundtrip { get; set; }
        public double MaxAcousticRoundtrip { get; set; }
    }

    internal class EzLatencyCollector
    {
        private readonly List<EzLatencyRecord> records = new List<EzLatencyRecord>();
        private readonly Lock lockObject = new Lock();

        public void AddRecord(EzLatencyRecord record)
        {
            // Accept records if input data is valid (best-effort), even if hardware data isn't available.
            if (!record.InputData.IsValid)
                return;

            lock (lockObject)
            {
                records.Add(record);
            }
        }

        public void Clear()
        {
            lock (lockObject)
            {
                records.Clear();
            }
        }

        public EzLatencyStatistics GetStatistics()
        {
            lock (lockObject)
            {
                if (records.Count == 0)
                    return new EzLatencyStatistics { RecordCount = 0 };

                // Filter out invalid or incomplete differences (<= 0) to avoid extreme/garbage values.
                var inputToJudge = records
                                   .Where(r => r.InputTime > 0 && r.JudgeTime > 0)
                                   .Select(r => r.JudgeTime - r.InputTime)
                                   .Where(d => Math.Abs(d) <= 1000) // sanity cap: ignore absurdly large diffs (>1000ms)
                                   .ToList();

                var inputToPlayback = records
                                      .Where(r => r.InputTime > 0 && r.PlaybackTime > 0)
                                      .Select(r => r.PlaybackTime - r.InputTime)
                                      .Where(d => Math.Abs(d) <= 1000)
                                      .ToList();

                var playbackToJudge = records
                                      .Where(r => r.PlaybackTime > 0 && r.JudgeTime > 0)
                                      .Select(r => r.JudgeTime - r.PlaybackTime)
                                      .Where(d => Math.Abs(d) <= 1000)
                                      .ToList();

                // Non-acoustic hardware stamps (legacy path); acoustic uses LatencyDifference instead.
                var hardwareLatency = records
                                      .Where(r => r.Note != EzLatencyAnalyzer.NOTE_ACOUSTIC_LOOPBACK)
                                      .Select(r => r.OutputHardwareTime)
                                      .Where(h => h > 0)
                                      .ToList();

                var acoustic = records
                               .Where(r => r.Note == EzLatencyAnalyzer.NOTE_ACOUSTIC_LOOPBACK ||
                                           (r.Note != null && r.Note.StartsWith("acoustic-", StringComparison.Ordinal)))
                               .Select(r => r.LatencyDifference > 0 ? r.LatencyDifference : r.OutputHardwareTime)
                               .Where(d => d > 0 && d <= 1000)
                               .ToList();

                double avgInputToJudge = inputToJudge.Count > 0 ? inputToJudge.Average() : 0;
                double avgInputToPlayback = inputToPlayback.Count > 0 ? inputToPlayback.Average() : 0;
                double avgPlaybackToJudge = playbackToJudge.Count > 0 ? playbackToJudge.Average() : 0;

                return new EzLatencyStatistics
                {
                    RecordCount = records.Count,
                    AvgInputToJudge = avgInputToJudge,
                    MinInputToJudge = inputToJudge.Count > 0 ? inputToJudge.Min() : 0,
                    MaxInputToJudge = inputToJudge.Count > 0 ? inputToJudge.Max() : 0,
                    AvgInputToPlayback = avgInputToPlayback,
                    MinInputToPlayback = inputToPlayback.Count > 0 ? inputToPlayback.Min() : 0,
                    MaxInputToPlayback = inputToPlayback.Count > 0 ? inputToPlayback.Max() : 0,
                    AvgPlaybackToJudge = avgPlaybackToJudge,
                    MinPlaybackToJudge = playbackToJudge.Count > 0 ? playbackToJudge.Min() : 0,
                    MaxPlaybackToJudge = playbackToJudge.Count > 0 ? playbackToJudge.Max() : 0,
                    AvgHardwareLatency = hardwareLatency.Count > 0 ? hardwareLatency.Average() : 0,
                    AcousticRecordCount = acoustic.Count,
                    AvgAcousticRoundtrip = acoustic.Count > 0 ? acoustic.Average() : 0,
                    MinAcousticRoundtrip = acoustic.Count > 0 ? acoustic.Min() : 0,
                    MaxAcousticRoundtrip = acoustic.Count > 0 ? acoustic.Max() : 0
                };
            }
        }

        public int Count
        {
            get
            {
                lock (lockObject)
                {
                    return records.Count;
                }
            }
        }
    }
}
