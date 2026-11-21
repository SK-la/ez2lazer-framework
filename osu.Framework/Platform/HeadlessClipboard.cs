// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics.Imaging;

namespace osu.Framework.Platform
{
    /// <summary>
    /// Virtual clipboard for use in headless execution.
    /// </summary>
    /// <remarks>
    /// Stores all data in-memory, so the host OS clipboard is not affected.
    /// </remarks>
    public class HeadlessClipboard : Clipboard
    {
        private string? clipboardText;
        private RgbaImage? clipboardImage;

        public override string? GetText() => clipboardText;

        public override void SetText(string text)
        {
            clipboardImage?.Dispose();
            clipboardImage = null;
            clipboardText = text;
        }

        public override RgbaImage? GetImage() => clipboardImage?.Clone();

        public override bool SetImage(RgbaImage image)
        {
            var copy = image.Clone();
            clipboardImage?.Dispose();
            clipboardText = null;
            clipboardImage = copy;
            return true;
        }
    }
}
