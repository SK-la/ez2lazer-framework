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
