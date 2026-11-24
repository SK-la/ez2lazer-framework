// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.InteropServices;

namespace osu.Framework.Graphics.Imaging
{
    /// <summary>
    /// Serialises Svg.Skia rasterisation against framework FreeType use on Linux.
    /// </summary>
    internal static class SvgImageDecodeScheduler
    {
        internal static readonly object SYNC_ROOT = new object();

        public static bool UseIsolatedSerialisation => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        public static void Run(Action action)
        {
            if (!UseIsolatedSerialisation)
            {
                action();
                return;
            }

            lock (SYNC_ROOT)
                action();
        }
    }
}
