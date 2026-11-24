using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Movies.Translations;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.ReviewFixes
{
    // A write (e.g. CDH import, a grab from RSS) lands after the fast indexer answered but
    // before the slow indexer answered. Stock decides every release after the last response,
    // so every decision sees the write.
    [TestFixture]
    public class R2OverlapFixture : CoreTest<ReleaseSearchService>
    {
        [Test]
        public async Task fast_indexer_releases_are_decided_after_last_response_like_stock()
        {
            var fast = new Mock<IIndexer>();
            fast.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = 1 });
            var slow = new Mock<IIndexer>();
            slow.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = 2 });

            var slowResponse = new TaskCompletionSource<IList<ReleaseInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
            fast.Setup(v => v.Fetch(It.IsAny<MovieSearchCriteria>()))
                .Returns(Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo> { new ReleaseInfo { Title = "fast", Guid = "fast" } }));
            slow.Setup(v => v.Fetch(It.IsAny<MovieSearchCriteria>())).Returns(slowResponse.Task);

            Mocker.GetMock<IIndexerFactory>().Setup(s => s.AutomaticSearchEnabled(true))
                  .Returns(new List<IIndexer> { fast.Object, slow.Object });
            Mocker.GetMock<IMovieTranslationService>().Setup(s => s.GetAllTranslationsForMovieMetadata(It.IsAny<int>()))
                  .Returns(new List<MovieTranslation>());

            var dbState = "before-write";
            var seen = new Dictionary<string, string>();
            Mocker.GetMock<IMakeDownloadDecision>()
                .Setup(s => s.GetSearchDecision(It.IsAny<List<ReleaseInfo>>(), It.IsAny<SearchCriteriaBase>()))
                .Returns<List<ReleaseInfo>, SearchCriteriaBase>((reports, c) =>
                {
                    lock (seen)
                    {
                        foreach (var r in reports)
                        {
                            seen[r.Title] = dbState;
                        }
                    }

                    return reports.Select(r => new DownloadDecision(new RemoteMovie { Release = r })).ToList();
                });

            var movie = Builder<Movie>.CreateNew().With(v => v.Monitored = true).Build();

            var search = Subject.MovieSearch(movie, false, false);
            await Task.Delay(500);

            dbState = "after-write";
            slowResponse.SetResult(new List<ReleaseInfo> { new ReleaseInfo { Title = "slow", Guid = "slow" } });

            var decisions = await search;
            TestContext.Progress.WriteLine($"R2OVERLAP fast={seen.GetValueOrDefault("fast")} slow={seen.GetValueOrDefault("slow")} decisions={decisions.Count}");
            decisions.Count.Should().Be(2);
            seen["slow"].Should().Be("after-write");
            seen["fast"].Should().Be("after-write", "stock decides all releases after the slowest indexer responds");
        }
    }
}
