using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using FizzWare.NBuilder;
using FluentAssertions;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.History;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.PerfNext
{
    // Which count query the history page uses, without a PostgreSQL server: the repository runs on the SQLite test
    // database through a wrapper that reports a chosen DatabaseType, and the SQL it builds is read from the SQL log.
    // The fast count (all rows minus orphans) is for unfiltered pages on SQLite only; PostgreSQL and filtered pages
    // keep stock's joined count. HistoryPagedCountFixture checks that the totals are equal; this pins the choice.
    [TestFixture]
    [NonParallelizable]
    public class HistoryPagedCountQueryFixture : DbTest<HistoryRepository, EpisodeHistory>
    {
        private const string JoinedCountMarker = "AS \"Inner\"";
        private const string FastCountMarker = "NOT IN (SELECT \"Id\" FROM \"Episodes\")";

        private MemoryTarget _sqlLog;
        private LoggingRule _sqlRule;
        private bool _logSql;

        private class ReportedTypeDatabase : IMainDatabase
        {
            private readonly IDatabase _inner;

            public ReportedTypeDatabase(IDatabase inner, DatabaseType databaseType)
            {
                _inner = inner;
                DatabaseType = databaseType;
            }

            public DatabaseType DatabaseType { get; }

            public Version Version => _inner.Version;

            public int Migration => _inner.Migration;

            public DbConnection OpenConnection() => _inner.OpenConnection();

            public void Vacuum() => _inner.Vacuum();
        }

        [SetUp]
        public void Setup()
        {
            _logSql = SqlBuilderExtensions.LogSql;
            SqlBuilderExtensions.LogSql = true;

            _sqlLog = new MemoryTarget { Layout = "${message}" };
            _sqlRule = new LoggingRule("SqlBuilderExtensions", LogLevel.Trace, _sqlLog);

            var config = LogManager.Configuration ?? new LoggingConfiguration();
            config.LoggingRules.Insert(0, _sqlRule);
            LogManager.Configuration = config;

            var series = Builder<Series>.CreateNew().With(s => s.Id = 0).With(s => s.QualityProfileId = 1).BuildNew();
            Db.Insert(series);

            var episode = Builder<Episode>.CreateNew().With(e => e.Id = 0).With(e => e.SeriesId = series.Id).BuildNew();
            Db.Insert(episode);

            var history = Builder<EpisodeHistory>.CreateListOfSize(3)
                .All()
                .With(h => h.Id = 0)
                .With(h => h.SeriesId = series.Id)
                .With(h => h.EpisodeId = episode.Id)
                .With(h => h.Quality = new QualityModel(Quality.HDTV720p))
                .With(h => h.Languages = new List<Language> { Language.English })
                .With(h => h.EventType = EpisodeHistoryEventType.Grabbed)
                .TheLast(1)
                .With(h => h.EpisodeId = episode.Id + 1000)
                .BuildListOfNew();
            Db.InsertMany(history);
        }

        // DbTest.TearDown is not virtual; NUnit runs both [TearDown] methods.
        [TearDown]
        public new void TearDown()
        {
            SqlBuilderExtensions.LogSql = _logSql;

            var config = LogManager.Configuration;

            if (config != null)
            {
                config.LoggingRules.Remove(_sqlRule);
                LogManager.Configuration = config;
            }
        }

        private string CountSql(DatabaseType databaseType, int[] languages = null, int[] qualities = null, params Expression<Func<EpisodeHistory, bool>>[] filters)
        {
            var repository = new HistoryRepository(new ReportedTypeDatabase(Mocker.Resolve<IMainDatabase>(), databaseType), Mocker.Resolve<IEventAggregator>());

            var spec = new PagingSpec<EpisodeHistory>
            {
                Page = 1,
                PageSize = 10,
                SortKey = "date",
                SortDirection = SortDirection.Descending
            };

            spec.FilterExpressions.AddRange(filters);

            _sqlLog.Logs.Clear();

            var result = repository.GetPaged(spec, languages, qualities);

            // One history row is orphaned (its episode is gone): every count must leave it out.
            result.TotalRecords.Should().Be(2);

            var counts = _sqlLog.Logs.Where(l => l.Contains("COUNT(")).ToList();
            counts.Should().ContainSingle("one count query per page");

            return counts.Single();
        }

        [Test]
        public void unfiltered_page_on_sqlite_should_use_the_fast_count()
        {
            var sql = CountSql(DatabaseType.SQLite);

            sql.Should().Contain(FastCountMarker);
            sql.Should().NotContain(JoinedCountMarker);
        }

        [Test]
        public void unfiltered_page_on_postgres_should_use_the_stock_joined_count()
        {
            var sql = CountSql(DatabaseType.PostgreSQL);

            sql.Should().Contain(JoinedCountMarker);
            sql.Should().NotContain(FastCountMarker);
        }

        [Test]
        public void filtered_page_on_sqlite_should_use_the_stock_joined_count()
        {
            CountSql(DatabaseType.SQLite, filters: h => h.EventType == EpisodeHistoryEventType.Grabbed).Should().Contain(JoinedCountMarker);
            CountSql(DatabaseType.SQLite, qualities: new[] { Quality.HDTV720p.Id }).Should().Contain(JoinedCountMarker);
            CountSql(DatabaseType.SQLite, languages: new[] { Language.English.Id }).Should().Contain(JoinedCountMarker);
        }

        [Test]
        public void filtered_page_on_postgres_should_use_the_stock_joined_count()
        {
            CountSql(DatabaseType.PostgreSQL, filters: h => h.EventType == EpisodeHistoryEventType.Grabbed).Should().Contain(JoinedCountMarker);
        }
    }
}
