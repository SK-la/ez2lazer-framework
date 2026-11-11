// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Graphics.Textures;
using osu.Framework.Logging;

namespace osu.Framework.Graphics.Rendering
{
    /// <summary>
    /// Shared drain path for a texture's local <see cref="ITextureUpload"/> queue.
    /// Respects the renderer's per-frame region / pixel / time budget so one atlas cannot dump thousands of uploads in a single draw call.
    /// </summary>
    internal static class TextureUploadDrainer
    {
        public static void LogIfQueueLarge(INativeTexture texture, int queueCount)
        {
            if (queueCount < 100 || queueCount % 100 != 0)
                return;

            string name = !string.IsNullOrEmpty(texture.Label) ? texture.Label : texture.Identifier;
            Logger.Log($"Texture [{name}] {texture.Width}x{texture.Height} upload queue is large ({queueCount})", LoggingTarget.Performance);
        }

        public static int Drain(Queue<ITextureUpload> uploadQueue, IRenderer renderer, bool bypassQueueing, Action<ITextureUpload> performUpload)
        {
            int uploaded = 0;

            while (true)
            {
                ITextureUpload upload;

                lock (uploadQueue)
                {
                    if (uploadQueue.Count == 0)
                        break;

                    upload = uploadQueue.Peek();
                    int pixels = Math.Max(0, upload.Bounds.Width * upload.Bounds.Height);

                    if (!renderer.TryBeginTextureUpload(pixels, bypassQueueing))
                        break;

                    uploadQueue.Dequeue();
                }

                using (upload)
                    performUpload(upload);

                uploaded++;
            }

            return uploaded;
        }
    }
}
