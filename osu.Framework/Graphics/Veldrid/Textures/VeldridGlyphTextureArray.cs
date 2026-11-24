// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using osu.Framework.Development;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Textures;
using osu.Framework.Platform;
using Veldrid;
using PixelFormat = Veldrid.PixelFormat;
using Texture = Veldrid.Texture;

namespace osu.Framework.Graphics.Veldrid.Textures
{
    internal sealed class VeldridGlyphTextureArray : IVeldridTexture
    {
        private readonly IVeldridRenderer renderer;
        private readonly uint layerCount;
        private readonly TextureFilteringMode filteringMode;
        private readonly Queue<LayerUpload> uploads = new Queue<LayerUpload>();
        private readonly VeldridTextureResources?[] resourcesArray = new VeldridTextureResources?[1];
        private NativeMemoryTracker.NativeMemoryLease? memoryLease;
        private bool disposed;

        private VeldridTextureResources? resources
        {
            get => resourcesArray[0];
            set => resourcesArray[0] = value;
        }

        public IRenderer Renderer => renderer;

        TextureFilteringMode INativeTexture.FilteringMode => filteringMode;

        TexturePixelFormat INativeTexture.PixelFormat => TexturePixelFormat.R8Float;

        public string Identifier => !Available || resources == null ? "-" : resources.Texture.Name;

        public string Label { get; set; } = "GlyphTextureArray";

        public int MaxSize => renderer.MaxTextureSize;

        public int Width { get; set; }

        public int Height { get; set; }

        public int? MipLevel { get; set; }

        public bool Available { get; private set; } = true;

        ulong INativeTexture.TotalBindCount { get; set; }

        public bool BypassTextureUploadQueueing { get; set; }

        public bool UploadComplete
        {
            get
            {
                lock (uploads)
                    return uploads.Count == 0;
            }
        }

        public VeldridGlyphTextureArray(IVeldridRenderer renderer, int width, int height, uint layers, TextureFilteringMode filteringMode = TextureFilteringMode.Linear)
        {
            if (width <= 0 || width > renderer.MaxTextureSize)
                throw new ArgumentOutOfRangeException(nameof(width));

            if (height <= 0 || height > renderer.MaxTextureSize)
                throw new ArgumentOutOfRangeException(nameof(height));

            ArgumentOutOfRangeException.ThrowIfZero(layers);

            this.renderer = renderer;
            layerCount = layers;
            this.filteringMode = filteringMode;
            Width = width;
            Height = height;
        }

        public void SetRegionData(uint layer, int x, int y, int width, int height, ReadOnlySpan<byte> coverage)
            => SetRegionDataOwned(layer, x, y, width, height, coverage.ToArray());

        internal void SetRegionDataOwned(uint layer, int x, int y, int width, int height, byte[] coverage)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(layer, layerCount);
            if (x < 0 || y < 0 || width <= 0 || height <= 0 || x > Width - width || y > Height - height)
                throw new ArgumentOutOfRangeException(nameof(width), "Coverage region must fit within the texture layer.");
            if (coverage.Length != checked(width * height))
                throw new ArgumentException("Coverage data length must match the update region.", nameof(coverage));

            ObjectDisposedException.ThrowIf(!Available, this);

            lock (uploads)
            {
                bool enqueue = uploads.Count == 0;

                uploads.Enqueue(new LayerUpload(layer, x, y, width, height, coverage));
                TextureUploadDrainer.LogIfQueueLarge(this, uploads.Count);

                if (enqueue && !BypassTextureUploadQueueing)
                    renderer.EnqueueTextureUpload(this);
            }
        }

        public void SetLayerData(uint layer, ReadOnlySpan<byte> coverage)
            => SetRegionData(layer, 0, 0, Width, Height, coverage);

        public bool Upload()
        {
            ObjectDisposedException.ThrowIf(!Available, this);
            ThreadSafety.EnsureDrawThread();

            bool uploaded = false;

            while (tryTakeUpload(out LayerUpload? upload))
            {
                ensureResources();
                renderer.UpdateTextureArrayLayer(resources!.Texture, upload.Layer, upload.X, upload.Y, upload.Width, upload.Height, upload.Coverage);
                uploaded = true;
            }

            if (!BypassTextureUploadQueueing && !UploadComplete)
                renderer.EnqueueTextureUpload(this);

            return uploaded;
        }

        private bool tryTakeUpload([NotNullWhen(true)] out LayerUpload? upload)
        {
            lock (uploads)
            {
                if (uploads.Count == 0 || !renderer.TryBeginTextureUpload(uploads.Peek().Width * uploads.Peek().Height, BypassTextureUploadQueueing))
                {
                    upload = null;
                    return false;
                }

                upload = uploads.Dequeue();
                return true;
            }
        }

        private void ensureResources()
        {
            if (resources != null)
                return;

            ThreadSafety.EnsureDrawThread();
            var description = TextureDescription.Texture2D((uint)Width, (uint)Height, 1, layerCount, PixelFormat.R8UNorm, TextureUsage.Sampled);
            Texture texture = renderer.Factory.CreateTexture(ref description);
            Sampler sampler = renderer.Factory.CreateSampler(new SamplerDescription
            {
                AddressModeU = SamplerAddressMode.Clamp,
                AddressModeV = SamplerAddressMode.Clamp,
                AddressModeW = SamplerAddressMode.Clamp,
                Filter = filteringMode.ToSamplerFilter(),
                MinimumLod = 0,
                MaximumLod = 0,
                MaximumAnisotropy = 0,
            });

            resources = new VeldridTextureResources(renderer, texture, sampler);
            memoryLease = NativeMemoryTracker.AddMemory(this, (long)Width * Height * layerCount);
        }

        public IReadOnlyList<VeldridTextureResources> GetResourceList()
        {
            if (resources == null)
                return Array.Empty<VeldridTextureResources>();

            return resourcesArray!;
        }

        public int GetByteSize() => checked((int)((long)Width * Height * layerCount));

        public void FlushUploads()
        {
            lock (uploads)
                uploads.Clear();
        }

        public void SetData(ITextureUpload upload)
            => throw new NotSupportedException("Glyph coverage arrays only accept byte coverage uploads.");

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            renderer.ScheduleDisposal(static array =>
            {
                array.FlushUploads();
                array.memoryLease?.Dispose();
                array.memoryLease = null;
                array.resources?.Dispose();
                array.resources = null;
                array.Available = false;
            }, this);
        }

        private sealed record LayerUpload(uint Layer, int X, int Y, int Width, int Height, byte[] Coverage);
    }
}
