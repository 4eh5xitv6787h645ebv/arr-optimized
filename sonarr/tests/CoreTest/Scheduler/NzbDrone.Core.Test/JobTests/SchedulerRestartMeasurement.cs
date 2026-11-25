// Measurement (not a regression test, marked Explicit): drives Scheduler through N-1 in-process restarts, shortens
// the timer interval to 2 ms and records, for 10 s, how often each host's TaskManager is asked for pending tasks and
// whether ticks stop altogether (longest gap without any tick). Run one case per process so a static timer left
// over from an earlier case cannot skew the next one.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.JobTests
{
    [TestFixture]
    [Explicit]
    [NonParallelizable]
    public class SchedulerRestartMeasurement : CoreTest
    {
        [TestCase(1)]
        [TestCase(4)]
        [TestCase(8)]
        public void measure_ticks_after_restarts(int hosts)
        {
            var counters = new int[hosts];
            var schedulers = new List<Scheduler>();

            for (var i = 0; i < hosts; i++)
            {
                var index = i;
                var taskManager = new Mock<ITaskManager>();
                taskManager.Setup(s => s.GetPending()).Returns(() =>
                {
                    Interlocked.Increment(ref counters[index]);
                    return new List<ScheduledTask>();
                });

                if (schedulers.Any())
                {
                    schedulers.Last().Handle(new ApplicationShutdownRequested(true));
                }

                var scheduler = new Scheduler(taskManager.Object, new Mock<IManageCommandQueue>().Object, TestLogger);
                scheduler.Handle(new ApplicationStartedEvent());
                schedulers.Add(scheduler);
            }

            var timer = typeof(Scheduler)
                .GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)
                .Where(f => f.FieldType == typeof(System.Timers.Timer))
                .Select(f => (System.Timers.Timer)f.GetValue(f.IsStatic ? null : schedulers.Last()))
                .Single(t => t != null);

            timer.Interval = 2;

            var stopwatch = Stopwatch.StartNew();
            var lastTotal = 0;
            var lastChange = TimeSpan.Zero;
            var longestGap = TimeSpan.Zero;

            while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
            {
                Thread.Sleep(5);
                var total = counters.Sum();

                if (total != lastTotal)
                {
                    lastTotal = total;
                    lastChange = stopwatch.Elapsed;
                }

                var gap = stopwatch.Elapsed - lastChange;
                longestGap = gap > longestGap ? gap : longestGap;
            }

            var current = counters.Last();
            var stale = counters.Take(hosts - 1).Sum();
            var stalled = stopwatch.Elapsed - lastChange > TimeSpan.FromSeconds(1);

            TestContext.Progress.WriteLine(
                "SCHEDULER-MEASURE hosts={0} currentHostTicks={1} staleHostTicks={2} longestGapMs={3:F0} stalledAtEnd={4} timerEnabled={5}",
                hosts, current, stale, longestGap.TotalMilliseconds, stalled, timer.Enabled);

            schedulers.Last().Handle(new ApplicationShutdownRequested());
        }
    }
}
