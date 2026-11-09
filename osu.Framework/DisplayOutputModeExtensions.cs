// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Logging;
using osu.Framework.Platform;
using Veldrid;

namespace osu.Framework
{
    public static class DisplayOutputModeExtensions
    {
        public static SwapchainDisplayMode ToSwapchainDisplayMode(this DisplayOutputMode mode, GraphicsSurfaceType surfaceType, bool logFallback = true)
        {
            if (surfaceType != GraphicsSurfaceType.Direct3D11)
            {
                if (logFallback && mode != DisplayOutputMode.Standard)
                {
                    Logger.Log($"Display output mode {mode} is only supported on Direct3D 11; using Standard.", LoggingTarget.Runtime, LogLevel.Important);
                }

                return SwapchainDisplayMode.Standard8Bit;
            }

            return mode switch
            {
                DisplayOutputMode.Native10Bit => DisplayOutputPolicy.ResolveNative10BitSwapchain(logFallback),
                DisplayOutputMode.Hdr10 => DisplayOutputPolicy.ResolveHdr10Swapchain(logFallback),
                _ => SwapchainDisplayMode.Standard8Bit,
            };
        }
    }
}
