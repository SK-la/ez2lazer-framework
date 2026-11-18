// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace osu.Framework.Graphics.Textures
{
    internal static class GifFrameCompositor
    {
        /// <summary>
        /// Extracts full logical-screen RGBA images for each GIF frame.
        /// ImageSharp's GIF decoder already applies disposal when loading <see cref="Rgba32"/> frames.
        /// </summary>
        public static (List<Image<Rgba32>> Images, List<double> FrameDurationMs) Compose(Image<Rgba32> source, int? maxFrames)
        {
            var images = new List<Image<Rgba32>>();
            var durations = new List<double>();

            int frameCount = source.Frames.Count;
            if (maxFrames.HasValue)
                frameCount = Math.Min(frameCount, Math.Max(1, maxFrames.Value));

            try
            {
                for (int i = 0; i < frameCount; i++)
                {
                    var frameMetadata = source.Frames[i].Metadata.GetGifMetadata();
                    var image = source.Frames.CloneFrame(i);

                    try
                    {
                        images.Add(image);
                    }
                    catch
                    {
                        image.Dispose();
                        throw;
                    }

                    durations.Add(Math.Max(frameMetadata.FrameDelay * 10, 20));
                }

                return (images, durations);
            }
            catch
            {
                foreach (var image in images)
                    image.Dispose();

                throw;
            }
        }
    }
}
