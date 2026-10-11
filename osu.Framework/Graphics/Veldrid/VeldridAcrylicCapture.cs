// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Textures;
using osu.Framework.Graphics.Veldrid.Buffers;
using osu.Framework.Graphics.Veldrid.Textures;
using osuTK;
using Veldrid;

namespace osu.Framework.Graphics.Veldrid
{
    /// <summary>
    /// Copies the whole swapchain once per call. D3D11, Metal, and Vulkan can do this after the render pass ends.
    /// CopyTexture cannot scale, so the shared blur downsamples afterwards.
    /// </summary>
    internal sealed class VeldridAcrylicCapture
    {
        private readonly VeldridRenderer renderer;

        private SwapchainCopyTexture? snapshotTexture;
        private VeldridFrameBuffer? snapshotBuffer;
        private PixelFormat snapshotFormat;

        public VeldridAcrylicCapture(VeldridRenderer renderer)
        {
            this.renderer = renderer;
        }

        public bool TryCapture(out IFrameBuffer color, out Vector2I screenPixels, out bool alreadyHalfResolution)
        {
            color = null!;
            screenPixels = default;
            alreadyHalfResolution = false;

            if (!renderer.TryGetBackbufferPixelSize(out var size, out var format))
                return false;

            if (snapshotBuffer == null || snapshotFormat != format)
            {
                snapshotBuffer?.Dispose();
                snapshotTexture?.Dispose();

                snapshotTexture = new SwapchainCopyTexture(renderer, format);
                snapshotBuffer = new VeldridFrameBuffer(renderer, snapshotTexture, 0);
                snapshotFormat = format;
            }

            var target = new Vector2(size.X, size.Y);

            if (snapshotBuffer.Size != target)
                snapshotBuffer.Size = target;

            if (!renderer.TryCopyFullBackbuffer(snapshotBuffer))
                return false;

            color = snapshotBuffer;
            screenPixels = size;
            return true;
        }

        private sealed class SwapchainCopyTexture : VeldridTexture
        {
            private readonly PixelFormat format;

            protected override PixelFormat NativePixelFormat => format;

            protected override uint MipLevelCount => 1;

            public SwapchainCopyTexture(IVeldridRenderer renderer, PixelFormat format)
                : base(renderer, 1, 1, TexturePixelFormat.R8G8B8A8Float, manualMipmaps: true, TextureFilteringMode.Linear)
            {
                this.format = format;
                BypassTextureUploadQueueing = true;
                SetData(new TextureUpload());
                Upload();
            }
        }
    }
}
