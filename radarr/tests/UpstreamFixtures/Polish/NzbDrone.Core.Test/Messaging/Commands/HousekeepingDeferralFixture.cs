using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using FluentAssertions;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Housekeeping;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Movies.Commands;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Update.Commands;

namespace NzbDrone.Core.Test.Messaging.Commands
{
    // Housekeeping is exclusive and waits until no other command runs. A stuck or very long command must not keep it
    // from running forever: after waiting HousekeepingCommand.MaxDeferral (30 minutes) since it was queued it starts
    // like stock (not exclusive). Uses only stock members (the constant is read through reflection), so the fixture
    // also compiles against the build without the bound, where the tests that expect housekeeping to start fail.
    [TestFixture]
    public class HousekeepingDeferralFixture : CoreTest<CommandQueue>
    {
        private static readonly TimeSpan Deferral = TimeSpan.FromMinutes(30);

        private CommandModel GivenCommand(string name, Command body, CommandStatus status, DateTime queuedAt, DateTime? startedAt = null, CommandPriority priority = CommandPriority.Normal)
        {
            var commandModel = Builder<CommandModel>
                .CreateNew()
                .With(c => c.Name = name)
                .With(c => c.Body = body)
                .With(c => c.Status = status)
                .With(c => c.Priority = priority)
                .With(c => c.QueuedAt = queuedAt)
                .With(c => c.StartedAt = startedAt ?? (status == CommandStatus.Started ? queuedAt : null))
                .Build();

            Subject.Add(commandModel);

            return commandModel;
        }

        private DateTime? DeferredUntil()
        {
            var field = typeof(CommandQueue).GetField("_housekeepingDeferredUntil", BindingFlags.NonPublic | BindingFlags.Instance);

            field.Should().NotBeNull();

            return (DateTime?)field.GetValue(Subject);
        }

        private List<string> CaptureQueueLog(Action action)
        {
            var target = new MemoryTarget { Name = "HousekeepingDeferralFixture", Layout = "${level}|${message}" };
            var rule = new LoggingRule("CommandQueue", LogLevel.Trace, target);

            LogManager.Configuration.AddTarget(target);
            LogManager.Configuration.LoggingRules.Add(rule);
            LogManager.ReconfigExistingLoggers();

            try
            {
                action();
            }
            finally
            {
                LogManager.Configuration.LoggingRules.Remove(rule);
                LogManager.Configuration.RemoveTarget(target.Name);
                LogManager.ReconfigExistingLoggers();
            }

            return target.Logs.ToList();
        }

        private List<Type> StartAllStartable()
        {
            var started = new List<Type>();

            while (Subject.TryGet(out var command))
            {
                started.Add(command.Body.GetType());
            }

            return started;
        }

        [Test]
        public void max_deferral_should_be_a_public_constant_of_thirty_minutes()
        {
            var minutes = typeof(HousekeepingCommand).GetField("MaxDeferralMinutes", BindingFlags.Public | BindingFlags.Static);
            var span = typeof(HousekeepingCommand).GetField("MaxDeferral", BindingFlags.Public | BindingFlags.Static);

            minutes.Should().NotBeNull("the page lists HousekeepingCommand.MaxDeferralMinutes");
            minutes.IsLiteral.Should().BeTrue();
            minutes.GetValue(null).Should().Be(30);
            span.Should().NotBeNull();
            span.GetValue(null).Should().Be(Deferral);
        }

        [Test]
        public void should_keep_deferring_housekeeping_queued_less_than_max_deferral_ago()
        {
            var now = DateTime.UtcNow;
            GivenCommand("RefreshMovie", new RefreshMovieCommand(), CommandStatus.Started, now.AddHours(-5));
            GivenCommand("Housekeeping", new HousekeepingCommand(), CommandStatus.Queued, now - Deferral + TimeSpan.FromMinutes(1));

            Subject.TryGet(out var command).Should().BeFalse();
            command.Should().BeNull();
        }

        // The reviewer's demonstration: one long command stays Started; on the build without the bound housekeeping
        // never started in 1000 re-evaluations.
        [Test]
        public void should_start_housekeeping_after_max_deferral_while_a_long_command_stays_started()
        {
            var now = DateTime.UtcNow;
            GivenCommand("RefreshMovie", new RefreshMovieCommand(), CommandStatus.Started, now.AddHours(-5));
            GivenCommand("Housekeeping", new HousekeepingCommand(), CommandStatus.Queued, now - Deferral - TimeSpan.FromMinutes(1));

            CommandModel command = null;

            for (var i = 0; i < 1000 && command == null; i++)
            {
                Subject.TryGet(out command);
            }

            command.Should().NotBeNull();
            command.Body.Should().BeOfType<HousekeepingCommand>();
            command.Status.Should().Be(CommandStatus.Started);
        }

