// Regression tests for the Scheduler timer handler leak. Before the fix the scheduler timer was static, so every
// in-process restart (UI or API restart, backup restore) built a new Scheduler that added another Elapsed handler
// to the same timer. The handlers of earlier hosts kept running and queued tasks through their stale TaskManager and
// CommandQueueManager, and the timer kept every earlier Scheduler (and what it references) reachable.
//
// The tests drive Scheduler through the same lifecycle events the host publishes. To avoid waiting 30 seconds for a
// tick they shorten the interval of the scheduler's timer(s) through reflection. They find every
// System.Timers.Timer field on Scheduler, static or instance, so the same tests run on the unfixed and the fixed code.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.JobTests
{
    [TestFixture]
    [NonParallelizable]
    public class SchedulerFixture : CoreTest
    {
        private const double FastInterval = 20;

        private List<Scheduler> _schedulers;

        private class Host
        {
            public Mock<ITaskManager> TaskManager { get; } = new Mock<ITaskManager>();
            public Mock<IManageCommandQueue> CommandQueue { get; } = new Mock<IManageCommandQueue>();
            public Scheduler Subject { get; set; }

            public int PendingCalls => TaskManager.Invocations.Count(i => i.Method.Name == nameof(ITaskManager.GetPending));
            public int PushCalls => CommandQueue.Invocations.Count(i => i.Method.Name == nameof(IManageCommandQueue.Push));
        }

        [SetUp]
        public void Setup()
        {
            _schedulers = new List<Scheduler>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var scheduler in _schedulers)
            {
                scheduler.Handle(new ApplicationShutdownRequested());
            }
        }

        private Host StartHost(params ScheduledTask[] pending)
        {
            var host = new Host();

            host.TaskManager.Setup(s => s.GetPending()).Returns(() => pending.ToList());

            return StartHost(host);
        }

        private Host StartHost(Host host)
        {
            host.Subject = new Scheduler(host.TaskManager.Object, host.CommandQueue.Object, TestLogger);
            _schedulers.Add(host.Subject);

            host.Subject.Handle(new ApplicationStartedEvent());

            return host;
        }

        private static void StopHost(Host host, bool restarting)
        {
            host.Subject.Handle(new ApplicationShutdownRequested(restarting));
        }

        private static List<System.Timers.Timer> GetTimers(Scheduler scheduler)
        {
            return typeof(Scheduler)
                .GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)
                .Where(f => f.FieldType == typeof(System.Timers.Timer))
                .Select(f => (System.Timers.Timer)f.GetValue(f.IsStatic ? null : scheduler))
                .Where(t => t != null)
                .ToList();
        }

        private static void SpeedUp(IEnumerable<System.Timers.Timer> timers)
        {
            foreach (var timer in timers)
            {
                timer.Interval = FastInterval;
            }
        }

        private static void SpeedUp(Host host)
        {
            var timers = GetTimers(host.Subject);

            timers.Should().NotBeEmpty("Scheduler should own a timer once the application has started");

            SpeedUp(timers);
        }

        private static void WaitFor(Func<bool> condition)
        {
            var stopwatch = Stopwatch.StartNew();

            while (!condition())
            {
                if (stopwatch.Elapsed > TimeSpan.FromSeconds(15))
                {
                    Assert.Fail("Timed out waiting for the scheduler to tick");
                }

                Thread.Sleep(10);
            }
        }

        // Starts a host and shuts it down for a restart, without keeping any reference to it in the fixture.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private WeakReference StartAndRestartHost()
        {
            var taskManager = new Mock<ITaskManager>();
            taskManager.Setup(s => s.GetPending()).Returns(new List<ScheduledTask>());

            var scheduler = new Scheduler(taskManager.Object, new Mock<IManageCommandQueue>().Object, TestLogger);
            scheduler.Handle(new ApplicationStartedEvent());
            scheduler.Handle(new ApplicationShutdownRequested(true));

            return new WeakReference(scheduler);
        }

        [TestCase(1)]
        [TestCase(5)]
        public void scheduler_of_a_previous_host_should_not_tick_after_in_process_restart(int restarts)
        {
            var previous = new List<Host>();
            var previousTimers = new List<System.Timers.Timer>();
            var current = StartHost(new ScheduledTask { TypeName = "Current" });

            for (var i = 0; i < restarts; i++)
            {
                // Keep the timers the host had while it was running, so a timer that shutdown failed to stop is sped up too.
                previousTimers.AddRange(GetTimers(current.Subject));
                StopHost(current, true);
                previous.Add(current);
                current = StartHost(new ScheduledTask { TypeName = "Current" });
            }

            var pushesBefore = previous.Select(h => h.PushCalls).ToList();

            SpeedUp(previousTimers);
            SpeedUp(current);
            WaitFor(() => current.PendingCalls >= 5);

            for (var i = 0; i < previous.Count; i++)
            {
                previous[i].PendingCalls.Should().Be(0, "host {0} was shut down before the restart", i);
                previous[i].PushCalls.Should().Be(pushesBefore[i]);
            }
        }

        [Test]
        public void scheduler_of_a_previous_host_should_be_collectable_after_in_process_restart()
        {
            var previous = Enumerable.Range(0, 5).Select(_ => StartAndRestartHost()).ToList();

            StartHost();

            for (var i = 0; i < 3 && previous.Any(r => r.IsAlive); i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            previous.Count(r => r.IsAlive).Should().Be(0, "a host that was shut down for a restart should not be kept alive by its timer");
        }

        [Test]
        public void tick_should_push_every_pending_task_as_scheduled()
        {
            var lastExecution = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
            var lastStart = new DateTime(2026, 11, 1, 9, 59, 0, DateTimeKind.Utc);

            var rss = new ScheduledTask { TypeName = "NzbDrone.Core.Indexers.RssSyncCommand", LastExecution = lastExecution, LastStartTime = lastStart, Priority = CommandPriority.Low };
            var housekeeping = new ScheduledTask { TypeName = "NzbDrone.Core.Housekeeping.HousekeepingCommand", LastExecution = lastExecution.AddHours(-1), LastStartTime = lastStart.AddHours(-1), Priority = CommandPriority.Normal };

            var host = StartHost(rss, housekeeping);

            SpeedUp(host);
            WaitFor(() => host.PushCalls >= 4);

            host.CommandQueue.Verify(v => v.Push(rss.TypeName, lastExecution, lastStart, CommandPriority.Low, CommandTrigger.Scheduled), Times.AtLeastOnce());
            host.CommandQueue.Verify(v => v.Push(housekeeping.TypeName, lastExecution.AddHours(-1), lastStart.AddHours(-1), CommandPriority.Normal, CommandTrigger.Scheduled), Times.AtLeastOnce());
            host.CommandQueue.Verify(v => v.Push(It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CommandPriority>(), It.Is<CommandTrigger>(t => t != CommandTrigger.Scheduled)), Times.Never());
        }

        [Test]
        public void shutdown_should_stop_ticks()
        {
            var host = StartHost(new ScheduledTask { TypeName = "Task" });

            SpeedUp(host);
            WaitFor(() => host.PendingCalls >= 2);

            StopHost(host, false);

            // Allow a tick that was already running when shutdown was requested to finish.
            Thread.Sleep(100);
            var calls = host.PendingCalls;

            Thread.Sleep(300);
            host.PendingCalls.Should().Be(calls);
        }

        [Test]
        public void shutdown_during_a_tick_should_not_reenable_the_timer()
        {
            var entered = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var host = new Host();

            host.TaskManager.Setup(s => s.GetPending()).Returns(() =>
            {
                entered.Set();
                release.Wait(5000);

                return new List<ScheduledTask>();
            });

            StartHost(host);

            var timers = GetTimers(host.Subject);
            SpeedUp(host);

            entered.Wait(5000).Should().BeTrue("the first tick should start");

            // Shut down while the tick is still inside GetPending, then let the tick finish.
            StopHost(host, false);
            release.Set();

            Thread.Sleep(300);
            var calls = host.PendingCalls;

            Thread.Sleep(300);
            host.PendingCalls.Should().Be(calls);
            timers.Should().OnlyContain(t => !t.Enabled);
            ExceptionVerification.ExpectedErrors(0);
        }

        [Test]
        public void ticks_should_continue_after_a_tick_throws()
        {
            var host = new Host();
            var calls = 0;

            host.TaskManager.Setup(s => s.GetPending()).Returns(() =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new InvalidOperationException("first tick fails");
                }

                return new List<ScheduledTask>();
            });

            StartHost(host);

            SpeedUp(host);
            WaitFor(() => host.PendingCalls >= 3);

            ExceptionVerification.WaitForErrors(1, 5000);
        }
    }
}
