// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Platform;
using osu.Framework.Testing;

namespace osu.Framework.Tests.Ez
{
    /// <summary>
    /// [Ez] Covers the wiring that keeps the per-thread performance monitors idle until a diagnostic overlay
    /// actually asks for statistics.
    /// </summary>
    [TestFixture]
    public partial class PerformanceCollectionDemandTest
    {
        private const int timeout = 10000;

        [Test]
        public void TestCollectionFollowsDemand()
        {
            SignallingGame? game = null;
            TestRunHeadlessGameHost? host = null;
            var gameCreated = new ManualResetEventSlim();

            var task = Task.Factory.StartNew(() =>
            {
                using (host = new TestRunHeadlessGameHost())
                {
                    game = new SignallingGame();
                    gameCreated.Set();
                    host.Run(game);
                }
            }, TaskCreationOptions.LongRunning);

            try
            {
                gameCreated.Wait(timeout);
                Assert.That(game != null && game.BecameAlive.Wait(timeout), Is.True);

                // no overlay has been opened, so every thread should be idle.
                if (host != null)
                {
                    assertCollecting(host, false);

                    setConsumers(host, 1);
                    assertCollecting(host, true);

                    // a second consumer appearing and going away again must not stop the first one's collection.
                    setConsumers(host, 2);
                    setConsumers(host, 1);
                    assertCollecting(host, true);

                    setConsumers(host, 0);
                    assertCollecting(host, false);

                    // performance logging is an independent reason to collect.
                    schedule(host, () => host.PerformanceLogging.Value = true);
                    assertCollecting(host, true);

                    schedule(host, () => host.PerformanceLogging.Value = false);
                    assertCollecting(host, false);
                }
            }
            finally
            {
                host?.Exit();
                task.Wait(timeout);
            }
        }

        private static void setConsumers(GameHost host, int count) =>
            schedule(host, () => host.FrameStatisticsConsumers.Value = count);

        /// <summary>
        /// Bindables are not thread safe, so drive them from the thread that owns them.
        /// </summary>
        private static void schedule(GameHost host, System.Action action)
        {
            var completed = new ManualResetEventSlim();

            host.UpdateThread.Scheduler.Add(() =>
            {
                action();
                completed.Set();
            });

            Assert.That(completed.Wait(timeout), Is.True);
        }

        private static void assertCollecting(GameHost host, bool expected)
        {
            // the request is sampled once per frame on each thread, so allow a few frames to pass.
            Assert.That(() => host.Threads.All(t => t.Monitor == null || t.Monitor.Collecting == expected),
                Is.True.After(timeout, 50));
        }

        private partial class SignallingGame : TestGame
        {
            public readonly ManualResetEventSlim BecameAlive = new ManualResetEventSlim();

            protected override void LoadComplete()
            {
                base.LoadComplete();
                BecameAlive.Set();
            }
        }
    }
}
