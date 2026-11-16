// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using ManagedBass;

namespace osu.Framework.Audio.Wasapi
{
    internal static class WasapiExclusiveBufferLadder
    {
        public static bool ShouldAbortLadder(Errors error)
            => error == Errors.Busy;
    }
}
