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
            if (Array.IndexOf(args, "--image-decoding") >= 0)
            {
                BenchmarkImageDecoding.Run();
                BenchmarkAnimatedImageDecoding.Run();
                return;
            }

            BenchmarkSwitcher
                .FromAssembly(typeof(Program).Assembly)
                .Run(args, DefaultConfig.Instance.WithOption(ConfigOptions.DisableOptimizationsValidator, true));
        }
    }
}
