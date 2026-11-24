// Regression test for the re-review finding on patch 0004 (in-memory SQLite LIKE for blocklist titles).
// SQLite's patternCompare reads characters with sqlite3Utf8Read, which maps U+FFFE and U+FFFF to U+FFFD,
// so a stored blocklist title and a release title that differ only by these characters match on stock.
// The original emulation compared them by code point and accepted the release.
//
// Each title pair is decided through the real BlocklistRepository and test database twice: outside a
// decision batch (the stock SQL query) and inside one (the in-memory path). Both must agree, and the
// U+FFFD family must be rejected as Blocklisted through DownloadDecisionMaker.
using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Indexers;
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
    public class BlocklistLikeReviewFixture : DbTest
    {
        private static readonly DateTime Published = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        private Series _series;
        private BlocklistRepository _repository;

        [SetUp]
        public void Setup()
        {
            _series = Builder<Series>.CreateNew().With(s => s.Id = 0).With(s => s.Tags = new HashSet<int>()).Build();
            _series.Id = Db.Insert(_series).Id;
            _repository = Mocker.Resolve<BlocklistRepository>();
        }

        private void Block(string sourceTitle)
        {
            _repository.Insert(new Blocklist
            {
                SeriesId = _series.Id,
                EpisodeIds = new List<int> { 1 },
                Quality = new QualityModel(Quality.WEBDL1080p),
                Languages = new List<Language> { Language.English },
                SourceTitle = sourceTitle,
                Date = DateTime.UtcNow,
                PublishedDate = Published,
                Indexer = "Idx",
                Protocol = DownloadProtocol.Usenet
            });
        }

        private static ReleaseInfo Release(string title)
        {
            return new ReleaseInfo { Title = title, Indexer = "Idx", PublishDate = Published, DownloadProtocol = DownloadProtocol.Usenet };
        }

        // (stored blocklist title, new release title, blocklisted on stock). The tests take an index: NUnit
        // silently drops test cases whose generated names contain U+FFFE/U+FFFF.
        private static readonly (string Stored, string Title, bool Expected)[] Pairs =
        {
            ("Series.Title.S01E01.\uFFFE.1080p-GRP", "Series.Title.S01E01.\uFFFD.1080p-GRP", true),
            ("Series.Title.S01E01.\uFFFF.1080p-GRP", "Series.Title.S01E01.\uFFFE.1080p-GRP", true),
            ("Series.Title.S01E01.\uFFFD.1080p-GRP", "series.title.s01e01.\uFFFF.1080P-grp", true),
            ("Series.Title.S01E01.\uFFFC.1080p-GRP", "Series.Title.S01E01.\uFFFD.1080p-GRP", false),
            ("Series.Title.S01E01.1080p-GRP", "series.title.s01e01.1080p-grp", true),
            ("S\u00E9rie.Title.S01E01.1080p-GRP", "S\u00C9RIE.Title.S01E01.1080p-GRP", false),
            ("Series.Title.S01E01.1080p-GRP", "Series_Title_S01E01_1080p-GRP", true),
            ("Series.Title.S01E01.1080p-GRP", "Series%S01E01%-GRP", true),
            ("Series.Title.S01E01.e\u0301.1080p-GRP", "Series.Title.S01E01.\u00E9.1080p-GRP", false),
            ("Series.Title.S01E01.\u212A.1080p-GRP", "Series.Title.S01E01.k.1080p-GRP", false),
        };

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        [TestCase(7)]
        [TestCase(8)]
        [TestCase(9)]
        public void in_batch_blocklist_match_equals_stock_sql(int pair)
        {
            var (stored, title, expected) = Pairs[pair];
            Block(stored);
            var service = new BlocklistService(_repository);

            var stock = service.Blocklisted(_series.Id, Release(title));
            bool batched;
            using (DecisionScope.Begin())
            {
                batched = service.Blocklisted(_series.Id, Release(title));
            }

            stock.Should().Be(expected);
            batched.Should().Be(stock);
        }

        [Test]
        public void replacement_character_variants_are_rejected_as_blocklisted_in_a_batch()
        {
            Block("Series.Title.S01E01.1080p.WEB-DL.￾.H264-GRP");
            Mocker.SetConstant<IEnumerable<IDownloadDecisionEngineSpecification>>(new IDownloadDecisionEngineSpecification[]
            {
                new BlocklistSpecification(new BlocklistService(_repository), TestLogger)
            });
            Mocker.GetMock<IParsingService>()
                  .Setup(c => c.Map(It.IsAny<ParsedEpisodeInfo>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<SearchCriteriaBase>()))
                  .Returns<ParsedEpisodeInfo, int, int, string, SearchCriteriaBase>((p, a, b, c, d) => new RemoteEpisode
                  {
                      ParsedEpisodeInfo = p,
                      Series = _series,
                      Episodes = new List<Episode> { new Episode { Id = 1, SeriesId = _series.Id } }
                  });

            var decisions = Mocker.Resolve<DownloadDecisionMaker>().GetRssDecision(new List<ReleaseInfo>
            {
                Release("Series.Title.S01E01.1080p.WEB-DL.�.H264-GRP"),
                Release("Series.Title.S01E01.1080p.WEB-DL.￿.H264-GRP"),
                Release("Series.Title.S01E01.1080p.WEB-DL.￼.H264-GRP"),
            });

            decisions[0].Rejections.Select(r => r.Reason).Should().Equal(DownloadRejectionReason.Blocklisted);
            decisions[1].Rejections.Select(r => r.Reason).Should().Equal(DownloadRejectionReason.Blocklisted);
            decisions[2].Approved.Should().BeTrue();
        }
    }
}
