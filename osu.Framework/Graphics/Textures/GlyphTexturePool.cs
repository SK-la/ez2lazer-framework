// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Veldrid;
using osu.Framework.Graphics.Veldrid.Textures;

namespace osu.Framework.Graphics.Textures
{
    internal sealed class GlyphTexturePool : IDisposable
    {
        private const int default_page_size = 2048;
        private const int default_page_count = 4;
        private const int glyph_padding = 4;
        private const int reclamation_retry_interval = 128;

        private static readonly Lock pool_lock = new Lock();
        private static readonly ConditionalWeakTable<IRenderer, GlyphTexturePool> pools = new ConditionalWeakTable<IRenderer, GlyphTexturePool>();

        private readonly Lock allocationLock = new Lock();
        private readonly VeldridGlyphTextureArray nativeTexture;
        private readonly int pageSize;
        private readonly int pageCount;
        private readonly List<GlyphAllocation> allocations = new List<GlyphAllocation>();
        private readonly List<FreeRegion> freeRegions = new List<FreeRegion>();

        private int currentLayer;
        private int cursorX;
        private int cursorY;
        private int shelfHeight;
        private int allocationAttempts;
        private int nextReclamationAttempt;
        private int referenceCount;
        private int fallbackCount;
        private int glyphCount;
        private bool disposed;

        public Texture Texture { get; }

        public int FallbackCount => Volatile.Read(ref fallbackCount);

        public int GlyphCount => Volatile.Read(ref glyphCount);

        public static bool IsEnabled(IRenderer renderer)
            => Environment.GetEnvironmentVariable("EZ2LAZER_GLYPH_TEXTURE_ARRAY") == "1"
               && renderer is VeldridRenderer { SurfaceType: Platform.GraphicsSurfaceType.Direct3D11 };

        public static GlyphTexturePool? Create(IRenderer renderer)
            => create(renderer, forceEnable: false);

        internal static GlyphTexturePool? CreateForVisualTest(IRenderer renderer)
            => renderer is VeldridRenderer { SurfaceType: Platform.GraphicsSurfaceType.Direct3D11 }
                ? create(renderer, forceEnable: true)
                : null;

        private static GlyphTexturePool? create(IRenderer renderer, bool forceEnable)
        {
            if ((!forceEnable && !IsEnabled(renderer)) || renderer is not IVeldridRenderer veldridRenderer)
                return null;

            lock (pool_lock)
            {
                if (pools.TryGetValue(renderer, out var existing))
                {
                    existing.referenceCount++;
                    return existing;
                }

                var created = new GlyphTexturePool(veldridRenderer)
                {
                    referenceCount = 1
                };
                pools.Add(renderer, created);
                return created;
            }
        }

        private GlyphTexturePool(IVeldridRenderer renderer, int pageSize = default_page_size, int pageCount = default_page_count)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);

