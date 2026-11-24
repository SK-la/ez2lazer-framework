// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using osu.Framework.Graphics.Primitives;

namespace osu.Framework.Graphics.Imaging
{
    internal static class SvgImageDecoderWorker
    {
        internal static bool TryDecodeInWorkerProcess(Stream stream, long initialPosition, Vector2I? targetSize, [NotNullWhen(true)] out RgbaImage? image)
        {
            image = null;

            string? workerPath = resolveWorkerAssemblyPath();

            if (workerPath == null)
                return false;

            string tempPath = Path.Combine(Path.GetTempPath(), $"ez-svg-{Guid.NewGuid():N}.svg");

            Process? process = null;
            CancellationTokenSource? timeout = null;
            CancellationTokenRegistration killRegistration = default;

            try
            {
                stream.Position = initialPosition;

                using (var temp = File.Create(tempPath))
                    stream.CopyTo(temp);

                string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");

                if (string.IsNullOrEmpty(host))
                {
                    string? self = Environment.ProcessPath;
                    host = self != null && Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? self : null;
                }

                if (host == null)
                    return false;

                string arguments = targetSize is Vector2I size
                    ? $"exec \"{workerPath}\" \"{tempPath}\" {size.X.ToString(CultureInfo.InvariantCulture)} {size.Y.ToString(CultureInfo.InvariantCulture)}"
                    : $"exec \"{workerPath}\" \"{tempPath}\"";

                var start = new ProcessStartInfo
                {
                    FileName = host,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                process = Process.Start(start);

                if (process == null)
                    return false;

                timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                killRegistration = timeout.Token.Register(static state =>
                {
                    var proc = (Process)state!;

                    try
                    {
                        if (!proc.HasExited)
                            proc.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                    }
                }, process);

                using var stdout = process.StandardOutput.BaseStream;

                byte[] header = new byte[8];
                stdout.ReadExactly(header);

                int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
                int height = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));

                if (width <= 0 || height <= 0)
                {
                    process.Kill(entireProcessTree: true);
                    return false;
                }

                image = new RgbaImage(width, height);
                stdout.ReadExactly(MemoryMarshal.AsBytes(image.PixelSpan));

                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();

                if (process.ExitCode != 0)
                {
                    image.Dispose();
                    image = null;
                    return false;
                }

                return true;
            }
            catch
            {
                try
                {
                    if (process != null && !process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                image?.Dispose();
                image = null;
                return false;
            }
            finally
            {
                killRegistration.Dispose();
                timeout?.Dispose();
                process?.Dispose();
                stream.Position = initialPosition;

                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                }
            }
        }

        private static string? resolveWorkerAssemblyPath()
        {
            string candidate = Path.Combine(AppContext.BaseDirectory, "osu.Framework.SvgWorker.dll");
            return File.Exists(candidate) ? candidate : null;
        }

        private static bool documentContainsText(Stream stream, long initialPosition)
        {
            stream.Position = initialPosition;

            using var reader = new StreamReader(stream, leaveOpen: true);

            string? line;

            while ((line = reader.ReadLine()) != null)
            {
                if (line.Contains("<text", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            stream.Position = initialPosition;
            return false;
        }

        internal static bool ShouldUseWorkerDecode(Stream stream, long initialPosition)
            => SvgImageDecodeScheduler.UseIsolatedSerialisation && documentContainsText(stream, initialPosition);
    }
}
