// Regression tests for "tracked download stuck in Importing when the import throws".
// CompletedDownloadService.Import sets the state to Importing before ProcessPath and VerifyImport. On stock an
// exception from either leaves the download in Importing, and DownloadProcessingService (the real caller used
// here) only imports ImportPending downloads, so the download is never imported again until a restart.
using System;
using System.Collections.Generic;
using System.IO;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.ImportFixes
{
    [TestFixture]
    public class ImportFailureRetryFixture : CoreTest<DownloadProcessingService>
    {
        private TrackedDownload _trackedDownload;
        private Episode _episode;

        [SetUp]
        public void Setup()
        {
            // The first CoreTest set-up in a test process replaces the NLog configuration that LoggingTest set up,
            // dropping the ExceptionVerification target, so warnings in the first test would not be counted.
            InitLogging();

            _episode = new Episode { Id = 1, SeasonNumber = 1, EpisodeNumber = 1 };

            var completed = Builder<DownloadClientItem>.CreateNew()
                                                       .With(h => h.Status = DownloadItemStatus.Completed)
                                                       .With(h => h.OutputPath = new OsPath(@"C:\DropFolder\MyDownload".AsOsAgnostic()))
                                                       .With(h => h.Title = "Drone.S01E01.HDTV")
                                                       .Build();

            _trackedDownload = Builder<TrackedDownload>.CreateNew()
                                                       .With(c => c.State = TrackedDownloadState.ImportPending)
                                                       .With(c => c.IsTrackable = true)
                                                       .With(c => c.DownloadItem = completed)
                                                       .With(c => c.RemoteEpisode = new RemoteEpisode
                                                       {
                                                           Series = new Series(),
                                                           Episodes = new List<Episode> { _episode }
                                                       })
                                                       .Build();

            Mocker.SetConstant<ICompletedDownloadService>(Mocker.Resolve<CompletedDownloadService>());

            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.EnableCompletedDownloadHandling)
                  .Returns(true);

            Mocker.GetMock<ITrackedDownloadService>()
                  .Setup(s => s.GetTrackedDownloads())
                  .Returns(new List<TrackedDownload> { _trackedDownload });

            Mocker.GetMock<IProvideImportItemService>()
                  .Setup(s => s.ProvideImportItem(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientItem>()))
                  .Returns<DownloadClientItem, DownloadClientItem>((i, p) => i);

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Returns(new List<EpisodeHistory>());
        }

        private void GivenProcessPathThrows(string message)
        {
            Mocker.GetMock<IDownloadedEpisodesImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()))
                  .Throws(new IOException(message));
        }

        private void GivenProcessPathImportsTheEpisode()
        {
            Mocker.GetMock<IDownloadedEpisodesImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
                           {
                               new ImportResult(new ImportDecision(new LocalEpisode { Path = @"C:\TestPath\Droned.S01E01.mkv".AsOsAgnostic(), Episodes = { _episode } }))
                           });
        }

        private void ProcessMonitoredDownloads()
        {
            Subject.Execute(new ProcessMonitoredDownloadsCommand());
        }

        private void VerifyNotImported()
        {
            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Never());
        }

        [Test]
        public void should_set_import_pending_and_warn_when_processing_the_path_throws()
        {
            GivenProcessPathThrows("Stale file handle");

            ProcessMonitoredDownloads();

            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportPending);
            _trackedDownload.Status.Should().Be(TrackedDownloadStatus.Warning);
            _trackedDownload.StatusMessages.Should().ContainSingle()
                            .Which.Messages.Should().ContainSingle(m => m.Contains("Stale file handle"));

            VerifyNotImported();
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_set_import_pending_when_verifying_the_import_throws()
        {
            Mocker.GetMock<IDownloadedEpisodesImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>());

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Throws(new InvalidOperationException("database is locked"));

            ProcessMonitoredDownloads();

            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportPending);
            _trackedDownload.StatusMessages.Should().ContainSingle()
                            .Which.Messages.Should().ContainSingle(m => m.Contains("database is locked"));

            VerifyNotImported();
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_import_on_the_next_run_after_a_failed_attempt()
        {
            GivenProcessPathThrows("Stale file handle");

            ProcessMonitoredDownloads();

            GivenProcessPathImportsTheEpisode();

            ProcessMonitoredDownloads();

            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                  .Verify(v => v.ProcessPath(It.IsAny<string>(), ImportMode.Auto, It.IsAny<Series>(), It.IsAny<DownloadClientItem>()), Times.Exactly(2));

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Once());

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_keep_retrying_and_warn_once_when_the_same_failure_repeats()
        {
            GivenProcessPathThrows("Permission denied");

            ProcessMonitoredDownloads();
            ProcessMonitoredDownloads();
            ProcessMonitoredDownloads();

            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportPending);

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                  .Verify(v => v.ProcessPath(It.IsAny<string>(), ImportMode.Auto, It.IsAny<Series>(), It.IsAny<DownloadClientItem>()), Times.Exactly(3));

            VerifyNotImported();
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_not_rethrow_out_of_import_when_the_import_throws()
        {
            // The failure is handled where it happens (logged once, retried later); rethrowing would only make
            // DownloadProcessingService log it a second time.
            GivenProcessPathThrows("Stale file handle");

            Action import = () => Mocker.Resolve<ICompletedDownloadService>().Import(_trackedDownload);

            import.Should().NotThrow();
            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportPending);
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_stay_imported_when_publishing_the_completed_event_throws()
        {
            // The download is already Imported when DownloadCompletedEvent is published, so a failure from there must
            // not send it back to ImportPending, or it would be imported a second time. The real EventAggregator catches
            // handler exceptions, so this can't happen today; the test pins the State == Importing guard.
            GivenProcessPathImportsTheEpisode();

            Mocker.GetMock<IEventAggregator>()
                  .Setup(s => s.PublishEvent(It.IsAny<DownloadCompletedEvent>()))
                  .Throws(new InvalidOperationException("handler failed"));

            ProcessMonitoredDownloads();

            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);
            _trackedDownload.Status.Should().Be(TrackedDownloadStatus.Ok);
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_not_change_state_when_import_succeeds()
        {
            GivenProcessPathImportsTheEpisode();

            ProcessMonitoredDownloads();

            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);
            _trackedDownload.Status.Should().Be(TrackedDownloadStatus.Ok);

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Once());
        }
    }
}
