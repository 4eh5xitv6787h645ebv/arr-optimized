using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dapper;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.History;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.PerfNext
{
    // Regression test for the unfiltered history total on PostgreSQL. The SQLite-only count (all rows minus
    // orphaned rows, written with NOT IN subqueries) is planned by PostgreSQL as a plain SubPlan once the Episodes
    // list no longer fits in hash_mem, which rescans Episodes for every History row and does not finish for large
    // libraries. On PostgreSQL the page must keep stock's joined count. Runs only when the Postgres test variables
    // (Sonarr__Postgres__Host etc.) are set; it is ignored on SQLite.
    [TestFixture]
    public class HistoryPagedCountPostgresFixture : DbTest<HistoryRepository, EpisodeHistory>
    {
        private const int EpisodeCount = 300000;
        private const int HistoryCount = 300000;

        private static string Columns(System.Data.IDbConnection connection, string table, Func<string, string> map)
        {
            var columns = connection.Query<string>(
                "SELECT column_name FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = @table AND column_name <> 'Id' ORDER BY ordinal_position",
                new { table }).ToList();

            return string.Join(", ", columns.Select(map));
        }

        [Test]
        public void unfiltered_total_should_match_stock_on_a_large_postgres_library()
        {
            if (Db.DatabaseType != DatabaseType.PostgreSQL)
            {
                Assert.Ignore("PostgreSQL only");
            }

            var series = Builder<Series>.CreateListOfSize(3).All().With(s => s.Id = 0).With(s => s.QualityProfileId = 1).BuildListOfNew().ToList();
            Db.InsertMany(series);
            series = Db.All<Series>().OrderBy(s => s.Id).ToList();

            Db.Insert(new Episode { SeriesId = series[0].Id, SeasonNumber = 1, EpisodeNumber = 1, Title = "Episode", AirDate = "2020-01-01" });
            var template = Db.All<Episode>().Single();

            using (var connection = Db.OpenConnection())
            {
                // Copy the template row EpisodeCount - 1 times, spread over the three series.
                var episodeColumns = Columns(connection, "Episodes", c => "\"" + c + "\"");
                var episodeSelect = Columns(connection, "Episodes", c => c switch
                {
                    "SeriesId" => $"(ARRAY[{series[0].Id},{series[1].Id},{series[2].Id}])[1 + g % 3]",
                    "EpisodeNumber" => "g",
                    "TvdbId" => "g",
                    _ => "\"" + c + "\""
                });

                connection.Execute($"INSERT INTO \"Episodes\" ({episodeColumns}) SELECT {episodeSelect} FROM \"Episodes\", generate_series(2, {EpisodeCount}) AS g WHERE \"Episodes\".\"Id\" = {template.Id}");

                Db.Insert(new EpisodeHistory
                {
                    EpisodeId = template.Id,
                    SeriesId = template.SeriesId,
                    SourceTitle = "Some.Series.S01E01.Release",
                    Quality = new QualityModel(Quality.HDTV720p),
                    Languages = new List<Language> { Language.English },
                    Date = DateTime.UtcNow,
                    EventType = EpisodeHistoryEventType.Grabbed
                });

                var historyColumns = Columns(connection, "History", c => "\"" + c + "\"");
                var historySelect = Columns(connection, "History", c => c switch
                {
                    "EpisodeId" => "\"e\".\"Id\"",
                    "SeriesId" => "\"e\".\"SeriesId\"",
                    _ => "\"h\".\"" + c + "\""
                });

                connection.Execute($"INSERT INTO \"History\" ({historyColumns}) SELECT {historySelect} FROM \"History\" AS \"h\", " +
                                   $"(SELECT \"Id\", \"SeriesId\" FROM \"Episodes\" ORDER BY \"Id\" LIMIT {HistoryCount - 1}) AS \"e\"");

                // Orphans: some episodes deleted, as before housekeeping runs.
                connection.Execute("DELETE FROM \"Episodes\" WHERE \"Id\" IN (SELECT \"Id\" FROM \"Episodes\" ORDER BY \"Id\" LIMIT 60 OFFSET 10)");
                connection.Execute("ANALYZE \"Series\"; ANALYZE \"Episodes\"; ANALYZE \"History\"");
            }

            int expected;
            using (var connection = Db.OpenConnection())
            {
                expected = connection.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM \"History\" INNER JOIN \"Series\" ON \"History\".\"SeriesId\" = \"Series\".\"Id\" INNER JOIN \"Episodes\" ON \"History\".\"EpisodeId\" = \"Episodes\".\"Id\"");
            }

            var spec = new PagingSpec<EpisodeHistory> { Page = 1, PageSize = 20, SortKey = "date", SortDirection = SortDirection.Descending };

            var clock = Stopwatch.StartNew();
            var result = Subject.GetPaged(spec, null, null);
            clock.Stop();

            TestContext.Progress.WriteLine("Unfiltered GetPaged on PostgreSQL with {0} episodes and {1} history rows: {2:0} ms, total {3}", EpisodeCount, HistoryCount, clock.Elapsed.TotalMilliseconds, result.TotalRecords);

            expected.Should().Be(HistoryCount - 60);
            result.TotalRecords.Should().Be(expected);
            result.Records.Should().HaveCount(20);
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        }
    }
}
