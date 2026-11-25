// Regression tests for "a download with zero mapped episodes is treated as already imported".
// TrackedDownloadAlreadyImported.IsImported returned Episodes.All(...), which is true for an empty list. On stock a
// download with a series but no mapped episodes (for example, the grabbed episodes were removed by a series refresh)
// was marked Imported and DownloadCompletedEvent was published although nothing was imported; without a series,
// VerifyImport threw a NullReferenceException.
using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
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
    public class ZeroMappedEpisodesAlreadyImportedFixture : CoreTest<TrackedDownloadAlreadyImported>
    {
        [Test]
        public void should_return_false_if_no_episodes_are_mapped()
        {
            var trackedDownload = Builder<TrackedDownload>.CreateNew()
                                                          .With(t => t.RemoteEpisode = new RemoteEpisode { Series = new Series(), Episodes = new List<Episode>() })
                                                          .With(t => t.DownloadItem = Builder<DownloadClientItem>.CreateNew().Build())
                                                          .Build();

            var historyItems = new List<EpisodeHistory>
                               {
                                   Builder<EpisodeHistory>.CreateNew().With(h => h.EventType = EpisodeHistoryEventType.Grabbed).Build()
                               };

            Subject.IsImported(trackedDownload, historyItems).Should().BeFalse();
        }
    }

    [TestFixture]
    public class ZeroMappedEpisodesImportFixture : CoreTest<CompletedDownloadService>
    {
        private TrackedDownload _trackedDownload;

        [SetUp]
        public void Setup()
        {
            var completed = Builder<DownloadClientItem>.CreateNew()
                                                       .With(h => h.Status = DownloadItemStatus.Completed)
                                                       .With(h => h.OutputPath = new OsPath(@"C:\DropFolder\MyDownload".AsOsAgnostic()))
                                                       .With(h => h.Title = "Drone.S01E01.HDTV")
                                                       .Build();

            _trackedDownload = Builder<TrackedDownload>.CreateNew()
                                                       .With(c => c.State = TrackedDownloadState.ImportPending)
                                                       .With(c => c.DownloadItem = completed)
                                                       .Build();

            Mocker.SetConstant<ITrackedDownloadAlreadyImported>(Mocker.Resolve<TrackedDownloadAlreadyImported>());

            Mocker.GetMock<IProvideImportItemService>()
                  .Setup(s => s.ProvideImportItem(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientItem>()))
                  .Returns<DownloadClientItem, DownloadClientItem>((i, p) => i);

            // The download's grab history (episode ids that no longer map to an episode).
            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Returns(Builder<EpisodeHistory>.CreateListOfSize(2)
                                                  .All()
                                                  .With(h => h.EventType = EpisodeHistoryEventType.Grabbed)
                                                  .BuildList());

            Mocker.GetMock<IEpisodeService>()
                  .Setup(s => s.GetEpisodes(It.IsAny<IEnumerable<int>>()))
                  .Returns(new List<Episode>());

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
                           {
                               new ImportResult(new ImportDecision(null, new ImportRejection(ImportRejectionReason.UnknownSeries, "Unknown Series")), "Unknown Series")
                           });
        }

        private void VerifyNotImported()
        {
            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Never());

            _trackedDownload.State.Should().NotBe(TrackedDownloadState.Imported);
            _trackedDownload.State.Should().NotBe(TrackedDownloadState.Importing);
        }

        [Test]
        public void should_not_mark_as_imported_if_series_is_mapped_but_no_episodes_are()
        {
            _trackedDownload.RemoteEpisode = new RemoteEpisode { Series = new Series { Id = 5 }, Episodes = new List<Episode>(), ParsedEpisodeInfo = new ParsedEpisodeInfo() };

            Subject.Import(_trackedDownload);

            VerifyNotImported();
            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportBlocked);
            ExceptionVerification.IgnoreWarns();
        }

        [Test]
        public void should_not_mark_as_imported_if_no_files_were_found_and_no_episodes_are_mapped()
        {
            _trackedDownload.RemoteEpisode = new RemoteEpisode { Series = new Series { Id = 5 }, Episodes = new List<Episode>(), ParsedEpisodeInfo = new ParsedEpisodeInfo() };

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>());

            Subject.Import(_trackedDownload);

            VerifyNotImported();
            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportPending);
        }

        [Test]
        public void should_block_import_if_no_series_or_episodes_are_mapped()
        {
            _trackedDownload.RemoteEpisode = new RemoteEpisode { ParsedEpisodeInfo = new ParsedEpisodeInfo() };

            Subject.Import(_trackedDownload);

            VerifyNotImported();
            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportBlocked);
            ExceptionVerification.IgnoreWarns();
        }
    }
}
