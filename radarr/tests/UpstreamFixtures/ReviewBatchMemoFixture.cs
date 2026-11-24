using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.DecisionEngine.Specifications.RssSync;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.Test.ReviewFixes
{
    // Review finding 3: data written by another thread in the middle of a decision batch
    // (Completed Download Handling history rows, indexer edits/deletes) must be seen by the
    // later releases of that batch exactly as stock sees it.
    [TestFixture]
    public class ReviewBatchMemoFixture : CoreTest
    {
        private static DownloadDecisionMaker Maker(IDownloadDecisionEngineSpecification spec, Func<RemoteMovie> remote)
        {
            var parsing = new Mock<IParsingService>();
            parsing.Setup(c => c.Map(It.IsAny<ParsedMovieInfo>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SearchCriteriaBase>()))
                   .Returns(remote);
            var cf = new Mock<ICustomFormatCalculationService>();
            cf.Setup(c => c.ParseCustomFormat(It.IsAny<RemoteMovie>(), It.IsAny<long>())).Returns(new List<CustomFormat>());

            return new DownloadDecisionMaker(new[] { spec }, parsing.Object, cf.Object, new Mock<IRemoteMovieAggregationService>().Object, LogManager.GetLogger("t"));
        }

        // What ProviderFactory does after every indexer write: publish the provider event to all
        // IHandle<> implementations. Resolved by name so this also compiles against older builds,
        // where no handler exists and the write is simply not observed.
        private static void PublishIndexerEvent(object message)
        {
            var handler = typeof(DownloadDecisionMaker).Assembly.GetType("NzbDrone.Core.DecisionEngine.DecisionBatchCacheInvalidator", false);
            handler?.GetMethod("Handle", new[] { message.GetType() })?.Invoke(Activator.CreateInstance(handler), new[] { message });
        }

        [Test]
        public void history_written_mid_batch_is_seen_by_later_release_like_stock()
        {
            const string t = "Trolls.2016.1080p.WEB-DL.DD5.1.H264-FGT";
            var grabbed = new MovieHistory { MovieId = 1, EventType = MovieHistoryEventType.Grabbed, SourceTitle = t, DownloadId = "X", Quality = new QualityModel(Quality.WEBDL1080p) };
            var imported = new MovieHistory { MovieId = 1, EventType = MovieHistoryEventType.DownloadFolderImported, SourceTitle = t, DownloadId = "X", Quality = new QualityModel(Quality.WEBDL720p) };

            var history = new Mock<IHistoryService>();
            var calls = 0;

            // CDH writes the DownloadFolderImported row on another thread after release 1 is decided.
            history.Setup(h => h.GetByMovieId(1, null)).Returns(() =>
            {
                calls++;
                return calls == 1 ? new List<MovieHistory> { grabbed } : new List<MovieHistory> { imported, grabbed };
            });
            var config = new Mock<IConfigService>();
            config.SetupGet(c => c.EnableCompletedDownloadHandling).Returns(true);
            var spec = new AlreadyImportedSpecification(history.Object, config.Object, LogManager.GetLogger("t"));

            var maker = Maker(spec, () => new RemoteMovie { Movie = new Movie { Id = 1, MovieFileId = 5 }, ParsedMovieInfo = new ParsedMovieInfo() });
            var decisions = maker.GetRssDecision(new List<ReleaseInfo>
            {
                new ReleaseInfo { Title = "Trolls.2016.2160p.WEB-DL.DD5.1.H264-OTHER" },
                new ReleaseInfo { Title = t }
            }).ToList();

            decisions[0].Approved.Should().BeTrue();
            decisions[1].Approved.Should().BeFalse("stock re-reads history and sees the new import");
            decisions[1].Rejections.Select(r => r.Reason).Should().Contain(DownloadRejectionReason.AlreadyImportedSameName);
        }

        [Test]
        public void indexer_tags_edited_mid_batch_are_seen_by_later_release_like_stock()
        {
            var definition = new IndexerDefinition { Id = 3, Tags = new HashSet<int> { 9 } };
            var factory = new Mock<IIndexerFactory>();
            var reads = 0;
            factory.Setup(f => f.Get(3)).Returns(() =>
            {
                reads++;
                var current = new IndexerDefinition { Id = 3, Tags = new HashSet<int>(definition.Tags) };
                if (reads == 1)
                {
                    // The user removes the tag while release 1 is being decided.
                    definition.Tags = new HashSet<int>();
                    PublishIndexerEvent(new ProviderUpdatedEvent<IIndexer>(definition));
                }

                return current;
            });

            var spec = new IndexerTagSpecification(LogManager.GetLogger("t"), factory.Object);
            var maker = Maker(spec, () => new RemoteMovie { Movie = new Movie { Id = 1, Tags = new HashSet<int>() }, ParsedMovieInfo = new ParsedMovieInfo() });
            var decisions = maker.GetRssDecision(new List<ReleaseInfo>
            {
                new ReleaseInfo { Title = "Trolls.2016.1080p.WEB-DL.DD5.1.H264-A", IndexerId = 3 },
                new ReleaseInfo { Title = "Trolls.2016.1080p.WEB-DL.DD5.1.H264-B", IndexerId = 3 }
            }).ToList();

            decisions[0].Approved.Should().BeFalse("the first release saw the tagged indexer");
            decisions[1].Approved.Should().BeTrue("stock re-reads the indexer and sees the tag removed");
        }

        [Test]
        public void indexer_deleted_mid_batch_is_skipped_like_stock()
        {
            var factory = new Mock<IIndexerFactory>();
            var reads = 0;
            factory.Setup(f => f.Get(3)).Returns(() =>
            {
                reads++;
                if (reads > 1)
                {
                    throw new ModelNotFoundException(typeof(IndexerDefinition), 3);
                }

                PublishIndexerEvent(new ProviderDeletedEvent<IIndexer>(3));
                return new IndexerDefinition { Id = 3, Tags = new HashSet<int> { 9 } };
            });

            var spec = new IndexerTagSpecification(LogManager.GetLogger("t"), factory.Object);
            var maker = Maker(spec, () => new RemoteMovie { Movie = new Movie { Id = 1, Tags = new HashSet<int>() }, ParsedMovieInfo = new ParsedMovieInfo() });
            var decisions = maker.GetRssDecision(new List<ReleaseInfo>
            {
                new ReleaseInfo { Title = "Trolls.2016.1080p.WEB-DL.DD5.1.H264-A", IndexerId = 3 },
                new ReleaseInfo { Title = "Trolls.2016.1080p.WEB-DL.DD5.1.H264-B", IndexerId = 3 }
            }).ToList();

            decisions[0].Approved.Should().BeFalse();
            decisions[1].Approved.Should().BeTrue("stock catches ModelNotFoundException and skips the tag check");
        }

        [Test]
        public void deleted_indexer_seen_by_find_does_not_make_get_based_specs_throw()
        {
            // AggregateLanguages uses Find (null when missing); the specifications use Get. A
            // shared memo key must never hand Get callers a cached null.
            var factory = new Mock<IIndexerFactory>();
            factory.Setup(f => f.Find(4)).Returns((IndexerDefinition)null);
            factory.Setup(f => f.Get(4)).Throws(new ModelNotFoundException(typeof(IndexerDefinition), 4));

            var tag = new IndexerTagSpecification(LogManager.GetLogger("t"), factory.Object);
            var flags = new RequiredIndexerFlagsSpecification(factory.Object, LogManager.GetLogger("t"));
            var remote = new RemoteMovie { Movie = new Movie { Id = 1, Tags = new HashSet<int>() }, Release = new ReleaseInfo { IndexerId = 4 } };

            using (DecisionBatchCacheScope())
            {
                FindThroughMemo(factory.Object, 4).Should().BeNull();
                tag.IsSatisfiedBy(remote, new ReleaseDecisionInformation()).Accepted.Should().BeTrue();
                flags.IsSatisfiedBy(remote, new ReleaseDecisionInformation()).Accepted.Should().BeTrue();
            }
        }

        // Same path as AggregateLanguages: the memoized Find.
        private static IndexerDefinition FindThroughMemo(IIndexerFactory factory, int id)
        {
            var cache = typeof(DownloadDecisionMaker).Assembly.GetType("NzbDrone.Core.DecisionEngine.DecisionBatchCache", false);
            var indexer = cache?.GetMethod("Indexer", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            return indexer == null ? factory.Find(id) : (IndexerDefinition)indexer.Invoke(null, new object[] { id, new Func<int, IndexerDefinition>(factory.Find) });
        }

        private static IDisposable DecisionBatchCacheScope()
        {
            var cache = typeof(DownloadDecisionMaker).Assembly.GetType("NzbDrone.Core.DecisionEngine.DecisionBatchCache", false);
            return (IDisposable)cache?.GetMethod("Begin", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.Invoke(null, null) ?? new System.IO.MemoryStream();
        }
    }
}
