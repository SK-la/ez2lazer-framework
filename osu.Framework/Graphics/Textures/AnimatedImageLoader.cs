// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using osu.Framework.Graphics.Imaging;
using SkiaAnimatedImageDecoder = osu.Framework.Graphics.Imaging.SkiaAnimatedImageDecoder;
using SkiaStaticImageDecoder = osu.Framework.Graphics.Imaging.SkiaStaticImageDecoder;

namespace osu.Framework.Graphics.Textures
{
    /// <summary>
    /// Loads multi-frame still animations (currently GIF) into <see cref="TextureUpload"/> sequences.
    /// Does not interact with <see cref="TextureStore"/>; callers upload frames via <see cref="Texture.SetData(ITextureUpload)"/>.
    /// </summary>
    public static class AnimatedImageLoader
    {
        private const double default_static_duration_ms = 1000.0 / 60.0;

        /// <summary>
        /// Decode an image stream. Animated GIFs expand to one upload per composited frame; other formats yield a single frame.
        /// </summary>
        /// <param name="stream">Readable image stream; position is reset to 0.</param>
        /// <param name="maxFrames">Optional cap on GIF frames (inclusive minimum 1).</param>
        public static AnimatedImageFrameLoadResult? LoadFromStream(Stream stream, int? maxFrames = null)
        {
            if (!stream.CanRead)
                return null;

            MemoryStream? copy = null;

            try
            {
                if (stream.CanSeek)
                    stream.Position = 0;
                else
                {
                    copy = new MemoryStream();
                    stream.CopyTo(copy);
                    copy.Position = 0;
                    stream = copy;
                }

                if (SkiaStaticImageDecoder.IsGif(stream))
                    return SkiaAnimatedImageDecoder.TryDecode(stream, maxFrames);

                var upload = new TextureUpload(ImageCodec.Decode(stream));
                return new AnimatedImageFrameLoadResult(new[] { upload }, new[] { default_static_duration_ms });
            }
            catch
            {
                return null;
            }
            finally
            {
                copy?.Dispose();
            }
        }
    }
}
