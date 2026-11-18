// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;

namespace osu.Framework.Tests.Resources
{
    internal static class GifTestResources
    {
        public const string SAMPLE_FLAME_GIF = "sample-flame.gif";

        public const string TEXTURES_PREFIX = "Resources/Textures";

        public static readonly string SAMPLE_FLAME_GIF_PATH = $"{TEXTURES_PREFIX}/{SAMPLE_FLAME_GIF}";

        private static readonly DllResourceStore assembly_store = new DllResourceStore(typeof(GifTestResources).Assembly);

        public static Stream OpenSampleFlameGifStream() => assembly_store.GetStream(SAMPLE_FLAME_GIF_PATH)
                                                           ?? throw new FileNotFoundException($"Embedded GIF not found: {SAMPLE_FLAME_GIF_PATH}");

        public static TextureLoaderStore CreateTexturesLoaderStore() => new TextureLoaderStore(new NamespacedResourceStore<byte[]>(assembly_store, TEXTURES_PREFIX));

        public static AnimatedImageFrameLoadResult LoadSampleFlameGif() =>
            CreateTexturesLoaderStore().LoadAnimatedFrames(SAMPLE_FLAME_GIF)
            ?? throw new FileNotFoundException($"Could not decode embedded GIF: {SAMPLE_FLAME_GIF}");
    }
}
