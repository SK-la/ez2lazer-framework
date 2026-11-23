// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using osu.Framework.Graphics.Textures;
using SkiaSharp;

namespace osu.Framework.Graphics.Imaging
{
    internal static unsafe class SkiaAnimatedImageDecoder
    {
        private const double default_static_duration_ms = 1000.0 / 60.0;

        public static AnimatedImageFrameLoadResult? TryDecode(Stream stream, int? maxFrames)
        {
            if (!stream.CanRead || !stream.CanSeek)
                return null;

            long initialPosition = stream.Position;
            var uploads = new List<TextureUpload>();
            bool completed = false;

            try
            {
                using var skStream = new SKManagedStream(stream, disposeManagedStream: false);
                using var codec = SKCodec.Create(skStream);
                if (codec == null || codec.FrameCount <= 0)
                    return null;

                var frameInfos = codec.FrameInfo;
                int frameCount = maxFrames.HasValue ? Math.Min(codec.FrameCount, Math.Max(1, maxFrames.Value)) : codec.FrameCount;
                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                using var canvas = new RgbaImage(info.Width, info.Height);
                double[] durations = new double[frameCount];
                bool singleFrame = frameCount == 1;

                for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    SKCodecResult decodeResult;
                    int prior = -1;
                    if (frameIndex > 0 && frameInfos[frameIndex].RequiredFrame != -1
                        && frameInfos[frameIndex - 1].DisposalMethod != SKCodecAnimationDisposalMethod.RestorePrevious)
                        prior = frameIndex - 1;

                    fixed (Rgba32* pixels = canvas.PixelSpan)
                        decodeResult = codec.GetPixels(info, (IntPtr)pixels, info.RowBytes, new SKCodecOptions(frameIndex, prior));

                    if (decodeResult != SKCodecResult.Success)
                        return null;

                    var frameImage = new RgbaImage(info.Width, info.Height, canvas.PixelSpan);
                    var upload = new TextureUpload(frameImage);

                    try
                    {
                        uploads.Add(upload);
                    }
                    catch
                    {
                        upload.Dispose();
                        throw;
                    }

                    int duration = frameInfos[frameIndex].Duration;
                    durations[frameIndex] = singleFrame
                        ? Math.Max(duration, default_static_duration_ms)
                        : Math.Max(duration, 20);
                }

                var result = new AnimatedImageFrameLoadResult(uploads.ToArray(), durations);
                completed = true;
                return result;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
            {
                return null;
            }
            finally
            {
                if (!completed)
                {
                    foreach (var upload in uploads)
                        upload.Dispose();

                    stream.Position = initialPosition;
                }
            }
        }
    }
}
