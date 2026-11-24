// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Graphics.Primitives;

if (args.Length < 1)
    Environment.Exit(2);

string path = args[0];
Vector2I? target = null;

if (args.Length >= 3
    && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
    && int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h))
{
    target = new Vector2I(w, h);
}

Environment.SetEnvironmentVariable("EZ2LAZER_SVG_WORKER", "1");

using var stream = File.OpenRead(path);

if (!SvgImageDecoder.TryDecodeWithoutWorker(stream, target, out var image) || image == null)
    Environment.Exit(1);

try
{
    using var stdout = Console.OpenStandardOutput();

    Span<byte> header = stackalloc byte[8];
    BinaryPrimitives.WriteInt32LittleEndian(header, image.Width);
    BinaryPrimitives.WriteInt32LittleEndian(header[4..], image.Height);
    stdout.Write(header);
    stdout.Write(MemoryMarshal.AsBytes(image.PixelSpan));
}
finally
{
    image.Dispose();
}