// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework
{
    /// <summary>
    /// Requested display output encoding for the main swapchain (applied at host startup; D3D11 on Windows).
    /// </summary>
    public enum DisplayOutputMode
    {
        Standard,
        Native10Bit,
        Hdr10,
    }
}
