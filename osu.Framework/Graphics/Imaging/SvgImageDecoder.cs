// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Xml;
using osu.Framework.Graphics.Primitives;
using SkiaSharp;
using Svg;
using Svg.Model;
using Svg.Model.Services;
using Svg.Skia;

namespace osu.Framework.Graphics.Imaging
{
    internal static unsafe class SvgImageDecoder
    {
        private const string svg_namespace = "http://www.w3.org/2000/svg";
        private const float default_viewport_width = 300;
        private const float default_viewport_height = 150;
        private const int max_intrinsic_dimension = 4096;
        private const long max_embedded_image_pixels = max_intrinsic_dimension * (long)max_intrinsic_dimension;

        public static bool TryDecode(Stream stream, Vector2I? targetSize, [NotNullWhen(true)] out RgbaImage? image)
        {
            image = null;

            if (!stream.CanRead)
                return false;

            if (!stream.CanSeek)
            {
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                copy.Position = 0;
                return TryDecode(copy, targetSize, out image);
            }

            if (targetSize is Vector2I size && (size.X <= 0 || size.Y <= 0))
                throw new ArgumentOutOfRangeException(nameof(targetSize));

            long initialPosition = stream.Position;

            if (!looksLikeXml(stream, initialPosition))
                return false;

            bool decoded = false;
            RgbaImage? result = null;
            BoundedSvgAssetLoader? assetLoader = null;

            try
            {
                if (!tryReadDocumentInfo(stream, initialPosition, out SvgDocumentInfo documentInfo))
                    return false;

                Vector2I outputSize = calculateOutputSize(documentInfo, targetSize);
                var viewport = new SKRect(0, 0, outputSize.X, outputSize.Y);

                stream.Position = initialPosition;

                var parameters = new SvgParameters
                {
                    LoadOptions = new SvgDocumentLoadOptions
                    {
                        ProcessingMode = SvgProcessingMode.SecureStatic,
                        ExternalResources = SvgExternalResourcePolicy.SameDocumentAndDataOnly,
                    },
                };

                var settings = new SKSvgSettings
                {
                    StandaloneViewport = viewport,
                    ColorType = SKColorType.Rgba8888,
                    AlphaType = SKAlphaType.Premul,
                    EnableJavaScript = false,
                    EnableExternalJavaScript = false,
                };
                var skiaModel = new SkiaModel(settings);
                assetLoader = new BoundedSvgAssetLoader(skiaModel);
                SvgDocument? svgDocument = SvgService.Open(stream, parameters);
                if (svgDocument == null)
                    return false;

                var sceneViewport = new ShimSkiaSharp.SKRect(0, 0, outputSize.X, outputSize.Y);
                if (!SvgSceneRuntime.TryCompile(svgDocument, assetLoader, DrawAttributes.None, sceneViewport, out SvgSceneDocument? scene)
                    || scene == null || assetLoader.ExceededLimits)
                    return false;

                ShimSkiaSharp.SKPicture? model = scene.CreateModel();
                if (model == null || assetLoader.ExceededLimits)
                    return false;

                using var svgPicture = skiaModel.ToSKPicture(model);
                if (svgPicture == null || assetLoader.ExceededLimits)
                    return false;

                var surfaceInfo = new SKImageInfo(outputSize.X, outputSize.Y, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(surfaceInfo);
                if (surface == null)
                    return false;

                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.DrawPicture(svgPicture);
                surface.Canvas.Flush();

                var imageInfo = new SKImageInfo(outputSize.X, outputSize.Y, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                result = new RgbaImage(outputSize.X, outputSize.Y);

                fixed (Rgba32* pixels = result.PixelSpan)
                {
                    if (!surface.ReadPixels(imageInfo, (IntPtr)pixels, imageInfo.RowBytes, 0, 0))
                        return false;
                }

                image = result;
                decoded = true;
                return true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
            {
                return false;
            }
            finally
            {
                if (!decoded)
                    result?.Dispose();

                assetLoader?.ClearDocumentFonts();
                stream.Position = initialPosition;
            }
        }

        private sealed class BoundedSvgAssetLoader : SkiaSvgAssetLoader, ISvgAssetLoader
        {
            private long remainingPixels = max_embedded_image_pixels;

            public bool ExceededLimits { get; private set; }

            public BoundedSvgAssetLoader(SkiaModel skiaModel)
                : base(skiaModel)
            {
            }

            // Reimplement the library's final interface method so nested documents share the same pixel budget.
            ShimSkiaSharp.SKImage ISvgAssetLoader.LoadImage(Stream stream)
            {
                Stream imageStream = stream;
                MemoryStream? copy = null;
                long initialPosition = stream.CanSeek ? stream.Position : 0;

                try
                {
                    if (!stream.CanSeek)
                    {
                        copy = new MemoryStream();
                        stream.CopyTo(copy);
                        copy.Position = 0;
                        imageStream = copy;
                    }

                    long imagePosition = imageStream.Position;
                    long imagePixels;

                    using (var skiaStream = new SKManagedStream(imageStream, disposeManagedStream: false))
                    using (var codec = SKCodec.Create(skiaStream))
                    {
                        if (codec == null)
                            return null!;

                        SKImageInfo info = codec.Info;
                        imagePixels = (long)info.Width * info.Height;

                        if (info.Width is <= 0 or > max_intrinsic_dimension
                            || info.Height is <= 0 or > max_intrinsic_dimension
                            || imagePixels > remainingPixels)
                        {
                            ExceededLimits = true;
                            return null!;
                        }
                    }

                    imageStream.Position = imagePosition;
                    var image = base.LoadImage(imageStream);
                    remainingPixels -= imagePixels;
                    return image;
                }
                finally
                {
                    copy?.Dispose();

                    if (stream.CanSeek)
                        stream.Position = initialPosition;
                }
            }
        }

        private static XmlReaderSettings createReaderSettings() => new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            CloseInput = false,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        private static bool looksLikeXml(Stream stream, long initialPosition)
        {
            Span<byte> prefix = stackalloc byte[16];
            int read = 0;

            try
            {
                while (read < prefix.Length)
                {
                    int count = stream.Read(prefix[read..]);
                    if (count == 0)
                        break;

                    read += count;
                }
            }
            finally
            {
                stream.Position = initialPosition;
            }

            ReadOnlySpan<byte> data = prefix[..read];
            int offset = 0;
            int stride = 1;
            bool littleEndian = true;

            if (data.Length >= 4 && data[0] == 0xff && data[1] == 0xfe && data[2] == 0x00 && data[3] == 0x00)
            {
                offset = 4;
                stride = 4;
            }
            else if (data.Length >= 4 && data[0] == 0x00 && data[1] == 0x00 && data[2] == 0xfe && data[3] == 0xff)
            {
                offset = 4;
                stride = 4;
                littleEndian = false;
            }
            else if (data.Length >= 3 && data[0] == 0xef && data[1] == 0xbb && data[2] == 0xbf)
                offset = 3;
            else if (data.Length >= 2 && ((data[0] == 0xff && data[1] == 0xfe) || (data[0] == 0xfe && data[1] == 0xff)))
            {
                littleEndian = data[0] == 0xff;
                offset = 2;
                stride = 2;
            }

            while (offset < data.Length)
            {
                int characterOffset = offset + (littleEndian ? 0 : stride - 1);
                if (characterOffset >= data.Length)
                    return false;

                byte value = data[characterOffset];

                if (value is not (byte)' ' and not (byte)'\t' and not (byte)'\r' and not (byte)'\n')
                {
                    if (value != (byte)'<' || offset + stride > data.Length)
                        return false;

                    for (int i = offset; i < offset + stride; i++)
                    {
                        if (i != characterOffset && data[i] != 0)
                            return false;
                    }

                    return true;
                }

                offset += stride;
            }

            return false;
        }

        private static bool tryReadDocumentInfo(Stream stream, long initialPosition, out SvgDocumentInfo documentInfo)
        {
            documentInfo = default;
            bool foundRoot = false;
            string? viewBox = null;
            string? width = null;
            string? height = null;

            stream.Position = initialPosition;

            using var reader = XmlReader.Create(stream, createReaderSettings());

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (!foundRoot)
                    {
                        if (!string.Equals(reader.LocalName, "svg", StringComparison.OrdinalIgnoreCase)
                            || (reader.NamespaceURI.Length > 0 && reader.NamespaceURI != svg_namespace))
                            return false;

                        foundRoot = true;
                        viewBox = reader.GetAttribute("viewBox");
                        width = reader.GetAttribute("width");
                        height = reader.GetAttribute("height");
                    }
                    else if (reader.LocalName.Equals("script", StringComparison.OrdinalIgnoreCase)
                             || reader.LocalName.Equals("foreignObject", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    if (!validateAttributes(reader))
                        return false;
                }
                else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA && !validateResourceReferences(reader.Value))
                    return false;
            }

            if (!foundRoot)
                return false;

            bool hasViewBox = tryParseViewBox(viewBox, out SKRect parsedViewBox);
            float viewportWidth = parseLength(width) ?? (hasViewBox ? parsedViewBox.Width : default_viewport_width);
            float viewportHeight = parseLength(height) ?? (hasViewBox ? parsedViewBox.Height : default_viewport_height);

            if (!(viewportWidth > 0) || !(viewportHeight > 0) || !float.IsFinite(viewportWidth) || !float.IsFinite(viewportHeight))
                return false;

            documentInfo = new SvgDocumentInfo(viewportWidth, viewportHeight);
            return true;
        }

        private static bool validateAttributes(XmlReader reader)
        {
            if (!reader.HasAttributes)
                return true;

            while (reader.MoveToNextAttribute())
            {
                if (reader.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase))
                {
                    string reference = reader.Value.Trim();
                    if (!reference.StartsWith('#') && !reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        return false;
                }

                if (reader.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    || !validateResourceReferences(reader.Value))
                    return false;
            }

            reader.MoveToElement();
            return true;
        }

        private static bool validateResourceReferences(string value)
        {
            if (value.Contains("@import", StringComparison.OrdinalIgnoreCase))
                return false;

            int position = 0;

            while (position < value.Length)
            {
                int start = value.IndexOf("url", position, StringComparison.OrdinalIgnoreCase);
                if (start < 0)
                    return true;

                int openParenthesis = value.IndexOf('(', start + 3);
                if (openParenthesis < 0)
                    return true;

                int closeParenthesis = value.IndexOf(')', openParenthesis + 1);
                if (closeParenthesis < 0)
                    return false;

                ReadOnlySpan<char> reference = value.AsSpan(openParenthesis + 1, closeParenthesis - openParenthesis - 1).Trim();
                if (reference.Length >= 2 && ((reference[0] == '\'' && reference[^1] == '\'') || (reference[0] == '"' && reference[^1] == '"')))
                    reference = reference[1..^1].Trim();

                if (!reference.StartsWith("#", StringComparison.Ordinal) && !reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    return false;

                position = closeParenthesis + 1;
            }

            return true;
        }

        private static bool tryParseViewBox(string? value, out SKRect viewBox)
        {
            viewBox = default;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            string[] parts = value.Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4
                || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float minX)
                || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float minY)
                || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float width)
                || !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float height)
                || !(width > 0)
                || !(height > 0)
                || !float.IsFinite(minX)
                || !float.IsFinite(minY)
                || !float.IsFinite(width)
                || !float.IsFinite(height))
                return false;

