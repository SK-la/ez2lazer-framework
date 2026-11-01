// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using osu.Framework.Graphics;
using osuTK;

namespace osu.Framework.Tests.Ez
{
    [TestFixture]
    public class EffectBufferSizeQuantiserTest
    {
        /// <summary>
        /// 重建次数 == 尺寸赋值变化的次数，因此可以直接量化改动收益。
        /// 这里模拟主菜单 logo 随音乐脉动：以约 60fps 做幅度 ±5% 的往复缩放。
        /// </summary>
        [Test]
        public void TestPulsingCardDoesNotReallocateEveryFrame()
        {
            const int frames = 600;
            const float base_size = 340;
            const float pulse_amplitude = 0.05f;

            int quantisedChanges = 0;
            int unquantisedChanges = 0;

            var quantiser = new EffectBufferSizeQuantiser();
            Vector2 previousQuantised = Vector2.Zero;
            Vector2 previousUnquantised = Vector2.Zero;

            for (int i = 0; i < frames; i++)
            {
                float scale = 1 + pulse_amplitude * MathF.Sin(i * 0.12f);
                var size = new Vector2(base_size * scale);

                // 旧行为：精确到 1px，逐帧重新分配。
                var unquantised = new Vector2(MathF.Ceiling(size.X), MathF.Ceiling(size.Y));

                if (unquantised != previousUnquantised)
                {
                    unquantisedChanges++;
                    previousUnquantised = unquantised;
                }

                Vector2 quantised = quantiser.Quantise(size);

                if (quantised != previousQuantised)
                {
                    quantisedChanges++;
                    previousQuantised = quantised;
                }
            }

            TestContext.WriteLine($"未量化：{unquantisedChanges} 次变更 / {frames} 帧");
            TestContext.WriteLine($"量化后：{quantisedChanges} 次变更 / {frames} 帧");

            // 未量化时尺寸几乎每帧都在变，正是被修复的开销来源。
            Assert.That(unquantisedChanges, Is.GreaterThan(frames / 2), "未量化路径应当几乎每帧重建，作为对照");

            // 量化后应大幅下降：600 帧里只允许个位数次容量变化。
            Assert.That(quantisedChanges, Is.LessThan(10));
            Assert.That(quantisedChanges, Is.LessThan(unquantisedChanges / 10));
        }

        /// <summary>
        /// 尺寸停在粒度边界附近时必须完全不抖动，否则迟滞就失去了意义。
        /// </summary>
        [Test]
        public void TestNoReallocationWhenSizeWobblesAcrossBucketBoundary()
        {
            var quantiser = new EffectBufferSizeQuantiser();

            // 336 的边界：335.9 与 336.1 会分别向上取到 336 与 352。
            // 先完成首次分配（这一帧允许变化），之后的抖动必须一次都不再重建。
            quantiser.Quantise(new Vector2(336.1f));

            int changes = 0;
            Vector2 previous = quantiser.AllocatedSize;

            for (int i = 0; i < 200; i++)
            {
                var size = new Vector2(i % 2 == 0 ? 335.9f : 336.1f);
                Vector2 result = quantiser.Quantise(size);

                if (result != previous)
                {
                    changes++;
                    previous = result;
                }
            }

            Assert.That(changes, Is.Zero, "边界抖动不应触发任何重建");
        }

        /// <summary>
        /// 容量只在必要时增长，且明显变小时才收缩，不会无限膨胀。
        /// </summary>
        [Test]
        public void TestCapacityGrowsOnDemandAndShrinksOnlyWhenClearlySmaller()
        {
            var quantiser = new EffectBufferSizeQuantiser();

            Assert.That(quantiser.Quantise(new Vector2(340)), Is.EqualTo(new Vector2(352)));

            // 略小于当前容量：按迟滞保留容量，避免边界乒乓。
            Assert.That(quantiser.Quantise(new Vector2(340)), Is.EqualTo(new Vector2(352)));

            // 明显变小：回收。
            Assert.That(quantiser.Quantise(new Vector2(200)), Is.EqualTo(new Vector2(208)));

            // 增长立即生效。
            Assert.That(quantiser.Quantise(new Vector2(700)), Is.EqualTo(new Vector2(704)));
        }

        /// <summary>
        /// 零尺寸（卡片尚未布局）必须被钳到至少一级，避免分配 0 尺寸缓冲。
        /// </summary>
        [Test]
        public void TestZeroSizeIsClamped()
        {
            var quantiser = new EffectBufferSizeQuantiser();

            Assert.That(quantiser.Quantise(Vector2.Zero), Is.EqualTo(new Vector2(EffectBufferSizeQuantiser.SIZE_QUANTISATION)));
        }
    }
}
