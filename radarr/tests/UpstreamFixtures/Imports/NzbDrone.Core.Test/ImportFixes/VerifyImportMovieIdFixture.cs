// Regression tests for the VerifyImport NullReferenceException (Radarr PR #11635).
// Movie added/edited/deleted events rebuild cached tracked downloads from the download title alone, so a download
// whose movie came from grab history can lose RemoteMovie.Movie (or RemoteMovie itself, when the title doesn't parse)
// while it is being imported (an upgrade deletes the old file, which updates the movie and publishes MovieEditedEvent).
// On stock VerifyImport, and TrackedDownloadAlreadyImported.IsImported, then threw after the file had been imported
// and DownloadCompletedEvent was never published.
using System;
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
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.ImportFixes
{
    [TestFixture]
    public class VerifyImportMovieIdFixture : CoreTest<CompletedDownloadService>
    {
        private TrackedDownload _trackedDownload;
        private Movie _movie;

        [SetUp]
        public void Setup()
        {
            InitLogging();

            _movie = new Movie { Id = 7 };

            var completed = Builder<DownloadClientItem>.CreateNew()
                                                       .With(h => h.Status = DownloadItemStatus.Completed)
                                                       .With(h => h.OutputPath = new OsPath(@"C:\DropFolder\MyDownload".AsOsAgnostic()))
                                                       .With(h => h.Title = "Drone.1998")
                                                       .Build();

            _trackedDownload = Builder<TrackedDownload>.CreateNew()
                                                       .With(c => c.State = TrackedDownloadState.ImportPending)
                                                       .With(c => c.DownloadItem = completed)
                                                       .With(c => c.ImportItem = completed)
                                                       .With(c => c.RemoteMovie = new RemoteMovie { Movie = _movie })
                                                       .Build();

            Mocker.GetMock<IProvideImportItemService>()
                  .Setup(s => s.ProvideImportItem(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientItem>()))
                  .Returns<DownloadClientItem, DownloadClientItem>((i, p) => i);

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Returns(new List<MovieHistory>());
        }

        private List<ImportResult> ImportedResultFor(Movie movie)
        {
            return new List<ImportResult>
                   {
                       new ImportResult(new ImportDecision(new LocalMovie { Path = @"C:\TestPath\Droned.1998.mkv".AsOsAgnostic(), Movie = movie }))
                   };
        }

        private void GivenProcessPathUnlinksTheMovie(Action unlink, List<ImportResult> results)
        {
            // What TrackedDownloadService.UpdateCachedItem does when MovieEditedEvent fires during ProcessPath and
            // the download title alone does not map to a movie.
            Mocker.GetMock<IDownloadedMovieImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Movie>(), It.IsAny<DownloadClientItem>()))
                  .Callback(unlink)
                  .Returns(results);
        }

        private void VerifyCompletedEventFor(int movieId)
        {
            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.Is<DownloadCompletedEvent>(c => c.MovieId == movieId)), Times.Once());

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Once());
        }

        [Test]
        public void should_use_movie_from_import_results_when_tracked_download_is_no_longer_linked_to_a_movie()
        {
            _trackedDownload.RemoteMovie.Movie = null;

            Subject.VerifyImport(_trackedDownload, ImportedResultFor(_movie)).Should().BeTrue();

            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);
            VerifyCompletedEventFor(_movie.Id);
        }

        [Test]
        public void should_mark_as_imported_when_tracked_download_loses_its_movie_during_the_import()
        {
            GivenProcessPathUnlinksTheMovie(() => _trackedDownload.RemoteMovie = new RemoteMovie { Movie = null }, ImportedResultFor(_movie));

            Subject.Import(_trackedDownload);

            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);
            VerifyCompletedEventFor(_movie.Id);
        }

        [Test]
        public void should_mark_as_imported_when_tracked_download_loses_its_remote_movie_during_the_import()
        {
            // UpdateCachedItem sets RemoteMovie itself to null when the download title doesn't parse.
            GivenProcessPathUnlinksTheMovie(() => _trackedDownload.RemoteMovie = null, ImportedResultFor(_movie));

            Subject.Import(_trackedDownload);

            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);
            VerifyCompletedEventFor(_movie.Id);
        }

        [Test]
        public void should_mark_as_imported_from_history_when_nothing_was_imported_and_tracked_download_lost_its_movie()
        {
            // Real TrackedDownloadAlreadyImported, as in production: the file was imported earlier (history says so), this
            // pass imported nothing, and the cached item lost its movie during ProcessPath.
            Mocker.SetConstant<ITrackedDownloadAlreadyImported>(Mocker.Resolve<TrackedDownloadAlreadyImported>());

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.IsAny<string>()))
                  .Returns(new List<MovieHistory>
                           {
                               new MovieHistory { MovieId = _movie.Id, EventType = MovieHistoryEventType.Grabbed, Date = DateTime.UtcNow.AddHours(-2) },
                               new MovieHistory { MovieId = _movie.Id, EventType = MovieHistoryEventType.DownloadFolderImported, Date = DateTime.UtcNow.AddHours(-1) }
                           });

            GivenProcessPathUnlinksTheMovie(() => _trackedDownload.RemoteMovie = new RemoteMovie { Movie = null }, new List<ImportResult>());

            Subject.Import(_trackedDownload);

            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);
            VerifyCompletedEventFor(_movie.Id);
        }

        [Test]
        public void should_not_mark_as_imported_with_movie_id_0_when_no_movie_can_be_found()
        {
            // The movie is unlinked by another thread between IsImported and reading the movie id, and neither the import
            // results nor the history give a movie: don't publish DownloadCompletedEvent with MovieId 0 (it would add a
            // DownloadImported history row for no movie and could remove the download from the client).
            Mocker.GetMock<ITrackedDownloadAlreadyImported>()
                  .Setup(s => s.IsImported(_trackedDownload, It.IsAny<List<MovieHistory>>()))
                  .Callback(() => _trackedDownload.RemoteMovie = new RemoteMovie { Movie = null })
                  .Returns(true);

            Subject.VerifyImport(_trackedDownload, new List<ImportResult>()).Should().BeFalse();

            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportPending);

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<DownloadCompletedEvent>()), Times.Never());

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_use_tracked_download_movie_when_it_is_still_linked()
        {
            // Same id as stock: the tracked download's movie wins over the import results.
            _trackedDownload.RemoteMovie.Movie = new Movie { Id = 3 };

            Subject.VerifyImport(_trackedDownload, ImportedResultFor(_movie)).Should().BeTrue();

            VerifyCompletedEventFor(3);
        }

        [Test]
        public void should_use_tracked_download_movie_even_when_its_id_is_0()
        {
            // Same as stock: a linked movie is used as it is, the "no movie found" check only applies to the fallbacks.
            _trackedDownload.RemoteMovie.Movie = new Movie();

            Subject.VerifyImport(_trackedDownload, ImportedResultFor(_trackedDownload.RemoteMovie.Movie)).Should().BeTrue();

            _trackedDownload.State.Should().Be(TrackedDownloadState.Imported);
            VerifyCompletedEventFor(0);
        }
    }

    [TestFixture]
    public class AlreadyImportedWithoutMovieFixture : CoreTest<TrackedDownloadAlreadyImported>
    {
        private TrackedDownload _trackedDownload;

        [SetUp]
        public void Setup()
        {
            _trackedDownload = Builder<TrackedDownload>.CreateNew()
                                                       .With(c => c.DownloadItem = Builder<DownloadClientItem>.CreateNew().Build())
                                                       .With(c => c.RemoteMovie = new RemoteMovie { Movie = null })
                                                       .Build();
        }

        // VerifyImport passes the download's history newest first.
        private static List<MovieHistory> History(params (int movieId, MovieHistoryEventType eventType)[] newestFirst)
        {
            var history = new List<MovieHistory>();
            var date = DateTime.UtcNow;

            foreach (var item in newestFirst)
            {
                history.Add(new MovieHistory { MovieId = item.movieId, EventType = item.eventType, Date = date });
                date = date.AddMinutes(-1);
            }

            return history;
        }

        [Test]
        public void should_be_imported_when_the_latest_history_of_the_download_is_an_import()
        {
            Subject.IsImported(_trackedDownload, History((7, MovieHistoryEventType.DownloadFolderImported), (7, MovieHistoryEventType.Grabbed)))
                   .Should().BeTrue();
        }

        [Test]
        public void should_be_imported_when_remote_movie_is_null_and_the_latest_history_of_the_download_is_an_import()
        {
            _trackedDownload.RemoteMovie = null;

            Subject.IsImported(_trackedDownload, History((7, MovieHistoryEventType.DownloadFolderImported), (7, MovieHistoryEventType.Grabbed)))
                   .Should().BeTrue();
        }

        [Test]
        public void should_not_be_imported_when_the_latest_history_of_the_download_is_not_an_import()
        {
            Subject.IsImported(_trackedDownload, History((7, MovieHistoryEventType.Grabbed)))
                   .Should().BeFalse();
        }

        [Test]
        public void should_only_use_the_movies_history_when_the_movie_is_linked()
        {
            // Same as stock: with a linked movie only that movie's history counts.
            _trackedDownload.RemoteMovie = new RemoteMovie { Movie = new Movie { Id = 7 } };

            Subject.IsImported(_trackedDownload, History((9, MovieHistoryEventType.DownloadFolderImported), (7, MovieHistoryEventType.Grabbed)))
                   .Should().BeFalse();
        }
    }
}