            viewBox = new SKRect(minX, minY, minX + width, minY + height);
            return true;
        }

        private static float? parseLength(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.EndsWith('%'))
                return null;

            ReadOnlySpan<char> span = value.AsSpan().Trim();
            int numericEnd = 0;

            while (numericEnd < span.Length
                   && (char.IsAsciiDigit(span[numericEnd]) || span[numericEnd] is '+' or '-' or '.' or 'e' or 'E'))
                numericEnd++;

            if (numericEnd == 0 || !float.TryParse(span[..numericEnd], NumberStyles.Float, CultureInfo.InvariantCulture, out float number))
                return null;

            ReadOnlySpan<char> unit = span[numericEnd..].Trim();
            float multiplier = unit.ToString().ToLowerInvariant() switch
            {
                "" or "px" => 1,
                "pt" => 96f / 72,
                "pc" => 16,
                "mm" => 96f / 25.4f,
                "cm" => 96f / 2.54f,
                "in" => 96,
                "q" => 96f / 101.6f,
                _ => float.NaN
            };

            float result = number * multiplier;
            return float.IsFinite(result) && result > 0 ? result : null;
        }

        private static Vector2I calculateOutputSize(SvgDocumentInfo documentInfo, Vector2I? targetSize)
        {
            Vector2I target = targetSize ?? new Vector2I(
                (int)MathF.Min(MathF.Ceiling(documentInfo.Width), max_intrinsic_dimension),
                (int)MathF.Min(MathF.Ceiling(documentInfo.Height), max_intrinsic_dimension));

            float scale = MathF.Min(target.X / documentInfo.Width, target.Y / documentInfo.Height);
            if (targetSize == null)
                scale = MathF.Min(scale, 1);

            return new Vector2I(
                Math.Clamp(checked((int)MathF.Ceiling(documentInfo.Width * scale)), 1, target.X),
                Math.Clamp(checked((int)MathF.Ceiling(documentInfo.Height * scale)), 1, target.Y));
        }

        private readonly record struct SvgDocumentInfo(float Width, float Height);
    }
}
