// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace osu.Framework.Graphics.Textures
{
    public partial class TextureAtlas
    {
        // We are adding an extra padding on top of the padding required by
        // mipmap blending in order to support smooth edges without antialiasing which requires
        // inflating texture rectangles.
        internal const int PADDING = (1 << IRenderer.MAX_MIPMAP_LEVELS) * Sprite.MAX_EDGE_SMOOTHNESS;
        internal const int WHITE_PIXEL_SIZE = 1;

        private readonly IRenderer renderer;
        private readonly int atlasWidth;
        private readonly int atlasHeight;

        private int maxFittableWidth => atlasWidth - PADDING * 2;
        private int maxFittableHeight => atlasHeight - PADDING * 2;

        private readonly bool manualMipmaps;
        private readonly TextureFilteringMode filteringMode;
        private readonly Lock textureRetrievalLock = new Lock();

        /// <summary>
        /// Identifies this atlas in logs, so that an overflow can be traced back to the store which caused it.
        /// </summary>
        private readonly string label;

        /// <summary>
        /// All pages backing this atlas, oldest first.
        /// </summary>
        /// <remarks>
        /// Overflowing the current page creates an additional page rather than discarding the current one, so that
        /// space left over on earlier pages is still usable by later allocations. Previously the leftover space on
        /// an overflowing page was permanently wasted, which is the main reason a store could end up with far more
        /// pages (and therefore more texture binds) than the total texture area warranted.
        /// Pages are filled in creation order, so the oldest page is the one most likely to be completely full and
        /// the newest page is the one holding the most free space.
        /// </remarks>
        private readonly List<Page> pages = new List<Page>();

        /// <summary>
        /// The number of pages backing this atlas, for diagnostics and tests.
        /// </summary>
        internal int PageCount
        {
            get
            {
                lock (textureRetrievalLock)
                    return pages.Count;
            }
        }

        internal TextureWhitePixel WhitePixel
        {
            get
            {
                lock (textureRetrievalLock)
                {
                    if (pages.Count == 0)
                        Reset();

                    Debug.Assert(pages.Count > 0, "Atlas should have at least one page after Reset().");

                    return new TextureWhitePixel(pages[0].Texture);
                }
            }
        }

        public TextureAtlas(IRenderer renderer, int width, int height, bool manualMipmaps = false, TextureFilteringMode filteringMode = TextureFilteringMode.Linear, string label = "unnamed")
        {
            this.renderer = renderer;
            atlasWidth = width;
            atlasHeight = height;
            this.manualMipmaps = manualMipmaps;
            this.filteringMode = filteringMode;
            this.label = label;
        }

        public void DisposeResources()
        {
            lock (textureRetrievalLock)
            {
                foreach (Page page in pages)
                    new DisposableTexture(page.Texture).Dispose();

                pages.Clear();
            }
        }

        private int exceedCount;

        /// <summary>
        /// Discards all existing pages, leaving the atlas with a single empty page.
        /// </summary>
        /// <remarks>
        /// Existing textures created via <see cref="Add"/> are not cleared and remain accessible by usages.
        /// </remarks>
        public void Reset()
        {
            lock (textureRetrievalLock)
            {
                pages.Clear();
                pages.Add(createPage());
            }
        }

        /// <summary>
        /// Add (allocate) a new texture in the atlas.
        /// </summary>
        /// <param name="width">The width of the requested texture.</param>
        /// <param name="height">The height of the requested texture.</param>
        /// <param name="wrapModeS">The horizontal wrap mode of the texture.</param>
        /// <param name="wrapModeT">The vertical wrap mode of the texture.</param>
        /// <returns>A texture, or null if the requested size exceeds the atlas' bounds.</returns>
        public Texture? Add(int width, int height, WrapMode wrapModeS = WrapMode.None, WrapMode wrapModeT = WrapMode.None)
        {
            if (!canFitEmptyTextureAtlas(width, height))
                return null;

            lock (textureRetrievalLock)
            {
                if (pages.Count == 0)
                    pages.Add(createPage());

                foreach (Page page in pages)
                {
                    if (tryAllocate(page, width, height, out Vector2I position))
                        return createRegion(page, position, width, height, wrapModeS, wrapModeT);
                }

                // Every existing page is full. Add another one rather than resetting an existing page, as that would
                // permanently strand whatever space is left on it.
                // Every extra page is another texture the renderer has to bind between, and each of those binds breaks
                // the current batch. The first overflow is the one worth surfacing; the rest only add detail.
                Logger.Log($"TextureAtlas [{label}] size exceeded {++exceedCount} time(s); generating new texture ({atlasWidth}x{atlasHeight})", LoggingTarget.Performance);

                var newPage = createPage();
                pages.Add(newPage);

                // canFitEmptyTextureAtlas() has already guaranteed this cannot fail.
                if (!tryAllocate(newPage, width, height, out Vector2I newPosition))
                {
                    Debug.Assert(false, "A texture which fits an empty page failed to allocate on a freshly created page.");
                    return null;
                }

                return createRegion(newPage, newPosition, width, height, wrapModeS, wrapModeT);
            }
        }

        private Texture createRegion(Page page, Vector2I position, int width, int height, WrapMode wrapModeS, WrapMode wrapModeT)
            => new TextureRegion(page.Texture, new RectangleI(position.X, position.Y, width, height), wrapModeS, wrapModeT);

        private Page createPage()
        {
            var texture = new BackingAtlasTexture(renderer, atlasWidth, atlasHeight, manualMipmaps, filteringMode, PADDING / 2);

            RectangleI bounds = new RectangleI(0, 0, WHITE_PIXEL_SIZE, WHITE_PIXEL_SIZE);

            using (var whiteTex = new TextureRegion(texture, bounds, WrapMode.Repeat, WrapMode.Repeat))
                // Generate white padding as if the white texture was wrapped, even though it isn't
                whiteTex.SetData(new TextureUpload(new Image<Rgba32>(SixLabors.ImageSharp.Configuration.Default, whiteTex.Width, whiteTex.Height, new Rgba32(Vector4.One))));

            // The first shelf starts after the white pixel; the next shelf starts below the white pixel, which is the
            // lowest thing allocated so far.
            return new Page(texture, new Vector2I(PADDING + WHITE_PIXEL_SIZE, PADDING), PADDING + WHITE_PIXEL_SIZE);
        }

        /// <summary>
        /// Whether or not a texture of the given width and height could be placed into a completely empty texture atlas.
        /// </summary>
        /// <param name="width">The width of the texture.</param>
        /// <param name="height">The height of the texture.</param>
        /// <returns>True if the texture could fit an empty texture atlas, false if it could not</returns>
        private bool canFitEmptyTextureAtlas(int width, int height)
        {
            // exceeds bounds in one direction
            if (width > maxFittableWidth || height > maxFittableHeight)
                return false;

            // exceeds bounds in both directions (in this one, we have to account for the white pixel)
            if (width + WHITE_PIXEL_SIZE > maxFittableWidth && height + WHITE_PIXEL_SIZE > maxFittableHeight)
                return false;

            return true;
        }

        /// <summary>
        /// Attempts to place a texture of the given size on a single page, advancing that page's shelf state on success.
        /// </summary>
        /// <param name="page">The page to allocate on.</param>
        /// <param name="width">The width of the requested texture.</param>
        /// <param name="height">The height of the requested texture.</param>
        /// <param name="position">The position within the page to place the texture at, when this returns true.</param>
        /// <returns>Whether the texture fitted on the page.</returns>
        private bool tryAllocate(Page page, int width, int height, out Vector2I position)
        {
            // The current shelf still has room.
            if (page.Cursor.X + width + PADDING <= atlasWidth && page.Cursor.Y + height + PADDING <= atlasHeight)
            {
                position = page.Cursor;
                page.Cursor.X += width + PADDING;
                page.ShelfHeight = Math.Max(page.ShelfHeight, height);
                page.NextShelfY = Math.Max(page.NextShelfY, position.Y + height + PADDING);
                return true;
            }

            // Start a new shelf below every shelf allocated so far. The width is known to fit thanks to
            // canFitEmptyTextureAtlas(), so only the height needs checking.
            int shelfY = page.NextShelfY;

            if (PADDING + width + PADDING > atlasWidth || shelfY + height + PADDING > atlasHeight)
            {
                position = default;
                return false;
            }

            position = new Vector2I(PADDING, shelfY);
            page.Cursor = new Vector2I(position.X + width + PADDING, shelfY);
            page.ShelfHeight = height;
            page.NextShelfY = shelfY + height + PADDING;
            return true;
        }

        /// <summary>
        /// A single backing texture and the shelf packing state used to allocate within it.
        /// </summary>
        private sealed class Page
        {
            public readonly BackingAtlasTexture Texture;

            /// <summary>
            /// The position to write the next texture of the current shelf at. <see cref="Vector2I.X"/> advances as
            /// textures are added, while <see cref="Vector2I.Y"/> stays fixed at the top of the current shelf.
            /// </summary>
            public Vector2I Cursor;

            /// <summary>
            /// The height of the tallest texture placed on the current shelf, which decides where the next shelf starts.
            /// </summary>
            public int ShelfHeight;

            /// <summary>
            /// The Y coordinate at which the next shelf will be placed, i.e. below every texture allocated so far.
            /// </summary>
            public int NextShelfY;

            public Page(BackingAtlasTexture texture, Vector2I cursor, int nextShelfY)
            {
                Texture = texture;
                Cursor = cursor;
                NextShelfY = nextShelfY;
            }
        }
    }
}
