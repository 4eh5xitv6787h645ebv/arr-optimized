using System;
using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Housekeeping;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Movies.Commands;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Update.Commands;

namespace NzbDrone.Core.Test.Messaging.Commands
{
    [TestFixture]
    public class HousekeepingCommandQueueFixture : CoreTest<CommandQueue>
    {
        private DateTime _queuedAt;

        [SetUp]
        public void Setup()
        {
            _queuedAt = DateTime.UtcNow.AddMinutes(-10);
        }

        private CommandModel GivenCommand(string name, Command body, CommandStatus status, CommandPriority priority = CommandPriority.Normal)
        {
            _queuedAt = _queuedAt.AddSeconds(1);

            var commandModel = Builder<CommandModel>
                .CreateNew()
                .With(c => c.Name = name)
                .With(c => c.Body = body)
                .With(c => c.Status = status)
                .With(c => c.Priority = priority)
                .With(c => c.QueuedAt = _queuedAt)
                .Build();

            Subject.Add(commandModel);

            return commandModel;
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
        public void housekeeping_command_should_be_exclusive()
        {
            new HousekeepingCommand().IsExclusive.Should().BeTrue();
        }

        [Test]
        public void should_return_housekeeping_command_if_nothing_running()
        {
            GivenCommand("Housekeeping", new HousekeepingCommand(), CommandStatus.Queued);

            Subject.TryGet(out var command);

            command.Should().NotBeNull();
            command.Body.Should().BeOfType<HousekeepingCommand>();
            command.Status.Should().Be(CommandStatus.Started);
        }

        [Test]
        public void should_not_return_housekeeping_command_while_process_monitored_downloads_is_running()
        {
            GivenCommand("ProcessMonitoredDownloads", new ProcessMonitoredDownloadsCommand(), CommandStatus.Started);
            GivenCommand("Housekeeping", new HousekeepingCommand(), CommandStatus.Queued);

            Subject.TryGet(out var command);

            command.Should().BeNull();
        }

        [Test]
        public void should_not_return_housekeeping_command_while_a_short_command_is_running()
        {
            GivenCommand("Backup", new Backup.BackupCommand(), CommandStatus.Started);
            GivenCommand("Housekeeping", new HousekeepingCommand(), CommandStatus.Queued);

            Subject.TryGet(out var command);

            command.Should().BeNull();
        }

        [Test]
        public void should_not_return_any_command_while_housekeeping_is_running()
        {
            GivenCommand("Housekeeping", new HousekeepingCommand(), CommandStatus.Started);
            GivenCommand("ProcessMonitoredDownloads", new ProcessMonitoredDownloadsCommand(), CommandStatus.Queued);
            GivenCommand("RefreshMovie", new RefreshMovieCommand(), CommandStatus.Queued);

            Subject.TryGet(out var command);

            command.Should().BeNull();
        }

        [Test]
        public void should_skip_housekeeping_and_return_next_command_while_long_running_command_is_running()
        {
            GivenCommand("RssSync", new RssSyncCommand(), CommandStatus.Started);
            GivenCommand("Housekeeping", new HousekeepingCommand(), CommandStatus.Queued);
            GivenCommand("RefreshMovie", new RefreshMovieCommand(), CommandStatus.Queued);

            Subject.TryGet(out var command);

            command.Should().NotBeNull();
            command.Body.Should().BeOfType<RefreshMovieCommand>();
        }

        [Test]
        public void should_start_scheduled_commands_queued_after_housekeeping_while_a_short_command_is_running()
        {
            // Scheduled tasks are queued with Low priority; a manual search is Normal and not "long running".
            GivenCommand("MissingMoviesSearch", new MissingMoviesSearchCommand(), CommandStatus.Started);
            GivenCommand("Housekeeping", new HousekeepingCommand(), CommandStatus.Queued, CommandPriority.Low);
            GivenCommand("RssSync", new RssSyncCommand(), CommandStatus.Queued, CommandPriority.Low);
            GivenCommand("ImportListSync", new ImportListSyncCommand(), CommandStatus.Queued, CommandPriority.Low);

            var started = StartAllStartable();

            started.Should().Contain(typeof(RssSyncCommand));
            started.Should().Contain(typeof(ImportListSyncCommand));
            started.Should().NotContain(typeof(HousekeepingCommand));
        }

        [Test]
        public void should_start_commands_queued_after_housekeeping_while_a_bulk_move_is_running()
        {
            GivenCommand("BulkMoveMovie", new BulkMoveMovieCommand(), CommandStatus.Started);
            GivenCommand("Housekeeping", new HousekeepingCommand(), CommandStatus.Queued);
            GivenCommand("RssSync", new RssSyncCommand(), CommandStatus.Queued);

            var started = StartAllStartable();

            started.Should().Contain(typeof(RssSyncCommand));
            started.Should().NotContain(typeof(HousekeepingCommand));
        }

        [Test]
        public void should_start_housekeeping_once_running_commands_have_completed()
        {
            var running = GivenCommand("MissingMoviesSearch", new MissingMoviesSearchCommand(), CommandStatus.Started);
            GivenCommand("Housekeeping", new HousekeepingCommand(), CommandStatus.Queued, CommandPriority.Low);

            Subject.TryGet(out var command);
            command.Should().BeNull();

            running.Status = CommandStatus.Completed;

            Subject.TryGet(out command);

            command.Should().NotBeNull();
            command.Body.Should().BeOfType<HousekeepingCommand>();
        }

        [Test]
        public void should_still_hold_queue_for_application_update_while_a_command_is_running()
        {
            // Stock behaviour for the other exclusive command is unchanged.
            GivenCommand("Backup", new Backup.BackupCommand(), CommandStatus.Started);
            GivenCommand("ApplicationUpdate", new ApplicationUpdateCommand(), CommandStatus.Queued);
            GivenCommand("RssSync", new RssSyncCommand(), CommandStatus.Queued);

            Subject.TryGet(out var command);

            command.Should().BeNull();
        }
    }
}
