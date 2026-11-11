// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.InteropServices;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Shaders;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Veldrid;

namespace osu.Framework.Graphics.Veldrid
{
    /// <summary>
    /// Composites the frame in <see cref="PixelFormat.R16G16B16A16Float"/> and presents it to a 10-bit or HDR swapchain.
    /// Standard keeps drawing directly to the swapchain and never constructs this.
    /// </summary>
    internal sealed class VeldridDisplayPresent : IDisposable
    {
        private const string vertex_hlsl = """
            cbuffer Params : register(b0)
            {
                float Exposure;
                float PaperWhiteNits;
                float Hdr;
                float Pad;
                float2 UvScale;
                float2 UvOffset;
            };

            struct Out
            {
                float4 Position : SV_Position;
                float2 Uv : TEXCOORD0;
            };

            Out main_vs(uint id : SV_VertexID)
            {
                float2 corner = float2((id << 1) & 2, id & 2);
                Out output;
                output.Position = float4(corner * float2(2, -2) + float2(-1, 1), 0, 1);
                output.Uv = corner * UvScale + UvOffset;
                return output;
            }
            """;

        private const string fragment_hlsl = """
            Texture2D SceneTexture : register(t0);
            SamplerState SceneSampler : register(s0);

            cbuffer Params : register(b0)
            {
                float Exposure;
                float PaperWhiteNits;
                float Hdr;
                float Pad;
                float2 UvScale;
                float2 UvOffset;
            };

            struct In
            {
                float4 Position : SV_Position;
                float2 Uv : TEXCOORD0;
            };

            float3 srgbToLinear(float3 c)
            {
                c = max(c, 0);
                return lerp(pow((c + 0.055) / 1.055, 2.4), c / 12.92, step(c, 0.04045));
            }

            float3 rec709ToBt2020(float3 c)
            {
                const float3x3 m = float3x3(
                    0.6274040, 0.3292820, 0.0433136,
                    0.0690973, 0.9195404, 0.0113623,
                    0.0163914, 0.0880133, 0.8955953);
                return mul(m, c);
            }

            float3 toPq(float3 nits)
            {
                const float m1 = 0.1593017578125;
                const float m2 = 78.84375;
                const float c1 = 0.8359375;
                const float c2 = 18.8515625;
                const float c3 = 18.6875;
                float3 yp = pow(max(nits, 0) / 10000.0, m1);
                return pow((c1 + c2 * yp) / (1.0 + c3 * yp), m2);
            }

            float4 main_ps(In input) : SV_Target
            {
                float4 color = SceneTexture.Sample(SceneSampler, input.Uv);

                if (Hdr > 0.5)
                {
                    float3 sceneLinear = srgbToLinear(color.rgb) * Exposure;
                    color.rgb = toPq(rec709ToBt2020(sceneLinear) * PaperWhiteNits);
                    color.a = 1;
                }
                else
                    color.rgb *= Exposure;

                return color;
            }
            """;

        private readonly VeldridDevice device;
        private readonly bool hdr;
        private readonly Shader vertexShader;
        private readonly Shader fragmentShader;
        private readonly Sampler sampler;
        private readonly DeviceBuffer parameters;
        private readonly ResourceLayout layout;
        private readonly GraphicsPipelineDescription pipelineTemplate;
        private readonly Pipeline presentPipeline;
        private Pipeline? blitPipeline;
        private OutputDescription blitOutputs;

        private Texture? color;
        private Texture? depth;
        private Framebuffer? framebuffer;
        private ResourceSet? resourceSet;

