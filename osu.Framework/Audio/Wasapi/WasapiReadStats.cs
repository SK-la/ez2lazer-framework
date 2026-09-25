// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Threading;

namespace osu.Framework.Audio.Wasapi
{
    /// <summary>
    /// Wall-clock statistics of the NAudio render thread's pulls into <see cref="BassMixerWaveProvider"/>.
    /// </summary>
    /// <remarks>
    /// The mixer position can show <em>that</em> the source stopped for a period, but not why: NAudio asks for
    /// every free frame rather than a fixed period, so a late wake-up and a slow producer look identical there.
    /// The wall gap between consecutive pulls and the requested frame count separate the two.
    /// Collection is off unless <see cref="Enabled"/> is set; enabling it costs one timestamp read per pull.
    /// Writes come from the render thread, reads/resets from anywhere, hence the lock.
    /// </remarks>
    public static class WasapiReadStats
    {
        public const double GAP_BUCKET_MS = 0.5;
        public const int GAP_BUCKETS = 80;

        /// <summary>Bucket size of the requested-size histogram, in engine periods (one period is <c>sampleRate / 100</c> frames).</summary>
        public const double PERIOD_BUCKET = 0.25;

        public const int PERIOD_BUCKETS = 16;

        private static readonly Lock sync = new Lock();
        private static readonly long[] gap_buckets = new long[GAP_BUCKETS];
        private static readonly long[] period_buckets = new long[PERIOD_BUCKETS];

        private static int enabled;
        private static long reads;
        private static long gapOverflow;
        private static long periodOverflow;
        private static long totalGapTicks;
        private static long maxGapTicks;
        private static long lastTicks;
        private static long totalPeriodQ;
        private static long maxPeriodQ;

        public static bool Enabled
        {
            get => Volatile.Read(ref enabled) != 0;
            set => Volatile.Write(ref enabled, value ? 1 : 0);
        }

        /// <summary>Drops all counters and any pending gap, so the next observed pull starts a fresh window.</summary>
        public static void Reset()
        {
            lock (sync)
            {
                Array.Clear(gap_buckets, 0, gap_buckets.Length);
                Array.Clear(period_buckets, 0, period_buckets.Length);
                reads = gapOverflow = periodOverflow = totalGapTicks = maxGapTicks = lastTicks = totalPeriodQ = maxPeriodQ = 0;
            }
        }

        /// <summary>
        /// Records one pull. <paramref name="byteCount"/> is what NAudio requested, which is every free frame in
        /// the endpoint buffer at that moment (not one period) — a value at or above two periods means the thread
        /// did not wake for at least one engine period.
        /// </summary>
        public static void Observe(int byteCount, int blockAlign, int sampleRate)
        {
            if (!Enabled || byteCount <= 0 || blockAlign <= 0 || sampleRate <= 0)
                return;

            long now = Stopwatch.GetTimestamp();
            long periodQ = (long)Math.Round(byteCount / (double)blockAlign / (sampleRate / 100.0) / PERIOD_BUCKET);

            lock (sync)
            {
                if (lastTicks != 0)
                {
                    long ticks = now - lastTicks;
                    totalGapTicks += ticks;

                    if (ticks > maxGapTicks)
                        maxGapTicks = ticks;

                    int bucket = (int)Math.Floor(ticks * 1000.0 / Stopwatch.Frequency / GAP_BUCKET_MS);

                    if (bucket < 0)
                        bucket = 0;

                    if (bucket < GAP_BUCKETS)
                        gap_buckets[bucket]++;
                    else
                        gapOverflow++;
                }

                int periodBucket = (int)Math.Min(PERIOD_BUCKETS - 1, Math.Max(0, periodQ));

                if (periodQ >= PERIOD_BUCKETS)
                    periodOverflow++;
                else
                    period_buckets[periodBucket]++;

                totalPeriodQ += periodQ;

                if (periodQ > maxPeriodQ)
                    maxPeriodQ = periodQ;

                reads++;
                lastTicks = now;
            }
        }

        public static Snapshot GetSnapshot()
        {
            lock (sync)
            {
                return new Snapshot(
                    reads,
                    gap_buckets,
                    period_buckets,
                    gapOverflow,
                    periodOverflow,
                    totalGapTicks / (double)Stopwatch.Frequency * 1000.0,
                    maxGapTicks / (double)Stopwatch.Frequency * 1000.0,
                    totalPeriodQ / 4.0,
                    maxPeriodQ / 4.0);
            }
        }

        public readonly struct Snapshot
        {
            /// <summary>Pulls observed (one per render-thread wake-up).</summary>
            public readonly long Reads;

            /// <summary>Gap histogram; bucket <c>i</c> covers <c>[i, i+1) * GAP_BUCKET_MS</c>.</summary>
            public readonly long[] Gaps;

            /// <summary>Requested-size histogram in engine periods; bucket <c>i</c> covers <c>[i, i+1) * PERIOD_BUCKET</c>.</summary>
            public readonly long[] Periods;

            /// <summary>Gaps and requests that fell past the last bucket.</summary>
            public readonly long GapOverflow;

            public readonly long PeriodOverflow;

            /// <summary>Sum and peak of the observed gaps, in milliseconds.</summary>
            public readonly double TotalGapMs;

            public readonly double MaxGapMs;

            /// <summary>Sum and peak of the requested sizes, in engine periods.</summary>
            public readonly double TotalPeriods;

            public readonly double MaxPeriods;

            internal Snapshot(long reads, long[] gaps, long[] periods, long gapOverflow, long periodOverflow,
                              double totalGapMs, double maxGapMs, double totalPeriods, double maxPeriods)
            {
                Reads = reads;
                Gaps = (long[])gaps.Clone();
                Periods = (long[])periods.Clone();
                GapOverflow = gapOverflow;
                PeriodOverflow = periodOverflow;
                TotalGapMs = totalGapMs;
                MaxGapMs = maxGapMs;
                TotalPeriods = totalPeriods;
                MaxPeriods = maxPeriods;
            }
        }
    }
}
