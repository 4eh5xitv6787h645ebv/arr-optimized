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
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Housekeeping
{
    // Runs every housekeeping task that only needs the main database (discovered by
    // reflection, the same set the container injects into HousekeepingService) against
    // ExtraFiles rows that already contain a duplicate (SeriesId, RelativePath) pair.
    [TestFixture]
    public class HousekeepingDuplicateExtraFilesFixture : DbTest
    {
        private const string OrigRelativePath = "Season 1/Series Title - S01E01.nfo-orig";

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

        private EpisodeFile GivenSeriesWithEpisodeFile(int number)
        {
            var series = Builder<Series>.CreateNew()
                                        .With(s => s.TvdbId = number)
                                        .With(s => s.TitleSlug = "series-" + number)
                                        .With(s => s.CleanTitle = "series" + number)
                                        .With(s => s.Path = "/tv/series-" + number)
                                        .BuildNew();
            Db.Insert(series);

            var episodeFile = Builder<EpisodeFile>.CreateNew()
                                                  .With(f => f.SeriesId = series.Id)
                                                  .With(f => f.RelativePath = "Season 1/Series Title - S01E01.mkv")
                                                  .With(f => f.Quality = new QualityModel())
                                                  .With(f => f.Languages = new List<Language> { Language.English })
                                                  .BuildNew();
            Db.Insert(episodeFile);

            var episode = Builder<Episode>.CreateNew()
                                          .With(e => e.SeriesId = series.Id)
                                          .With(e => e.TvdbId = number)
                                          .With(e => e.EpisodeFileId = episodeFile.Id)
                                          .BuildNew();
            Db.Insert(episode);

            return episodeFile;
        }

        private OtherExtraFile GivenExtraFile(EpisodeFile episodeFile, string relativePath)
        {
            var extraFile = Builder<OtherExtraFile>.CreateNew()
                                                   .With(f => f.SeriesId = episodeFile.SeriesId)
                                                   .With(f => f.EpisodeFileId = episodeFile.Id)
                                                   .With(f => f.SeasonNumber = 1)
                                                   .With(f => f.RelativePath = relativePath)
                                                   .With(f => f.Extension = ".nfo-orig")
                                                   .BuildNew();
            Db.Insert(extraFile);

            return extraFile;
        }

        [Test]
        public void housekeeping_should_collapse_duplicate_extra_file_rows_to_the_newest()
        {
            var episodeFile = GivenSeriesWithEpisodeFile(1);
            var otherEpisodeFile = GivenSeriesWithEpisodeFile(2);

            GivenExtraFile(episodeFile, OrigRelativePath);
            var newest = GivenExtraFile(episodeFile, OrigRelativePath);
            var distinct = GivenExtraFile(episodeFile, "Season 1/Series Title - S01E01.txt");
            var otherSeries = GivenExtraFile(otherEpisodeFile, OrigRelativePath);

            var housekeepers = DatabaseOnlyHousekeepers();
            housekeepers.Should().NotBeEmpty();

            foreach (var housekeeper in housekeepers)
            {
                housekeeper.Clean();
            }

            Db.All<OtherExtraFile>().Select(f => f.Id).Should().BeEquivalentTo(new[] { newest.Id, distinct.Id, otherSeries.Id });

            var repository = Mocker.Resolve<OtherExtraFileRepository>();
            Action lookup = () => repository.FindByPath(episodeFile.SeriesId, OrigRelativePath);
            lookup.Should().NotThrow();
            repository.FindByPath(episodeFile.SeriesId, OrigRelativePath).Id.Should().Be(newest.Id);
        }
    }
}
