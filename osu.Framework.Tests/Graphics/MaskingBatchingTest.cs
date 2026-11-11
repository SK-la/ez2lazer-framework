// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NUnit.Framework;
using osu.Framework.Development;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Rendering.Dummy;
using osu.Framework.Graphics.Rendering.Vertices;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Platform;
using osuTK;

namespace osu.Framework.Tests.Graphics
{
    /// <summary>
    /// Quantifies masking batching: how many vertices end up in each draw call, and with which masking
    /// parameters each draw is submitted. Runs on the dummy renderer, so the numbers are deterministic and
    /// independent of a GPU.
    /// </summary>
    [TestFixture]
    public class MaskingBatchingTest
    {
        private DummyRenderer renderer = null!;
        private RecordingShader shader = null!;
        private RecordingBatch batch = null!;
        private bool wasDrawThread;

        [SetUp]
        public void SetUp()
        {
            wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;
            renderer = new DummyRenderer();
            ((IRenderer)renderer).Initialise(new TestSurface());
            renderer.BeginFrame(new Vector2(100, 100));
            shader = new RecordingShader();
            renderer.BindShader(shader);
            batch = new RecordingBatch(renderer);
        }

        [TearDown]
        public void TearDown()
        {
            renderer.FinishFrame();
            shader.Dispose();
            batch.Dispose();
            ThreadSafety.IsDrawThread = wasDrawThread;
        }

