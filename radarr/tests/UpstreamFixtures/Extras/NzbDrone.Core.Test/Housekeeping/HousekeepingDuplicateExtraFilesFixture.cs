using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.Housekeeping;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Housekeeping
{
    // Runs every housekeeping task that only needs the main database (discovered by
    // reflection, the same set the container injects into HousekeepingService) against
    // ExtraFiles rows that already contain a duplicate (MovieId, RelativePath) pair.
    [TestFixture]
    public class HousekeepingDuplicateExtraFilesFixture : DbTest
    {
        private const string OrigRelativePath = "Movie Title (2020).nfo-orig";

        private List<IHousekeepingTask> DatabaseOnlyHousekeepers()
        {
            var mainDb = Mocker.Resolve<IMainDatabase>();

            return typeof(IHousekeepingTask).Assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && typeof(IHousekeepingTask).IsAssignableFrom(t))
                .Select(t => t.GetConstructors().SingleOrDefault())
                .Where(c => c != null && c.GetParameters().All(p => p.ParameterType == typeof(IMainDatabase) || p.ParameterType == typeof(Logger)))
                .Select(c => (IHousekeepingTask)c.Invoke(c.GetParameters().Select(p => p.ParameterType == typeof(IMainDatabase) ? (object)mainDb : TestLogger).ToArray()))
                .ToList();
        }

        private MovieFile GivenMovieWithMovieFile(int number)
        {
            var metadata = Builder<MovieMetadata>.CreateNew()
                                                 .With(m => m.TmdbId = number)
                                                 .With(m => m.ImdbId = "tt000000" + number)
                                                 .With(m => m.CleanTitle = "movie" + number)
                                                 .BuildNew();
            Db.Insert(metadata);

            var movie = Builder<Movie>.CreateNew()
                                      .With(m => m.MovieMetadataId = metadata.Id)
                                      .With(m => m.Path = "/movies/movie-" + number)
                                      .BuildNew();
            Db.Insert(movie);

            var movieFile = Builder<MovieFile>.CreateNew()
                                              .With(f => f.MovieId = movie.Id)
                                              .With(f => f.RelativePath = "Movie Title (2020).mkv")
                                              .With(f => f.Quality = new QualityModel())
                                              .With(f => f.Languages = new List<Language> { Language.English })
                                              .BuildNew();
            Db.Insert(movieFile);

            movie.MovieFileId = movieFile.Id;
            Db.Update(movie);

            return movieFile;
        }

        private OtherExtraFile GivenExtraFile(MovieFile movieFile, string relativePath)
        {
            var extraFile = Builder<OtherExtraFile>.CreateNew()
                                                   .With(f => f.MovieId = movieFile.MovieId)
                                                   .With(f => f.MovieFileId = movieFile.Id)
                                                   .With(f => f.RelativePath = relativePath)
                                                   .With(f => f.Extension = ".nfo-orig")
                                                   .BuildNew();
            Db.Insert(extraFile);

            return extraFile;
        }

        [Test]
        public void housekeeping_should_collapse_duplicate_extra_file_rows_to_the_newest()
        {
            var movieFile = GivenMovieWithMovieFile(1);
            var otherMovieFile = GivenMovieWithMovieFile(2);

            GivenExtraFile(movieFile, OrigRelativePath);
            var newest = GivenExtraFile(movieFile, OrigRelativePath);
            var distinct = GivenExtraFile(movieFile, "Movie Title (2020).txt");
            var otherMovie = GivenExtraFile(otherMovieFile, OrigRelativePath);

            var housekeepers = DatabaseOnlyHousekeepers();
            housekeepers.Should().NotBeEmpty();

            foreach (var housekeeper in housekeepers)
            {
                housekeeper.Clean();
            }

            Db.All<OtherExtraFile>().Select(f => f.Id).Should().BeEquivalentTo(new[] { newest.Id, distinct.Id, otherMovie.Id });

            var repository = Mocker.Resolve<OtherExtraFileRepository>();
            Action lookup = () => repository.FindByPath(movieFile.MovieId, OrigRelativePath);
            lookup.Should().NotThrow();
            repository.FindByPath(movieFile.MovieId, OrigRelativePath).Id.Should().Be(newest.Id);
        }
    }
}
