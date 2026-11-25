using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.SeriesStats;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.PerfNext
{
    // Equivalence for the season-statistics mapping (dictionary lookup instead of a
    // SingleOrDefault scan per season row). The reference is the stock mapping rule applied to
    // the very same raw query rows, so string aggregates are compared exactly.
    [TestFixture]
    public class SeriesStatisticsLookupFixture : DbTest<SeriesStatisticsRepository, Series>
    {
        private static readonly Quality[] Qualities = { Quality.HDTV720p, Quality.Bluray1080p, Quality.WEBDL1080p, Quality.SDTV };

        private List<Series> _series;
        private List<EpisodeFile> _files;

        private void GivenLibrary(int seriesCount, int seasonsPerSeries, int episodesPerSeason)
        {
            _series = Builder<Series>.CreateListOfSize(seriesCount)
                .All()
                .With(s => s.Id = 0)
                .With(s => s.Runtime = 30)
                .BuildListOfNew()
                .ToList();

            Db.InsertMany(_series);
            _series = Db.All<Series>().OrderBy(s => s.Id).ToList();

            var episodes = new List<Episode>();
            _files = new List<EpisodeFile>();
            var random = new Random(4242);

            foreach (var series in _series)
            {
                for (var season = 0; season < seasonsPerSeries; season++)
                {
                    // Every third season has no files at all, so the "no match" path is exercised.
                    var seasonHasFiles = (series.Id + season) % 3 != 0;

                    for (var number = 1; number <= episodesPerSeason; number++)
                    {
                        var hasFile = seasonHasFiles && random.Next(4) != 0;

                        episodes.Add(new Episode
                        {
                            SeriesId = series.Id,
                            SeasonNumber = season,
                            EpisodeNumber = number,
                            Title = "Episode " + number,
                            Monitored = random.Next(2) == 0,
                            AirDateUtc = DateTime.UtcNow.AddDays(random.Next(-400, 30)),
                            AirDate = "2020-01-01",
                            EpisodeFileId = hasFile ? 1 : 0
                        });

                        if (hasFile)
                        {
                            _files.Add(new EpisodeFile
                            {
                                SeriesId = series.Id,
                                SeasonNumber = season,
                                RelativePath = $"S{series.Id}/Season {season}/E{number}.mkv",
                                Size = random.Next(1, 5000) * 1024L * 1024L,
                                DateAdded = DateTime.UtcNow,
                                ReleaseGroup = random.Next(3) == 0 ? null : "GRP" + random.Next(5),
                                ReleaseType = (ReleaseType)random.Next(0, 3),
                                Quality = new QualityModel(Qualities[random.Next(Qualities.Length)]),
                                Languages = new List<Language> { Language.English }
                            });
                        }
                    }
                }

                // Files for a season that has no episode rows: neither stock nor the patch report them.
                if (series.Id % 5 == 0)
                {
                    _files.Add(new EpisodeFile
                    {
                        SeriesId = series.Id,
                        SeasonNumber = 99,
                        RelativePath = $"S{series.Id}/Season 99/orphan.mkv",
                        Size = 1234,
                        DateAdded = DateTime.UtcNow,
                        ReleaseGroup = "ORPHAN",
                        Quality = new QualityModel(Quality.SDTV),
                        Languages = new List<Language> { Language.English }
                    });
                }
            }

            Db.InsertMany(episodes);
            Db.InsertMany(_files);
        }

        private List<SeasonStatistics> InvokeQuery(string builderName, object[] builderArgs, string templateFieldName)
        {
            // These private members are reached by name. If upstream renames them, update the names here;
            // the assertions below fail with a clear message instead of a NullReferenceException.
            var type = typeof(SeriesStatisticsRepository);
            var builderMethod = type.GetMethod(builderName, BindingFlags.Instance | BindingFlags.NonPublic);
            var templateField = type.GetField(templateFieldName, BindingFlags.Static | BindingFlags.NonPublic);
            var query = type.GetMethod("Query", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(SqlBuilder), typeof(string) }, null);

            builderMethod.Should().NotBeNull($"SeriesStatisticsRepository.{builderName}() must exist (private member reached by name)");
            templateField.Should().NotBeNull($"SeriesStatisticsRepository.{templateFieldName} must exist (private member reached by name)");
            query.Should().NotBeNull("SeriesStatisticsRepository.Query(SqlBuilder, string) must exist (private member reached by name)");

            var builder = builderMethod.Invoke(Subject, builderArgs);
            var template = (string)templateField.GetValue(null);

            return (List<SeasonStatistics>)query.Invoke(Subject, new[] { builder, template });
        }

        // Stock v5.0.0.3041 MapResults, verbatim.
        private static List<SeasonStatistics> StockMapResults(List<SeasonStatistics> episodesResult, List<SeasonStatistics> filesResult)
        {
            episodesResult.ForEach(e =>
            {
                var file = filesResult.SingleOrDefault(f => f.SeriesId == e.SeriesId && f.SeasonNumber == e.SeasonNumber);

                e.SizeOnDisk = file?.SizeOnDisk ?? 0;
                e.ReleaseGroupsString = file?.ReleaseGroupsString;
                e.ReleaseTypesString = file?.ReleaseTypesString;
                e.EpisodeFileQualitiesString = file?.EpisodeFileQualitiesString;
            });

            return episodesResult;
        }

        private static object Key(SeasonStatistics s) => new
        {
            s.SeriesId,
            s.SeasonNumber,
            s.TotalEpisodeCount,
            s.EpisodeFileCount,
            s.MonitoredEpisodeCount,
            s.SizeOnDisk,
            s.ReleaseGroupsString,
            s.ReleaseTypesString,
            s.EpisodeFileQualitiesString
        };

        [Test]
        public void library_statistics_match_stock_mapping_of_the_same_rows()
        {
            GivenLibrary(40, 5, 6);

            var actual = Subject.SeriesStatistics();

            var reference = StockMapResults(
                InvokeQuery("EpisodesBuilder", new object[] { DateTime.UtcNow }, "_selectEpisodesTemplate"),
                InvokeQuery("EpisodeFilesBuilder", Array.Empty<object>(), "_selectEpisodeFilesTemplate"));

            actual.Should().HaveCount(40 * 5);
            actual.Select(Key).Should().Equal(reference.Select(Key));
        }

        [Test]
        public void library_statistics_match_the_stored_files()
        {
            GivenLibrary(25, 4, 5);

            var actual = Subject.SeriesStatistics();

            foreach (var row in actual)
            {
                var files = _files.Where(f => f.SeriesId == row.SeriesId && f.SeasonNumber == row.SeasonNumber).ToList();

                row.SizeOnDisk.Should().Be(files.Sum(f => f.Size));

                if (files.Empty())
                {
                    row.ReleaseGroupsString.Should().BeNull();
                    row.EpisodeFileQualitiesString.Should().BeNull();
                    row.ReleaseTypesString.Should().BeNull();
                }
                else
                {
                    row.ReleaseGroups.Distinct().OrderBy(g => g)
                        .Should().Equal(files.Where(f => f.ReleaseGroup != null).Select(f => f.ReleaseGroup).Distinct().OrderBy(g => g));
                    row.EpisodeFileQualitiesString.Split('|').Select(int.Parse).Distinct().OrderBy(q => q)
                        .Should().Equal(files.Select(f => f.Quality.Quality.Id).Distinct().OrderBy(q => q));
                }
            }

            actual.Should().NotContain(r => r.SeasonNumber == 99);
        }

        [Test]
        public void library_statistics_match_per_series_statistics()
        {
            GivenLibrary(15, 4, 4);

            var all = Subject.SeriesStatistics().ToDictionary(s => (s.SeriesId, s.SeasonNumber));

            foreach (var series in _series)
            {
                foreach (var row in Subject.SeriesStatistics(series.Id))
                {
                    var match = all[(row.SeriesId, row.SeasonNumber)];

                    match.SizeOnDisk.Should().Be(row.SizeOnDisk);
                    (match.ReleaseGroupsString == null).Should().Be(row.ReleaseGroupsString == null);
                    match.ReleaseGroups.OrderBy(g => g).Should().Equal(row.ReleaseGroups.OrderBy(g => g));
                }
            }
        }

        [Test]
        [Explicit]
        [Category("PerfNextBenchmark")]
        public void benchmark_library_statistics()
        {
            // 1,500 series x 7 seasons, one file per season (about 10,500 season rows).
            GivenLibrary(1500, 7, 1);

            Subject.SeriesStatistics();

            var best = double.MaxValue;
            for (var i = 0; i < 7; i++)
            {
                var sw = Stopwatch.StartNew();
                var rows = Subject.SeriesStatistics();
                sw.Stop();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                rows.Should().HaveCount(1500 * 7);
            }

            TestContext.Progress.WriteLine($"PERFNEXT SeriesStatistics() 1500x7: best of 7 = {best:F1} ms");
        }
    }
}
