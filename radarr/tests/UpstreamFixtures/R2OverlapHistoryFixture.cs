using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Translations;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.ReviewFixes
{
    [TestFixture]
    public class R2OverlapHistoryFixture : CoreTest<ReleaseSearchService>
    {
        [Test]
        public async Task import_during_slow_indexer_is_seen_by_fast_indexer_release_like_stock()
        {
            const string t = "Trolls.2016.1080p.WEB-DL.DD5.1.H264-FGT";
            var movie = Builder<Movie>.CreateNew().With(v => v.Monitored = true).With(v => v.MovieFileId = 5).Build();

            var grabbed = new MovieHistory { MovieId = movie.Id, EventType = MovieHistoryEventType.Grabbed, SourceTitle = t, DownloadId = "X", Quality = new QualityModel(Quality.WEBDL1080p) };
            var imported = new MovieHistory { MovieId = movie.Id, EventType = MovieHistoryEventType.DownloadFolderImported, SourceTitle = t, DownloadId = "X", Quality = new QualityModel(Quality.WEBDL720p) };
            var history = new List<MovieHistory> { grabbed };
            var historyService = new Mock<IHistoryService>();
            historyService.Setup(h => h.GetByMovieId(movie.Id, null)).Returns(() => { lock (history) { return history.ToList(); } });
            var config = new Mock<IConfigService>();
            config.SetupGet(c => c.EnableCompletedDownloadHandling).Returns(true);

            var parsing = new Mock<IParsingService>();
            parsing.Setup(c => c.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SearchCriteriaBase>()))
                   .Returns(() => new RemoteMovie { Movie = movie, ParsedMovieInfo = new ParsedMovieInfo() });
            var cf = new Mock<ICustomFormatCalculationService>();
            cf.Setup(c => c.ParseCustomFormat(It.IsAny<RemoteMovie>(), It.IsAny<long>())).Returns(new List<CustomFormat>());
            var maker = new DownloadDecisionMaker(new IDownloadDecisionEngineSpecification[] { new AlreadyImportedSpecification(historyService.Object, config.Object, LogManager.GetLogger("t")) },
                parsing.Object, cf.Object, new Mock<IRemoteMovieAggregationService>().Object, LogManager.GetLogger("t"));
            Mocker.SetConstant<IMakeDownloadDecision>(maker);

            var fast = new Mock<IIndexer>();
            fast.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = 1 });
            var slow = new Mock<IIndexer>();
            slow.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = 2 });
            var slowResponse = new TaskCompletionSource<IList<ReleaseInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
            fast.Setup(v => v.Fetch(It.IsAny<MovieSearchCriteria>()))
                .Returns(Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo> { new ReleaseInfo { Title = t, Guid = "fast", DownloadProtocol = DownloadProtocol.Usenet } }));
            slow.Setup(v => v.Fetch(It.IsAny<MovieSearchCriteria>())).Returns(slowResponse.Task);
            Mocker.GetMock<IIndexerFactory>().Setup(s => s.AutomaticSearchEnabled(true)).Returns(new List<IIndexer> { fast.Object, slow.Object });
            Mocker.GetMock<IMovieTranslationService>().Setup(s => s.GetAllTranslationsForMovieMetadata(It.IsAny<int>())).Returns(new List<MovieTranslation>());

            var search = Subject.MovieSearch(movie, false, false);
            await Task.Delay(500);

            // Completed Download Handling imports the earlier grab while the slow indexer is still answering.
            lock (history)
            {
                history.Insert(0, imported);
            }

            slowResponse.SetResult(new List<ReleaseInfo> { new ReleaseInfo { Title = "Trolls.2016.2160p.WEB-DL.DD5.1.H264-OTHER", Guid = "slow", DownloadProtocol = DownloadProtocol.Usenet } });

            var decisions = await search;
            var fastDecision = decisions.Single(d => d.RemoteMovie.Release.Guid == "fast");
            TestContext.Progress.WriteLine($"R2OVERLAPHIST fast approved={fastDecision.Approved} reasons={string.Join(",", fastDecision.Rejections.Select(r => r.Reason))}");
            fastDecision.Approved.Should().BeFalse("stock decides after the slow indexer and sees the import");
            fastDecision.Rejections.Select(r => r.Reason).Should().Contain(DownloadRejectionReason.AlreadyImportedSameName);
        }
    }
}
