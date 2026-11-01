// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Runtime.InteropServices;
using JetBrains.Annotations;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Graphics.Shaders.Types;
using osu.Framework.Utils;
using osuTK;
using osuTK.Graphics;

namespace osu.Framework.Graphics
{
    /// <summary>
    /// 真·全局毛玻璃（Acrylic）：内容无关地虚化"自身矩形正下方"已经渲染好的任意内容
    /// （图片 / 视频 / 故事板 / 其它 UI），无需知道下层是什么，也无需把下层作为捕获源传入。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 原理：渲染管线中，处于本节点之前（从后到前）的内容已经写入"当前绑定的离屏帧缓冲"
    /// （<see cref="IRenderer.CurrentFrameBuffer"/>）。本节点在自己被绘制时：
    /// 1. 绑定自己的效果缓冲，对当前帧缓冲按本卡片屏幕矩形取一块"切片"；
    /// 2. 高斯模糊该切片；
    /// 3. 把模糊结果绘制回原位。
    /// 因此实现了"毛玻璃卡片透出下层模糊内容"，且逐帧一致、零枚举、不影响其它绘制。
    /// </para>
    /// <para>
    /// 使用前提：本节点必须处在一个"离屏帧缓冲"之下，否则当前绑定目标是不可采样的 backbuffer
    /// （此时 <see cref="IRenderer.CurrentFrameBuffer"/> 为 null，本节点跳过绘制）。最简单的做法是把
    /// 需要被透视的整块内容包进一个全屏 <see cref="Containers.BufferedContainer"/>，再把本卡片作为它的
    /// 子节点放在任意位置。当前版本的几何映射假定该承载缓冲为"全屏、原点对齐、FrameBufferScale=1"，
    /// 这正是"全屏 HUD 任意拖动毛玻璃"的目标场景。
    /// </para>
    /// <para>
    /// D3D11、Metal、Vulkan 和桌面 OpenGL 在直接画到交换链时整帧捕获一次，再按整数 sigma 共用半分辨率虚化。
    /// 旋转或各向异性模糊仍走逐卡片路径。延迟渲染仍不能读交换链。
    /// </para>
    /// </remarks>
    public partial class AcrylicBackdropDrawable : Drawable, ITexturedShaderDrawable, IAcrylicEffectTarget
    {
        // 两个效果缓冲用于横/纵两趟高斯模糊的乒乓。
        // 用 Linear 而非 Nearest：效果缓冲尺寸按桶对齐后可能略大于卡片矩形，回贴是一次轻微缩小重采样。
        private readonly BufferedDrawNodeSharedData sharedData = new BufferedDrawNodeSharedData(2, TexturePixelFormat.R8G8B8A8Float, pixelSnapping: false, clipToRootNode: false);

        private bool effectEnabled = true;

        /// <summary>
        /// 是否启用模糊。关闭后本节点不产生绘制（开销接近零）。
        /// </summary>
        public bool EffectEnabled
        {
            get => effectEnabled;
            set
            {
                if (effectEnabled == value)
                    return;

                effectEnabled = value;

                // Pooled cards outlive one appearance. Dropping the ping-pong FBOs here is what stops
                // raw / horizontal-blur / full-blur textures from stacking after the card leaves the screen.
                if (!effectEnabled && sharedData.IsInitialised)
                    sharedData.ReleaseBuffers();

                Invalidate(Invalidation.DrawNode);
            }
        }

        private Vector2 blurSigma = new Vector2(12);

        /// <summary>
        /// 两个正交方向上的模糊强度。
        /// </summary>
        [UsedImplicitly]
        public Vector2 BlurSigma
        {
            get => blurSigma;
            set
            {
                if (blurSigma == value)
                    return;

                blurSigma = value;
                Invalidate(Invalidation.DrawNode);
            }
        }

        private float blurRotation;

        /// <summary>
        /// 顺时针旋转模糊核，单位为度。
        /// </summary>
        [UsedImplicitly]
        public float BlurRotation
        {
            get => blurRotation;
            set
            {
                if (blurRotation == value)
                    return;

                blurRotation = value;
                Invalidate(Invalidation.DrawNode);
            }
        }

        private Vector2 frameBufferScale = new Vector2(0.5f);

        /// <summary>
        /// 内部效果缓冲相对于本卡片大小的缩放。较低的值降低开销但牺牲画质（模糊本身会掩盖降采样）。
        /// </summary>
        [UsedImplicitly]
        public Vector2 FrameBufferScale
        {
            get => frameBufferScale;
            set
            {
                if (frameBufferScale == value)
                    return;

                frameBufferScale = value;
                Invalidate(Invalidation.DrawNode);
            }
        }

        private IShader textureShader;
        private IShader blurShader;

