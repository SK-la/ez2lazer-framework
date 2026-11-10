// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Logging;
using Veldrid;

namespace osu.Framework
{
    /// <summary>
    /// Display-output swapchain policy (monitor / OS HDR), not GPU vendor.
    /// </summary>
    internal static class DisplayOutputPolicy
    {
        public static SwapchainDisplayMode ResolveNative10BitSwapchain(bool log)
        {
            if (log)
            {
                Logger.Log(
                    "Display output Native10Bit: scene composites in R16F, then presents to R10G10B10A2. Standard is unchanged.",
                    LoggingTarget.Runtime,
                    LogLevel.Important);
            }

            return SwapchainDisplayMode.Native10BitSdr;
        }

        public static SwapchainDisplayMode ResolveHdr10Swapchain(bool log)
        {
            if (log)
            {
                Logger.Log(
                    "Display output Hdr10: scene composites in R16F, then sRGB→linear, Rec.709→BT.2020, paper white, ST.2084 PQ.",
                    LoggingTarget.Runtime,
                    LogLevel.Important);
            }

            return SwapchainDisplayMode.Hdr10;
        }

        public static void LogMonitorCapabilities(MonitorDisplayCapabilities? caps, float sdrExposure, float hdrPaperWhiteNits)
        {
            if (caps == null)
            {
                Logger.Log(
                    $"Display output tone: SDR exposure={sdrExposure:0.###}, HDR paper white={(hdrPaperWhiteNits > 0 ? $"{hdrPaperWhiteNits:0.#} nits" : "auto (monitor)")} (monitor metadata unavailable).",
                    LoggingTarget.Runtime,
                    LogLevel.Important);
                return;
            }

            float paperWhite = ResolveHdrPaperWhiteNits(hdrPaperWhiteNits, caps);

            Logger.Log($"Monitor \"{caps.Value.MonitorName}\": {caps.Value.BitsPerColor}bpc, HDR signal={caps.Value.HdrSignal}, "
                       + $"luminance max={caps.Value.MaxLuminance:0.#} / FFL={caps.Value.MaxFullFrameLuminance:0.#} nits. "
                       + $"Display tone: SDR exposure={sdrExposure:0.###}, HDR paper white={paperWhite:0.#} nits.");
        }

        // Zero tracks full-frame luminance, then peak. Manual values are not clamped to 80–400.
        public static float ResolveHdrPaperWhiteNits(float configured, MonitorDisplayCapabilities? caps)
        {
            if (configured > 0)
                return configured;

            if (caps == null)
                return 200f;

            float fullFrame = caps.Value.MaxFullFrameLuminance;

            if (fullFrame > 1)
                return fullFrame;

            float peak = caps.Value.MaxLuminance;
            return peak > 1 ? peak : 200f;
        }
    }
}
