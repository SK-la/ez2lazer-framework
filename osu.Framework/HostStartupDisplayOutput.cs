// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework
{
    /// <summary>
    /// Carries <see cref="HostOptions.DisplayOutputMode"/> from host construction into renderer initialisation.
    /// </summary>
    public static class HostStartupDisplayOutput
    {
        public static DisplayOutputMode Mode { get; internal set; }

        /// <summary>SDR/UI exposure multiplier for future display-output final pass (1 = unchanged).</summary>
        public static float SdrExposure { get; internal set; } = 1f;

        /// <summary>HDR paper white in nits; 0 = derive from monitor DXGI metadata at startup.</summary>
        public static float HdrPaperWhiteNits { get; internal set; }
    }
}
