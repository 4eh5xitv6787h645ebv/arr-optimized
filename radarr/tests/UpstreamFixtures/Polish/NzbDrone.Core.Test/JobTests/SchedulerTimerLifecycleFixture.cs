// Pins four details of the per-instance Scheduler timer that SchedulerFixture does not: a second started event stops
// the previous timer, shutdown disposes the timer, the timer belongs to the instance (an older host shutting down late
// does not stop a newer host), and a tick disables the timer so ticks never overlap (as stock does). Like
// SchedulerFixture it finds the timer fields through reflection and shortens their interval.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.JobTests
{
    [TestFixture]
    [NonParallelizable]
    public class SchedulerTimerLifecycleFixture : CoreTest
    {
        private List<Scheduler> _schedulers;

        private class Host
        {
            public Mock<ITaskManager> TaskManager { get; } = new Mock<ITaskManager>();
            public Mock<IManageCommandQueue> CommandQueue { get; } = new Mock<IManageCommandQueue>();
            public Scheduler Subject { get; set; }

            public int PendingCalls => TaskManager.Invocations.Count(i => i.Method.Name == nameof(ITaskManager.GetPending));
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

        private Host CreateHost(Func<List<ScheduledTask>> pending = null)
        {
            var host = new Host();
            host.TaskManager.Setup(s => s.GetPending()).Returns(pending ?? (() => new List<ScheduledTask>()));
            host.Subject = new Scheduler(host.TaskManager.Object, host.CommandQueue.Object, TestLogger);
            _schedulers.Add(host.Subject);

            return host;
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

        private static System.Timers.Timer SpeedUp(Host host, double interval)
        {
            var timer = GetTimers(host.Subject).Should().ContainSingle("a started Scheduler owns one timer").Subject;
            timer.Interval = interval;

            return timer;
        }

        private static void WaitFor(Func<bool> condition, string because)
        {
            var stopwatch = Stopwatch.StartNew();

            while (!condition())
            {
                if (stopwatch.Elapsed > TimeSpan.FromSeconds(15))
                {
                    Assert.Fail("Timed out waiting: " + because);
                }

                Thread.Sleep(10);
            }
        }

        [Test]
        public void second_started_event_should_stop_and_dispose_the_previous_timer()
        {
            var host = CreateHost();

            host.Subject.Handle(new ApplicationStartedEvent());
            var first = GetTimers(host.Subject).Single();
            var disposed = false;
            first.Disposed += (o, e) => disposed = true;

            host.Subject.Handle(new ApplicationStartedEvent());

            first.Enabled.Should().BeFalse();
            disposed.Should().BeTrue();
            GetTimers(host.Subject).Should().ContainSingle().Which.Should().NotBeSameAs(first);
        }

        [Test]
        public void shutdown_should_dispose_the_timer()
        {
            var host = CreateHost();

            host.Subject.Handle(new ApplicationStartedEvent());
            var timer = GetTimers(host.Subject).Single();
            var disposed = false;
            timer.Disposed += (o, e) => disposed = true;

            host.Subject.Handle(new ApplicationShutdownRequested(true));

            disposed.Should().BeTrue();
            timer.Enabled.Should().BeFalse();
            GetTimers(host.Subject).Should().BeEmpty();
        }

        [Test]
        public void older_host_shutting_down_after_a_newer_one_started_should_not_stop_the_newer_one()
        {
            var older = CreateHost();
            var newer = CreateHost();

            older.Subject.Handle(new ApplicationStartedEvent());
            newer.Subject.Handle(new ApplicationStartedEvent());

            GetTimers(older.Subject).Should().ContainSingle();
            GetTimers(newer.Subject).Should().ContainSingle();
            GetTimers(newer.Subject).Single().Should().NotBeSameAs(GetTimers(older.Subject).Single());

            older.Subject.Handle(new ApplicationShutdownRequested(true));

            SpeedUp(newer, 20);
            WaitFor(() => newer.PendingCalls >= 3, "the newer host should keep ticking");
            older.PendingCalls.Should().Be(0);
        }

        [Test]
        public void ticks_should_not_overlap_when_a_tick_takes_longer_than_the_interval()
        {
            var running = 0;
            var maxRunning = 0;

            var host = CreateHost(() =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref maxRunning, now);
                Thread.Sleep(400);
                Interlocked.Decrement(ref running);

                return new List<ScheduledTask>();
            });

            host.Subject.Handle(new ApplicationStartedEvent());
            SpeedUp(host, 100);

            WaitFor(() => host.PendingCalls >= 3, "three ticks");

            maxRunning.Should().Be(1);
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;

            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }
}
