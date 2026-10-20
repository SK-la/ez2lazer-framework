// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using osu.Framework.Extensions;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Platform;
using osu.Framework.Text;
using SixLabors.ImageSharp.PixelFormats;

namespace osu.Framework.Tests.Ez
{
    [TestFixture]
    public class OutlineFontSvgHooksTest
    {
        [Test]
        public void TestPlutoSvgHooksRegistered()
        {
            // Natives are packaged for win-x64 / linux-x64 only (see osu.Framework/runtimes).
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
                Assert.Ignore("plutosvgft is not shipped for this OS yet.");

            RuntimeHelpers.RunClassConstructor(typeof(OutlineFont).TypeHandle);

            Assert.True(NativePlutoSvgFt.HooksRegistered,
                "plutosvgft should register OT-SVG hooks on Windows/Linux desktop RIDs used for packaging.");
        }

        [Test]
        public void TestSvgColourEmojiRasterizesNonEmpty()
        {
            if (!NativePlutoSvgFt.HooksRegistered)
                Assert.Ignore("OT-SVG hooks not registered on this RID.");

            string? fontPath = findNotoColorEmojiPath();

            if (fontPath == null)
                Assert.Ignore("NotoColorEmoji-Regular.ttf not found (sibling osu-resources).");

            using var store = new StorageBackedResourceStore(new NativeStorage(Path.GetDirectoryName(fontPath)!));
            using var font = new OutlineFont(store, Path.GetFileNameWithoutExtension(fontPath));
            font.LoadAsync().WaitSafely();

            Assert.True(font.HasColourGlyphs);

            // Grinning face U+1F600
            uint glyph = font.GetGlyphIndex(0x1F600);
            Assert.NotZero(glyph);

            using TextureUpload? upload = font.RasterizeGlyph(glyph, null);
            Assert.NotNull(upload);
            Assert.Greater(upload!.Width, 1);
            Assert.Greater(upload.Height, 1);

            bool anyOpaque = false;

            foreach (Rgba32 pixel in upload.Data)
            {
                if (pixel.A > 0)
                {
                    anyOpaque = true;
                    break;
                }
            }

            Assert.True(anyOpaque, "SVG colour emoji glyph should not rasterize to a blank texture.");
        }

        private static string? findNotoColorEmojiPath()
        {
            string[] candidates =
            {
                Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "osu-resources", "osu.Game.Resources", "Fonts", "Emoji", "NotoColorEmoji-Regular.ttf")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "osu-resources", "osu.Game.Resources", "Fonts", "Emoji", "NotoColorEmoji-Regular.ttf")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "osu-resources", "osu.Game.Resources", "Fonts", "Emoji", "NotoColorEmoji-Regular.ttf")),
            };

            return candidates.FirstOrDefault(File.Exists);
        }
    }
}
