// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;

namespace osu.Framework.Benchmarks
{
    internal static class BenchmarkImageDecoding
    {
        private const int warmup_duration_ms = 250;
        private const int sample_count = 1000;

        private static readonly InputCase[] input_cases =
        {
            new InputCase("PNG-512x512", "Resources/Textures/Decoding/png-512.bin"),
            new InputCase("PNG-1024x576", "Resources/Textures/Decoding/png-1024.bin"),
            new InputCase("JPEG-1024x576", "Resources/Textures/Decoding/jpeg-1024.bin"),
            new InputCase("BMP-1024x576", "Resources/Textures/Decoding/bmp-1024.bin"),
            new InputCase("WebP-1024x576", "Resources/Textures/Decoding/webp-1024.bin"),
            new InputCase("GIF-preview", "Resources/Textures/sample-flame.gif"),
            new InputCase("TGA-128x128", "Resources/ImageDecoding/rle-128.tga"),
        };

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
            Console.WriteLine($"Runtime={RuntimeInformation.FrameworkDescription}; OS={Environment.OSVersion}; CPUs={Environment.ProcessorCount}; samples={sample_count}");
            Console.WriteLine("Input,Bytes,Frames,P50ms,P90ms,P99ms,AllocatedP50KiB,AllocatedP90KiB,AllocatedP99KiB");

            using var resources = new ResourceStore<byte[]>(new IResourceStore<byte[]>[]
            {
                new DllResourceStore(typeof(osu.Framework.Tests.Program).Assembly),
                new DllResourceStore(typeof(Program).Assembly),
            });

            foreach (InputCase input in input_cases)
            {
                byte[] encoded = resources.Get(input.EncodedFile)
                                 ?? throw new FileNotFoundException($"Image decoding fixture '{input.EncodedFile}' was not found.");
                using var store = new TextureLoaderStore(new ByteArrayResourceStore(encoded));

                Measure(input.Name, encoded.Length, 1, () =>
                {
                    using var upload = store.Get("image") ?? throw new InvalidDataException($"Could not decode '{input.EncodedFile}'.");
                });
            }

            BenchmarkAnimatedImageDecoding.Run(resources);
        }

        internal static void Measure(string inputName, int encodedBytes, int frameCount, [InstantHandle] Action decode)
        {
            var timer = Stopwatch.StartNew();

            while (timer.ElapsedMilliseconds < warmup_duration_ms)
                decode();

            double[] elapsedMilliseconds = new double[sample_count];
            double[] allocatedKiB = new double[sample_count];

            for (int i = 0; i < sample_count; i++)
            {
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                decode();
                elapsedMilliseconds[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                allocatedKiB[i] = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / 1024.0;
            }

            Array.Sort(elapsedMilliseconds);
            Array.Sort(allocatedKiB);
            Console.WriteLine(FormattableString.Invariant(
                $"{inputName},{encodedBytes},{frameCount},{median(elapsedMilliseconds):F3},{percentile(elapsedMilliseconds, 0.9):F3},{percentile(elapsedMilliseconds, 0.99):F3},{median(allocatedKiB):F1},{percentile(allocatedKiB, 0.9):F1},{percentile(allocatedKiB, 0.99):F1}"));
        }

        private static double median(double[] values) => (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;

        private static double percentile(double[] values, double percentile)
            => values[(int)Math.Ceiling(values.Length * percentile) - 1];

        private readonly record struct InputCase(string Name, string EncodedFile);

        private sealed class ByteArrayResourceStore : IResourceStore<byte[]>
        {
            private readonly byte[] data;

            public ByteArrayResourceStore(byte[] data)
            {
                this.data = data;
            }

            public byte[] Get(string name) => data;
            public Task<byte[]> GetAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(data);
            public Stream GetStream(string name) => new MemoryStream(data, writable: false);
            public IEnumerable<string> GetAvailableResources() => new[] { "image" };

            public void Dispose()
            {
            }
        }
    }
}
