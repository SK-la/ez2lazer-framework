// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.CompilerServices;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osuTK.Graphics;
using SixLabors.ImageSharp.PixelFormats;

namespace osu.Framework.Graphics.Textures
{
    public partial class TextureAtlas
    {
        /// <summary>
        /// A texture which is acting as the backing for an atlas.
        /// </summary>
        private class BackingAtlasTexture : Texture
        {
            /// <summary>
            /// The amount of padding around each texture in the atlas.
            /// </summary>
            private readonly int padding;

            private readonly RectangleI atlasBounds;

#pragma warning disable IDE0052
            // Keep a reference to the parent for the texture visualiser.
            // ReSharper disable once NotAccessedField.Local
            private readonly Texture parent;
#pragma warning restore IDE0052

            private static readonly Color4 initialisation_colour = default;

            public BackingAtlasTexture(IRenderer renderer, int width, int height, bool manualMipmaps, TextureFilteringMode filteringMode = TextureFilteringMode.Linear, int padding = 0,
                                       string? label = null)
                : this(renderer.CreateTexture(width, height, manualMipmaps, filteringMode, initialisationColour: initialisation_colour))
            {
                this.padding = padding;
                atlasBounds = new RectangleI(0, 0, Width, Height);
                NativeTexture.Label = label ?? string.Empty;
            }

            private BackingAtlasTexture(Texture parent)
                : base(parent)
            {
                this.parent = parent;
                IsAtlasTexture = parent.IsAtlasTexture = true;
            }

            internal override void SetData(ITextureUpload upload, WrapMode wrapModeS, WrapMode wrapModeT, Opacity? opacity)
            {
                RectangleI middleBounds = upload.Bounds;

                if (middleBounds.IsEmpty || middleBounds.Width * middleBounds.Height > upload.Data.Length)
                {
                    base.SetData(upload, wrapModeS, wrapModeT, Opacity.Mixed);
                    return;
                }

                int actualPadding = padding / (1 << upload.Level);

                if (actualPadding <= 0)
                {
                    base.SetData(upload, wrapModeS, wrapModeT, Opacity.Mixed);
                    return;
                }

                var data = upload.Data;
                bool wrapS = wrapModeS != WrapMode.None;
                bool wrapT = wrapModeT != WrapMode.None;

                if (!paddingWouldUpload(data, middleBounds))
                {
                    base.SetData(upload, wrapModeS, wrapModeT, Opacity.Mixed);
                    return;
                }

                var expanded = new RectangleI(
                    middleBounds.X - actualPadding,
                    middleBounds.Y - actualPadding,
                    middleBounds.Width + actualPadding * 2,
                    middleBounds.Height + actualPadding * 2).Intersect(atlasBounds);

                var combined = new MemoryAllocatorTextureUpload(expanded.Width, expanded.Height)
                {
                    Bounds = expanded,
                    Level = upload.Level
                };

                var dest = combined.RawData;
                dest.Clear();

                copyMiddle(data, middleBounds, dest, expanded);
                writeVerticalPadding(dest, expanded, data, middleBounds, actualPadding, wrapT);
                writeHorizontalPadding(dest, expanded, data, middleBounds, actualPadding, wrapS);
                writeCornerPadding(dest, expanded, data, middleBounds, actualPadding, wrapS && wrapT);

                upload.Dispose();
                base.SetData(combined, wrapModeS, wrapModeT, Opacity.Mixed);
            }

            private static void copyMiddle(ReadOnlySpan<Rgba32> source, RectangleI middleBounds, Span<Rgba32> dest, RectangleI expanded)
            {
                int srcW = middleBounds.Width;
                int destW = expanded.Width;
                int offsetX = middleBounds.X - expanded.X;
                int offsetY = middleBounds.Y - expanded.Y;

                for (int y = 0; y < middleBounds.Height; y++)
                    source.Slice(y * srcW, srcW).CopyTo(dest.Slice((offsetY + y) * destW + offsetX, srcW));
            }

            private bool paddingWouldUpload(ReadOnlySpan<Rgba32> upload, RectangleI middleBounds)
            {
                int width = middleBounds.Width;
                int height = middleBounds.Height;

                if (width == 0 || height == 0)
                    return false;

                for (int x = 0; x < width; x++)
                {
                    if (!checkEdgeRGB(upload[x]) || !checkEdgeRGB(upload[(height - 1) * width + x]))
                        return true;
                }

                for (int y = 1; y < height - 1; y++)
                {
                    if (!checkEdgeRGB(upload[y * width]) || !checkEdgeRGB(upload[y * width + width - 1]))
                        return true;
                }

                return false;
            }

