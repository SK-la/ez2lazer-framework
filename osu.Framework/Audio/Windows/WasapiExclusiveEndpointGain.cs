// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime.Versioning;
using ManagedBass;
using NAudio.CoreAudioApi;
using osu.Framework.Logging;
using osu.Framework.Threading;

namespace osu.Framework.Audio.Windows
{
    [SupportedOSPlatform("windows")]
    internal sealed class WasapiExclusiveEndpointGain : IDisposable
    {
        private readonly MMDevice device;
        private readonly Scheduler audioScheduler;
        private readonly int mixerHandle;
        private readonly Action<float> setPullGain;
        // BASS_ATTRIB_VOLDSP (19) is not exposed by the ManagedBass enum.
        private const ChannelAttribute volume_dsp_attribute = (ChannelAttribute)19;
        private bool disposed;

        public static WasapiExclusiveEndpointGain? TryAttach(string? endpointId, int mixerHandle, Scheduler audioScheduler, Action<float> setPullGain)
        {
            if (mixerHandle == 0)
                return null;

            MMDevice? device = null;

            try
            {
                device = WindowsAudioFormatQuery.TryOpenPlaybackDevice(endpointId);

                if (device == null)
                {
                    Logger.Log("WASAPI Exclusive endpoint volume: playback device unavailable.", name: "audio", level: LogLevel.Important);
                    return null;
                }

                var gain = new WasapiExclusiveEndpointGain(device, mixerHandle, audioScheduler, setPullGain);
                device = null;
                gain.subscribeAndApply();
                return gain;
            }
            catch (Exception ex)
            {
                Logger.Log($"WASAPI Exclusive endpoint volume attach failed: {ex.Message}", name: "audio", level: LogLevel.Important);
                device?.Dispose();
                return null;
            }
        }

        private WasapiExclusiveEndpointGain(MMDevice device, int mixerHandle, Scheduler audioScheduler, Action<float> setPullGain)
        {
            this.device = device;
            this.mixerHandle = mixerHandle;
            this.audioScheduler = audioScheduler;
            this.setPullGain = setPullGain;
        }

        private void subscribeAndApply()
        {
            var volume = device.AudioEndpointVolume;
            apply(volume.MasterVolumeLevelScalar, volume.Mute, volume.MasterVolumeLevel);
            volume.OnVolumeNotification += onVolumeNotification;
        }

        private void onVolumeNotification(AudioVolumeNotificationData data)
        {
            float scalar = data.MasterVolume;
            bool muted = data.Muted;
            audioScheduler.Add(() =>
            {
                if (disposed)
                    return;

                float levelDb = float.NaN;

                try
                {
                    levelDb = device.AudioEndpointVolume.MasterVolumeLevel;
                }
                catch
                {
                }

                apply(scalar, muted, levelDb);
            });
        }

        private void apply(float scalar, bool muted, float levelDb)
        {
            if (disposed)
                return;

            float volume = WasapiExclusiveEndpointGainPolicy.MixerVolume(scalar, muted, levelDb);
            setPullGain(volume);
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;

            try
            {
                device.AudioEndpointVolume.OnVolumeNotification -= onVolumeNotification;
            }
            catch
            {
            }

            try
            {
                setPullGain(1f);
                Bass.ChannelSetAttribute(mixerHandle, ChannelAttribute.Volume, 1f);
                Bass.ChannelSetAttribute(mixerHandle, volume_dsp_attribute, 1f);
            }
            catch
            {
            }

            device.Dispose();
        }
    }
}
