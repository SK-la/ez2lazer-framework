// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Statistics;
using osu.Framework.Threading;

namespace osu.Framework.Tests.Ez
{
    [TestFixture]
    public class PerformanceMonitorCollectionTest
    {
        [Test]
        public void TestNothingCollectedUntilRequested()
        {
            var monitor = createMonitor();

            monitor.NewFrame();

            // nothing has asked for statistics, so no timing should be collected.
            Assert.That(monitor.BeginCollecting(PerformanceCollectionType.Work), Is.Null);
        }

        [Test]
        public void TestRequestOnlyTakesEffectAtFrameBoundary()
        {
            var monitor = createMonitor();
            monitor.NewFrame();

            monitor.CollectionRequested = true;

            // sampling only happens in NewFrame, so that a request landing mid-frame cannot produce an
            // End without a matching Begin.
            Assert.That(monitor.BeginCollecting(PerformanceCollectionType.Work), Is.Null);

            monitor.NewFrame();

            Assert.That(monitor.BeginCollecting(PerformanceCollectionType.Work), Is.Not.Null);
        }

        [Test]
        public void TestCollectionStopsAgainWhenNoLongerRequested()
        {
            var monitor = createMonitor();

            monitor.CollectionRequested = true;
            monitor.NewFrame();

            using (monitor.BeginCollecting(PerformanceCollectionType.Work))
            {
            }

            monitor.CollectionRequested = false;
            monitor.NewFrame();

            Assert.That(monitor.BeginCollecting(PerformanceCollectionType.Work), Is.Null);
        }

        [Test]
        public void TestFramesOnlyQueuedWhileCollecting()
        {
            var monitor = createMonitor();

            for (int i = 0; i < 5; i++)
                monitor.NewFrame();

            Assert.That(monitor.PendingFrames, Is.Empty);

            monitor.CollectionRequested = true;

            for (int i = 0; i < 5; i++)
                monitor.NewFrame();

            Assert.That(monitor.PendingFrames, Is.Not.Empty);
        }

        [Test]
        public void TestResumingDoesNotAttributeIdlePeriodToOneFrame()
        {
            var monitor = createMonitor();

            // run stopped for a while, so that plenty of wall time passes unaccounted for.
            for (int i = 0; i < 50; i++)
                monitor.NewFrame();

            monitor.CollectionRequested = true;
            monitor.NewFrame();

            using (monitor.BeginCollecting(PerformanceCollectionType.Work))
            {
            }

            monitor.NewFrame();

            Assert.That(monitor.PendingFrames, Is.Not.Empty);

            foreach (var frame in monitor.PendingFrames)
            {
                foreach (double collected in frame.CollectedTimes.Values)
                    Assert.That(collected, Is.LessThan(1000));
            }
        }

        private static PerformanceMonitor createMonitor()
        {
            var thread = new TestGameThread();

            Assert.That(thread.Monitor, Is.Not.Null);

            return thread.Monitor!;
        }

        private class TestGameThread : GameThread
        {
            public TestGameThread()
                : base(name: "test")
            {
            }
        }
    }
}