        [Test]
        public void should_start_overdue_housekeeping_while_a_disk_command_is_running()
        {
            // ProcessMonitoredDownloads needs disk access and is long running; housekeeping needs neither, as on stock.
            var now = DateTime.UtcNow;
            GivenCommand("ProcessMonitoredDownloads", new ProcessMonitoredDownloadsCommand(), CommandStatus.Started, now.AddHours(-1));
            GivenCommand("Housekeeping", new HousekeepingCommand(), CommandStatus.Queued, now - Deferral);

            Subject.TryGet(out var command);

            command.Should().NotBeNull();
            command.Body.Should().BeOfType<HousekeepingCommand>();
        }

        [Test]
        public void overdue_housekeeping_should_run_alongside_other_commands_like_stock()
        {
            var now = DateTime.UtcNow;
            GivenCommand("RefreshMovie", new RefreshMovieCommand(), CommandStatus.Started, now.AddHours(-5));
            GivenCommand("Housekeeping", new HousekeepingCommand(), CommandStatus.Queued, now - Deferral - TimeSpan.FromMinutes(5), priority: CommandPriority.Low);
            GivenCommand("RssSync", new RssSyncCommand(), CommandStatus.Queued, now.AddMinutes(-1), priority: CommandPriority.Low);
            GivenCommand("MoviesSearch", new MoviesSearchCommand(), CommandStatus.Queued, now, priority: CommandPriority.Normal);

            var started = StartAllStartable();

            started.Should().BeEquivalentTo(new[] { typeof(HousekeepingCommand), typeof(RssSyncCommand), typeof(MoviesSearchCommand) });
        }

        [Test]
        public void housekeeping_started_within_max_deferral_should_stay_exclusive_while_it_runs()
        {
            // Queued 40 minutes ago but started after 5 minutes, when nothing ran: still exclusive although it now runs
            // longer than the deferral since it was queued.
            var now = DateTime.UtcNow;
            GivenCommand("Housekeeping", new HousekeepingCommand(), CommandStatus.Started, now.AddMinutes(-40), now.AddMinutes(-35));
            GivenCommand("RssSync", new RssSyncCommand(), CommandStatus.Queued, now.AddMinutes(-1));

            Subject.TryGet(out var command).Should().BeFalse();
            command.Should().BeNull();
        }

        [Test]
        public void should_start_housekeeping_queued_for_hours_at_once_when_nothing_runs()
        {
            // Unchanged: with nothing running, housekeeping starts at once and is exclusive.
            var now = DateTime.UtcNow;
            GivenCommand("Housekeeping", new HousekeepingCommand(), CommandStatus.Queued, now.AddHours(-3), priority: CommandPriority.Low);
            GivenCommand("RssSync", new RssSyncCommand(), CommandStatus.Queued, now.AddMinutes(-1), priority: CommandPriority.Low);

            var started = StartAllStartable();

            started.Should().Equal(typeof(HousekeepingCommand));
        }

        [Test]
        public void overdue_housekeeping_should_still_wait_for_a_running_application_update()
        {
            var now = DateTime.UtcNow;
            GivenCommand("ApplicationUpdate", new ApplicationUpdateCommand(), CommandStatus.Started, now.AddHours(-1));
            GivenCommand("Housekeeping", new HousekeepingCommand(), CommandStatus.Queued, now.AddHours(-2));

            Subject.TryGet(out var command).Should().BeFalse();
            command.Should().BeNull();
        }

        // A consumer waiting on the queue is woken when the deferral runs out, even if no command is added or completes
        // in the meantime (a stuck command never completes).
        [Test]
        public void waiting_consumer_should_start_housekeeping_when_max_deferral_runs_out()
        {
            var now = DateTime.UtcNow;
            GivenCommand("RefreshMovie", new RefreshMovieCommand(), CommandStatus.Started, now.AddHours(-5));
            GivenCommand("Housekeeping", new HousekeepingCommand(), CommandStatus.Queued, now - Deferral + TimeSpan.FromSeconds(2));

            using var cts = new CancellationTokenSource();

            var consumer = Task.Run(() => Subject.GetConsumingEnumerable(cts.Token).FirstOrDefault());

            var finished = consumer.Wait(TimeSpan.FromSeconds(10));

            cts.Cancel();

            if (!finished)
            {
                // Release the consumer so the test ends.
                Subject.PulseAllConsumers();
                consumer.Wait(TimeSpan.FromSeconds(5));
            }

            finished.Should().BeTrue("the consumer should wake up when the deferral runs out");
            consumer.Result.Should().NotBeNull();
            consumer.Result.Body.Should().BeOfType<HousekeepingCommand>();
        }

