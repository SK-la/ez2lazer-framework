// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.IO;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;

namespace osu.Framework.Graphics.Textures
{
    /// <summary>
    /// Low level class for queueing texture uploads to the GPU.
    /// Should be manually disposed if not queued for upload via <see cref="Texture.SetData(ITextureUpload)"/>.
    /// </summary>
    public class TextureUpload : ITextureUpload
    {
        /// <summary>
        /// The target mipmap level to upload into.
        /// </summary>
        public int Level { get; set; }

        /// <summary>
        /// The target bounds for this upload. If not specified, will assume to be (0, 0, width, height).
        /// </summary>
        public RectangleI Bounds { get; set; }

        public ReadOnlySpan<Rgba32> Data => rgbaImage == null ? ReadOnlySpan<Rgba32>.Empty : rgbaImage.PixelSpan;

        public int Width => rgbaImage?.Width ?? 0;

        public int Height => rgbaImage?.Height ?? 0;

        private static int sampleCursor;

        /// <summary>
        /// Reads one already-decoded opaque pixel. Does not allocate.
        /// </summary>
        public bool TrySampleOpaquePixel(out Colour4 colour)
        {
            colour = default;

            ReadOnlySpan<Rgba32> data = Data;
            int length = data.Length;

            if (length == 0)
                return false;

            uint start = (uint)System.Threading.Interlocked.Increment(ref sampleCursor);

            for (int n = 0; n < 8; n++)
            {
                Rgba32 pixel = data[(int)((start + (uint)n * 997u) % (uint)length)];

                if (pixel.A < 16)
                    continue;

                colour = new Colour4(pixel.R / 255f, pixel.G / 255f, pixel.B / 255f, 1);
                return true;
            }

            return false;
        }

        /// <summary>
        /// The backing texture. A handle is kept to avoid early GC.
        /// </summary>
        private readonly RgbaImage rgbaImage;

        public TextureUpload(RgbaImage image)
        {
            rgbaImage = image ?? throw new ArgumentNullException(nameof(image));
        }

        /// <summary>
        /// Create an upload from an arbitrary image stream.
        /// Note that this bypasses per-platform image loading optimisations.
        /// Use <see cref="TextureLoaderStore"/> as provided from GameHost where possible.
        /// </summary>
        /// <param name="stream">The image content.</param>
        public TextureUpload(Stream stream)
        {
            rgbaImage = ImageCodec.DecodeTexture(stream);
        }

        /// <summary>
        /// Create an empty upload. Used by <see cref="IFrameBuffer"/> for initialisation.
        /// </summary>
        internal TextureUpload()
        {
        }

        #region IDisposable Support

        private bool disposed;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool isDisposing)
        {
            if (disposed)
                return;

            rgbaImage?.Dispose();

            disposed = true;
        }

        #endregion
    }
}
