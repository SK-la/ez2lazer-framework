// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace osu.Framework.Benchmarks
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--sprite-text-cjk")
            {
                BenchmarkSpriteTextRunner.Run(args[1..]);
                return;
            }

            if (args.Length > 0 && args[0] == "--sprite-text-roboto")
            {
                BenchmarkSpriteTextRunner.RunRoboto(args[1..]);
                return;
            }

            if (args.Length > 0 && args[0] == "--sprite-text-roboto-metrics")
            {
                BenchmarkSpriteTextRunner.ReportRobotoMetrics(args[1..]);
                return;
            }

            if (Array.IndexOf(args, "--glyph-texture-pool") >= 0)
            {
                BenchmarkGlyphTexturePool.Run();
                return;
            }

            if (Array.IndexOf(args, "--image-decoding") >= 0)
            {
                BenchmarkImageDecoding.Run();
                return;
            }

            BenchmarkSwitcher
                .FromAssembly(typeof(Program).Assembly)
                .Run(args, DefaultConfig.Instance.WithOption(ConfigOptions.DisableOptimizationsValidator, true));
        }
    }
}
