// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Graphics.Textures;
using osu.Framework.Logging;
using osu.Framework.Text;

namespace osu.Framework.IO.Stores
{
    /// <summary>
    /// A glyph store that rasterizes glyphs from outlines.
    /// </summary>
    public class OutlineGlyphStore : IGlyphStore, IResourceStore<TextureUpload>
    {
        protected OutlineFont Font { get; }

        private RawFontVariation? rawVariation;

        public FontVariation? Variation { get; }

        public string FontName { get; }

        public float? Baseline => Font.Baseline;

        private readonly bool selfContained;
        private int isDisposed;

        /// <summary>
        /// Create a glyph store for a font using the specified OpenType named instance.
        /// </summary>
        /// <param name="font">The underlying font.</param>
        /// <param name="namedInstance">The named instance to select.</param>
        /// <param name="nameOverride">
        /// The value of <see cref="FontName"/>. If null, <paramref name="namedInstance"/> will be used.
        /// </param>
        public OutlineGlyphStore(OutlineFont font, string namedInstance, string? nameOverride = null)
            : this(font, new FontVariation { NamedInstance = namedInstance }, nameOverride)
        {
        }

        /// <summary>
        /// Create a glyph store for a font using the specified OpenType variation parameters.
        /// </summary>
        /// <param name="font">The underlying font.</param>
        /// <param name="variation">The font variation parameters.</param>
        /// <param name="nameOverride">
        /// The value of <see cref="FontName"/>. If null, it will be computed using a naming scheme based on
        /// <see href="https://download.macromedia.com/pub/developer/opentype/tech-notes/5902.AdobePSNameGeneration.html"/>.
        /// </param>
        public OutlineGlyphStore(OutlineFont font, FontVariation? variation = null, string? nameOverride = null)
        {
            Font = font;
            Variation = variation;

            FontName = nameOverride ?? variation?.GenerateInstanceName(font.AssetName) ?? font.AssetName;
        }

        /// <summary>
        /// Load a new font from a filesystem path and create a glyph store for it.
        /// </summary>
        /// <param name="filePath">Absolute path to a <c>.ttf</c>/<c>.otf</c>/<c>.ttc</c> file.</param>
        /// <param name="fontName">Lookup name exposed to <see cref="FontStore"/> (prefer a stable family id without weight suffix).</param>
        /// <param name="faceIndex">Face index within a font collection.</param>
        public OutlineGlyphStore(string filePath, string fontName, int faceIndex = 0)
            : this(new OutlineFont(new FileResourceStore(filePath), "font", faceIndex) { Resolution = 100 }, variation: null, nameOverride: fontName)
        {
            selfContained = true;
        }

        /// <summary>
        /// Load a new font and create a glyph store for it.
        /// </summary>
        /// <param name="store">The font's resource store.</param>
        /// <param name="assetName">The asset name of the font.</param>
        public OutlineGlyphStore(IResourceStore<byte[]> store, string assetName)
            : this(new OutlineFont(store, assetName, 0) { Resolution = 100 }, variation: null, nameOverride: assetName)
        {
            selfContained = true;
        }

        ~OutlineGlyphStore()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool isDisposing)
        {
            if (Interlocked.Exchange(ref isDisposed, 1) != 0)
                return;

            if (selfContained)
                Font.Dispose();
        }

        public async Task LoadFontAsync()
        {
            try
            {
                await Font.LoadAsync().ConfigureAwait(false);
                rawVariation = Font.DecodeFontVariation(Variation);
            }
            catch (Exception e)
            {
                // Soft-fail for bad/unsupported files (common when enumerating system fonts).
                Logger.Log($"Couldn't load font {FontName} from {Font.AssetPath}: {e.Message}", level: LogLevel.Verbose);
                throw;
            }
        }

        public bool HasGlyph(char c) => HasGlyph((int)c);

        public bool HasGlyph(int codepoint) => Font.HasGlyph(codepoint);

        public CharacterGlyph? Get(char c) => Get((int)c);

        public CharacterGlyph? Get(int codepoint)
        {
            if (!Rune.IsValid(codepoint))
                return null;

            var metrics = Font.GetMetrics(Font.GetGlyphIndex(codepoint), rawVariation);

            if (metrics is null)
                return null;

            return new CharacterGlyph(codepoint, metrics.XOffset, metrics.YOffset, metrics.XAdvance, metrics.Baseline, this, Font.HasColourGlyphs);
        }

        public int GetKerning(char left, char right) => GetKerning(left, (int)right);

        public int GetKerning(int leftCodepoint, int rightCodepoint)
        {
            return Font.GetKerning(Font.GetGlyphIndex(leftCodepoint), Font.GetGlyphIndex(rightCodepoint), rawVariation);
        }

        Task<CharacterGlyph> IResourceStore<CharacterGlyph>.GetAsync(string name, CancellationToken cancellationToken)
            => Task.Run(() =>
            {
                if (!tryParseCodepointFromResourceName(name, out int codepoint))
                    return null!;

                return Get(codepoint)!;
            }, cancellationToken);

        CharacterGlyph IResourceStore<CharacterGlyph>.Get(string name)
        {
            if (!tryParseCodepointFromResourceName(name, out int codepoint))
                return null!;

            return Get(codepoint)!;
        }

        public TextureUpload Get(string name)
        {
            if (name.Length > 1 && !name.StartsWith($@"{FontName}/", StringComparison.Ordinal))
                return null!;

            if (!tryParseCodepointFromResourceName(name, out int codepoint))
                return null!;

            uint glyphIndex = Font.GetGlyphIndex(codepoint);

            return Font.RasterizeGlyph(glyphIndex, rawVariation)!;
        }

        public async Task<TextureUpload> GetAsync(string name, CancellationToken cancellationToken = default)
        {
            if (name.Length > 1 && !name.StartsWith($@"{FontName}/", StringComparison.Ordinal))
                return null!;

            if (!tryParseCodepointFromResourceName(name, out int codepoint))
                return null!;

            uint glyphIndex = await Font.GetGlyphIndexAsync(codepoint).ConfigureAwait(false);

            return await Font.RasterizeGlyphAsync(glyphIndex, rawVariation, cancellationToken).ConfigureAwait(false);
        }

        public Stream GetStream(string name) => throw new NotSupportedException();

        public IEnumerable<string> GetAvailableResources()
        {
            return Font.GetAvailableChars().Select(c => $@"{FontName}/{c}");
        }

        private static bool tryParseCodepointFromResourceName(string name, out int codepoint)
        {
            codepoint = 0;

            if (string.IsNullOrEmpty(name))
                return false;

            int slashIndex = name.LastIndexOf('/');
            string suffix = slashIndex >= 0 ? name[(slashIndex + 1)..] : name;

            if (suffix.Length == 0)
                return false;

            if (suffix.Length == 1)
            {
                codepoint = suffix[0];
                return true;
            }

            return int.TryParse(suffix, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out codepoint)
                   && Rune.IsValid(codepoint);
        }
    }
}
