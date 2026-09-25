// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Framework.Audio
{
    public static class AudioOutputDefaults
    {
        public const int DEFAULT_SAMPLE_RATE = 48000;
        public const int SECONDARY_SAMPLE_RATE = 44100;
        public const int DEFAULT_ASIO_BUFFER_SIZE = 128;

        public const float WASAPI_EXCLUSIVE_BUFFER_SECONDS = 0.05f;
        public const float WASAPI_EXCLUSIVE_PERIOD_SECONDS = 0.01f;

        /// <summary>
        /// Requested shared-mode latency for the default NAudio WASAPI output path (milliseconds).
        /// Combined with preferred (non-required) <c>WithLowLatency()</c> so IAudioClient3 can negotiate
        /// a smaller period when available, otherwise standard shared mode is used.
        /// </summary>
        public const int DEFAULT_NAUDIO_LATENCY_MS = 10;

        /// <summary>
        /// MMCSS task name for the NAudio render thread. NAudio only registers the thread with MMCSS when a
        /// task name is supplied; without it the thread runs at Normal priority and can miss its 10 ms engine
        /// wake-up while a 2000 Hz update/draw thread competes for the CPU, which the player then repairs by
        /// reading two periods at once (a ~20 ms step in <c>BassMix.ChannelGetPosition</c>). See
        /// <c>docs/EZ-PERFORMANCE.md</c> §2.4.14.
        /// </summary>
        public const string DEFAULT_NAUDIO_MMCSS_TASK = "Pro Audio";
    }
}
