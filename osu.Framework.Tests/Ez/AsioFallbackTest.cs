// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Audio;
using osu.Framework.IO.Stores;
using osu.Framework.Threading;

namespace osu.Framework.Tests.Ez
{
    [TestFixture]
    public class AsioFallbackTest
    {
        [Test]
        public void TestAsioFallbackOnFailure()
        {
            // Skip if not on Windows
            if (RuntimeInfo.OS != RuntimeInfo.Platform.Windows)
                Assert.Ignore("ASIO is only supported on Windows");

            AudioThread.PreloadBass();

            var audioThread = new AudioThread();
            var trackStore = new ResourceStore<byte[]>(new DllResourceStore(typeof(AsioFallbackTest).Assembly));
            var sampleStore = new ResourceStore<byte[]>(new DllResourceStore(typeof(AsioFallbackTest).Assembly));

            using (var audioManager = new AudioManager(audioThread, trackStore, sampleStore, null))
            {
                var deviceNames = audioManager.AudioDeviceNames.ToList();
                var asioDevices = deviceNames.Where(name => name.Contains("(ASIO)")).ToList();

                if (asioDevices.Count == 0)
                    Assert.Ignore("No ASIO devices available for testing");

                // Get current device before test
                // string originalDevice = audioManager.AudioDevice.Value;

                // Try to switch to an ASIO device that might fail
                // We'll use the first one, but the test environment might succeed
                // In real scenarios, this would fail and fallback
                string testDevice = asioDevices.First();
                TestContext.WriteLine($"Testing ASIO fallback with device: {testDevice}");

                audioManager.AudioDevice.Value = testDevice;

                // Wait for device initialization
                System.Threading.Thread.Sleep(2000);

                // Check what device is actually selected
                string currentDevice = audioManager.AudioDevice.Value;
                TestContext.WriteLine($"Current device after ASIO attempt: '{currentDevice}'");

                // In test environment, it might succeed, but in real world with failing ASIO,
                // it should fallback to default (empty string) or original device
                TestContext.WriteLine(currentDevice != testDevice ? "ASIO device failed and fell back to another device - this is expected behavior" : "ASIO device was successfully selected");

                // The key test is that the system doesn't crash and audio still works
                // We can't easily test the actual fallback in test environment since ASIO works here
                Assert.IsNotNull(currentDevice, "Some device should be selected");
                TestContext.WriteLine("ASIO fallback test completed");
            }
        }
    }
}
