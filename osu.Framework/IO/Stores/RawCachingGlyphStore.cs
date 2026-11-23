// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using osu.Framework.Extensions;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Graphics.Textures;
using osu.Framework.Platform;
using SharpFNT;

namespace osu.Framework.IO.Stores
{
    /// <summary>
    /// A glyph store which caches font sprite sheets as raw pixels to disk on first use.
    /// </summary>
    /// <remarks>
    /// This results in memory efficient lookups with good performance on solid state backed devices.
    /// Consider <see cref="TimedExpiryGlyphStore"/> if disk IO is limited and memory usage is not an issue.
    /// </remarks>
    public class RawCachingGlyphStore : GlyphStore
    {
        /// <summary>
        /// A storage backing to be used for storing decompressed glyph sheets.
        /// </summary>
        internal Storage? CacheStorage { get; set; }

        private readonly Dictionary<string, Stream> pageStreamHandles = new Dictionary<string, Stream>();

        private readonly Dictionary<int, PageInfo> pageLookup = new Dictionary<int, PageInfo>();

        public RawCachingGlyphStore(ResourceStore<byte[]> store, string? assetName = null, IResourceStore<TextureUpload>? textureLoader = null)
            : base(store, assetName, textureLoader)
        {
        }

        protected override TextureUpload LoadCharacter(Character character)
        {
            if (CacheStorage == null)
                throw new InvalidOperationException($"{nameof(CacheStorage)} should be set before requesting characters.");

            // Use simple global locking for the time being.
            // If necessary, a per-lookup-key (page number) locking mechanism could be implemented similar to TextureStore.
            lock (pageLookup)
            {
                if (!pageLookup.TryGetValue(character.Page, out var pageInfo))
                    pageInfo = createCachedPageInfo(character.Page);

                return createTextureUpload(character, pageInfo);
            }
        }

        private PageInfo createCachedPageInfo(int page)
        {
            Debug.Assert(CacheStorage != null);

            string filename = GetFilenameForPage(page);

            using (var stream = Store.GetStream(filename))
            {
                // The md5 of the original (compressed png) content.
                string streamMd5 = stream.ComputeMD5Hash();

                // The md5 of the access filename, including font name and page number.
                string filenameMd5 = filename.ComputeMD5Hash();

                string accessFilename = $"{filenameMd5}#{streamMd5}";

                // Finding an existing file validates that the file both exists on disk, and was generated for the correct font.
                // It doesn't guarantee that the generated cache file is in a good state.
                string? existing = CacheStorage.GetFiles(string.Empty, $"{accessFilename}*").FirstOrDefault();

                if (existing != null)
                {
                    // Filename format is "filenameHashMD5#contentHashMD5#width#height"
                    string[] split = existing.Split('#');

                    if (split.Length == 4
                        && int.TryParse(split[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width)
                        && int.TryParse(split[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height))
                    {
                        // Sanity check that the length of the file is expected, based on the width and height.
                        // If we ever see corrupt files in the wild, this should be changed to a full md5 check. Hopefully it will never happen.
                        using (var testStream = CacheStorage.GetStream(existing))
                        {
                            if (testStream.Length == width * height)
                            {
                                return pageLookup[page] = new PageInfo
                                {
                                    Width = width,
                                    Height = height,
                                    Filename = existing
                                };
                            }
                        }
                    }
                }

                using (var convert = GetPageImage(page))
                using (var buffer = MemoryPool<byte>.Shared.Rent(checked(convert.Width * convert.Height)))
                {
                    var output = buffer.Memory.Span[..checked(convert.Width * convert.Height)];
                    var source = convert.Data;

                    for (int i = 0; i < output.Length; i++)
                        output[i] = source[i].A;

                    // ensure any stale cached versions are deleted.
                    foreach (string f in CacheStorage.GetFiles(string.Empty, $"{filenameMd5}*"))
                        CacheStorage.Delete(f);

                    accessFilename += FormattableString.Invariant($"#{convert.Width}#{convert.Height}");

                    using (var outStream = CacheStorage.CreateFileSafely(accessFilename))
                        outStream.Write(output);

                    return pageLookup[page] = new PageInfo
                    {
                        Width = convert.Width,
                        Height = convert.Height,
                        Filename = accessFilename
                    };
                }
            }
        }

        private TextureUpload createTextureUpload(Character character, PageInfo page)
        {
            Debug.Assert(CacheStorage != null);

            int pageWidth = page.Width;

            int readableHeight = Math.Max(0, Math.Min(character.Height, page.Height - character.Y));
            int readableWidth = Math.Max(0, Math.Min(character.Width, pageWidth - character.X));
            int characterByteRegion = checked(pageWidth * readableHeight);
            byte[] readBuffer = ArrayPool<byte>.Shared.Rent(characterByteRegion);
            RgbaImage? image = null;

            try
            {
                image = new RgbaImage(character.Width, character.Height, new Rgba32(255, 255, 255, 0));

                if (!pageStreamHandles.TryGetValue(page.Filename, out var source))
                    source = pageStreamHandles[page.Filename] = CacheStorage.GetStream(page.Filename);

                // consider to use System.IO.RandomAccess in .NET 6
                source.Seek(pageWidth * character.Y, SeekOrigin.Begin);
                if (characterByteRegion > 0)
                    source.ReadExactly(readBuffer.AsSpan(0, characterByteRegion));

                for (int y = 0; y < character.Height; y++)
                {
                    var span = image.GetPixelRowSpan(y);
                    int readOffset = y * pageWidth + character.X;

                    if (y < readableHeight)
                    {
                        for (int x = 0; x < readableWidth; x++)
                            span[x] = new Rgba32(255, 255, 255, readBuffer[readOffset + x]);
                    }
                }

                var upload = new TextureUpload(image);
                image = null;
                return upload;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(readBuffer);
                image?.Dispose();
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (pageStreamHandles.IsNotNull())
            {
                foreach (var h in pageStreamHandles)
                    h.Value.Dispose();
            }
        }

        private record PageInfo
        {
            public string Filename { get; set; } = string.Empty;
            public int Width { get; set; }
            public int Height { get; set; }
        }
    }
}