        // With no housekeeping held back, consumers wait like stock (Monitor.Wait without a timeout). The deadline set
        // while housekeeping waited must be cleared once it starts, or idle consumers would wake every second forever.
        [Test]
        public void should_clear_the_housekeeping_deadline_once_housekeeping_starts()
        {
            var now = DateTime.UtcNow;
            var refresh = GivenCommand("RefreshMovie", new RefreshMovieCommand(), CommandStatus.Started, now.AddMinutes(-5));
            GivenCommand("Housekeeping", new HousekeepingCommand(), CommandStatus.Queued, now.AddMinutes(-1));

            Subject.TryGet(out var first).Should().BeFalse();
            first.Should().BeNull();
            DeferredUntil().Should().BeCloseTo(now.AddMinutes(-1) + Deferral, TimeSpan.FromSeconds(1));

            refresh.Status = CommandStatus.Completed;

            Subject.TryGet(out var second).Should().BeTrue();
            second.Body.Should().BeOfType<HousekeepingCommand>();
            DeferredUntil().Should().BeNull();

            Subject.TryGet(out var third).Should().BeFalse();
            third.Should().BeNull();
            DeferredUntil().Should().BeNull();
        }

        [Test]
        public void should_clear_the_housekeeping_deadline_when_the_waiting_housekeeping_is_removed()
        {
            var now = DateTime.UtcNow;
            GivenCommand("RefreshMovie", new RefreshMovieCommand(), CommandStatus.Started, now.AddMinutes(-5));
            var housekeeping = GivenCommand("Housekeeping", new HousekeepingCommand(), CommandStatus.Queued, now.AddMinutes(-1));

            Subject.TryGet(out _).Should().BeFalse();
            DeferredUntil().Should().NotBeNull();

            Subject.RemoveMany(new[] { housekeeping });

            Subject.TryGet(out _).Should().BeFalse();
            DeferredUntil().Should().BeNull();
        }

        [Test]
        public void should_log_the_wait_once_at_debug_while_housekeeping_is_deferred()
        {
            var now = DateTime.UtcNow;
            GivenCommand("RefreshMovie", new RefreshMovieCommand(), CommandStatus.Started, now.AddMinutes(-5));
            GivenCommand("Housekeeping", new HousekeepingCommand(), CommandStatus.Queued, now.AddMinutes(-1));

            var logs = CaptureQueueLog(() =>
            {
                for (var i = 0; i < 5; i++)
                {
                    Subject.TryGet(out _).Should().BeFalse();
                }
            });

            logs.Should().ContainSingle();
            logs[0].Should().StartWith("Debug|Housekeeping");
            logs[0].Should().Contain("RefreshMovie").And.Contain("30 minutes");
        }

        [Test]
        public void should_log_at_info_when_overdue_housekeeping_starts_alongside_running_commands()
        {
            var now = DateTime.UtcNow;
            GivenCommand("RefreshMovie", new RefreshMovieCommand(), CommandStatus.Started, now.AddHours(-5));
            GivenCommand("Housekeeping", new HousekeepingCommand(), CommandStatus.Queued, now - Deferral - TimeSpan.FromMinutes(1));

            CommandModel command = null;
            var logs = CaptureQueueLog(() => Subject.TryGet(out command).Should().BeTrue());

            command.Body.Should().BeOfType<HousekeepingCommand>();
            logs.Should().ContainSingle();
            logs[0].Should().StartWith("Info|Housekeeping has waited 30 minutes");
            logs[0].Should().Contain("RefreshMovie");
        }

        [Test]
        public void should_not_log_when_housekeeping_starts_with_nothing_running()
        {
            var now = DateTime.UtcNow;
            GivenCommand("Housekeeping", new HousekeepingCommand(), CommandStatus.Queued, now.AddHours(-3));

            var logs = CaptureQueueLog(() => Subject.TryGet(out _).Should().BeTrue());

            logs.Should().BeEmpty();
        }
    }
}
