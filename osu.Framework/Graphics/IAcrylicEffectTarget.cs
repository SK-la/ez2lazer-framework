// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osuTK;

namespace osu.Framework.Graphics
{
    /// <summary>
    /// Control surface shared by drawables which render a blurred backdrop effect.
    /// </summary>
    /// <remarks>
    /// Lets hosts toggle and tune a backdrop effect without taking a dependency on a concrete material.
    /// </remarks>
    public interface IAcrylicEffectTarget
    {
        /// <summary>
        /// Whether the effect is drawn. Disabling it releases the internal frame buffers.
        /// </summary>
        bool EffectEnabled { get; set; }

        /// <summary>
        /// Blur strength, in screen pixels.
        /// </summary>
        Vector2 BlurSigma { get; set; }
    }
}
