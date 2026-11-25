using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.History;
using NzbDrone.Core.Housekeeping.Housekeepers;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.PerfNext
{
    // Equivalence for the paged-history total. Unfiltered pages are counted as all History rows minus the
    // orphaned ones (series or episode gone) instead of through the Series/Episodes joins; filtered pages
    // use stock's joined count. The total must always equal what stock reports, which is the number of
    // rows the joined records query returns for the same filters, also while orphaned rows exist.
    [TestFixture]
    public class HistoryPagedCountFixture : DbTest<HistoryRepository, EpisodeHistory>
    {
        private static readonly Quality[] Qualities = { Quality.HDTV720p, Quality.Bluray1080p, Quality.DVD, Quality.WEBDL1080p };
        private static readonly Language[] Languages = { Language.English, Language.French, Language.German, Language.Japanese };
        private static readonly EpisodeHistoryEventType[] EventTypes =
        {
            EpisodeHistoryEventType.Grabbed,
            EpisodeHistoryEventType.DownloadFolderImported,
            EpisodeHistoryEventType.DownloadFailed,
            EpisodeHistoryEventType.EpisodeFileDeleted,
            EpisodeHistoryEventType.DownloadIgnored
        };

        private List<Series> _series;
        private List<Episode> _episodes;
        private List<EpisodeHistory> _history;

        private void GivenHistory(int seriesCount, int episodesPerSeries, int historyCount, int dataPadding = 0)
        {
            _series = Builder<Series>.CreateListOfSize(seriesCount)
                .All()
                .With(s => s.Id = 0)
                .With(s => s.QualityProfileId = 1)
                .BuildListOfNew()
                .ToList();
            Db.InsertMany(_series);
            _series = Db.All<Series>().OrderBy(s => s.Id).ToList();

            var episodes = new List<Episode>();
            foreach (var series in _series)
            {
                for (var i = 1; i <= episodesPerSeries; i++)
                {
                    episodes.Add(new Episode
                    {
                        SeriesId = series.Id,
                        SeasonNumber = 1,
                        EpisodeNumber = i,
                        Title = "Episode " + i,
                        AirDate = "2020-01-01"
                    });
                }
            }

            Db.InsertMany(episodes);
            _episodes = Db.All<Episode>().OrderBy(e => e.Id).ToList();

            var random = new Random(1717);
            var padding = new string('x', dataPadding);
            var history = new List<EpisodeHistory>();

            for (var i = 0; i < historyCount; i++)
            {
                var episode = _episodes[random.Next(_episodes.Count)];
                var languages = random.Next(4) == 0
                    ? new List<Language> { Languages[random.Next(Languages.Length)], Languages[random.Next(Languages.Length)] }.Distinct().ToList()
                    : new List<Language> { Languages[random.Next(Languages.Length)] };

                var item = new EpisodeHistory
                {
                    EpisodeId = episode.Id,
                    SeriesId = episode.SeriesId,
                    SourceTitle = "Some.Series.S01E01.Release-" + i,
                    Quality = new QualityModel(Qualities[random.Next(Qualities.Length)]),
                    Date = DateTime.UtcNow.AddMinutes(-random.Next(1, 500000)),
                    EventType = EventTypes[random.Next(EventTypes.Length)],
                    Languages = languages,
                    DownloadId = "dl" + random.Next(historyCount / 3 + 1)
                };

                item.Data.Add("indexer", "Indexer " + random.Next(5));
                if (dataPadding > 0)
                {
                    item.Data.Add("padding", padding);
                }

                history.Add(item);
            }

            Db.InsertMany(history);
            _history = Db.All<EpisodeHistory>();
        }

        private PagingSpec<EpisodeHistory> Spec(int page, int pageSize, string sortKey, params Expression<Func<EpisodeHistory, bool>>[] filters)
        {
            var spec = new PagingSpec<EpisodeHistory>
            {
                Page = page,
                PageSize = pageSize,
                SortKey = sortKey,
                SortDirection = SortDirection.Descending
            };

            spec.FilterExpressions.AddRange(filters);

            return spec;
        }

        private static IEnumerable<TestCaseData> Cases()
        {
            int[] none = null;

            yield return new TestCaseData("no filters", null, none, none);
            yield return new TestCaseData("event type", new[] { (int)EpisodeHistoryEventType.Grabbed }, none, none);
            yield return new TestCaseData("two event types", new[] { (int)EpisodeHistoryEventType.Grabbed, (int)EpisodeHistoryEventType.DownloadFailed }, none, none);
            yield return new TestCaseData("one language", null, new[] { Language.French.Id }, none);
            yield return new TestCaseData("two languages", null, new[] { Language.French.Id, Language.Japanese.Id }, none);
            yield return new TestCaseData("one quality", null, none, new[] { Quality.Bluray1080p.Id });
            yield return new TestCaseData("two qualities", null, none, new[] { Quality.DVD.Id, Quality.HDTV720p.Id });
            yield return new TestCaseData("everything", new[] { (int)EpisodeHistoryEventType.Grabbed }, new[] { Language.English.Id }, new[] { Quality.Bluray1080p.Id, Quality.WEBDL1080p.Id });
        }

        // Stock's language filter (BuildLanguageWhereClause) uses SQLite-only SQL ('...' || CHAR(13) || '...') and fails
        // on PostgreSQL with 42601 on stock and patched alike, so language-filtered pages cannot be compared there.
        private bool LanguageFilterUnsupported(int[] languages) => languages is { Length: > 0 } && Db.DatabaseType == DatabaseType.PostgreSQL;

        private void AssertTotalMatchesRecords(string sortKey, int[] eventTypes, int[] languages, int[] qualities, params Expression<Func<EpisodeHistory, bool>>[] extraFilters)
        {
            if (LanguageFilterUnsupported(languages))
            {
                TestContext.Progress.WriteLine("Skipped a language-filtered comparison: stock's language filter does not run on PostgreSQL");
                return;
            }

            var filters = new List<Expression<Func<EpisodeHistory, bool>>>(extraFilters);
            if (eventTypes != null)
            {
                filters.Add(v => eventTypes.Contains((int)v.EventType));
            }

            var all = Subject.GetPaged(Spec(1, 100000, sortKey, filters.ToArray()), languages, qualities);
            var page = Subject.GetPaged(Spec(2, 3, sortKey, filters.ToArray()), languages, qualities);

            all.TotalRecords.Should().Be(all.Records.Count);
            page.TotalRecords.Should().Be(all.Records.Count);

            // Stock lists (and counts) only rows whose series and episode still exist.
            var liveSeries = Db.All<Series>().Select(x => x.Id).ToHashSet();
            var liveEpisodes = Db.All<Episode>().Select(x => x.Id).ToHashSet();
            var expected = Db.All<EpisodeHistory>().Where(h =>
                liveSeries.Contains(h.SeriesId) &&
                liveEpisodes.Contains(h.EpisodeId) &&
                (eventTypes == null || eventTypes.Contains((int)h.EventType)) &&
                (languages is not { Length: > 0 } || h.Languages.Any(l => languages.Contains(l.Id))) &&
                (qualities is not { Length: > 0 } || qualities.Contains(h.Quality.Quality.Id)) &&
                extraFilters.All(f => f.Compile()(h)));

            all.Records.Select(r => r.Id).OrderBy(i => i).Should().Equal(expected.Select(h => h.Id).OrderBy(i => i));
        }

        [TestCaseSource(nameof(Cases))]
        public void total_matches_stock_for_filters(string name, int[] eventTypes, int[] languages, int[] qualities)
        {
            if (LanguageFilterUnsupported(languages))
            {
                Assert.Ignore("Stock's language filter does not run on PostgreSQL (upstream bug, same on stock)");
            }

            GivenHistory(4, 6, 300);

            AssertTotalMatchesRecords("date", eventTypes, languages, qualities);
            AssertTotalMatchesRecords("quality", eventTypes, languages, qualities);
        }

        [Test]
        public void total_matches_stock_for_api_filters()
        {
            GivenHistory(4, 6, 300);

            var episodeId = _episodes[3].Id;
            var downloadId = _history[10].DownloadId;
            var seriesIds = new List<int> { _series[0].Id, _series[2].Id };

            AssertTotalMatchesRecords("date", null, null, null, h => h.EpisodeId == episodeId);
            AssertTotalMatchesRecords("date", null, null, null, h => h.DownloadId == downloadId);
            AssertTotalMatchesRecords("date", null, null, null, h => seriesIds.Contains(h.SeriesId));
            AssertTotalMatchesRecords("quality", new[] { (int)EpisodeHistoryEventType.Grabbed }, new[] { Language.English.Id }, null, h => seriesIds.Contains(h.SeriesId));
            AssertTotalMatchesRecords("date", null, null, null, h => h.EpisodeId == -1);
        }

        [Test]
        public void total_is_zero_for_empty_history()
        {
            var result = Subject.GetPaged(Spec(1, 20, "date"), null, null);

            result.TotalRecords.Should().Be(0);
            result.Records.Should().BeEmpty();
        }

        private void GivenOrphanedHistory()
        {
            GivenHistory(4, 6, 300);

            // A refresh removed an episode: its history rows stay until housekeeping.
            Db.Delete(_episodes[2]);

            // A series row is gone while its episodes and history are still there.
            Db.Delete(_series[3]);

            // Rows whose episode and series are both gone must be subtracted once.
            Db.Delete(_episodes[7]);
            Db.Delete(_series[1]);

            // A row that points at an episode that never existed.
            var orphan = Builder<EpisodeHistory>.CreateNew()
                .With(h => h.Id = 0)
                .With(h => h.SeriesId = _series[0].Id)
                .With(h => h.EpisodeId = 999999)
                .With(h => h.EventType = EpisodeHistoryEventType.Grabbed)
                .With(h => h.Quality = new QualityModel(Quality.DVD))
                .With(h => h.Languages = new List<Language> { Language.English })
                .BuildNew();
            Db.Insert(orphan);

            var liveSeries = Db.All<Series>().Select(x => x.Id).ToHashSet();
            var liveEpisodes = Db.All<Episode>().Select(x => x.Id).ToHashSet();
            var orphans = Db.All<EpisodeHistory>().Count(h => !liveSeries.Contains(h.SeriesId) || !liveEpisodes.Contains(h.EpisodeId));
            orphans.Should().BeGreaterThan(50);
        }

        [Test]
        public void total_matches_stock_with_orphaned_rows()
        {
            GivenOrphanedHistory();

            AssertTotalMatchesRecords("date", null, null, null);
            AssertTotalMatchesRecords("quality", null, null, null);
            AssertTotalMatchesRecords("date", null, new int[0], new int[0]);
            AssertTotalMatchesRecords("date", new[] { (int)EpisodeHistoryEventType.Grabbed }, null, null);
            AssertTotalMatchesRecords("quality", null, new[] { Language.English.Id }, new[] { Quality.DVD.Id });
            AssertTotalMatchesRecords("date", null, null, null, h => h.EpisodeId == 999999);
            var deletedEpisodeId = _episodes[2].Id;
            AssertTotalMatchesRecords("date", null, null, null, h => h.EpisodeId == deletedEpisodeId);
        }

        [Test]
        public void total_matches_stock_after_housekeeping_removes_orphans()
        {
            GivenOrphanedHistory();
            var before = Subject.GetPaged(Spec(1, 20, "date"), null, null).TotalRecords;

            Mocker.Resolve<CleanupOrphanedHistoryItems>().Clean();

            var after = Subject.GetPaged(Spec(1, 20, "date"), null, null);
            after.TotalRecords.Should().Be(before);
            after.TotalRecords.Should().Be(Db.All<EpisodeHistory>().Count);
            AssertTotalMatchesRecords("date", null, null, null);
        }

        [Test]
        [Explicit]
        [Category("PerfNextBenchmark")]
        public void benchmark_paged_history()
        {
            // 50,000 history rows of about 1.2 KB each, 100 series x 50 episodes.
            GivenHistory(100, 50, 50000, 1000);

            void Measure(string name, int[] eventTypes)
            {
                var best = double.MaxValue;
                var total = 0;
                for (var i = 0; i < 6; i++)
                {
                    var filters = eventTypes == null
                        ? Array.Empty<Expression<Func<EpisodeHistory, bool>>>()
                        : new Expression<Func<EpisodeHistory, bool>>[] { v => eventTypes.Contains((int)v.EventType) };
                    var sw = Stopwatch.StartNew();
                    var result = Subject.GetPaged(Spec(1, 20, "date", filters), null, null);
                    sw.Stop();
                    total = result.TotalRecords;
                    if (i > 0)
                    {
                        best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                    }
                }

                TestContext.Progress.WriteLine($"PERFNEXT History GetPaged page 1/20 {name}: total={total} best of 5 = {best:F1} ms");
            }

            Measure("unfiltered", null);
            Measure("grabbed", new[] { (int)EpisodeHistoryEventType.Grabbed });
            Measure("imported", new[] { (int)EpisodeHistoryEventType.DownloadFolderImported });

            // Same after a refresh removed 60 episodes (their history rows are orphaned until housekeeping).
            foreach (var episode in _episodes.Skip(1000).Take(60))
            {
                Db.Delete(episode);
            }

            var listable = Subject.GetPaged(Spec(1, 100000, "date"), null, null).Records.Count;
            TestContext.Progress.WriteLine($"PERFNEXT History rows listable after deleting 60 episodes: {listable} of {_history.Count}");
            Measure("unfiltered, with orphans", null);
            Measure("grabbed, with orphans", new[] { (int)EpisodeHistoryEventType.Grabbed });
        }
    }
}
