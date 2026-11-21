// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using ImageSharpImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
using ImageSharpRgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace osu.Framework.Benchmarks
{
    internal static class BenchmarkImageDecoding
    {
        private const int warmup_duration_ms = 250;
        private const int samples_per_pass = 500;

        private static readonly InputCase[] input_cases =
        {
            new InputCase("PNG-512x512", 512, 512, new PngEncoder()),
            new InputCase("PNG-1024x576", 1024, 576, new PngEncoder()),
            new InputCase("JPEG-1024x576", 1024, 576, new JpegEncoder { Quality = 90 }),
            new InputCase("BMP-1024x576", 1024, 576, new BmpEncoder()),
            new InputCase("WebP-1024x576", 1024, 576, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless }),
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
            using var sourceStream = resources.GetStream("Resources/Textures/h264-hd-screenshot.png")
                                     ?? throw new FileNotFoundException("Image decoding benchmark source was not found.");
            using var source = Image.Load<ImageSharpRgba32>(sourceStream);

            foreach (InputCase inputCase in input_cases)
            {
                byte[] encoded = encode(source, inputCase);
                run(inputCase.Name, encoded);
            }
        }

        private static byte[] encode(ImageSharpImage source, InputCase inputCase)
        {
            using var resized = source.Clone();
            resized.Mutate(context => context.Resize(inputCase.Width, inputCase.Height));

            using var stream = new MemoryStream();
            resized.Save(stream, inputCase.Encoder);
            return stream.ToArray();
        }

        private static void run(string inputName, byte[] encoded)
        {
            using var legacyStore = new LegacyTextureLoaderStore(encoded);
            using var productionStore = new TextureLoaderStore(new ByteArrayResourceStore(encoded));
            var results = new[]
            {
                new Samples(samples_per_pass * 2),
                new Samples(samples_per_pass * 2),
            };

            warmup(legacyStore);
            warmup(productionStore);

            for (int pass = 0; pass < 2; pass++)
            {
                for (int operation = 0; operation < 2; operation++)
                {
                    int route = pass == 0 ? operation : 1 - operation;
                    TextureLoaderStore store = route == 0 ? legacyStore : productionStore;

                    for (int sample = 0; sample < samples_per_pass; sample++)
                        measure(store, results[route]);
                }
            }

            writeResult(inputName, encoded.Length, "Legacy ImageSharp/StbiSharp", results[0]);
            writeResult(inputName, encoded.Length, "TextureLoaderStore.Get", results[1]);
        }

        private static void decode(TextureLoaderStore store)
        {
            using var upload = store.Get("image");
            if (upload == null)
                throw new InvalidDataException("The production texture loader could not decode a benchmark image.");
        }

        private static void warmup(TextureLoaderStore store)
        {
            var timer = Stopwatch.StartNew();

            while (timer.ElapsedMilliseconds < warmup_duration_ms)
                decode(store);
        }

        private static void measure(TextureLoaderStore store, Samples samples)
        {
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            decode(store);

            int index = samples.Count++;
            samples.ElapsedMilliseconds[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            samples.AllocatedKiB[index] = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / 1024.0;
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

        private readonly record struct InputCase(string Name, int Width, int Height, IImageEncoder Encoder);

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

        private sealed class LegacyTextureLoaderStore : TextureLoaderStore
        {
            public LegacyTextureLoaderStore(byte[] data)
                : base(new ByteArrayResourceStore(data))
            {
            }

            protected override TextureUpload LoadTextureUpload(Stream stream)
                => new TextureUpload(ImageFromStream<ImageSharpRgba32>(stream));
        }
    }
}
