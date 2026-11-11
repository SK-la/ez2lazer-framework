// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Graphics.Textures;

namespace osu.Framework.Graphics.Rendering.Dummy
{
    /// <summary>
    /// An <see cref="INativeTexture"/> that does not talk to a GPU.
    /// It still queues and budget-drains uploads so headless tests can exercise the atlas burst path.
    /// </summary>
    internal class DummyNativeTexture : INativeTexture
    {
        private readonly Queue<ITextureUpload> uploadQueue = new Queue<ITextureUpload>();

        public IRenderer Renderer { get; }
        public TextureFilteringMode FilteringMode => TextureFilteringMode.Linear;
        public TexturePixelFormat PixelFormat => TexturePixelFormat.R8G8B8A8Float;

        public string Identifier => string.Empty;
        public string Label { get; set; } = string.Empty;
        public int MaxSize => 4096; // Sane default for testing purposes.
        public int Width { get; set; }
        public int Height { get; set; }
        public int? MipLevel { get; set; }
        public bool Available => true;
        public bool BypassTextureUploadQueueing { get; set; }
        public bool IsQueuedForUpload { get; set; }
        ulong INativeTexture.TotalBindCount { get; set; }

        public DummyNativeTexture(IRenderer renderer, int width, int height)
        {
            Renderer = renderer;
            Width = width;
            Height = height;
        }

        public void FlushUploads()
        {
            lock (uploadQueue)
            {
                while (uploadQueue.Count > 0)
                    uploadQueue.Dequeue().Dispose();
            }
        }

        public void SetData(ITextureUpload upload)
        {
            lock (uploadQueue)
            {
                TextureUploadDrainer.LogIfQueueLarge(this, uploadQueue.Count + 1);

                bool requireUpload = uploadQueue.Count == 0;
                uploadQueue.Enqueue(upload);

                if (requireUpload && !BypassTextureUploadQueueing)
                    ((Renderer)Renderer).EnqueueTextureUpload(this);
            }
        }

        public bool Upload()
        {
            int uploaded = TextureUploadDrainer.Drain(uploadQueue, Renderer, BypassTextureUploadQueueing, _ => { });

            if (!BypassTextureUploadQueueing && !UploadComplete)
                ((Renderer)Renderer).EnqueueTextureUpload(this);

            return uploaded > 0;
        }

        public bool UploadComplete
        {
            get
            {
                lock (uploadQueue)
                    return uploadQueue.Count == 0;
            }
        }

        public bool Bind(int unit) => true;

        public int GetByteSize() => 0;

        public ulong BindCount => 0;

        public void Dispose()
        {
            FlushUploads();
        }
    }
}
