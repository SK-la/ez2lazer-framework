// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Runtime.InteropServices;
using osu.Framework.Graphics.Imaging;
using osu.Framework.Platform.Apple;
using osu.Framework.Platform.Apple.Native;

namespace osu.Framework.Platform.MacOS
{
    internal static partial class MacOSClipboardImageCodec
    {
        private const int utf8_string_encoding = 0x08000100;

        public static byte[] EncodeTiff(RgbaImage image)
        {
            byte[] pixels = MemoryMarshal.AsBytes(image.PixelSpan).ToArray();
            premultiplyAlpha(pixels);

            GCHandle pixelsHandle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            CGColorSpace colorSpace = default;
            CGBitmapContext bitmapContext = default;
            IntPtr cgImage = IntPtr.Zero;
            IntPtr outputData = IntPtr.Zero;
            IntPtr typeIdentifier = IntPtr.Zero;
            IntPtr imageDestination = IntPtr.Zero;

            try
            {
                colorSpace = CGColorSpace.CreateDeviceRGB();
                bitmapContext = CGBitmapContext.Create(pixelsHandle.AddrOfPinnedObject(), (nuint)image.Width, (nuint)image.Height, 8, (nuint)(image.Width * 4), colorSpace, CGBitmapInfo.ByteOrder32Big | CGBitmapInfo.PremultipliedLast);
                if (bitmapContext.Handle == IntPtr.Zero)
                    throw new InvalidOperationException("Could not create the TIFF bitmap context.");

                cgImage = CGImageCreate(bitmapContext.Handle);
                if (cgImage == IntPtr.Zero)
                    throw new InvalidOperationException("Could not create the TIFF image.");

                outputData = CFDataCreateMutable(IntPtr.Zero, 0);
                typeIdentifier = CFStringCreateWithCString(IntPtr.Zero, "public.tiff", utf8_string_encoding);
                imageDestination = CGImageDestinationCreateWithData(outputData, typeIdentifier, 1, IntPtr.Zero);
                if (imageDestination == IntPtr.Zero)
                    throw new InvalidOperationException("Could not create the TIFF destination.");

                CGImageDestinationAddImage(imageDestination, cgImage, IntPtr.Zero);
                if (!CGImageDestinationFinalize(imageDestination))
                    throw new InvalidOperationException("Could not finish TIFF encoding.");

                nuint length = CFDataGetLength(outputData);
                byte[] result = new byte[checked((int)length)];
                Marshal.Copy(CFDataGetBytePtr(outputData), result, 0, result.Length);
                return result;
            }
            finally
            {
                release(imageDestination);
                release(typeIdentifier);
                release(outputData);
                release(cgImage);
                release(bitmapContext.Handle);
                release(colorSpace.Handle);
                pixelsHandle.Free();
            }
        }

        public static unsafe RgbaImage DecodeTiff(byte[] encoded)
        {
            IntPtr data = IntPtr.Zero;
            IntPtr source = IntPtr.Zero;
            IntPtr imageHandle = IntPtr.Zero;

            try
            {
                fixed (byte* pointer = encoded)
                    data = CFDataCreate(IntPtr.Zero, pointer, encoded.Length);

                if (data == IntPtr.Zero)
                    throw new InvalidOperationException("Could not allocate TIFF data.");

                source = CGImageSourceCreateWithData(data, IntPtr.Zero);
                if (source == IntPtr.Zero)
                    throw new InvalidDataException("Could not create a TIFF image source.");

                imageHandle = CGImageSourceCreateImageAtIndex(source, 0, IntPtr.Zero);
                if (imageHandle == IntPtr.Zero)
                    throw new InvalidDataException("Could not decode the TIFF image.");

                return AppleTextureLoaderStore.ImageFromCGImage(new CGImage(imageHandle));
            }
            finally
            {
                release(imageHandle);
                release(source);
                release(data);
            }
        }

        private static void premultiplyAlpha(Span<byte> pixels)
        {
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                int alpha = pixels[offset + 3];
                pixels[offset] = (byte)((pixels[offset] * alpha + 127) / 255);
                pixels[offset + 1] = (byte)((pixels[offset + 1] * alpha + 127) / 255);
                pixels[offset + 2] = (byte)((pixels[offset + 2] * alpha + 127) / 255);
            }
        }

        private static void release(IntPtr handle)
        {
            if (handle != IntPtr.Zero)
                CFRelease(handle);
        }

        [LibraryImport(Interop.LIB_CORE_GRAPHICS, EntryPoint = "CGBitmapContextCreateImage")]
        private static partial IntPtr CGImageCreate(IntPtr context);

        [LibraryImport(Interop.LIB_IMAGE_IO, EntryPoint = "CGImageDestinationCreateWithData")]
        private static partial IntPtr CGImageDestinationCreateWithData(IntPtr data, IntPtr typeIdentifier, nuint imageCount, IntPtr options);

        [LibraryImport(Interop.LIB_IMAGE_IO, EntryPoint = "CGImageDestinationAddImage")]
        private static partial void CGImageDestinationAddImage(IntPtr destination, IntPtr image, IntPtr properties);

        [LibraryImport(Interop.LIB_IMAGE_IO, EntryPoint = "CGImageDestinationFinalize")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static partial bool CGImageDestinationFinalize(IntPtr destination);

        [LibraryImport(Interop.LIB_IMAGE_IO, EntryPoint = "CGImageSourceCreateWithData")]
        private static partial IntPtr CGImageSourceCreateWithData(IntPtr data, IntPtr options);

        [LibraryImport(Interop.LIB_IMAGE_IO, EntryPoint = "CGImageSourceCreateImageAtIndex")]
        private static partial IntPtr CGImageSourceCreateImageAtIndex(IntPtr source, nuint index, IntPtr options);

        [LibraryImport(Interop.LIB_CORE_FOUNDATION, EntryPoint = "CFDataCreate")]
        private static unsafe partial IntPtr CFDataCreate(IntPtr allocator, byte* bytes, nint length);

        [LibraryImport(Interop.LIB_CORE_FOUNDATION, EntryPoint = "CFDataCreateMutable")]
        private static partial IntPtr CFDataCreateMutable(IntPtr allocator, nint capacity);

        [LibraryImport(Interop.LIB_CORE_FOUNDATION, EntryPoint = "CFDataGetLength")]
        private static partial nuint CFDataGetLength(IntPtr data);

        [LibraryImport(Interop.LIB_CORE_FOUNDATION, EntryPoint = "CFDataGetBytePtr")]
        private static partial IntPtr CFDataGetBytePtr(IntPtr data);

        [LibraryImport(Interop.LIB_CORE_FOUNDATION, EntryPoint = "CFStringCreateWithCString", StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string value, int encoding);

        [LibraryImport(Interop.LIB_CORE_FOUNDATION, EntryPoint = "CFRelease")]
        private static partial void CFRelease(IntPtr value);
    }
}
