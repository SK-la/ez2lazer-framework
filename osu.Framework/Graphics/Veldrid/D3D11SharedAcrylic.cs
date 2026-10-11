// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Graphics.Shaders.Types;
using osu.Framework.Graphics.Textures;
using osu.Framework.Graphics.Veldrid.Buffers;
using osu.Framework.Graphics.Veldrid.Textures;
using osu.Framework.Utils;
using osuTK;
using osuTK.Graphics;
using Veldrid;

namespace osu.Framework.Graphics.Veldrid
{
    /// <summary>
    /// D3D11 immediate mode: one full swapchain copy per frame (CopyTexture cannot scale), one half-resolution
    /// downsample, then one separable blur per quantized sigma. Cards sample the matching result.
    /// </summary>
    /// <remarks>
    /// TODO(OpenGL): Desktop GL can glBlitFramebuffer from the default framebuffer straight into the half-resolution image. GLES on Android usually cannot; keep the offscreen capture there.
    /// TODO(Vulkan): vkCmdBlitImage from the swapchain can scale in the same copy. End the render pass first.
    /// TODO(Metal): Blit the drawable texture after ending the render encoder. It cannot be sampled while the pass is open.
    /// TODO(Deferred): No backend has swapchain pixels until the frame executes. Blur the offscreen scene target once per sigma instead of copying the backbuffer.
    /// </remarks>
    internal sealed class D3D11SharedAcrylic
    {
        private const float resolution_scale = 0.5f;

        private readonly VeldridRenderer renderer;

        private SwapchainCopyTexture? snapshotTexture;
        private VeldridFrameBuffer? snapshotBuffer;
        private PixelFormat snapshotFormat;
        private bool snapshotReady;
        private Vector2I snapshotPixelSize;

        private IFrameBuffer? sourceHalf;
        private bool sourceHalfReady;
        private Vector2 sourceHalfSize;

        private readonly Dictionary<int, Bucket> buckets = new Dictionary<int, Bucket>();

        public D3D11SharedAcrylic(VeldridRenderer renderer)
        {
            this.renderer = renderer;
        }

        public void BeginFrame()
        {
            snapshotReady = false;
            sourceHalfReady = false;

            foreach (var bucket in buckets.Values)
                bucket.Ready = false;
        }

        public bool TryGetBlur(float sigma, IShader? textureShader, IShader? blurShader, out IFrameBuffer blurred, out RectangleF screenRectangle)
        {
            blurred = null!;
            screenRectangle = default;

            if (textureShader == null || blurShader == null)
                return false;

            if (!ensureSnapshot())
                return false;

            if (!ensureHalf(textureShader))
                return false;

            screenRectangle = new RectangleF(0, 0, snapshotPixelSize.X, snapshotPixelSize.Y);

            int key = (int)MathF.Round(Math.Max(0, sigma));

            if (key == 0)
            {
                blurred = sourceHalf!;
                return true;
            }

            if (!buckets.TryGetValue(key, out var bucket))
                buckets[key] = bucket = new Bucket();

            if (!bucket.Ready)
                blur(bucket, key * resolution_scale, blurShader);

            blurred = bucket.Result;
            return bucket.Ready;
        }

        private bool ensureSnapshot()
        {
            if (snapshotReady)
                return true;

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

            snapshotPixelSize = size;
            snapshotReady = true;
            sourceHalfReady = false;
            return true;
        }

        private bool ensureHalf(IShader textureShader)
        {
            if (sourceHalfReady)
                return true;

            var size = new Vector2(
                Math.Max(1, (int)MathF.Ceiling(snapshotPixelSize.X * resolution_scale)),
                Math.Max(1, (int)MathF.Ceiling(snapshotPixelSize.Y * resolution_scale)));

            sourceHalf ??= renderer.CreateFrameBuffer(filteringMode: TextureFilteringMode.Linear);

            if (sourceHalf.Size != size)
                sourceHalf.Size = size;

            sourceHalfSize = size;

            drawInto(sourceHalf, () =>
            {
                textureShader.Bind();
                renderer.DrawFrameBuffer(snapshotBuffer!, new RectangleF(0, 0, size.X, size.Y), ColourInfo.SingleColour(Color4.White));
                textureShader.Unbind();
            });

            sourceHalfReady = true;
            return true;
        }

        private void blur(Bucket bucket, float scaledSigma, IShader blurShader)
        {
            int radius = Blur.KernelSize(scaledSigma);

            bucket.Ping ??= renderer.CreateFrameBuffer(filteringMode: TextureFilteringMode.Linear);
            bucket.Pong ??= renderer.CreateFrameBuffer(filteringMode: TextureFilteringMode.Linear);

            if (bucket.Ping.Size != sourceHalfSize)
                bucket.Ping.Size = sourceHalfSize;

            if (bucket.Pong.Size != sourceHalfSize)
                bucket.Pong.Size = sourceHalfSize;

            if (radius <= 0)
            {
                bucket.Result = sourceHalf!;
                bucket.Ready = true;
                return;
            }

            drawBlurPass(sourceHalf!, bucket.Ping, radius, scaledSigma, 0, blurShader);
            drawBlurPass(bucket.Ping, bucket.Pong, radius, scaledSigma, 90, blurShader);

            bucket.Result = bucket.Pong;
            bucket.Ready = true;
        }

        private void drawBlurPass(IFrameBuffer current, IFrameBuffer target, int radius, float sigma, float rotation, IShader blurShader)
        {
            drawInto(target, () =>
            {
                float radians = float.DegreesToRadians(rotation);

                using (var parameters = ((IRenderer)renderer).CreateUniformBuffer<BlurParameters>())
                {
                    parameters.Data = new BlurParameters
                    {
                        TexSize = current.Size,
                        Radius = radius,
                        Sigma = sigma,
                        Direction = new Vector2(MathF.Cos(radians), MathF.Sin(radians)),
                    };

                    blurShader.BindUniformBlock("m_BlurParameters", parameters);
                    blurShader.Bind();
                    renderer.DrawFrameBuffer(current, new RectangleF(0, 0, current.Texture.Width, current.Texture.Height), ColourInfo.SingleColour(Color4.White));
                    blurShader.Unbind();
                }
            });
        }

        private void drawInto(IFrameBuffer target, Action draw)
        {
            var rect = new RectangleF(0, 0, target.Size.X, target.Size.Y);
            var rectI = new RectangleI(0, 0, (int)target.Size.X, (int)target.Size.Y);

            renderer.PushScissorState(false);
            renderer.PushMaskingInfo(new MaskingInfo
            {
                ScreenSpaceAABB = rectI,
                MaskingRect = rect,
                ToMaskingSpace = Matrix3.Identity,
                BlendRange = 1,
                AlphaExponent = 1,
            }, true);
            renderer.PushViewport(rectI);
            renderer.PushOrtho(rect);

            target.Bind();
            renderer.Clear(new ClearInfo(new Color4(0, 0, 0, 0)));
            renderer.SetBlend(BlendingParameters.None);
            draw();
            target.Unbind();

            renderer.PopOrtho();
            renderer.PopViewport();
            renderer.PopMaskingInfo();
            renderer.PopScissorState();
        }

        private sealed class Bucket
        {
            public IFrameBuffer? Ping;
            public IFrameBuffer? Pong;
            public IFrameBuffer Result = null!;
            public bool Ready;
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

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private record struct BlurParameters
        {
            public UniformVector2 TexSize;
            public UniformInt Radius;
            public UniformFloat Sigma;
            public UniformVector2 Direction;
            private readonly UniformPadding8 pad1;
        }
    }
}
