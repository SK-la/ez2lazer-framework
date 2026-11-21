// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Veldrid;

namespace osu.Framework.Graphics.Veldrid.Buffers.Staging
{
    /// <summary>
    /// A staging buffer that uses a buffer in managed memory as its storage medium.
    /// </summary>
    internal class ManagedStagingBuffer<T> : IStagingBuffer<T>
        where T : unmanaged
    {
        private readonly VeldridRenderer renderer;
        private readonly Memory<T> vertexMemory;
        private readonly IMemoryOwner<T> memoryOwner;

        public ManagedStagingBuffer(VeldridRenderer renderer, uint count)
        {
            this.renderer = renderer;
            Count = count;
            SizeInBytes = (uint)(Unsafe.SizeOf<T>() * count);

            memoryOwner = MemoryPool<T>.Shared.Rent(checked((int)Count));
            vertexMemory = memoryOwner.Memory[..checked((int)Count)];
            vertexMemory.Span.Clear();
        }

        public uint SizeInBytes { get; }

        public uint Count { get; }

        public BufferUsage CopyTargetUsageFlags => BufferUsage.Dynamic;

        public Span<T> Data => vertexMemory.Span;

        public void CopyTo(DeviceBuffer buffer, uint srcOffset, uint dstOffset, uint count)
        {
            renderer.Device.UpdateBuffer(
                buffer,
                (uint)(dstOffset * Unsafe.SizeOf<T>()),
                ref Data[(int)srcOffset],
                (uint)(count * Unsafe.SizeOf<T>()));
        }

        public void Dispose()
        {
            memoryOwner.Dispose();
        }
    }
}