        [Test]
        public void TestEquivalentSiblingMasksStayInOneBatch()
        {
            var mask = createMask();

            for (int i = 0; i < 100; i++)
            {
                renderer.PushMaskingInfo(mask);
                batch.AddQuad();
                renderer.PopMaskingInfo();
            }

            renderer.FlushCurrentBatch(null);
            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 400 }));
            Assert.That(shader.Draws[0].IsMasking.Value, Is.True);
            Assert.That(shader.Draws[0].CornerRadius.Value, Is.EqualTo(mask.CornerRadius));
        }

        [Test]
        public void TestEmptyDifferentMaskDoesNotSplitBatch()
        {
            var mask = createMask();
            renderer.PushMaskingInfo(mask);
            batch.AddQuad();
            var emptyMask = mask;
            emptyMask.CornerRadius = 17;
            renderer.PushMaskingInfo(emptyMask);
            renderer.PopMaskingInfo();
            batch.AddQuad();
            renderer.FlushCurrentBatch(null);
            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 8 }));
        }

        [Test]
        public void TestEncodableMasksOnTexturedQuadsStayInOneBatch()
        {
            var quadBatch = new RecordingQuadBatch(renderer);
            renderer.BeginVertexEncodedBatch(true);

            var mask = createMask();
            renderer.PushMaskingInfo(mask);
            quadBatch.AddQuad();
            renderer.PopMaskingInfo();

            mask.CornerRadius = 17;
            mask.ScreenSpaceAABB = new RectangleI(30, 30, 40, 40);
            mask.MaskingRect = new RectangleF(30, 30, 40, 40);
            renderer.PushMaskingInfo(mask);
            quadBatch.AddQuad();
            renderer.PopMaskingInfo();

            renderer.EndVertexEncodedBatch();
            renderer.FlushCurrentBatch(null);

            Assert.That(quadBatch.DrawCounts, Is.EqualTo(new[] { 8 }));
            Assert.That(shader.Draws, Has.Count.EqualTo(1));
            Assert.That(shader.Draws[0].IsMasking.Value, Is.False);
        }

        [Test]
        public void TestBorderedMaskStillSplitsTexturedQuads()
        {
            var quadBatch = new RecordingQuadBatch(renderer);

            var mask = createMask();
            renderer.BeginVertexEncodedBatch(renderer.IsVertexMaskEncodable);
            renderer.PushMaskingInfo(mask);
            quadBatch.AddQuad();
            renderer.PopMaskingInfo();
            renderer.EndVertexEncodedBatch();

            mask.BorderThickness = 2;
            renderer.BeginVertexEncodedBatch(false);
            renderer.PushMaskingInfo(mask);
            quadBatch.AddQuad();
            renderer.PopMaskingInfo();
            renderer.EndVertexEncodedBatch();

            renderer.FlushCurrentBatch(null);

            Assert.That(quadBatch.DrawCounts, Is.EqualTo(new[] { 4, 4 }));
            Assert.That(shader.Draws[1].IsMasking.Value, Is.True);
            Assert.That(shader.Draws[1].BorderThickness.Value, Is.GreaterThan(0));
        }

        [Test]
        public void TestDifferentShaderParametersSplitBeforeNewVertices()
        {
            var mask = createMask();
            renderer.PushMaskingInfo(mask);
            batch.AddQuad();
            renderer.PopMaskingInfo();
            mask.CornerRadius = 17;
            renderer.PushMaskingInfo(mask);
            batch.AddQuad();
            renderer.PopMaskingInfo();
            renderer.FlushCurrentBatch(null);

            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 4, 4 }));
            Assert.That(shader.Draws[0].CornerRadius.Value, Is.EqualTo(4));
            Assert.That(shader.Draws[1].CornerRadius.Value, Is.EqualTo(17));
        }

        [Test]
        public void TestCullingMetadataDoesNotSplitBatch()
        {
            var mask = createMask();
            renderer.PushMaskingInfo(mask);
            batch.AddQuad();
            renderer.PopMaskingInfo();
            mask.ConservativeScreenSpaceQuad = new Quad(1, 2, 3, 4);
            renderer.PushMaskingInfo(mask);
            batch.AddQuad();
            renderer.FlushCurrentBatch(null);
            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 8 }));
            Assert.That(renderer.CurrentMaskingInfo.ConservativeScreenSpaceQuad, Is.EqualTo(mask.ConservativeScreenSpaceQuad));
        }

        [Test]
        public void TestDifferentEffectiveScissorsSplitBatch()
        {
            var mask = createMask();
            renderer.PushMaskingInfo(mask);
            batch.AddQuad();
            renderer.PopMaskingInfo();
            mask.ScreenSpaceAABB = new RectangleI(20, 20, 30, 30);
            renderer.PushMaskingInfo(mask);
            batch.AddQuad();
            renderer.FlushCurrentBatch(null);
            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 4, 4 }));
        }

        [Test]
        public void TestIdenticalMaskStillPushesAndRestoresScissor()
        {
            var mask = createMask();
            renderer.PushMaskingInfo(mask);
            var parentScissor = renderer.Scissor;
            var narrowScissor = new RectangleI(20, 20, 10, 10);
            renderer.PushScissor(narrowScissor);
            renderer.PushMaskingInfo(mask, true);
            Assert.That(renderer.Scissor, Is.EqualTo(parentScissor));
            renderer.PopMaskingInfo();
            Assert.That(renderer.Scissor, Is.EqualTo(narrowScissor));
            renderer.PopScissor();
            Assert.That(renderer.Scissor, Is.EqualTo(parentScissor));
            renderer.PopMaskingInfo();
            Assert.That(renderer.Scissor, Is.EqualTo(new RectangleI(0, 0, 100, 100)));
        }

        [Test]
        public void TestIdenticalDefaultMaskStillEnablesShaderMasking()
        {
            var mask = renderer.CurrentMaskingInfo;
            batch.AddQuad();
            renderer.PushMaskingInfo(mask);
            Assert.That(renderer.IsMaskingActive, Is.True);
            batch.AddQuad();
            renderer.PopMaskingInfo();
            Assert.That(renderer.IsMaskingActive, Is.False);
            batch.AddQuad();
            renderer.FlushCurrentBatch(null);

            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 4, 4, 4 }));
            Assert.That(shader.Draws[0].IsMasking.Value, Is.False);
            Assert.That(shader.Draws[1].IsMasking.Value, Is.True);
            Assert.That(shader.Draws[2].IsMasking.Value, Is.False);
        }

        [Test]
        public void TestUnrelatedFlushRetainsSubmittedMask()
        {
            renderer.PushMaskingInfo(createMask());
            batch.AddQuad();
            renderer.PopMaskingInfo();
            renderer.PushScissorState(false);
            Assert.That(shader.Draws[0].IsMasking.Value, Is.True);
            Assert.That(shader.Draws[0].CornerRadius.Value, Is.EqualTo(4));
            renderer.PopScissorState();
            batch.AddQuad();
            renderer.FlushCurrentBatch(null);
            Assert.That(shader.Draws[1].IsMasking.Value, Is.False);
        }

        [Test]
        public void TestVertexMaskIndexReusesOneSlotPerDistinctMask()
        {
            var mask = createMask();
            renderer.PushMaskingInfo(mask);
            renderer.BeginVertexEncodedBatch(true);

            var first = new TexturedVertex2D(renderer);
            var second = new TexturedVertex2D(renderer);
            renderer.ApplyVertexMask(ref first);
            renderer.ApplyVertexMask(ref second);

            mask.CornerRadius = 17;
            renderer.PushMaskingInfo(mask);
            var third = new TexturedVertex2D(renderer);
            renderer.ApplyVertexMask(ref third);

            renderer.PopMaskingInfo();
            var fourth = new TexturedVertex2D(renderer);
            renderer.ApplyVertexMask(ref fourth);
            renderer.EndVertexEncodedBatch();

            Assert.That(first.MaskIndex, Is.EqualTo(1));
            Assert.That(second.MaskIndex, Is.EqualTo(1));
            Assert.That(third.MaskIndex, Is.EqualTo(2));
            Assert.That(fourth.MaskIndex, Is.EqualTo(1));
            Assert.That(Marshal.SizeOf<TexturedVertex2D>(), Is.EqualTo(64));
            Assert.That(Marshal.SizeOf<VertexMaskRecord>(), Is.EqualTo(vertex_mask_record_size));
            Assert.That(Marshal.SizeOf<VertexMaskTable>(), Is.EqualTo(VertexMaskTable.CAPACITY * vertex_mask_record_size));
        }

        [Test]
        public void TestUnencodedVerticesDoNotTakeAMaskSlot()
        {
            renderer.PushMaskingInfo(createMask());
            renderer.BeginVertexEncodedBatch(false);

            var vertex = new TexturedVertex2D(renderer);
            renderer.ApplyVertexMask(ref vertex);
            renderer.EndVertexEncodedBatch();

            Assert.That(vertex.MaskIndex, Is.EqualTo(0));
        }

        [Test]
        public void TestFullMaskTableFlushesBeforeTheNextDistinctMask()
        {
            var quadBatch = new RecordingQuadBatch(renderer);
            renderer.BeginVertexEncodedBatch(true);
            quadBatch.AddQuad();

            for (int i = 0; i < VertexMaskTable.CAPACITY; i++)
            {
                var mask = createMask();
                mask.CornerRadius = i + 1;
                renderer.PushMaskingInfo(mask);

                var vertex = new TexturedVertex2D(renderer);
                renderer.ApplyVertexMask(ref vertex);
                Assert.That(vertex.MaskIndex, Is.EqualTo(i + 1));
            }

            Assert.That(quadBatch.DrawCounts, Is.Empty);

            var overflowMask = createMask();
            overflowMask.CornerRadius = VertexMaskTable.CAPACITY + 10;
            renderer.PushMaskingInfo(overflowMask);

            var overflow = new TexturedVertex2D(renderer);
            renderer.ApplyVertexMask(ref overflow);
            renderer.EndVertexEncodedBatch();

            Assert.That(quadBatch.DrawCounts, Is.EqualTo(new[] { 4 }));
            Assert.That(overflow.MaskIndex, Is.EqualTo(1));
        }

        [Test]
        public void TestFullMaskTableFlushesBeforeTheNextQuadStarts()
        {
            var quadBatch = new RecordingQuadBatch(renderer);
            renderer.BeginVertexEncodedBatch(true);
            quadBatch.AddQuad();

            for (int i = 0; i < VertexMaskTable.CAPACITY; i++)
            {
                var mask = createMask();
                mask.CornerRadius = i + 1;
                renderer.PushMaskingInfo(mask);

                var vertex = new TexturedVertex2D(renderer);
                renderer.ApplyVertexMask(ref vertex);
            }

            var overflowMask = createMask();
            overflowMask.CornerRadius = VertexMaskTable.CAPACITY + 10;
            renderer.PushMaskingInfo(overflowMask);
            renderer.BeginVertexEncodedBatch(true);

            Assert.That(quadBatch.DrawCounts, Is.EqualTo(new[] { 4 }));

            var overflow = new TexturedVertex2D(renderer);
            renderer.ApplyVertexMask(ref overflow);
            renderer.EndVertexEncodedBatch();

            Assert.That(overflow.MaskIndex, Is.EqualTo(1));
            Assert.That(quadBatch.DrawCounts, Is.EqualTo(new[] { 4 }));
        }

        private const int vertex_mask_record_size = 80;

        [Test]
        public void TestDirectDrawAppliesPendingMaskAfterFlushingOldGeometry()
        {
            renderer.PushMaskingInfo(createMask());
            batch.AddQuad();
            renderer.PopMaskingInfo();
            renderer.DrawVertices(PrimitiveTopology.Triangles, 0, 3);
            Assert.That(batch.DrawCounts, Is.EqualTo(new[] { 4 }));
            Assert.That(shader.Draws.Count, Is.EqualTo(2));
            Assert.That(shader.Draws[0].IsMasking.Value, Is.True);
            Assert.That(shader.Draws[1].IsMasking.Value, Is.False);
        }

        private static MaskingInfo createMask() => new MaskingInfo
        {
            ScreenSpaceAABB = new RectangleI(10, 10, 80, 80),
            MaskingRect = new RectangleF(10, 10, 80, 80),
            ToMaskingSpace = Matrix3.Identity,
            CornerRadius = 4,
            CornerExponent = 2.5f,
            BlendRange = 1,
            AlphaExponent = 1,
        };

        private sealed class RecordingQuadBatch : IVertexBatch<TexturedVertex2D>
        {
            private readonly Renderer renderer;
            private int pendingVertices;

            public RecordingQuadBatch(Renderer renderer)
            {
                this.renderer = renderer;
            }

            public int Size => 1000;
            public List<int> DrawCounts { get; } = new List<int>();
            public Action<TexturedVertex2D> AddAction => _ => AddQuad();
            public void Add(TexturedVertex2D vertex) => AddQuad();

            public void AddQuad()
            {
                renderer.SetActiveBatch(this);
                pendingVertices += 4;
            }

            public int Draw()
            {
                int count = pendingVertices;
                pendingVertices = 0;

                if (count > 0)
                {
                    renderer.DrawBatchVertices(PrimitiveTopology.Triangles, 0, count);
                    DrawCounts.Add(count);
                }

                return count;
            }

            void IVertexBatch.ResetCounters() => pendingVertices = 0;
            public void Dispose() { }
        }

        private sealed class RecordingBatch : IVertexBatch
        {
            private readonly Renderer renderer;
            private int pendingVertices;
            public readonly List<int> DrawCounts = new List<int>();

            public RecordingBatch(Renderer renderer)
            {
                this.renderer = renderer;
            }

            public int Size => 1000;

            public void AddQuad()
            {
                renderer.SetActiveBatch(this);
                pendingVertices += 4;
            }

            public int Draw()
            {
                int count = pendingVertices;
                pendingVertices = 0;

                if (count > 0)
                {
                    renderer.DrawBatchVertices(PrimitiveTopology.Triangles, 0, count);
                    DrawCounts.Add(count);
                }

                return count;
            }

            void IVertexBatch.ResetCounters() => pendingVertices = 0;
            public void Dispose() { }
        }

        private sealed class RecordingShader : IShader
        {
            public readonly List<GlobalUniformData> Draws = new List<GlobalUniformData>();
            public bool IsLoaded => true;
            public bool IsBound => true;
            public void Bind() { }
            public void Unbind() { }
            public void Dispose() { }

            public void BindUniformBlock(string blockName, IUniformBuffer buffer)
            {
                if (buffer is IUniformBuffer<GlobalUniformData> global)
                    Draws.Add(global.Data);
            }
        }

        private sealed class TestSurface : IGraphicsSurface
        {
            public IntPtr WindowHandle => IntPtr.Zero;
            public GraphicsSurfaceType Type => GraphicsSurfaceType.Direct3D11;
            public void Initialise() { }
            public System.Drawing.Size GetDrawableSize() => new System.Drawing.Size(100, 100);
        }
    }
}
