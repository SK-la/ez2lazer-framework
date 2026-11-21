// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;

namespace osu.Framework.Benchmarks
{
    internal static class BenchmarkImageDecoding
    {
        private const int warmup_duration_ms = 250;
        private const int samples_per_pass = 500;

        private static readonly InputCase[] input_cases =
        {
            new InputCase("PNG-512x512", "png-512.bin"),
            new InputCase("PNG-1024x576", "png-1024.bin"),
            new InputCase("JPEG-1024x576", "jpeg-1024.bin"),
            new InputCase("BMP-1024x576", "bmp-1024.bin"),
            new InputCase("WebP-1024x576", "webp-1024.bin"),
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
            Console.WriteLine(
                $"Runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; OS={Environment.OSVersion}; CPUs={Environment.ProcessorCount}; samples={samples_per_pass * 2}");
            Console.WriteLine("Input,Bytes,Route,P50ms,P90ms,P99ms,AllocatedP50KiB,AllocatedP90KiB,AllocatedP99KiB");

            using var resources = new DllResourceStore(typeof(osu.Framework.Tests.Program).Assembly);

            foreach (InputCase input in input_cases)
            {
                using var inputStream = resources.GetStream($"Resources/Textures/Decoding/{input.EncodedFile}")
                                        ?? throw new FileNotFoundException($"Image decoding fixture '{input.EncodedFile}' was not found.");
                using var encodedBuffer = new MemoryStream();
                inputStream.CopyTo(encodedBuffer);
                run(input.Name, encodedBuffer.ToArray());
            }
        }

        private static void run(string inputName, byte[] encoded)
        {
            using var store = new TextureLoaderStore(new ByteArrayResourceStore(encoded));
            var directSamples = new Samples(samples_per_pass * 2);
            var productionSamples = new Samples(samples_per_pass * 2);

            warmup(encoded, store, direct: true);
            warmup(encoded, store, direct: false);

            for (int pass = 0; pass < 2; pass++)
            {
                for (int operation = 0; operation < 2; operation++)
                {
                    bool direct = pass == 0 ? operation == 0 : operation != 0;
                    Samples samples = direct ? directSamples : productionSamples;

                    for (int sample = 0; sample < samples_per_pass; sample++)
                        measure(encoded, store, direct, samples);
                }
            }

            writeResult(inputName, encoded.Length, "Direct Skia codec", directSamples);
            writeResult(inputName, encoded.Length, "TextureLoaderStore.Get", productionSamples);
        }

        private static void warmup(byte[] encoded, TextureLoaderStore store, bool direct)
        {
            var timer = Stopwatch.StartNew();

            while (timer.ElapsedMilliseconds < warmup_duration_ms)
                decode(encoded, store, direct);
        }

        private static void measure(byte[] encoded, TextureLoaderStore store, bool direct, Samples samples)
        {
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            decode(encoded, store, direct);

            int index = samples.Count++;
            samples.ElapsedMilliseconds[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            samples.AllocatedKiB[index] = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / 1024.0;
        }

        private static void decode(byte[] encoded, TextureLoaderStore store, bool direct)
        {
            if (direct)
            {
                using var stream = new MemoryStream(encoded, writable: false);
                if (!SkiaStaticImageDecoder.TryDecode(stream, out RgbaImage? image))
                    throw new InvalidDataException("Skia could not decode an image benchmark fixture.");

                using var upload = new TextureUpload(image);
                return;
            }

            using var productionUpload = store.Get("image");
            if (productionUpload == null)
                throw new InvalidDataException("The production texture loader could not decode an image benchmark fixture.");
        }

        private static void writeResult(string inputName, int encodedBytes, string route, Samples samples)
        {
            Array.Sort(samples.ElapsedMilliseconds);
            Array.Sort(samples.AllocatedKiB);
            Console.WriteLine(FormattableString.Invariant(
                $"{inputName},{encodedBytes},{route},{median(samples.ElapsedMilliseconds):F3},{percentile(samples.ElapsedMilliseconds, 0.9):F3},{percentile(samples.ElapsedMilliseconds, 0.99):F3},{median(samples.AllocatedKiB):F1},{percentile(samples.AllocatedKiB, 0.9):F1},{percentile(samples.AllocatedKiB, 0.99):F1}"));
        }

        private static double median(double[] values) => (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;

        private static double percentile(double[] values, double percentile)
            => values[(int)Math.Ceiling(values.Length * percentile) - 1];

        private readonly record struct InputCase(string Name, string EncodedFile);

        private sealed class Samples
        {
            public double[] ElapsedMilliseconds { get; }
            public double[] AllocatedKiB { get; }
            public int Count { get; set; }

            public Samples(int count)
            {
                ElapsedMilliseconds = new double[count];
                AllocatedKiB = new double[count];
            }
        }

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
