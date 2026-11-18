// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;

namespace osu.Framework.Graphics.Textures
{
    /// <summary>
    /// Decoded animation frames ready for GPU upload. Each entry pairs with the same index in <see cref="FrameDurationMs"/>.
    /// </summary>
    public sealed class AnimatedImageFrameLoadResult : IDisposable
    {
        /// <summary>
        /// Full logical frames (for GIF, disposal-composited onto the canvas size).
        /// </summary>
        public IReadOnlyList<TextureUpload> Frames { get; }

        /// <summary>
        /// Display duration per frame in milliseconds.
        /// </summary>
        public IReadOnlyList<double> FrameDurationMs { get; }

        public AnimatedImageFrameLoadResult(IReadOnlyList<TextureUpload> frames, IReadOnlyList<double> frameDurationMs)
        {
            if (frames.Count != frameDurationMs.Count)
                throw new ArgumentException("Frame count must match duration count.");

            Frames = frames;
            FrameDurationMs = frameDurationMs;
        }

        public void Dispose()
        {
            foreach (var frame in Frames)
                frame.Dispose();
        }
    }
}