            private void writeVerticalPadding(Span<Rgba32> dest, RectangleI expanded, ReadOnlySpan<Rgba32> upload, RectangleI middleBounds, int actualPadding, bool fillOpaque)
            {
                RectangleI[] sideBoundsArray =
                {
                    new RectangleI(middleBounds.X, middleBounds.Y - actualPadding, middleBounds.Width, actualPadding).Intersect(atlasBounds),
                    new RectangleI(middleBounds.X, middleBounds.Y + middleBounds.Height, middleBounds.Width, actualPadding).Intersect(atlasBounds),
                };

                int[] sideIndices =
                {
                    0,
                    (middleBounds.Height - 1) * middleBounds.Width,
                };

                int destW = expanded.Width;

                for (int i = 0; i < 2; ++i)
                {
                    RectangleI sideBounds = sideBoundsArray[i];

                    if (sideBounds.IsEmpty)
                        continue;

                    int index = sideIndices[i];

                    for (int y = 0; y < sideBounds.Height; ++y)
                    {
                        int destRow = (sideBounds.Y - expanded.Y + y) * destW + (sideBounds.X - expanded.X);

                        for (int x = 0; x < sideBounds.Width; ++x)
                            transferBorderPixel(ref dest[destRow + x], upload[index + x], fillOpaque);
                    }
                }
            }

            private void writeHorizontalPadding(Span<Rgba32> dest, RectangleI expanded, ReadOnlySpan<Rgba32> upload, RectangleI middleBounds, int actualPadding, bool fillOpaque)
            {
                RectangleI[] sideBoundsArray =
                {
                    new RectangleI(middleBounds.X - actualPadding, middleBounds.Y, actualPadding, middleBounds.Height).Intersect(atlasBounds),
                    new RectangleI(middleBounds.X + middleBounds.Width, middleBounds.Y, actualPadding, middleBounds.Height).Intersect(atlasBounds),
                };

                int[] sideIndices =
                {
                    0,
                    middleBounds.Width - 1,
                };

                int destW = expanded.Width;
                int stride = middleBounds.Width;

                for (int i = 0; i < 2; ++i)
                {
                    RectangleI sideBounds = sideBoundsArray[i];

                    if (sideBounds.IsEmpty)
                        continue;

                    int index = sideIndices[i];

                    for (int y = 0; y < sideBounds.Height; ++y)
                    {
                        int destRow = (sideBounds.Y - expanded.Y + y) * destW + (sideBounds.X - expanded.X);

                        for (int x = 0; x < sideBounds.Width; ++x)
                            transferBorderPixel(ref dest[destRow + x], upload[index + y * stride], fillOpaque);
                    }
                }
            }

            private void writeCornerPadding(Span<Rgba32> dest, RectangleI expanded, ReadOnlySpan<Rgba32> upload, RectangleI middleBounds, int actualPadding, bool fillOpaque)
            {
                RectangleI[] cornerBoundsArray =
                {
                    new RectangleI(middleBounds.X - actualPadding, middleBounds.Y - actualPadding, actualPadding, actualPadding).Intersect(atlasBounds),
                    new RectangleI(middleBounds.X + middleBounds.Width, middleBounds.Y - actualPadding, actualPadding, actualPadding).Intersect(atlasBounds),
                    new RectangleI(middleBounds.X - actualPadding, middleBounds.Y + middleBounds.Height, actualPadding, actualPadding).Intersect(atlasBounds),
                    new RectangleI(middleBounds.X + middleBounds.Width, middleBounds.Y + middleBounds.Height, actualPadding, actualPadding).Intersect(atlasBounds),
                };

                int[] cornerIndices =
                {
                    0,
                    middleBounds.Width - 1,
                    (middleBounds.Height - 1) * middleBounds.Width,
                    (middleBounds.Height - 1) * middleBounds.Width + middleBounds.Width - 1,
                };

                int destW = expanded.Width;

                for (int i = 0; i < 4; ++i)
                {
                    RectangleI cornerBounds = cornerBoundsArray[i];
                    int nCornerPixels = cornerBounds.Width * cornerBounds.Height;
                    Rgba32 pixel = upload[cornerIndices[i]];

                    if (nCornerPixels == 0 || checkEdgeRGB(pixel))
                        continue;

                    for (int y = 0; y < cornerBounds.Height; ++y)
                    {
                        int destRow = (cornerBounds.Y - expanded.Y + y) * destW + (cornerBounds.X - expanded.X);

                        for (int x = 0; x < cornerBounds.Width; ++x)
                            transferBorderPixel(ref dest[destRow + x], pixel, fillOpaque);
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void transferBorderPixel(ref Rgba32 dest, Rgba32 source, bool fillOpaque)
            {
                dest.R = source.R;
                dest.G = source.G;
                dest.B = source.B;
                dest.A = fillOpaque ? source.A : (byte)0;
            }

            /// <summary>
            /// Check whether the provided upload edge pixel's RGB components match the initialisation colour.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private bool checkEdgeRGB(Rgba32 cornerPixel)
                => cornerPixel.R == initialisation_colour.R
                   && cornerPixel.G == initialisation_colour.G
                   && cornerPixel.B == initialisation_colour.B;
        }
    }
}