            this.pageSize = pageSize;
            this.pageCount = pageCount;
            nativeTexture = new VeldridGlyphTextureArray(renderer, pageSize, pageSize, (uint)pageCount);
            Texture = new Texture(nativeTexture, WrapMode.None, WrapMode.None);
            Texture.ScaleAdjust = 1;
        }

        internal static GlyphTexturePool CreateForTesting(IVeldridRenderer renderer, int pageSize, int pageCount)
            => new GlyphTexturePool(renderer, pageSize, pageCount) { referenceCount = 1 };

        public bool TryAdd(TextureUpload upload, out GlyphAtlasRegion? glyph)
        {
            glyph = null;

            lock (allocationLock)
            {
                int attempt = ++allocationAttempts;
                int x = 0;
                int y = 0;
                int layer = 0;
                bool allocated = !disposed && (tryAllocateFromFreeRegions(upload.Width + glyph_padding * 2, upload.Height + glyph_padding * 2, out x, out y, out layer)
                                               || tryAllocate(upload.Width, upload.Height, out x, out y, out layer));

                if (!allocated && !disposed && attempt >= nextReclamationAttempt)
                {
                    reclaimUnusedRegions();
                    nextReclamationAttempt = attempt + reclamation_retry_interval;
                    allocated = tryAllocateFromFreeRegions(upload.Width + glyph_padding * 2, upload.Height + glyph_padding * 2, out x, out y, out layer)
                                || tryAllocate(upload.Width, upload.Height, out x, out y, out layer);
                }

                if (!allocated)
                {
                    Interlocked.Increment(ref fallbackCount);
                    return false;
                }

                int uploadWidth = upload.Width + glyph_padding * 2;
                int uploadHeight = upload.Height + glyph_padding * 2;
                byte[] coverage = new byte[checked(uploadWidth * uploadHeight)];
                ReadOnlySpan<Rgba32> pixels = upload.Data;

                for (int row = 0; row < upload.Height; row++)
                {
                    int sourceRow = row * upload.Width;
                    int targetRow = (row + glyph_padding) * uploadWidth + glyph_padding;

                    for (int column = 0; column < upload.Width; column++)
                        coverage[targetRow + column] = pixels[sourceRow + column].A;
                }

                nativeTexture.SetRegionData((uint)layer, x, y, uploadWidth, uploadHeight, coverage);
                glyph = new GlyphAtlasRegion(Texture, new RectangleI(x + glyph_padding, y + glyph_padding, upload.Width, upload.Height), layer);
                allocations.Add(new GlyphAllocation(x, y, uploadWidth, uploadHeight, layer, new WeakReference<GlyphAtlasRegion>(glyph)));
                Interlocked.Increment(ref glyphCount);
                return true;
            }
        }

        internal void ReclaimUnusedRegions()
        {
            lock (allocationLock)
            {
                reclaimUnusedRegions();
                nextReclamationAttempt = allocationAttempts;
            }
        }

        public void Dispose()
        {
            lock (allocationLock)
            {
                lock (pool_lock)
                {
                    if (disposed)
                        return;

                    if (--referenceCount > 0)
                        return;

                    disposed = true;
                    pools.Remove(nativeTexture.Renderer);
                    nativeTexture.Dispose();
                }
            }
        }

        private bool tryAllocate(int width, int height, out int x, out int y, out int layer)
        {
            int paddedWidth = checked(width + glyph_padding * 2);
            int paddedHeight = checked(height + glyph_padding * 2);

            if (width <= 0 || height <= 0 || paddedWidth > pageSize || paddedHeight > pageSize)
            {
                x = y = layer = 0;
                return false;
            }

            if (cursorX + paddedWidth > pageSize
                || cursorY + Math.Max(shelfHeight, paddedHeight) > pageSize)
            {
                cursorX = 0;
                cursorY += shelfHeight;
                shelfHeight = 0;
            }

            if (cursorY + paddedHeight > pageSize)
            {
                if (currentLayer + 1 >= pageCount)
                {
                    x = y = layer = 0;
                    return false;
                }

                currentLayer++;
                cursorX = 0;
                cursorY = 0;
                shelfHeight = 0;
            }

            x = cursorX;
            y = cursorY;
            layer = currentLayer;

            cursorX += paddedWidth;
            shelfHeight = Math.Max(shelfHeight, paddedHeight);
            return true;
        }

        private bool tryAllocateFromFreeRegions(int width, int height, out int x, out int y, out int layer)
        {
            int bestIndex = -1;
            long bestWaste = long.MaxValue;

            for (int i = 0; i < freeRegions.Count; i++)
            {
                FreeRegion region = freeRegions[i];

                if (region.Width < width || region.Height < height)
                    continue;

                long waste = (long)region.Width * region.Height - (long)width * height;
                if (waste >= bestWaste)
                    continue;

                bestIndex = i;
                bestWaste = waste;
            }

            if (bestIndex < 0)
            {
                x = y = layer = 0;
                return false;
            }

            FreeRegion available = freeRegions[bestIndex];
            freeRegions.RemoveAt(bestIndex);

            x = available.X;
            y = available.Y;
            layer = available.Layer;

            int remainingWidth = available.Width - width;
            int remainingHeight = available.Height - height;

            if (remainingWidth > 0)
                freeRegions.Add(new FreeRegion(x + width, y, remainingWidth, height, layer));

            if (remainingHeight > 0)
                freeRegions.Add(new FreeRegion(x, y + height, available.Width, remainingHeight, layer));

            return true;
        }

        private void reclaimUnusedRegions()
        {
            bool reclaimed = false;

            for (int i = allocations.Count - 1; i >= 0; i--)
            {
                GlyphAllocation allocation = allocations[i];

                if (allocation.Glyph.TryGetTarget(out _))
                    continue;

                freeRegions.Add(new FreeRegion(allocation.X, allocation.Y, allocation.Width, allocation.Height, allocation.Layer));
                allocations.RemoveAt(i);
                reclaimed = true;
            }

            if (reclaimed)
                mergeFreeRegions();
        }

        private void mergeFreeRegions()
        {
            bool merged;

            do
            {
                merged = false;
                freeRegions.Sort(static (left, right) =>
                {
                    int result = left.Layer.CompareTo(right.Layer);
                    if (result != 0)
                        return result;

                    result = left.Y.CompareTo(right.Y);
                    if (result != 0)
                        return result;

                    result = left.Height.CompareTo(right.Height);
                    return result != 0 ? result : left.X.CompareTo(right.X);
                });

                for (int i = 0; i < freeRegions.Count - 1;)
                {
                    FreeRegion left = freeRegions[i];
                    FreeRegion right = freeRegions[i + 1];

                    if (left.Layer == right.Layer && left.Y == right.Y && left.Height == right.Height && left.X + left.Width == right.X)
                    {
                        freeRegions[i] = new FreeRegion(left.X, left.Y, left.Width + right.Width, left.Height, left.Layer);
                        freeRegions.RemoveAt(i + 1);
                        merged = true;
                    }
                    else
                        i++;
                }

                freeRegions.Sort(static (top, bottom) =>
                {
                    int result = top.Layer.CompareTo(bottom.Layer);
                    if (result != 0)
                        return result;

                    result = top.X.CompareTo(bottom.X);
                    if (result != 0)
                        return result;

                    result = top.Width.CompareTo(bottom.Width);
                    return result != 0 ? result : top.Y.CompareTo(bottom.Y);
                });

                for (int i = 0; i < freeRegions.Count - 1;)
                {
                    FreeRegion top = freeRegions[i];
                    FreeRegion bottom = freeRegions[i + 1];

                    if (top.Layer == bottom.Layer && top.X == bottom.X && top.Width == bottom.Width && top.Y + top.Height == bottom.Y)
                    {
                        freeRegions[i] = new FreeRegion(top.X, top.Y, top.Width, top.Height + bottom.Height, top.Layer);
                        freeRegions.RemoveAt(i + 1);
                        merged = true;
                    }
                    else
                        i++;
                }
            } while (merged);
        }

        private readonly record struct GlyphAllocation(int X, int Y, int Width, int Height, int Layer, WeakReference<GlyphAtlasRegion> Glyph);

        private readonly record struct FreeRegion(int X, int Y, int Width, int Height, int Layer);
    }

    internal sealed class GlyphAtlasRegion : TextureRegion
    {
        internal int Layer { get; }

        internal Texture ArrayTexture { get; }

        internal bool IsArrayGlyph => true;

        public GlyphAtlasRegion(Texture parent, RectangleI bounds, int layer)
            : base(parent, bounds, WrapMode.None, WrapMode.None)
        {
            ArrayTexture = parent;
            Layer = layer;
        }
    }
}
