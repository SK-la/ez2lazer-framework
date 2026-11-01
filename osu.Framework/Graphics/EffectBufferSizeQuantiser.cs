// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osuTK;

namespace osu.Framework.Graphics
{
    /// <summary>
    /// 效果缓冲的容量分配策略：向上对齐到固定粒度，并带收缩迟滞，使连续缩放时复用同一批缓冲。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 效果缓冲一旦被赋予新的 <see cref="Rendering.IFrameBuffer.Size"/> 就会重建底层纹理，
    /// 因此尺寸必须只在必要时改变。本类型只在容量溢出时立即扩容、明显偏小时才收缩。
    /// </para>
    /// <para>
    /// 迟滞是必须的：尺寸停在粒度边界附近时（例如 335.9 与 336.1），纯向上取整会在相邻两级之间
    /// 逐帧横跳，等于每帧都重建缓冲，正是本类型要消除的开销。
    /// </para>
    /// </remarks>
    internal struct EffectBufferSizeQuantiser
    {
        /// <summary>
        /// 容量对齐粒度。缓冲尺寸向上取到该值的整数倍。
        /// </summary>
        internal const float SIZE_QUANTISATION = 16;

        /// <summary>
        /// 收缩阈值。需要尺寸相对当前容量小于该比例时才回收，用于挡住粒度边界处的逐帧横跳。
        /// </summary>
        internal const float SHRINK_RATIO = 0.9f;

        private Vector2 allocatedSize;

        /// <summary>
        /// 当前已分配的容量。尚未分配时为 <see cref="Vector2.Zero"/>。
        /// </summary>
        internal readonly Vector2 AllocatedSize => allocatedSize;

        /// <summary>
        /// 取得可安全复用的缓冲容量，必要时才改变分配。
        /// </summary>
        /// <param name="size">未对齐的期望尺寸。</param>
        /// <returns>可直接赋给 <see cref="Rendering.IFrameBuffer.Size"/> 的容量。</returns>
        internal Vector2 Quantise(Vector2 size)
        {
            // 未对齐尺寸可能因卡片矩形为 0 而为负或零，向上取整后钳到至少一级，避免分配 0 尺寸缓冲。
            var desired = new Vector2(
                MathF.Max(SIZE_QUANTISATION, MathF.Ceiling(size.X / SIZE_QUANTISATION) * SIZE_QUANTISATION),
                MathF.Max(SIZE_QUANTISATION, MathF.Ceiling(size.Y / SIZE_QUANTISATION) * SIZE_QUANTISATION));

            // 首次分配，或任一轴溢出当前容量：立即扩容。
            if (!isAllocated || desired.X > allocatedSize.X || desired.Y > allocatedSize.Y)
                allocatedSize = desired;
            // 两轴都明显变小才收缩，避免边界乒乓。
            else if (desired.X < allocatedSize.X * SHRINK_RATIO && desired.Y < allocatedSize.Y * SHRINK_RATIO)
                allocatedSize = desired;

            return allocatedSize;
        }

        private readonly bool isAllocated => allocatedSize.X >= 1 && allocatedSize.Y >= 1;
    }
}
