// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;

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

            if (stream.CanSeek)
                stream.Position = 0;

            IImageFormat? format;

            try
            {
                format = Image.DetectFormat(stream);
            }
            catch
            {
                return null;
            }

            if (stream.CanSeek)
                stream.Position = 0;

            if (format.Name == GifFormat.Instance.Name)
                return loadGif(stream, maxFrames);

            try
            {
                using var image = Image.Load<Rgba32>(TextureUpload.DECODER_OPTIONS, stream);
                var upload = new TextureUpload(image.Clone());
                return new AnimatedImageFrameLoadResult(new[] { upload }, new[] { default_static_duration_ms });
            }
            catch
            {
                return null;
            }
        }

        private static AnimatedImageFrameLoadResult? loadGif(Stream stream, int? maxFrames)
        {
            Image<Rgba32>? source = null;

            try
            {
                var options = new DecoderOptions
                {
                    Configuration = TextureUpload.DECODER_OPTIONS.Configuration,
                    MaxFrames = maxFrames.HasValue ? (uint)Math.Max(1, maxFrames.Value) : uint.MaxValue,
                };
                source = Image.Load<Rgba32>(options, stream);

                if (source.Frames.Count <= 1)
                {
                    var single = new TextureUpload(source.Clone());
                    double delay = Math.Max(source.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay * 10, default_static_duration_ms);
                    return new AnimatedImageFrameLoadResult(new[] { single }, new[] { delay });
                }

                var (images, durations) = GifFrameCompositor.Compose(source, maxFrames);
                List<TextureUpload>? uploads = null;
                int convertedImageCount = 0;

                try
                {
                    uploads = new List<TextureUpload>(images.Count);

                    while (convertedImageCount < images.Count)
                    {
                        var upload = new TextureUpload(images[convertedImageCount]);

                        try
                        {
                            uploads.Add(upload);
                        }
                        catch
                        {
                            upload.Dispose();
                            convertedImageCount++;
                            throw;
                        }

                        convertedImageCount++;
                    }

                    return new AnimatedImageFrameLoadResult(uploads, durations);
                }
                catch
                {
                    if (uploads != null)
                    {
                        foreach (var upload in uploads)
                            upload.Dispose();
                    }

                    for (int i = convertedImageCount; i < images.Count; i++)
                        images[i].Dispose();

                    throw;
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                source?.Dispose();
            }
        }
    }
}
