// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Framework.Audio.Windows
{
    internal static class WasapiExclusiveEndpointGainPolicy
    {
        public static float MixerVolume(float scalar, bool muted, float levelDb = float.NaN)
        {
            if (muted)
                return 0f;

            if (!float.IsNaN(levelDb) && !float.IsInfinity(levelDb))
            {
                double amplitude = Math.Pow(10, levelDb / 20.0);
                if (amplitude <= 0)
                    return 0f;

                return (float)Math.Clamp(amplitude, 0, 1);
            }

            if (float.IsNaN(scalar) || float.IsInfinity(scalar))
                return 1f;

            return Math.Clamp(scalar, 0f, 1f);
        }
    }
}
