using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.MovieStats;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.PerfNext
{
    // Equivalence for the movie-statistics mapping (dictionary lookup instead of a SingleOrDefault
    // scan per movie row). The reference is the stock mapping rule applied to the very same raw query
    // rows, so the string aggregates (release groups, qualities) are compared exactly.
    [TestFixture]
    public class MovieStatisticsLookupFixture : DbTest<MovieStatisticsRepository, Movie>
    {
        private static readonly Quality[] Qualities = { Quality.Bluray720p, Quality.Bluray1080p, Quality.WEBDL1080p, Quality.DVD };

        private List<Movie> _movies;
        private List<MovieFile> _files;

        private void GivenLibrary(int movieCount, int maxFilesPerMovie)
        {
            var random = new Random(9090);

            var movies = Enumerable.Range(1, movieCount).Select(i => new Movie
            {
                MovieMetadataId = i,
                Monitored = random.Next(2) == 0,
                QualityProfileId = 1,
                Path = "/movies/movie" + i,
                RootFolderPath = "/movies",
                Added = DateTime.UtcNow,
                MovieFileId = random.Next(3) == 0 ? 0 : i
            }).ToList();

            Db.InsertMany(movies);
            _movies = Db.All<Movie>().OrderBy(m => m.Id).ToList();

            _files = new List<MovieFile>();
            foreach (var movie in _movies)
            {
                // About a third of the movies have no file rows at all.
                var count = movie.Id % 3 == 0 ? 0 : random.Next(1, maxFilesPerMovie + 1);

                for (var f = 0; f < count; f++)
                {
                    _files.Add(new MovieFile
                    {
                        MovieId = movie.Id,
                        RelativePath = $"movie{movie.Id}-{f}.mkv",
                        Size = random.Next(1, 50000) * 1024L * 1024L,
                        DateAdded = DateTime.UtcNow,
                        ReleaseGroup = random.Next(3) == 0 ? null : "GRP" + random.Next(5),
                        Quality = new QualityModel(Qualities[random.Next(Qualities.Length)]),
                        Languages = new List<Language> { Language.English }
                    });
                }
            }

            // A file row for a movie that does not exist: neither stock nor the patch report it.
            _files.Add(new MovieFile
            {
                MovieId = 999999,
                RelativePath = "orphan.mkv",
                Size = 77,
                DateAdded = DateTime.UtcNow,
                ReleaseGroup = "ORPHAN",
                Quality = new QualityModel(Quality.DVD),
                Languages = new List<Language> { Language.English }
            });

            Db.InsertMany(_files);
        }

        private List<MovieStatistics> InvokeQuery(string builderName, string templateFieldName)
        {
            // These private members are reached by name. If upstream renames them, update the names here;
            // the assertions below fail with a clear message instead of a NullReferenceException.
            var type = typeof(MovieStatisticsRepository);
            var builderMethod = type.GetMethod(builderName, BindingFlags.Instance | BindingFlags.NonPublic);
            var templateField = type.GetField(templateFieldName, BindingFlags.Static | BindingFlags.NonPublic);
            var query = type.GetMethod("Query", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(SqlBuilder), typeof(string) }, null);

            builderMethod.Should().NotBeNull($"MovieStatisticsRepository.{builderName}() must exist (private member reached by name)");
            templateField.Should().NotBeNull($"MovieStatisticsRepository.{templateFieldName} must exist (private member reached by name)");
            query.Should().NotBeNull("MovieStatisticsRepository.Query(SqlBuilder, string) must exist (private member reached by name)");

            var builder = builderMethod.Invoke(Subject, Array.Empty<object>());
            var template = (string)templateField.GetValue(null);

            return (List<MovieStatistics>)query.Invoke(Subject, new[] { builder, template });
        }

        // Stock v7.0.3.10918 MapResults, verbatim.
        private static List<MovieStatistics> StockMapResults(List<MovieStatistics> moviesResult, List<MovieStatistics> filesResult)
        {
            moviesResult.ForEach(e =>
            {
                var file = filesResult.SingleOrDefault(f => f.MovieId == e.MovieId);

                e.SizeOnDisk = file?.SizeOnDisk ?? 0;
                e.ReleaseGroupsString = file?.ReleaseGroupsString;
                e.MovieFileQualitiesString = file?.MovieFileQualitiesString;
            });

            return moviesResult;
        }

        private static object Key(MovieStatistics s) => new
        {
            s.MovieId,
            s.MovieFileCount,
            s.SizeOnDisk,
            s.ReleaseGroupsString,
            s.MovieFileQualitiesString
        };

        [Test]
        public void library_statistics_match_stock_mapping_of_the_same_rows()
        {
            GivenLibrary(300, 3);

            var actual = Subject.MovieStatistics();

            var reference = StockMapResults(
                InvokeQuery("MoviesBuilder", "_selectMoviesTemplate"),
                InvokeQuery("MovieFilesBuilder", "_selectMovieFilesTemplate"));

            actual.Should().HaveCount(300);
            actual.Select(Key).Should().Equal(reference.Select(Key));
        }

        [Test]
        public void library_statistics_match_the_stored_files()
        {
            GivenLibrary(200, 3);

            var actual = Subject.MovieStatistics();

            actual.Select(s => s.MovieId).Should().BeEquivalentTo(_movies.Select(m => m.Id));

            foreach (var row in actual)
            {
                var files = _files.Where(f => f.MovieId == row.MovieId).ToList();

                row.SizeOnDisk.Should().Be(files.Sum(f => f.Size));

                if (files.Empty())
                {
                    row.ReleaseGroupsString.Should().BeNull();
                    row.MovieFileQualitiesString.Should().BeNull();
                }
                else
                {
                    row.ReleaseGroups.Distinct().OrderBy(g => g)
                        .Should().Equal(files.Where(f => f.ReleaseGroup != null).Select(f => f.ReleaseGroup).Distinct().OrderBy(g => g));

                    // The quality list feeds the movie-file-quality filter and the API field; it must survive the change.
                    row.MovieFileQualitiesString.Should().NotBeNullOrWhiteSpace();
                    row.MovieFileQualities.Select(q => q.Id).Distinct().OrderBy(q => q)
                        .Should().Equal(files.Select(f => f.Quality.Quality.Id).Distinct().OrderBy(q => q));
                }
            }
        }

        [Test]
        public void library_statistics_match_per_movie_statistics()
        {
            GivenLibrary(60, 3);

            var all = Subject.MovieStatistics().ToDictionary(s => s.MovieId);

            foreach (var movie in _movies)
            {
                var single = Subject.MovieStatistics(movie.Id).Single();
                var match = all[movie.Id];

                match.SizeOnDisk.Should().Be(single.SizeOnDisk);
                match.MovieFileCount.Should().Be(single.MovieFileCount);
                match.ReleaseGroups.OrderBy(g => g).Should().Equal(single.ReleaseGroups.OrderBy(g => g));
                match.MovieFileQualities.Select(q => q.Id).OrderBy(q => q).Should().Equal(single.MovieFileQualities.Select(q => q.Id).OrderBy(q => q));
            }
        }

        [Test]
        [Explicit]
        [Category("PerfNextBenchmark")]
        public void benchmark_library_statistics()
        {
            GivenLibrary(10000, 1);

            Subject.MovieStatistics();

            var best = double.MaxValue;
            for (var i = 0; i < 7; i++)
            {
                var sw = Stopwatch.StartNew();
                var rows = Subject.MovieStatistics();
                sw.Stop();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                rows.Should().HaveCount(10000);
            }

            TestContext.Progress.WriteLine($"PERFNEXT MovieStatistics() 10000 movies: best of 7 = {best:F1} ms");
        }
    }
}
