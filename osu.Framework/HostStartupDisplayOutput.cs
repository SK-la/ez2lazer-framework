// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Framework
{
    /// <summary>
    /// Display output mode is fixed at host construction. SDR exposure and HDR paper white can change while running.
    /// </summary>
    public static class HostStartupDisplayOutput
    {
        public static DisplayOutputMode Mode { get; internal set; }

        /// <summary>SDR/UI exposure multiplier for the display present pass (1 = unchanged).</summary>
        public static float SdrExposure { get; private set; } = 1f;

        /// <summary>HDR paper white in nits; 0 = monitor full-frame luminance.</summary>
        public static float HdrPaperWhiteNits { get; private set; }

        public static void SetTone(float sdrExposure, float hdrPaperWhiteNits)
        {
            SdrExposure = Math.Clamp(sdrExposure, 0.5f, 2f);
            HdrPaperWhiteNits = Math.Clamp(hdrPaperWhiteNits, 0f, 10000f);
        }
    }
}
