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
    /// 液态玻璃材质：在自身屏幕矩形正下方已渲染内容上做模糊，再叠加边缘折射、色散、高光与染色。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 抓取语义与 <see cref="AcrylicBackdropDrawable"/> 一致：优先采样 <see cref="IRenderer.CurrentFrameBuffer"/>，
    /// 没有离屏缓冲时回退到 <see cref="Renderer.SharedAcrylic"/> 的整屏快照，两者都不可用则静默跳过绘制。
    /// </para>
    /// <para>
    /// 逐卡片路径把抓取区按模糊核半径与折射深度向外扩，折射因此能取到卡片外的像素；模糊与玻璃着色都只在整个
    /// 外扩后区域内进行，绘制时只回贴卡片子矩形。回退路径复用整屏快照，天然包含卡片外的像素。
    /// </para>
    /// </remarks>
    public partial class LiquidGlassDrawable : Drawable, ITexturedShaderDrawable, IAcrylicEffectTarget
    {
        // 两个效果缓冲用于横/纵两趟高斯模糊的乒乓。
        private readonly BufferedDrawNodeSharedData sharedData = new BufferedDrawNodeSharedData(2, TexturePixelFormat.R8G8B8A8Float, pixelSnapping: true, clipToRootNode: false);

        private bool effectEnabled = true;

        /// <summary>
        /// 是否启用玻璃效果。关闭后本节点不产生绘制（开销接近零）。
        /// </summary>
        public bool EffectEnabled
        {
            get => effectEnabled;
            set
            {
                if (effectEnabled == value)
                    return;

                effectEnabled = value;

                // 复用池里的卡片会比外观存活更久，这里主动丢掉乒乓缓冲，避免禁用后仍留着三张纹理。
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

        private Vector2 frameBufferScale = Vector2.One;

        /// <summary>
        /// 效果缓冲相对于抓取区大小的缩放。液态玻璃的折射需要全分辨率，默认不降采样。
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

        private LiquidGlassMaterial material = LiquidGlassMaterial.Default;

        /// <summary>
        /// 折射、色散、高光与染色参数。
        /// </summary>
        public LiquidGlassMaterial Material
        {
            get => material;
            set
            {
                if (material == value)
                    return;

                material = value;
                Invalidate(Invalidation.DrawNode);
            }
        }

        private IShader textureShader;
        private IShader glassShader;
        private IShader blurShader;

        IShader ITexturedShaderDrawable.TextureShader => textureShader;

        [BackgroundDependencyLoader]
        private void load(ShaderManager shaders)
        {
            textureShader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.TEXTURE);
            glassShader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.LIQUID_GLASS);
            blurShader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.BLUR);
        }

        protected override DrawNode CreateDrawNode() => new LiquidGlassDrawNode(this, sharedData);

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            effectEnabled = false;
            textureShader = null;
            glassShader = null;
            blurShader = null;

            if (sharedData.IsInitialised)
                sharedData.Dispose();
        }

        private class LiquidGlassDrawNode : TexturedShaderDrawNode
        {
            protected new LiquidGlassDrawable Source => (LiquidGlassDrawable)base.Source;

            protected readonly BufferedDrawNodeSharedData SharedData;

            private bool effectEnabled;
            private RectangleF screenSpaceDrawRectangle;

            private Vector2 frameBufferScale;
            private Vector2 blurSigma;
            private Vector2I blurRadius;
            private LiquidGlassMaterial material;
            private ColourInfo drawColour;

            private IShader glassShader;
            private IShader blurShader;

            private IUniformBuffer<LiquidGlassParameters> parametersBuffer;
            private BlurParameters blurParameters;

            public LiquidGlassDrawNode(LiquidGlassDrawable source, BufferedDrawNodeSharedData sharedData)
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
                blurSigma = Source.BlurSigma;
                blurRadius = new Vector2I(Blur.KernelSize(blurSigma.X), Blur.KernelSize(blurSigma.Y));
                material = Source.Material;
                drawColour = DrawColourInfo.Colour;

                glassShader = Source.glassShader;
                blurShader = Source.blurShader;
            }

            protected override void Draw(IRenderer renderer)
            {
                if (!effectEnabled || screenSpaceDrawRectangle.Width < 1 || screenSpaceDrawRectangle.Height < 1)
                    return;

                if (tryDrawSharedBackbuffer(renderer))
                    return;

                IFrameBuffer scene = renderer.CurrentFrameBuffer;

                if (scene == null)
                    return;

                RectangleF sceneRectangle = screenRectangleFromProjection(renderer.ProjectionMatrix);
                RectangleF captureRectangle = RectangleF.Intersect(screenSpaceDrawRectangle.Inflate(capturePadding), sceneRectangle);

                if (captureRectangle.Width < 1 || captureRectangle.Height < 1)
                    return;

                Vector2 frameBufferSize = new Vector2(
                    MathF.Ceiling(captureRectangle.Width * frameBufferScale.X),
                    MathF.Ceiling(captureRectangle.Height * frameBufferScale.Y));

                if (frameBufferSize.X < 1 || frameBufferSize.Y < 1)
                    return;

                // 正交投影把 captureRectangle 映射到整个效果缓冲，因此这里用实际纹素/屏幕像素比，
                // 避免 ceil 取整后子矩形出现半个纹素的错位。
                var texelsPerPixel = new Vector2(frameBufferSize.X / captureRectangle.Width, frameBufferSize.Y / captureRectangle.Height);

                var cardTextureRectangle = new RectangleF(
                    (screenSpaceDrawRectangle.X - captureRectangle.X) * texelsPerPixel.X,
                    (screenSpaceDrawRectangle.Y - captureRectangle.Y) * texelsPerPixel.Y,
                    screenSpaceDrawRectangle.Width * texelsPerPixel.X,
                    screenSpaceDrawRectangle.Height * texelsPerPixel.Y);

                var cardUvRect = new RectangleF(
                    cardTextureRectangle.X / frameBufferSize.X,
                    cardTextureRectangle.Y / frameBufferSize.Y,
                    cardTextureRectangle.Width / frameBufferSize.X,
                    cardTextureRectangle.Height / frameBufferSize.Y);

                var uvPerPixel = new Vector2(1 / captureRectangle.Width, 1 / captureRectangle.Height);

                if (!SharedData.IsInitialised)
                    SharedData.Initialise(renderer);

                SharedData.ResetCurrentEffectBuffer();

                using (establishFrameBufferViewport(renderer, captureRectangle, frameBufferSize))
                {
                    using (bindFrameBuffer(SharedData.MainBuffer, frameBufferSize))
                    {
                        // 1) 取下层切片：把 scene 纹理按其真实屏幕矩形绘制到本卡片的效果缓冲。
                        renderer.PushOrtho(captureRectangle);
                        renderer.Clear(new ClearInfo(new Color4(0, 0, 0, 0)));
                        renderer.SetBlend(BlendingParameters.None);

                        BindTextureShader(renderer);
                        renderer.DrawFrameBuffer(scene, sceneRectangle, ColourInfo.SingleColour(Color4.White));
                        UnbindTextureShader(renderer);

                        renderer.PopOrtho();
                    }

                    // 2) 对切片做高斯模糊（横/纵两趟）。
                    populateBlur(renderer);
                }

                // 3) 只把卡片子矩形按玻璃材质画回原位。
                base.Draw(renderer);
                drawGlass(renderer, SharedData.CurrentEffectBuffer, cardUvRect, uvPerPixel, cardTextureRectangle);
            }

            /// <summary>
            /// 模糊核与折射都会读到卡片外的像素，抓取区按两者外扩。
            /// </summary>
            private float capturePadding => MathF.Max(blurRadius.X, blurRadius.Y) + MathF.Max(material.RefractionStrength, 0) + 2;

            private bool tryDrawSharedBackbuffer(IRenderer renderer)
            {
                if (renderer.CurrentFrameBuffer != null)
                    return false;

                if (blurSigma.X <= 0)
                    return false;

                if (renderer is not Renderer concrete || concrete.SharedAcrylic == null || !concrete.SupportsBackbufferRegionCopy)
                    return false;

                if (!concrete.SharedAcrylic.TryGetBlur(screenSpaceDrawRectangle, blurSigma.X, ((ITexturedShaderDrawable)Source).TextureShader, blurShader, out var blurred, out var textureCoords))
                    return false;

                if (blurred.Size.X < 1 || blurred.Size.Y < 1)
                    return false;

                // 整屏快照天然包含卡片外的像素，这里只把屏幕像素换算到快照纹理的 UV。
                var uvPerPixel = new Vector2(
                    textureCoords.Width / MathF.Max(screenSpaceDrawRectangle.Width, 1) / blurred.Size.X,
                    textureCoords.Height / MathF.Max(screenSpaceDrawRectangle.Height, 1) / blurred.Size.Y);

                var cardUvRect = new RectangleF(
                    textureCoords.X / blurred.Size.X,
                    textureCoords.Y / blurred.Size.Y,
                    textureCoords.Width / blurred.Size.X,
                    textureCoords.Height / blurred.Size.Y);

                base.Draw(renderer);
                drawGlass(renderer, blurred, cardUvRect, uvPerPixel, textureCoords);
                return true;
            }

            private void drawGlass(IRenderer renderer, IFrameBuffer source, RectangleF cardUvRect, Vector2 uvPerPixel, RectangleF cardTextureRectangle)
            {
                parametersBuffer ??= renderer.CreateUniformBuffer<LiquidGlassParameters>();

                parametersBuffer.Data = new LiquidGlassParameters
                {
                    CardUvRect = new Vector4(cardUvRect.X, cardUvRect.Y, cardUvRect.Width, cardUvRect.Height),
                    UvPerPixel = uvPerPixel,
                    CardSize = new Vector2(screenSpaceDrawRectangle.Width, screenSpaceDrawRectangle.Height),
                    LightDirection = material.LightDirection,
                    Tint = new Vector4(material.Tint.R, material.Tint.G, material.Tint.B, material.Tint.A),
                    CornerRadius = material.CornerRadius,
                    RefractionDepth = material.RefractionDepth,
                    RefractionStrength = material.RefractionStrength,
                    ChromaticStrength = material.ChromaticStrength,
                    EdgeHighlight = material.EdgeHighlight,
                    InnerShadow = material.InnerShadow,
                    TintStrength = material.TintStrength,
                    EdgeSaturation = material.EdgeSaturation,
                };

                glassShader.BindUniformBlock("m_LiquidGlassParameters", parametersBuffer);
                glassShader.Bind();

                renderer.DrawQuad(source.Texture, Quad.FromRectangle(screenSpaceDrawRectangle), drawColour, textureCoords: cardTextureRectangle);

                glassShader.Unbind();
            }

            private void populateBlur(IRenderer renderer)
            {
                if (blurRadius.X <= 0 && blurRadius.Y <= 0)
                    return;

                renderer.PushScissorState(false);

                if (blurRadius.X > 0)
                    drawBlurredFrameBuffer(renderer, blurRadius.X, blurSigma.X, 0);

                if (blurRadius.Y > 0)
                    drawBlurredFrameBuffer(renderer, blurRadius.Y, blurSigma.Y, 90);

                renderer.PopScissorState();
            }

            private void drawBlurredFrameBuffer(IRenderer renderer, int kernelRadius, float sigma, float rotation)
            {
                IFrameBuffer current = SharedData.CurrentEffectBuffer;
                IFrameBuffer target = SharedData.GetNextEffectBuffer();

                renderer.SetBlend(BlendingParameters.None);

                using (bindFrameBuffer(target, current.Size))
                {
                    float radians = float.DegreesToRadians(rotation);

                    using (var blurParametersBuffer = renderer.CreateUniformBuffer<BlurParameters>())
                    {
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
            }

            /// <summary>
            /// 反推 <see cref="RendererExtensions.PushOrtho"/> 所用的屏幕矩形。
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

            private ValueInvokeOnDisposal<IFrameBuffer> bindFrameBuffer(IFrameBuffer frameBuffer, Vector2 size)
            {
                frameBuffer.Size = size;
                frameBuffer.Bind();
                return new ValueInvokeOnDisposal<IFrameBuffer>(frameBuffer, static b => b.Unbind());
            }

            private ValueInvokeOnDisposal<(LiquidGlassDrawNode node, IRenderer renderer)> establishFrameBufferViewport(IRenderer renderer, RectangleF captureRectangle, Vector2 frameBufferSize)
            {
                var screenSpaceMaskingRect = new RectangleI(
                    (int)MathF.Floor(captureRectangle.X),
                    (int)MathF.Floor(captureRectangle.Y),
                    (int)MathF.Ceiling(captureRectangle.Width) + 1,
                    (int)MathF.Ceiling(captureRectangle.Height) + 1);

                renderer.PushMaskingInfo(new MaskingInfo
                {
                    ScreenSpaceAABB = screenSpaceMaskingRect,
                    MaskingRect = captureRectangle,
                    ToMaskingSpace = Matrix3.Identity,
                    BlendRange = 1,
                    AlphaExponent = 1,
                }, true);

                renderer.PushViewport(new RectangleI(0, 0, (int)frameBufferSize.X, (int)frameBufferSize.Y));
                renderer.PushScissor(new RectangleI(0, 0, (int)frameBufferSize.X, (int)frameBufferSize.Y));
                renderer.PushScissorOffset(screenSpaceMaskingRect.Location);

                return new ValueInvokeOnDisposal<(LiquidGlassDrawNode node, IRenderer renderer)>((this, renderer), static tup => tup.node.returnViewport(tup.renderer));
            }

            private void returnViewport(IRenderer renderer)
            {
                renderer.PopScissorOffset();
                renderer.PopViewport();
                renderer.PopScissor();
                renderer.PopMaskingInfo();
            }

            protected override void Dispose(bool isDisposing)
            {
                base.Dispose(isDisposing);

                parametersBuffer?.Dispose();
                parametersBuffer = null;
            }

            [StructLayout(LayoutKind.Sequential, Pack = 1)]
            private record struct LiquidGlassParameters
            {
                public UniformVector4 CardUvRect;
                public UniformVector2 UvPerPixel;
                public UniformVector2 CardSize;
                public UniformVector2 LightDirection;
                private readonly UniformPadding8 alignment;
                public UniformVector4 Tint;
                public UniformFloat CornerRadius;
                public UniformFloat RefractionDepth;
                public UniformFloat RefractionStrength;
                public UniformFloat ChromaticStrength;
                public UniformFloat EdgeHighlight;
                public UniformFloat InnerShadow;
                public UniformFloat TintStrength;
                public UniformFloat EdgeSaturation;
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

    /// <summary>
    /// 液态玻璃的外观参数。所有长度单位都是屏幕像素。
    /// </summary>
    public record struct LiquidGlassMaterial
    {
        /// <summary>
        /// 圆角半径，用于边缘折射与高光的形状定义。
        /// </summary>
        public float CornerRadius;

        /// <summary>
        /// 边缘折射带的宽度。越大越像厚玻璃，过渡越缓。
        /// </summary>
        public float RefractionDepth;

        /// <summary>
        /// 折射最大位移量。
        /// </summary>
        public float RefractionStrength;

        /// <summary>
        /// 色散强度，作为折射位移的比例。
        /// </summary>
        public float ChromaticStrength;

        /// <summary>
        /// 边缘高光强度。
        /// </summary>
        public float EdgeHighlight;

        /// <summary>
        /// 背光侧内阴影强度。
        /// </summary>
        public float InnerShadow;

        /// <summary>
        /// 染色的混合权重。
        /// </summary>
        public float TintStrength;

        /// <summary>
        /// 染色颜色，alpha 作为染色基础透明度。
        /// </summary>
        public Color4 Tint;

        /// <summary>
        /// 边缘带饱和度提升量，制造厚玻璃的体积感。0 表示不改变饱和度。
        /// </summary>
        public float EdgeSaturation;

        /// <summary>
        /// 虚拟光源方向（屏幕空间，指向光源）。
        /// </summary>
        public Vector2 LightDirection;

        public static LiquidGlassMaterial Default => new LiquidGlassMaterial
        {
            CornerRadius = 0,
            RefractionDepth = 22,
            RefractionStrength = 7,
            ChromaticStrength = 0.15f,
            EdgeHighlight = 0.4f,
            InnerShadow = 0.2f,
            TintStrength = 0.35f,
            EdgeSaturation = 0.35f,
            Tint = new Color4(1f, 1f, 1f, 0.08f),
            LightDirection = new Vector2(-0.65f, -0.76f),
        };
    }
}
