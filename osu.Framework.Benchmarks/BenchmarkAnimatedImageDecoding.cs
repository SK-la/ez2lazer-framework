// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;

namespace osu.Framework.Benchmarks
{
    internal static class BenchmarkAnimatedImageDecoding
    {
        internal static void Run(IResourceStore<byte[]> resources)
        {
            byte[] flame = resources.Get("Resources/Textures/sample-flame.gif")
                           ?? throw new FileNotFoundException("The short GIF benchmark fixture was not found.");

            foreach (int maxFrames in new[] { 1, 2, 5 })
                measure("GIF-flame", flame, maxFrames);

            // Each partial frame retains the pixels decoded for the preceding frame.
            byte[] dependent = resources.Get("Resources/ImageDecoding/dependent-120.gif")
                               ?? throw new FileNotFoundException("The long GIF benchmark fixture was not found.");
            measure("GIF-dependent", dependent, 30);
            measure("GIF-dependent", dependent, 60);
            measure("GIF-dependent", dependent, 120);
        }

        private static void measure(string inputName, byte[] encoded, int frameCount)
        {
            BenchmarkImageDecoding.Measure(inputName, encoded.Length, frameCount, () =>
            {
                using var stream = new MemoryStream(encoded, writable: false);
                using var result = AnimatedImageLoader.LoadFromStream(stream, frameCount);
                if (result?.Frames.Count != frameCount)
                    throw new InvalidDataException($"Expected {frameCount} frames from '{inputName}', received {result?.Frames.Count ?? 0}.");
            });
        }
    }
}
