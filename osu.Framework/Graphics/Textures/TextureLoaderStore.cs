// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Graphics.Primitives;
using osu.Framework.IO.Stores;
using SkiaStaticImageDecoder = osu.Framework.Graphics.Imaging.SkiaStaticImageDecoder;

namespace osu.Framework.Graphics.Textures
{
    /// <summary>
    /// Handles the parsing of image data from standard image formats into <see cref="TextureUpload"/>s ready for GPU consumption.
    /// </summary>
    public class TextureLoaderStore : IResourceStore<TextureUpload>
    {
        private readonly ResourceStore<byte[]> store;
        private readonly Vector2I? svgTargetSize;

        public TextureLoaderStore(IResourceStore<byte[]> store)
        {
            this.store = new ResourceStore<byte[]>(store);
            this.store.AddExtension(@"png");
            this.store.AddExtension(@"jpg");
            this.store.AddExtension(@"gif");
            this.store.AddExtension(@"svg");
        }

        public TextureLoaderStore(IResourceStore<byte[]> store, Vector2I svgTargetSize)
            : this(store)
        {
            if (svgTargetSize.X <= 0 || svgTargetSize.Y <= 0)
                throw new ArgumentOutOfRangeException(nameof(svgTargetSize));

            this.svgTargetSize = svgTargetSize;
        }

        public Task<TextureUpload> GetAsync(string name, CancellationToken cancellationToken = default) =>
            Task.Run(() => Get(name), cancellationToken);

        public TextureUpload Get(string name)
        {
            try
            {
                using (var stream = store.GetStream(name))
                {
                    if (stream != null)
                        return LoadTextureUpload(stream);
                }
            }
            catch
            {
            }

            return null;
        }

        /// <summary>
        /// Decode all frames for an animated still resource. <see cref="Get"/> continues to return a single preview frame for GIFs.
        /// </summary>
        public AnimatedImageFrameLoadResult LoadAnimatedFrames(string name, int? maxFrames = null)
        {
            using (var stream = store.GetStream(name))
            {
                if (stream == null)
                    return null;

                return AnimatedImageLoader.LoadFromStream(stream, maxFrames);
            }
        }

        public Stream GetStream(string name) => store.GetStream(name);

        protected virtual RgbaImage ImageFromStream(Stream stream) => LegacyImageDecoder.DecodeTexture(stream);

        protected virtual TextureUpload LoadTextureUpload(Stream stream)
        {
            using var copy = stream.CanSeek ? null : new MemoryStream();

            if (copy != null)
            {
                stream.CopyTo(copy);
                copy.Position = 0;
                stream = copy;
            }

            if (SkiaStaticImageDecoder.TryDecode(stream, out RgbaImage image))
                return new TextureUpload(image);

            if (SvgImageDecoder.TryDecode(stream, svgTargetSize, out RgbaImage svgImage))
                return new TextureUpload(svgImage);

            return new TextureUpload(ImageFromStream(stream));
        }

        public IEnumerable<string> GetAvailableResources() => store.GetAvailableResources();

        #region IDisposable Support

        private bool isDisposed;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (isDisposed)
                return;

            store.Dispose();

            isDisposed = true;
        }

        #endregion
    }
}
