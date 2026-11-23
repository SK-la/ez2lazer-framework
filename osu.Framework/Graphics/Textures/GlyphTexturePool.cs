// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
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
        private const int page_size = 2048;
        private const int page_count = 4;
        private const int glyph_padding = 4;

        private static readonly Lock pool_lock = new Lock();
        private static readonly ConditionalWeakTable<IRenderer, GlyphTexturePool> pools = new ConditionalWeakTable<IRenderer, GlyphTexturePool>();

        private readonly Lock allocationLock = new Lock();
        private readonly VeldridGlyphTextureArray nativeTexture;

        private int currentLayer;
        private int cursorX;
        private int cursorY;
        private int shelfHeight;
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

        private GlyphTexturePool(IVeldridRenderer renderer)
        {
            nativeTexture = new VeldridGlyphTextureArray(renderer, page_size, page_size, page_count);
            Texture = new Texture(nativeTexture, WrapMode.None, WrapMode.None);
            Texture.ScaleAdjust = 1;
        }

        public bool TryAdd(TextureUpload upload, out GlyphAtlasRegion? glyph)
        {
            glyph = null;

            lock (allocationLock)
            {
                if (disposed || !tryAllocate(upload.Width, upload.Height, out int x, out int y, out int layer))
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
                Interlocked.Increment(ref glyphCount);
                return true;
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

            if (width <= 0 || height <= 0 || paddedWidth > page_size || paddedHeight > page_size)
            {
                x = y = layer = 0;
                return false;
            }

            if (cursorX + paddedWidth > page_size
                || cursorY + Math.Max(shelfHeight, paddedHeight) > page_size)
            {
                cursorX = 0;
                cursorY += shelfHeight;
                shelfHeight = 0;
            }

            if (cursorY + paddedHeight > page_size)
            {
                if (currentLayer + 1 >= page_count)
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
