// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Timing;

namespace osu.Framework.Statistics
{
    internal static class TypePerformanceMonitor
    {
        private static readonly StopwatchClock clock = new StopwatchClock(true);

        private static readonly Stack<Type> current_collection_type_stack = new Stack<Type>();

        private static readonly Dictionary<Type, Collection> collected_times = new Dictionary<Type, Collection>();

        private static double consumptionTime;

        private static bool active;

        public static bool Active;

        /// <summary>
        /// Start collecting time for a specific object type.
        /// </summary>
        internal static void BeginCollecting(object obj)
        {
            if (!active) return;

            var type = obj.GetType();

            if (current_collection_type_stack.Count > 0)
            {
                Type t = current_collection_type_stack.Peek();

                if (!collected_times.TryGetValue(t, out var collection))
                    collected_times[t] = collection = new Collection();

                collection.TotalMilliseconds += consumeStopwatchElapsedTime();
            }

            current_collection_type_stack.Push(type);
        }

        /// <summary>
        /// End collecting time for a specific object type.
        /// </summary>
        internal static void EndCollecting(object obj)
        {
            if (!active) return;

            var type = obj.GetType();

            current_collection_type_stack.Pop();

            if (!collected_times.TryGetValue(type, out var collection))
                collected_times[type] = collection = new Collection();

            collection.TotalMilliseconds += consumeStopwatchElapsedTime();
            collection.Count++;
        }

        private static double lastReport;

        private static int framesSinceLastReport;

        private const string group_name = "Update (Top CPU consumers)";

        private const int reported_slot_count = 5;

        // Reused across reports so the statistics display does not rebuild items every second.
        private static GlobalStatistic<string>[]? reportSlots;

        private static GlobalStatistic<string>[] getReportSlots()
        {
            if (reportSlots != null)
                return reportSlots;

            reportSlots = new GlobalStatistic<string>[reported_slot_count];

            for (int i = 0; i < reportSlots.Length; i++)
                reportSlots[i] = GlobalStatistics.Get<string>(group_name, $"Top {i + 1}");

            return reportSlots;
        }

        public static void NewFrame()
        {
            if (Active != active)
            {
                active = Active;

                lastReport = 0;
                framesSinceLastReport = 0;

                GlobalStatistics.Clear(group_name);
                reportSlots = null;
                collected_times.Clear();
            }

            if (!active)
                return;

            //reset frame totals
            current_collection_type_stack.Clear();
            consumeStopwatchElapsedTime();

            if (framesSinceLastReport > 0 && clock.CurrentTime - lastReport > 1000)
            {
                int i = 0;

                foreach (var t in collected_times.OrderByDescending(t => t.Value.TotalMilliseconds).Take(reported_slot_count))
                {
                    string runCount = t.Value.Count / framesSinceLastReport > 1 ? $" ({t.Value.Count / framesSinceLastReport} instances)" : string.Empty;

                    getReportSlots()[i++].Value = $"{t.Key.Name}{runCount} {t.Value.TotalMilliseconds / framesSinceLastReport:N1}ms";
                }

                var slots = getReportSlots();

                for (; i < slots.Length; i++)
                    slots[i].Value = string.Empty;

                collected_times.Clear();
                framesSinceLastReport = 0;
                lastReport = clock.CurrentTime;
            }

            framesSinceLastReport++;
        }

        private static double consumeStopwatchElapsedTime()
        {
            double last = consumptionTime;

            consumptionTime = clock.CurrentTime;

            return consumptionTime - last;
        }

        private class Collection
        {
            public int Count;
            public double TotalMilliseconds;
        }
    }
}
