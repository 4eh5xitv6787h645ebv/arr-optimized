// Regression tests for the review findings on patch 0003 (DecisionScope batch memo). They run inside
// upstream's NzbDrone.Core.Test project: copy this directory to src/NzbDrone.Core.Test/SonarrNext/.
//
// 1. Deleted indexer: AggregateLanguages uses IIndexerFactory.Find (null for a missing indexer) while
//    IndexerTagSpecification and TorrentSeedingSpecification use Get (throws ModelNotFoundException).
//    All three share the memo key, so a memoized null used to reach the specifications and turn into
//    a NullReferenceException / DecisionError rejection. Stock accepts and logs "does not exist, skipping".
// 2. Rows written while a batch is running (Completed/Failed Download Handling on another thread):
//    stock sees the write from the next release on, so the memo must too.
//
// Each test fails on the original 0003/0004 and passes with the fix.
using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NLog;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.DecisionEngine.Specifications.RssSync;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Download.Aggregation.Aggregators;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.SonarrNext
{
    [TestFixture]
    public class DeletedIndexerMemoFixture : CoreTest<DownloadDecisionMaker>
    {
        private static RemoteEpisode MakeRemote(int indexerId)
        {
            var release = new TorrentInfo { Seeders = 50, IndexerId = indexerId, Indexer = null, Title = "Series.Title.S01E01.1080p.WEB-DL-Grp", DownloadProtocol = DownloadProtocol.Torrent };
            return new RemoteEpisode
            {
                Series = Builder<Series>.CreateNew().With(s => s.OriginalLanguage = Language.English).With(s => s.Tags = new HashSet<int> { 1 }).Build(),
                Release = release,
                Episodes = Builder<Episode>.CreateListOfSize(1).BuildList(),
                ParsedEpisodeInfo = new ParsedEpisodeInfo { ReleaseTitle = release.Title, Languages = new List<Language> { Language.English }, ReleaseTokens = "" }
            };
        }

        private static List<string> Evaluate(IIndexerFactory factory, bool inScope, out bool tagAccepted, out bool seedAccepted)
        {
            var target = new MemoryTarget("sonarrnext") { Layout = "${level}|${logger}|${message}" };
            var config = LogManager.Configuration;
            config.AddRuleForAllLevels(target, "sonarrnext.*");
            LogManager.Configuration = config;

            var aggregate = new AggregateLanguages(factory, LogManager.GetLogger("sonarrnext.aggregate"));
            var tag = new IndexerTagSpecification(LogManager.GetLogger("sonarrnext.tag"), factory);
            var seed = new TorrentSeedingSpecification(factory, LogManager.GetLogger("sonarrnext.seed"));
            var info = new ReleaseDecisionInformation(false, null);
            var scope = inScope ? DecisionScope.Begin() : null;
            try
            {
                // Same order as a batch: aggregation first, then the specifications, twice (two releases).
                tagAccepted = seedAccepted = true;
                for (var i = 0; i < 2; i++)
                {
                    var remote = MakeRemote(7);
                    aggregate.Aggregate(remote);
                    tagAccepted &= tag.IsSatisfiedBy(remote, info).Accepted;
                    seedAccepted &= seed.IsSatisfiedBy(remote, info).Accepted;
                }
            }
            finally
            {
                scope?.Dispose();
                config = LogManager.Configuration;
                config.RemoveTarget(target.Name);
                LogManager.Configuration = config;
            }

            return target.Logs.ToList();
        }

        // Adapted from review-scratch CfCacheReviewFixture.deleted_indexer_memo_null_breaks_tag_and_seeding_specs:
        // the batch must behave and log exactly like the unscoped (stock) code path.
        [Test]
        public void deleted_indexer_in_batch_is_accepted_and_logged_like_stock()
        {
            var factory = new Mock<IIndexerFactory>();
            factory.Setup(v => v.Find(7)).Returns((IndexerDefinition)null);
            factory.Setup(v => v.Get(7)).Throws(new ModelNotFoundException(typeof(IndexerDefinition), 7));

            var stockLogs = Evaluate(factory.Object, false, out var stockTag, out var stockSeed);
            var batchLogs = Evaluate(factory.Object, true, out var batchTag, out var batchSeed);

            stockTag.Should().BeTrue();
            stockSeed.Should().BeTrue();
            batchTag.Should().BeTrue();
            batchSeed.Should().BeTrue();
            stockLogs.Should().Contain("Debug|sonarrnext.tag|Indexer with id 7 does not exist, skipping indexer tags check");
            stockLogs.Should().Contain("Debug|sonarrnext.seed|Indexer with id 7 does not exist, skipping seeders check");
            batchLogs.Should().Equal(stockLogs);
        }

        [Test]
        public void existing_indexer_is_read_once_per_batch_by_find_and_get()
        {
            var definition = new IndexerDefinition { Id = 7, Tags = new HashSet<int>(), Settings = new NewznabSettings() };
            var factory = new Mock<IIndexerFactory>();
            factory.Setup(v => v.Find(7)).Returns(definition);
            factory.Setup(v => v.Get(7)).Returns(definition);

            Evaluate(factory.Object, true, out var tag, out var seed);

            tag.Should().BeTrue();
            seed.Should().BeTrue();
            (factory.Invocations.Count(i => i.Method.Name == "Find") + factory.Invocations.Count(i => i.Method.Name == "Get")).Should().Be(1);
        }

        // From review-scratch ReviewDeletedIndexerFixture: the full decision path.
        [Test]
        public void release_from_indexer_deleted_before_decision_is_accepted_like_stock()
        {
            var logger = LogManager.GetLogger("review");
            var indexerFactory = new Mock<IIndexerFactory>();
            indexerFactory.Setup(f => f.Find(5)).Returns((IndexerDefinition)null);
            indexerFactory.Setup(f => f.Get(5)).Throws(new ModelNotFoundException(typeof(IndexerDefinition), 5));

            Mocker.SetConstant<IRemoteEpisodeAggregationService>(
                new RemoteEpisodeAggregationService(new IAggregateRemoteEpisode[] { new AggregateLanguages(indexerFactory.Object, logger) }, logger));

            Mocker.SetConstant<IEnumerable<IDownloadDecisionEngineSpecification>>(new IDownloadDecisionEngineSpecification[]
            {
                new IndexerTagSpecification(logger, indexerFactory.Object),
                new TorrentSeedingSpecification(indexerFactory.Object, logger)
            });

            Mocker.GetMock<IParsingService>()
                  .Setup(c => c.Map(It.IsAny<ParsedEpisodeInfo>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<SearchCriteriaBase>()))
                  .Returns<ParsedEpisodeInfo, int, int, string, SearchCriteriaBase>((p, a, b, c, d) => new RemoteEpisode
                  {
                      ParsedEpisodeInfo = p,
                      Series = new Series { Title = "Series Title" },
                      Episodes = new List<Episode> { new Episode { Title = "Pilot" } }
                  });

            var reports = new List<ReleaseInfo>
            {
                new TorrentInfo { Title = "Series.Title.S01E01.720p.HDTV.x264-GRP", IndexerId = 5, Indexer = "Gone", Seeders = 1, DownloadProtocol = DownloadProtocol.Torrent },
                new TorrentInfo { Title = "Series.Title.S01E01.1080p.HDTV.x264-GRP", IndexerId = 5, Indexer = "Gone", Seeders = 1, DownloadProtocol = DownloadProtocol.Torrent }
            };

            var decisions = Subject.GetRssDecision(reports);

            decisions.Should().HaveCount(2);
            decisions.SelectMany(d => d.Rejections).Select(r => r.Message).Should().BeEmpty();
            decisions.Should().OnlyContain(d => d.Approved);
        }

        [Test]
        public void memo_never_stores_null()
        {
            var calls = 0;
            using (DecisionScope.Begin())
            {
                DecisionScope.Memo<string>("Indexers", 1, () => { calls++; return null; }).Should().BeNull();
                DecisionScope.Memo("Indexers", 1, () => { calls++; return "x"; }).Should().Be("x");
                DecisionScope.Memo("Indexers", 1, () => { calls++; return "y"; }).Should().Be("x");
            }

            calls.Should().Be(2);
        }
    }

    // A specification that performs a write the first time it runs, standing in for Completed or Failed
    // Download Handling committing a row on another thread between two releases of one batch.
    internal sealed class WriteOnceSpecification : IDownloadDecisionEngineSpecification
    {
        private Action _write;

        public WriteOnceSpecification(SpecificationPriority priority, Action write)
        {
            Priority = priority;
            _write = write;
        }

        public RejectionType Type => RejectionType.Permanent;
        public SpecificationPriority Priority { get; }

        public DownloadSpecDecision IsSatisfiedBy(RemoteEpisode subject, ReleaseDecisionInformation information)
        {
            var write = _write;
            _write = null;
            write?.Invoke();
            return DownloadSpecDecision.Accept();
        }
    }

    [TestFixture]
    public class MidBatchWriteFixture : DbTest
    {
        private const string GrabbedTitle = "Series.Title.S01E01.1080p.WEB-DL.DD5.1.H264-GRP";
        private const string OtherTitle = "Series.Title.S01E01.2160p.WEB-DL.DD5.1.H264-OTHER";
        private Series _series;

        [SetUp]
        public void Setup()
        {
            _series = Builder<Series>.CreateNew().With(s => s.Id = 0).With(s => s.Tags = new HashSet<int>()).Build();
            _series.Id = Db.Insert(_series).Id;
            Mocker.GetMock<IConfigService>().SetupGet(c => c.EnableCompletedDownloadHandling).Returns(true);
        }

        private List<DownloadDecision> Decide(IEnumerable<IDownloadDecisionEngineSpecification> specs, Func<string, List<Episode>> episodesForTitle, params ReleaseInfo[] reports)
        {
            Mocker.SetConstant(specs);
            Mocker.GetMock<IParsingService>()
                  .Setup(c => c.Map(It.IsAny<ParsedEpisodeInfo>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<SearchCriteriaBase>()))
                  .Returns<ParsedEpisodeInfo, int, int, string, SearchCriteriaBase>((p, a, b, c, d) => new RemoteEpisode
                  {
                      ParsedEpisodeInfo = p,
                      Series = _series,
                      Episodes = episodesForTitle(p.ReleaseTitle)
                  });

            return Mocker.Resolve<DownloadDecisionMaker>().GetRssDecision(reports.ToList());
        }

        private EpisodeHistory History(int episodeId, EpisodeHistoryEventType type, Quality quality, DateTime date)
        {
            return Builder<EpisodeHistory>.CreateNew()
                .With(h => h.Id = 0)
                .With(h => h.SeriesId = _series.Id)
                .With(h => h.EpisodeId = episodeId)
                .With(h => h.EventType = type)
                .With(h => h.SourceTitle = GrabbedTitle)
                .With(h => h.DownloadId = "HASH")
                .With(h => h.Date = date)
                .With(h => h.Languages = new List<Language> { Language.English })
                .With(h => h.Quality = new QualityModel(quality))
                .BuildNew();
        }

        // The Radarr review finding, checked for Sonarr: an upgrade was grabbed (history: Grabbed), and
        // Completed Download Handling imports it with a different quality while an RSS batch runs. Stock
        // rejects a later same-title release as AlreadyImportedSameName.
        [Test]
        public void history_written_mid_batch_is_seen_by_later_releases()
        {
            var episode = Builder<Episode>.CreateNew().With(e => e.Id = 0).With(e => e.SeriesId = _series.Id).With(e => e.EpisodeFileId = 5).Build();
            episode.Id = Db.Insert(episode).Id;

            var historyRepository = Mocker.Resolve<HistoryRepository>();
            historyRepository.Insert(History(episode.Id, EpisodeHistoryEventType.Grabbed, Quality.WEBDL1080p, DateTime.UtcNow.AddHours(-1)));

            var specs = new IDownloadDecisionEngineSpecification[]
            {
                new AlreadyImportedSpecification(new HistoryService(historyRepository, TestLogger), Mocker.GetMock<IConfigService>().Object, TestLogger),
                new WriteOnceSpecification(SpecificationPriority.Database, () => historyRepository.Insert(History(episode.Id, EpisodeHistoryEventType.DownloadFolderImported, Quality.WEBDL720p, DateTime.UtcNow)))
            };

            var decisions = Decide(specs, _ => new List<Episode> { episode },
                new ReleaseInfo { Title = OtherTitle, DownloadProtocol = DownloadProtocol.Usenet },
                new ReleaseInfo { Title = GrabbedTitle, DownloadProtocol = DownloadProtocol.Usenet });

            decisions[0].Approved.Should().BeTrue();
            decisions[1].Rejections.Select(r => r.Reason).Should().Equal(DownloadRejectionReason.AlreadyImportedSameName);
        }

        // Failed Download Handling blocklists a release while a batch runs.
        [Test]
        public void blocklist_row_written_mid_batch_is_seen_by_later_releases()
        {
            var episode = Builder<Episode>.CreateNew().With(e => e.Id = 0).With(e => e.SeriesId = _series.Id).Build();
            episode.Id = Db.Insert(episode).Id;
            var published = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

            var blocklistRepository = Mocker.Resolve<BlocklistRepository>();
            var specs = new IDownloadDecisionEngineSpecification[]
            {
                new BlocklistSpecification(new BlocklistService(blocklistRepository), TestLogger),
                new WriteOnceSpecification(SpecificationPriority.Database, () => blocklistRepository.Insert(new Blocklist
                {
                    SeriesId = _series.Id,
                    EpisodeIds = new List<int> { episode.Id },
                    Quality = new QualityModel(Quality.WEBDL1080p),
                    Languages = new List<Language> { Language.English },
                    SourceTitle = GrabbedTitle,
                    Date = DateTime.UtcNow,
                    PublishedDate = published,
                    Indexer = "Idx",
                    Protocol = DownloadProtocol.Usenet
                }))
            };

            var decisions = Decide(specs, _ => new List<Episode> { episode },
                new ReleaseInfo { Title = OtherTitle, Indexer = "Idx", PublishDate = published, DownloadProtocol = DownloadProtocol.Usenet },
                new ReleaseInfo { Title = GrabbedTitle, Indexer = "Idx", PublishDate = published, DownloadProtocol = DownloadProtocol.Usenet });

            decisions[0].Approved.Should().BeTrue();
            decisions[1].Rejections.Select(r => r.Reason).Should().Equal(DownloadRejectionReason.Blocklisted);
        }

        // An import moves episode 1 out of a two-episode file while a batch runs. Stock then sees that
        // the old file holds only episode 2, so a release for episode 2 is not rejected as
        // ExistingFileHasMoreEpisodes.
        [Test]
        public void episode_file_change_mid_batch_is_seen_by_later_releases()
        {
            var episodes = Builder<Episode>.CreateListOfSize(2).All().With(e => e.Id = 0).With(e => e.SeriesId = _series.Id).With(e => e.EpisodeFileId = 5).BuildList();
            var e1 = Db.Insert(episodes[0]);
            var e2 = Db.Insert(episodes[1]);

            var episodeRepository = Mocker.Resolve<EpisodeRepository>();
            Mocker.SetConstant<IEpisodeRepository>(episodeRepository);
            var specs = new IDownloadDecisionEngineSpecification[]
            {
                new SameEpisodesGrabSpecification(new SameEpisodesSpecification(Mocker.Resolve<EpisodeService>()), TestLogger),
                new WriteOnceSpecification(SpecificationPriority.Default, () => episodeRepository.SetFileId(episodeRepository.Get(e1.Id), 6))
            };

            var decisions = Decide(specs, title => title.Contains("E01E02") ? new List<Episode> { e1, e2 } : new List<Episode> { episodeRepository.Get(e2.Id) },
                new ReleaseInfo { Title = "Series.Title.S01E01E02.1080p.WEB-DL.DD5.1.H264-GRP", DownloadProtocol = DownloadProtocol.Usenet },
                new ReleaseInfo { Title = "Series.Title.S01E02.1080p.WEB-DL.DD5.1.H264-GRP", DownloadProtocol = DownloadProtocol.Usenet });

            decisions[0].Approved.Should().BeTrue();
            decisions[1].Approved.Should().BeTrue();
        }

        [Test]
        public void unchanged_rows_are_read_once_per_batch()
        {
            var historyRepository = Mocker.Resolve<HistoryRepository>();
            historyRepository.Insert(History(1, EpisodeHistoryEventType.Grabbed, Quality.WEBDL1080p, DateTime.UtcNow));
            var calls = 0;

            using (DecisionScope.Begin())
            {
                var first = DecisionScope.Memo("History", 1, () => { calls++; return historyRepository.FindByEpisodeId(1); });
                DecisionScope.Memo("History", 1, () => { calls++; return historyRepository.FindByEpisodeId(1); }).Should().BeSameAs(first);

                // A write to another table leaves the memo alone; a write to History invalidates it.
                Db.Insert(Builder<Episode>.CreateNew().With(e => e.Id = 0).Build());
                DecisionScope.Memo("History", 1, () => { calls++; return historyRepository.FindByEpisodeId(1); }).Should().BeSameAs(first);
                historyRepository.Insert(History(1, EpisodeHistoryEventType.DownloadFolderImported, Quality.WEBDL1080p, DateTime.UtcNow));
                DecisionScope.Memo("History", 1, () => { calls++; return historyRepository.FindByEpisodeId(1); }).Should().HaveCount(2);
            }

            calls.Should().Be(2);
        }
    }
}
