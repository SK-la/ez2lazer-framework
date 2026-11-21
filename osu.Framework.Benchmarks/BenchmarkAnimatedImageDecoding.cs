// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.IO;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;

namespace osu.Framework.Benchmarks
{
    internal static class BenchmarkAnimatedImageDecoding
    {
        private const int warmup_duration_ms = 250;
        private const int samples_per_pass = 500;

        public static void Run()
        {
            LogLevel previousLogLevel = Logger.Level;
            Logger.Level = LogLevel.Error;

            try
            {
                runBenchmarks();
            }
            finally
            {
                Logger.Level = previousLogLevel;
            }
        }

        private static void runBenchmarks()
        {
            using var resources = new DllResourceStore(typeof(osu.Framework.Tests.Program).Assembly);
            using var source = resources.GetStream("Resources/Textures/sample-flame.gif")
                               ?? throw new FileNotFoundException("GIF decoding benchmark source was not found.");
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            byte[] encoded = buffer.ToArray();

            Console.WriteLine("Input,Frames,Route,P50ms,P90ms,P99ms");
            foreach (int maxFrames in new[] { 1, 2, 5 })
                run("sample-flame.gif", maxFrames, encoded);
        }

        private static void run(string inputName, int maxFrames, byte[] encoded)
        {
            var directSamples = new Samples(samples_per_pass * 2);
            var productionSamples = new Samples(samples_per_pass * 2);

            warmup(encoded, maxFrames, direct: true);
            warmup(encoded, maxFrames, direct: false);

            for (int pass = 0; pass < 2; pass++)
            {
                for (int operation = 0; operation < 2; operation++)
                {
                    bool direct = pass == 0 ? operation == 0 : operation != 0;
                    Samples samples = direct ? directSamples : productionSamples;

                    for (int sample = 0; sample < samples_per_pass; sample++)
                        measure(encoded, maxFrames, direct, samples);
                }
            }

            writeResult(inputName, maxFrames, "Direct SKCodec", directSamples);
            writeResult(inputName, maxFrames, "AnimatedImageLoader.LoadFromStream", productionSamples);
        }

        private static void warmup(byte[] encoded, int maxFrames, bool direct)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < warmup_duration_ms)
                decode(encoded, maxFrames, direct);
        }

        private static void measure(byte[] encoded, int maxFrames, bool direct, Samples samples)
        {
            long start = Stopwatch.GetTimestamp();
            decode(encoded, maxFrames, direct);
            int index = samples.Count++;
            samples.ElapsedMilliseconds[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        private static void decode(byte[] encoded, int maxFrames, bool direct)
        {
            using var stream = new MemoryStream(encoded, writable: false);
            AnimatedImageFrameLoadResult? result = direct
                ? SkiaAnimatedImageDecoder.TryDecode(stream, maxFrames)
                : AnimatedImageLoader.LoadFromStream(stream, maxFrames);

            using (result)
            {
                if (result == null)
                    throw new InvalidDataException("Skia could not decode the GIF benchmark input.");
            }
        }

        private static void writeResult(string inputName, int maxFrames, string route, Samples samples)
        {
            Array.Sort(samples.ElapsedMilliseconds);
            Console.WriteLine(FormattableString.Invariant(
                $"{inputName},{maxFrames},{route},{median(samples.ElapsedMilliseconds):F3},{percentile(samples.ElapsedMilliseconds, 0.9):F3},{percentile(samples.ElapsedMilliseconds, 0.99):F3}"));
        }

        private static double median(double[] values) => (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;

        private static double percentile(double[] values, double percentile)
            => values[(int)Math.Ceiling(values.Length * percentile) - 1];

        private sealed class Samples
        {
            public double[] ElapsedMilliseconds { get; }
            public int Count { get; set; }

            public Samples(int count)
            {
                ElapsedMilliseconds = new double[count];
            }
        }
    }
}