        IShader ITexturedShaderDrawable.TextureShader => textureShader;

        [BackgroundDependencyLoader]
        private void load(ShaderManager shaders)
        {
            textureShader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.TEXTURE);
            blurShader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.BLUR);
        }

        protected override DrawNode CreateDrawNode() => new AcrylicBackdropDrawNode(this, sharedData);

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            effectEnabled = false;
            textureShader = null;
            blurShader = null;

            if (sharedData.IsInitialised)
                sharedData.Dispose();
        }

        private class AcrylicBackdropDrawNode : TexturedShaderDrawNode
        {
            protected new AcrylicBackdropDrawable Source => (AcrylicBackdropDrawable)base.Source;

            protected readonly BufferedDrawNodeSharedData SharedData;

            private bool effectEnabled;
            private RectangleF screenSpaceDrawRectangle;
            private Vector2 frameBufferScale;
            private Vector2 frameBufferSize;

            // 效果缓冲容量只增不减地量化复用，避免卡片连续缩放时逐帧重建纹理。
            // 详见 quantiseBufferSize。
            private Vector2 allocatedBufferSize;

            private ColourInfo drawColour;

            private Vector2 blurSigma;
            private Vector2 compensatedBlurSigma;
            private Vector2I blurRadius;
            private float blurRotation;

            private IShader blurShader;
            private IUniformBuffer<BlurParameters> blurParametersBuffer;
            private BlurParameters blurParameters;

            public AcrylicBackdropDrawNode(AcrylicBackdropDrawable source, BufferedDrawNodeSharedData sharedData)
                : base(source)
            {
                SharedData = sharedData;
            }

            public override void ApplyState()
            {
                base.ApplyState();

                effectEnabled = Source.EffectEnabled;
                screenSpaceDrawRectangle = Source.ScreenSpaceDrawQuad.AABBFloat;
                frameBufferScale = Source.FrameBufferScale;

                var unscaledBufferSize = new Vector2(
                    screenSpaceDrawRectangle.Width * frameBufferScale.X,
                    screenSpaceDrawRectangle.Height * frameBufferScale.Y);

                frameBufferSize = quantiseBufferSize(unscaledBufferSize);

                // 屏幕空间 sigma：共用后缓冲路径直接把它当整屏模糊强度用，必须保持原值。
                blurSigma = Source.BlurSigma;

                // 逐卡片离屏路径的 sigma 需补偿容量对齐带来的缩小，使屏幕上的模糊宽度与量化前一致。
                compensatedBlurSigma = new Vector2(
                    unscaledBufferSize.X > 0 ? blurSigma.X * (frameBufferSize.X / unscaledBufferSize.X) : blurSigma.X,
                    unscaledBufferSize.Y > 0 ? blurSigma.Y * (frameBufferSize.Y / unscaledBufferSize.Y) : blurSigma.Y);

                drawColour = DrawColourInfo.Colour;

                blurRadius = new Vector2I(Blur.KernelSize(compensatedBlurSigma.X), Blur.KernelSize(compensatedBlurSigma.Y));
                blurRotation = Source.BlurRotation;
                blurShader = Source.blurShader;
            }

            protected override void Draw(IRenderer renderer)
            {
                if (!effectEnabled || frameBufferSize.X < 1 || frameBufferSize.Y < 1)
                    return;

                if (tryDrawSharedBackbuffer(renderer))
                    return;

                // 当前绑定的离屏帧缓冲即"下层已渲染内容"。若直接渲染到 backbuffer（不可采样）则尝试从 backbuffer 拷贝区域。
                IFrameBuffer scene = renderer.CurrentFrameBuffer;
                RectangleF sceneScreenRectangle;

                if (scene == null && renderer.SupportsBackbufferRegionCopy)
                {
                    var screenRect = new RectangleI(
                        (int)MathF.Floor(screenSpaceDrawRectangle.X),
                        (int)MathF.Floor(screenSpaceDrawRectangle.Y),
                        (int)MathF.Ceiling(screenSpaceDrawRectangle.Width),
                        (int)MathF.Ceiling(screenSpaceDrawRectangle.Height));

                    scene = renderer.PrepareBackbufferRegionSnapshot(screenRect);

                    if (scene != null)
                        sceneScreenRectangle = new RectangleF(0, 0, scene.Size.X, scene.Size.Y);
                    else
                        return;
                }
                else if (scene == null)
                {
                    return;
                }
                else
                {
                    // 当前投影即"承载缓冲"渲染下层内容时使用的正交矩阵；反推出它对应的屏幕矩形，
                    // 这样无需依赖纹理像素尺寸（会受 FrameBufferScale / DPI 影响而错位），即可把
                    // scene 纹理精确地按它的真实屏幕位置绘制。
                    sceneScreenRectangle = screenRectangleFromProjection(renderer.ProjectionMatrix);
                }

                if (!SharedData.IsInitialised)
                    SharedData.Initialise(renderer);

                SharedData.ResetCurrentEffectBuffer();

                using (establishFrameBufferViewport(renderer))
                {
                    // 1) 取下层切片：把 scene 纹理按其真实屏幕矩形绘制到本卡片的效果缓冲，
                    //    当前视口/正交只覆盖本卡片屏幕矩形，于是只保留卡片正下方那一块。
                    using (bindFrameBuffer(SharedData.MainBuffer))
                    {
                        renderer.PushOrtho(screenSpaceDrawRectangle);
                        renderer.Clear(new ClearInfo(new Color4(0, 0, 0, 0)));
                        renderer.SetBlend(BlendingParameters.None);

                        BindTextureShader(renderer);
                        renderer.DrawFrameBuffer(scene, sceneScreenRectangle, ColourInfo.SingleColour(Color4.White));
                        UnbindTextureShader(renderer);

                        renderer.PopOrtho();
                    }

                    // 2) 对切片做高斯模糊（横/纵两趟）。
                    populateBlur(renderer);
                }

                // 3) 把模糊结果绘制回卡片原位。
                BindTextureShader(renderer);
                base.Draw(renderer);
                renderer.DrawFrameBuffer(SharedData.CurrentEffectBuffer, screenSpaceDrawRectangle, drawColour);
                UnbindTextureShader(renderer);
            }

            private bool tryDrawSharedBackbuffer(IRenderer renderer)
            {
                if (renderer.CurrentFrameBuffer != null)
                    return false;

                if (blurRotation != 0 || blurSigma.X != blurSigma.Y)
                    return false;

                if (renderer is not Renderer concrete || concrete.SharedAcrylic == null || !concrete.SupportsBackbufferRegionCopy)
                    return false;

                if (!concrete.SharedAcrylic.TryGetBlur(screenSpaceDrawRectangle, blurSigma.X, ((ITexturedShaderDrawable)Source).TextureShader, blurShader, out var blurred, out var textureCoords))
                    return false;

                BindTextureShader(renderer);

                // 与回退路径一致：先套用本节点的混合参数。SharedBackbufferAcrylic 离屏阶段会把混合关成
                // None，若沿用，mask 外 alpha=0 的片元会连边框色（默认黑）一起写进后缓冲，形成方块黑块。
                base.Draw(renderer);

                // 共享纹理是整屏快照，这里只取本卡片对应的子矩形，避免把整屏糊到所有其它绘制之上。
                renderer.DrawQuad(blurred.Texture, screenSpaceDrawRectangle, drawColour, textureCoords: textureCoords);
                UnbindTextureShader(renderer);
                return true;
            }

            private void populateBlur(IRenderer renderer)
            {
                if (blurRadius.X <= 0 && blurRadius.Y <= 0)
                    return;

                renderer.PushScissorState(false);

                if (blurRadius.X > 0)
                    drawBlurredFrameBuffer(renderer, blurRadius.X, compensatedBlurSigma.X, blurRotation);

                if (blurRadius.Y > 0)
                    drawBlurredFrameBuffer(renderer, blurRadius.Y, compensatedBlurSigma.Y, blurRotation + 90);

                renderer.PopScissorState();
            }

            private void drawBlurredFrameBuffer(IRenderer renderer, int kernelRadius, float sigma, float rotation)
            {
                IFrameBuffer current = SharedData.CurrentEffectBuffer;
                IFrameBuffer target = SharedData.GetNextEffectBuffer();

                renderer.SetBlend(BlendingParameters.None);

                using (bindFrameBuffer(target))
                {
                    float radians = float.DegreesToRadians(rotation);

                    blurParametersBuffer ??= renderer.CreateUniformBuffer<BlurParameters>();

                    blurParameters.Radius = kernelRadius;
                    blurParameters.Sigma = sigma;
                    blurParameters.TexSize = current.Size;
                    blurParameters.Direction = new Vector2(MathF.Cos(radians), MathF.Sin(radians));

                    blurParametersBuffer.Data = blurParameters;

                    blurShader.BindUniformBlock("m_BlurParameters", blurParametersBuffer);
                    blurShader.Bind();
                    renderer.DrawFrameBuffer(current, new RectangleF(0, 0, current.Texture.Width, current.Texture.Height), ColourInfo.SingleColour(Color4.White));
                    blurShader.Unbind();
                }
            }

            /// <summary>
            /// 向上对齐缓冲尺寸到固定容量桶，并套用迟滞，让卡片连续缩放时复用同一批缓冲。
            /// </summary>
            /// <remarks>
            /// 缓冲尺寸是「容量」：卡片变小不急着回收，因此只在溢出当前容量时扩容、明显偏小时才收缩。
            /// 迟滞（<see cref="buffer_shrink_ratio"/>）用于防止尺寸停在桶边界附近时逐帧横跳，
            /// 那会让每帧都重建纹理，正是本类要消除的开销。
            /// </remarks>
            private Vector2 quantiseBufferSize(Vector2 size)
            {
                var desired = new Vector2(
                    MathF.Ceiling(size.X / buffer_size_quantisation) * buffer_size_quantisation,
                    MathF.Ceiling(size.Y / buffer_size_quantisation) * buffer_size_quantisation);

                // 首次分配，或任一轴溢出当前容量：立即扩容。
                if (allocatedBufferSize.X < 1 || allocatedBufferSize.Y < 1
                    || desired.X > allocatedBufferSize.X || desired.Y > allocatedBufferSize.Y)
                {
                    allocatedBufferSize = desired;
                }
                // 两轴都明显变小才收缩，避免边界乒乓。
                else if (desired.X < allocatedBufferSize.X * buffer_shrink_ratio
                         && desired.Y < allocatedBufferSize.Y * buffer_shrink_ratio)
                {
                    allocatedBufferSize = desired;
                }

                return allocatedBufferSize;
            }

            /// <summary>
            /// 反推 <see cref="RendererExtensions.PushOrtho"/> 所用的屏幕矩形。
            /// 投影由 <c>Matrix4.CreateOrthographicOffCenter</c> 生成，这里把裁剪空间四角变换回屏幕空间。
            /// </summary>
            private static RectangleF screenRectangleFromProjection(Matrix4 projection)
            {
                Matrix4 inverse = projection.Inverted();

                Vector3 corner0 = Vector3.TransformPosition(new Vector3(-1, -1, 0), inverse);
                Vector3 corner1 = Vector3.TransformPosition(new Vector3(1, 1, 0), inverse);

                float left = MathF.Min(corner0.X, corner1.X);
                float right = MathF.Max(corner0.X, corner1.X);
                float top = MathF.Min(corner0.Y, corner1.Y);
                float bottom = MathF.Max(corner0.Y, corner1.Y);

                return new RectangleF(left, top, right - left, bottom - top);
            }

            private ValueInvokeOnDisposal<IFrameBuffer> bindFrameBuffer(IFrameBuffer frameBuffer)
            {
                // 设置 Size 会按需分配合适尺寸的纹理。
                frameBuffer.Size = frameBufferSize;
                frameBuffer.Bind();
                return new ValueInvokeOnDisposal<IFrameBuffer>(frameBuffer, static b => b.Unbind());
            }

            private ValueInvokeOnDisposal<(AcrylicBackdropDrawNode node, IRenderer renderer)> establishFrameBufferViewport(IRenderer renderer)
            {
                RectangleI screenSpaceMaskingRect = new RectangleI(
                    (int)Math.Floor(screenSpaceDrawRectangle.X),
                    (int)Math.Floor(screenSpaceDrawRectangle.Y),
                    (int)frameBufferSize.X + 1,
                    (int)frameBufferSize.Y + 1);

                renderer.PushMaskingInfo(new MaskingInfo
                {
                    ScreenSpaceAABB = screenSpaceMaskingRect,
                    MaskingRect = screenSpaceDrawRectangle,
                    ToMaskingSpace = Matrix3.Identity,
                    BlendRange = 1,
                    AlphaExponent = 1,
                }, true);

                renderer.PushViewport(new RectangleI(0, 0, (int)frameBufferSize.X, (int)frameBufferSize.Y));
                renderer.PushScissor(new RectangleI(0, 0, (int)frameBufferSize.X, (int)frameBufferSize.Y));
                renderer.PushScissorOffset(screenSpaceMaskingRect.Location);

                return new ValueInvokeOnDisposal<(AcrylicBackdropDrawNode node, IRenderer renderer)>((this, renderer), static tup => tup.node.returnViewport(tup.renderer));
            }

            private void returnViewport(IRenderer renderer)
            {
                renderer.PopScissorOffset();
                renderer.PopViewport();
                renderer.PopScissor();
                renderer.PopMaskingInfo();
            }

            /// <summary>
            /// 效果缓冲容量按此粒度向上对齐，使卡片连续缩放时复用同一批缓冲。
            /// </summary>
            private const float buffer_size_quantisation = 16;

            /// <summary>
            /// 容量收缩阈值：相对当前容量小于该比例才回收，防止尺寸停在桶边界附近时逐帧重建。
            /// </summary>
            private const float buffer_shrink_ratio = 0.9f;

            protected override void Dispose(bool isDisposing)
            {
                base.Dispose(isDisposing);

                blurParametersBuffer?.Dispose();
                blurParametersBuffer = null;
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
}
