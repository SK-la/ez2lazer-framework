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
using osu.Framework.Utils;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Graphics
{
    /// <summary>
    /// One captured backdrop per frame, then one half-resolution blur per quantized sigma.
    /// Each backend only has to fill <see cref="Capture"/>.
    /// </summary>
    internal sealed class SharedBackbufferAcrylic
    {
        private const float resolution_scale = 0.5f;

        // IRenderer 不暴露当前混合参数，而离屏绘制后必须还原它。
        private readonly Renderer renderer;
        private readonly Capture capture;

        private bool captured;
        private IFrameBuffer? fullSnapshot;
        private Vector2I screenPixels;

        private IFrameBuffer? sourceHalf;
        private bool sourceHalfReady;
        private Vector2 sourceHalfSize;

        private readonly Dictionary<int, Bucket> buckets = new Dictionary<int, Bucket>();

        public SharedBackbufferAcrylic(Renderer renderer, Capture capture)
        {
            this.renderer = renderer;
            this.capture = capture;
        }

        public delegate bool Capture(out IFrameBuffer color, out Vector2I screenPixels, out bool alreadyHalfResolution);

        public void BeginFrame()
        {
            captured = false;
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

            if (!ensureSource(textureShader))
                return false;

            screenRectangle = new RectangleF(0, 0, screenPixels.X, screenPixels.Y);

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

        private bool ensureSource(IShader textureShader)
        {
            if (!captured)
            {
                if (!capture(out var color, out screenPixels, out bool alreadyHalfResolution))
                    return false;

                captured = true;
                fullSnapshot = alreadyHalfResolution ? null : color;

                if (alreadyHalfResolution)
                {
                    sourceHalf = color;
                    sourceHalfSize = color.Size;
                    sourceHalfReady = true;
                }
                else
                {
                    sourceHalfReady = false;
                }
            }

            if (sourceHalfReady)
                return sourceHalf != null;

            if (fullSnapshot == null)
                return false;

            var size = new Vector2(
                Math.Max(1, (int)MathF.Ceiling(screenPixels.X * resolution_scale)),
                Math.Max(1, (int)MathF.Ceiling(screenPixels.Y * resolution_scale)));

            sourceHalf ??= renderer.CreateFrameBuffer(filteringMode: TextureFilteringMode.Linear);

            if (sourceHalf.Size != size)
                sourceHalf.Size = size;

            sourceHalfSize = size;

            drawInto(sourceHalf, () =>
            {
                textureShader.Bind();
                renderer.DrawFrameBuffer(fullSnapshot, new RectangleF(0, 0, size.X, size.Y), ColourInfo.SingleColour(Color4.White));
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

            // 离屏 setup 期间关混合，但必须还原：调用方可能正要靠混合参数画回后缓冲。
            BlendingParameters previousBlend = renderer.CurrentBlendingParameters;
            renderer.SetBlend(BlendingParameters.None);
            draw();
            renderer.SetBlend(previousBlend);

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
