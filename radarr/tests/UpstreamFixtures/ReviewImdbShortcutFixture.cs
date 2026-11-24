using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.CustomFormats;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.ReviewFixes
{
    // Review finding 1 (duplicate IMDb ids map like stock) and finding 2 (exact-ID mapping must
    // see the row as it is at decision time, not the search-start snapshot).
    [TestFixture]
    public class ReviewImdbShortcutFixture : DbTest<MovieRepository, Movie>
    {
        private Movie _a;
        private Movie _b;
        private ParsingService _parsing;
        private QualityProfile _profile2;

        [SetUp]
        public void Setup()
        {
            var profileRepo = Mocker.Resolve<QualityProfileRepository>();
            Mocker.SetConstant<IQualityProfileRepository>(profileRepo);
            Mocker.GetMock<ICustomFormatService>().Setup(x => x.All()).Returns(new List<CustomFormat>());

            QualityProfile NewProfile(string name) => new QualityProfile
            {
                Items = Qualities.QualityFixture.GetDefaultQualities(Quality.Bluray1080p, Quality.DVD, Quality.HDTV720p),
                FormatItems = CustomFormatsTestHelpers.GetDefaultFormatItems(),
                MinFormatScore = 0,
                Cutoff = Quality.Bluray1080p.Id,
                Name = name
            };

            var profile = profileRepo.Insert(NewProfile("P1"));
            _profile2 = profileRepo.Insert(NewProfile("P2"));

            var metaA = Builder<MovieMetadata>.CreateNew().With(x => x.Id = 0).With(x => x.TmdbId = 111).With(x => x.ImdbId = "tt1234567").With(x => x.Year = 2010).With(x => x.Title = "Alpha").With(x => x.CleanTitle = "alpha").BuildNew();
            var metaB = Builder<MovieMetadata>.CreateNew().With(x => x.Id = 0).With(x => x.TmdbId = 222).With(x => x.ImdbId = "tt1234567").With(x => x.Year = 2010).With(x => x.Title = "Beta").With(x => x.CleanTitle = "beta").BuildNew();
            Db.Insert(metaA);
            Db.Insert(metaB);

            _a = Builder<Movie>.CreateNew().With(x => x.Id = 0).With(x => x.MovieMetadataId = metaA.Id).With(x => x.QualityProfileId = profile.Id).With(x => x.Path = "/m/a").BuildNew();
            _b = Builder<Movie>.CreateNew().With(x => x.Id = 0).With(x => x.MovieMetadataId = metaB.Id).With(x => x.QualityProfileId = profile.Id).With(x => x.Path = "/m/b").BuildNew();
            Subject.Insert(_a);
            Subject.Insert(_b);

            Mocker.SetConstant<IMovieRepository>(Subject);
            _parsing = new ParsingService(Mocker.Resolve<MovieService>(), TestLogger);
        }

        private ParsedMovieInfo Parsed() => new ParsedMovieInfo
        {
            MovieTitles = new List<string> { "Some Other Release Title" },
            Languages = new List<Language> { Language.English },
            Year = 2010
        };

        [Test]
        public void duplicate_imdb_search_for_second_movie_maps_like_stock()
        {
            Subject.FindByImdbId("1234567").Id.Should().Be(_a.Id);

            var criteria = new MovieSearchCriteria { Movie = Subject.Get(_b.Id) };
            var remote = _parsing.Map(Parsed(), "1234567", 0, criteria);

            TestContext.Progress.WriteLine($"REVIEW duplicate-imdb mapped movie id={remote.Movie?.Id} requested={remote.MovieRequested} (A={_a.Id}, B={_b.Id})");
            remote.Movie.Id.Should().Be(_a.Id, "stock FindByImdbId returns the first DB row");
        }

        [Test]
        public void id_match_after_db_change_during_search_sees_fresh_row_like_stock()
        {
            var criteria = new MovieSearchCriteria { Movie = Subject.Get(_a.Id) };

            var fresh = Subject.Get(_a.Id);
            fresh.QualityProfileId = _profile2.Id;
            Subject.Update(fresh);

            var remote = _parsing.Map(Parsed(), "0", 111, criteria);

            TestContext.Progress.WriteLine($"REVIEW stale mapped profile={remote.Movie?.QualityProfile?.Name} (db now P2)");
            remote.Movie.QualityProfile.Name.Should().Be("P2");
        }

        [Test]
        public void imdb_match_after_db_change_during_search_sees_fresh_row_like_stock()
        {
            // B is the only movie whose IMDb id is unique once A's id changes; search for B.
            var metaA = Db.All<MovieMetadata>().Find(m => m.TmdbId == 111);
            metaA.ImdbId = "tt7654321";
            Db.Update(metaA);

            var criteria = new MovieSearchCriteria { Movie = Subject.Get(_b.Id) };

            var fresh = Subject.Get(_b.Id);
            fresh.QualityProfileId = _profile2.Id;
            fresh.Monitored = false;
            Subject.Update(fresh);

            var remote = _parsing.Map(Parsed(), "tt1234567", 0, criteria);

            remote.Movie.Id.Should().Be(_b.Id);
            remote.Movie.QualityProfile.Name.Should().Be("P2");
            remote.Movie.Monitored.Should().BeFalse();
        }
    }
}