        public VeldridDisplayPresent(VeldridDevice device, SwapchainDisplayMode mode)
        {
            this.device = device;
            hdr = mode == SwapchainDisplayMode.Hdr10;

            var compiler = new ShaderCompilationStore();
            byte[] vertexBytes = compiler.CompileHlslBytecode(vertex_hlsl, "main_vs", "vs_5_0");
            byte[] fragmentBytes = compiler.CompileHlslBytecode(fragment_hlsl, "main_ps", "ps_5_0");

            vertexShader = device.Factory.CreateShader(new ShaderDescription(ShaderStages.Vertex, vertexBytes, "main_vs"));
            fragmentShader = device.Factory.CreateShader(new ShaderDescription(ShaderStages.Fragment, fragmentBytes, "main_ps"));

            var samplerDescription = new SamplerDescription
            {
                AddressModeU = SamplerAddressMode.Clamp,
                AddressModeV = SamplerAddressMode.Clamp,
                AddressModeW = SamplerAddressMode.Clamp,
                Filter = SamplerFilter.MinPointMagPointMipPoint,
            };
            sampler = device.Factory.CreateSampler(ref samplerDescription);

            parameters = device.Factory.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<PresentParams>(), BufferUsage.UniformBuffer));

            layout = device.Factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("Scene", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("Sampler", ResourceKind.Sampler, ShaderStages.Fragment),
                new ResourceLayoutElementDescription("Params", ResourceKind.UniformBuffer, ShaderStages.Fragment | ShaderStages.Vertex)));

            var blend = new BlendStateDescription
            {
                AttachmentStates = new[]
                {
                    new BlendAttachmentDescription
                    {
                        BlendEnabled = false,
                        ColorWriteMask = ColorWriteMask.All,
                    },
                },
            };

            pipelineTemplate = new GraphicsPipelineDescription
            {
                BlendState = blend,
                DepthStencilState = new DepthStencilStateDescription(depthTestEnabled: false, depthWriteEnabled: false, ComparisonKind.Always),
                RasterizerState = RasterizerStateDescription.CULL_NONE,
                PrimitiveTopology = PrimitiveTopology.TriangleList,
                ShaderSet = new ShaderSetDescription(Array.Empty<VertexLayoutDescription>(), new[] { vertexShader, fragmentShader }),
                ResourceLayouts = new[] { layout },
                Outputs = device.Device.SwapchainFramebuffer.OutputDescription,
            };

            presentPipeline = device.Factory.CreateGraphicsPipeline(ref pipelineTemplate);
        }

        public Framebuffer Framebuffer => framebuffer ?? throw new InvalidOperationException("Display present target is not allocated.");

        public Texture Color => color ?? throw new InvalidOperationException("Display present target is not allocated.");

        public bool IsAllocated => framebuffer != null;

        public void Resize(uint width, uint height)
        {
            if (width == 0 || height == 0)
                return;

            if (color != null && color.Width == width && color.Height == height)
                return;

            resourceSet?.Dispose();
            framebuffer?.Dispose();
            depth?.Dispose();
            color?.Dispose();

            color = device.Factory.CreateTexture(TextureDescription.Texture2D(width, height, 1, 1, PixelFormat.R16G16B16A16Float, TextureUsage.RenderTarget | TextureUsage.Sampled));
            depth = device.Factory.CreateTexture(TextureDescription.Texture2D(width, height, 1, 1, PixelFormat.R16UNorm, TextureUsage.DepthStencil));
            framebuffer = device.Factory.CreateFramebuffer(new FramebufferDescription(depth, color));
            resourceSet = device.Factory.CreateResourceSet(new ResourceSetDescription(layout, color, sampler, parameters));
        }

        public void DrawToSwapchain(CommandList commands)
        {
            if (framebuffer == null)
                return;

            var swapchain = device.Device.SwapchainFramebuffer;
            draw(commands, swapchain, 0, 0, swapchain.Width, swapchain.Height, 1, 1, 0, 0, applyExposure: true, applyPq: hdr);
        }

        public void CopyRegion(CommandList commands, Framebuffer destination, RectangleI screenRect)
        {
            if (color == null)
                return;

            var dest = destination.ColorTargets[0].Target;

            if (dest.Format == color.Format)
            {
                commands.CopyTexture(
                    color, (uint)screenRect.X, (uint)screenRect.Y, 0, 0, 0,
                    dest, 0, 0, 0, 0, 0,
                    (uint)screenRect.Width, (uint)screenRect.Height, 1, 1);
                return;
            }

            draw(commands, destination, 0, 0, (uint)screenRect.Width, (uint)screenRect.Height,
                screenRect.Width / (float)color.Width,
                screenRect.Height / (float)color.Height,
                screenRect.X / (float)color.Width,
                screenRect.Y / (float)color.Height,
                applyExposure: false,
                applyPq: false);
        }

        public Image<Rgba32> TakeScreenshot()
        {
            if (color == null || framebuffer == null)
                throw new InvalidOperationException("Display present target is not allocated.");

            using var copy = device.Factory.CreateTexture(TextureDescription.Texture2D(color.Width, color.Height, 1, 1, PixelFormat.B8G8R8A8UNorm, TextureUsage.RenderTarget));
            using var copyFramebuffer = device.Factory.CreateFramebuffer(new FramebufferDescription(null, copy));
            using var commands = device.Factory.CreateCommandList();
            using var fence = device.Factory.CreateFence(false);

            commands.Begin();
            draw(commands, copyFramebuffer, 0, 0, color.Width, color.Height, 1, 1, 0, 0, applyExposure: false, applyPq: false);
            commands.End();
            device.Device.SubmitCommands(commands, fence);
            device.Device.WaitForFence(fence);

            return device.ExtractTexture<Bgra32>(copy, flipVertical: !device.IsUvOriginTopLeft);
        }

        public void Dispose()
        {
            resourceSet?.Dispose();
            framebuffer?.Dispose();
            depth?.Dispose();
            color?.Dispose();
            blitPipeline?.Dispose();
            presentPipeline.Dispose();
            layout.Dispose();
            parameters.Dispose();
            sampler.Dispose();
            vertexShader.Dispose();
            fragmentShader.Dispose();
        }

        private void draw(CommandList commands, Framebuffer target, uint x, uint y, uint width, uint height, float scaleX, float scaleY, float offsetX, float offsetY, bool applyExposure, bool applyPq)
        {
            if (resourceSet == null)
                return;

            float paperWhite = DisplayOutputPolicy.ResolveHdrPaperWhiteNits(
                HostStartupDisplayOutput.HdrPaperWhiteNits,
                device.Device.MainSwapchain.MonitorDisplay);

            if (applyPq)
            {
                var caps = device.Device.MainSwapchain.MonitorDisplay;
                float peak = caps?.MaxLuminance ?? paperWhite;
                float min = caps?.MinLuminance ?? 0;
                float fullFrame = caps?.MaxFullFrameLuminance ?? paperWhite;
                device.Device.MainSwapchain.SetHdr10Metadata(
                    peak > 1 ? MathF.Max(peak, paperWhite) : paperWhite,
                    min,
                    paperWhite,
                    fullFrame > 1 ? MathF.Min(fullFrame, paperWhite) : paperWhite);
            }

            var data = new PresentParams
            {
                Exposure = applyExposure ? HostStartupDisplayOutput.SdrExposure : 1,
                PaperWhiteNits = paperWhite,
                Hdr = applyPq ? 1 : 0,
                UvScaleX = scaleX,
                UvScaleY = scaleY,
                UvOffsetX = offsetX,
                UvOffsetY = offsetY,
            };

            commands.UpdateBuffer(parameters, 0, ref data);
            commands.SetFramebuffer(target);
            commands.SetViewport(0, new Viewport(x, y, width, height, 0, 1));
            commands.SetScissorRect(0, x, y, width, height);
            commands.SetPipeline(pipelineFor(target));
            commands.SetGraphicsResourceSet(0, resourceSet);
            commands.Draw(3);
        }

        private Pipeline pipelineFor(Framebuffer target)
        {
            var outputs = target.OutputDescription;

            if (outputs.Equals(pipelineTemplate.Outputs))
                return presentPipeline;

            if (blitPipeline != null && outputs.Equals(blitOutputs))
                return blitPipeline;

            blitPipeline?.Dispose();
            var description = pipelineTemplate;
            description.Outputs = outputs;
            blitPipeline = device.Factory.CreateGraphicsPipeline(ref description);
            blitOutputs = outputs;
            return blitPipeline;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PresentParams
        {
            public float Exposure;
            public float PaperWhiteNits;
            public float Hdr;
            public float Pad;
            public float UvScaleX;
            public float UvScaleY;
            public float UvOffsetX;
            public float UvOffsetY;
        }
    }
}
