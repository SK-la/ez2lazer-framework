// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.IO;
using System.Runtime.InteropServices;
using JetBrains.Annotations;
using osu.Framework.Extensions.ImageExtensions;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using StbiSharp;
using FrameworkRgba32 = osu.Framework.Graphics.Imaging.Rgba32;
using ImageSharpRgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;

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

        public ReadOnlySpan<FrameworkRgba32> Data
        {
            get
            {
                if (rgbaImage != null)
                    return rgbaImage.PixelSpan;

                return image == null ? ReadOnlySpan<FrameworkRgba32>.Empty : MemoryMarshal.Cast<ImageSharpRgba32, FrameworkRgba32>(pixelMemory.Span);
            }
        }

        public int Width => image?.Width ?? rgbaImage?.Width ?? 0;

        public int Height => image?.Height ?? rgbaImage?.Height ?? 0;

        /// <summary>
        /// The backing texture. A handle is kept to avoid early GC.
        /// </summary>
        private readonly Image<ImageSharpRgba32> image;

        private readonly RgbaImage rgbaImage;

        private ReadOnlyPixelMemory<ImageSharpRgba32> pixelMemory;

        /// <summary>
        /// Create an upload from a <see cref="TextureUpload"/>. This is the preferred method.
        /// </summary>
        /// <param name="image">The texture to upload.</param>
        public TextureUpload(Image<ImageSharpRgba32> image)
        {
            this.image = image;
            pixelMemory = image.CreateReadOnlyPixelMemory();
        }

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
            : this(LoadFromStream<ImageSharpRgba32>(stream))
        {
        }

        private static bool stbiNotFound;

        internal static readonly DecoderOptions DECODER_OPTIONS = new DecoderOptions
        {
            Configuration = new SixLabors.ImageSharp.Configuration(
                new PngConfigurationModule(),
                new JpegConfigurationModule(),
                new GifConfigurationModule(),
                new BmpConfigurationModule(),
                new WebpConfigurationModule()
            )
        };

        internal static Image<TPixel> LoadFromStream<TPixel>(Stream stream) where TPixel : unmanaged, IPixel<TPixel>
        {
            if (loadUsingStbi(stream, out Image<TPixel> loadPixelData))
                return loadPixelData;

            return Image.Load<TPixel>(DECODER_OPTIONS, stream);
        }

        private static bool loadUsingStbi<TPixel>(Stream stream, [CanBeNull] out Image<TPixel> image) where TPixel : unmanaged, IPixel<TPixel>
        {
            image = null;
            if (stbiNotFound)
                return false;

            long initialPos = stream.Position;

            try
            {
                using (var buffer = SixLabors.ImageSharp.Configuration.Default.MemoryAllocator.Allocate<byte>((int)stream.Length))
                {
                    stream.ReadExactly(buffer.Memory.Span);

                    using (var stbiImage = Stbi.LoadFromMemory(buffer.Memory.Span, 4))
                    {
                        image = Image.LoadPixelData(MemoryMarshal.Cast<byte, TPixel>(stbiImage.Data), stbiImage.Width, stbiImage.Height);
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                if (e is DllNotFoundException)
                    stbiNotFound = true;

                Logger.Log($"Texture could not be loaded via STB; falling back to ImageSharp: {e.Message}");
                stream.Position = initialPos;
            }

            return false;
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

            image?.Dispose();
            rgbaImage?.Dispose();
            pixelMemory.Dispose();

            disposed = true;
        }

        #endregion
    }
}
