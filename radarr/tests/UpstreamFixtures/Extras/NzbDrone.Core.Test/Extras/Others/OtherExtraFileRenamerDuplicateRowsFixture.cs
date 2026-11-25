using System;
using System.IO;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Extras.Others
{
    // Exercises OtherExtraFileRenamer against the real OtherExtraFileService and
    // repository so the ExtraFiles rows left behind are observable.
    [TestFixture]
    public class OtherExtraFileRenamerDuplicateRowsFixture : DbTest<OtherExtraFileRenamer, OtherExtraFile>
    {
        private Movie _movie;
        private string _nfoPath;
        private string _nfoRelativePath;
        private string _origRelativePath;

        [SetUp]
        public void Setup()
        {
            _movie = Builder<Movie>.CreateNew()
                                   .With(s => s.Id = 1)
                                   .With(s => s.Path = @"C:\Test\Movies\Movie Title (2020)".AsOsAgnostic())
                                   .Build();

            _nfoRelativePath = "Movie Title (2020).nfo";
            _origRelativePath = _nfoRelativePath + "-orig";
            _nfoPath = Path.Combine(_movie.Path, _nfoRelativePath);

            Mocker.SetConstant<IExtraFileRepository<OtherExtraFile>>(Mocker.Resolve<OtherExtraFileRepository>());
            Mocker.SetConstant<IOtherExtraFileService>(Mocker.Resolve<OtherExtraFileService>());

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FileExists(It.IsAny<string>()))
                  .Returns(true);
        }

        private OtherExtraFile GivenExtraFile(int movieId, string relativePath, string extension)
        {
            var extraFile = Builder<OtherExtraFile>.CreateNew()
                                                   .With(f => f.MovieId = movieId)
                                                   .With(f => f.MovieFileId = 1)
                                                   .With(f => f.RelativePath = relativePath)
                                                   .With(f => f.Extension = extension)
                                                   .BuildNew();

            Db.Insert(extraFile);

            return extraFile;
        }

        [Test]
        public void should_leave_a_single_row_for_the_orig_path_when_an_orig_file_is_replaced()
        {
            GivenExtraFile(_movie.Id, _origRelativePath, ".nfo-orig");
            var nfo = GivenExtraFile(_movie.Id, _nfoRelativePath, ".nfo");

            Subject.RenameOtherExtraFile(_movie, _nfoPath);

            AllStoredModels.Should().HaveCount(1);
            AllStoredModels.Single().Id.Should().Be(nfo.Id);
            AllStoredModels.Single().RelativePath.Should().Be(_origRelativePath);

            Action lookup = () => Mocker.Resolve<IOtherExtraFileService>().FindByPath(_movie.Id, _origRelativePath);
            lookup.Should().NotThrow();
        }

        [Test]
        public void should_not_accumulate_rows_when_an_orig_file_is_replaced_repeatedly()
        {
            GivenExtraFile(_movie.Id, _origRelativePath, ".nfo-orig");

            for (var i = 0; i < 3; i++)
            {
                GivenExtraFile(_movie.Id, _nfoRelativePath, ".nfo");
                Subject.RenameOtherExtraFile(_movie, _nfoPath);
            }

            AllStoredModels.Where(f => f.MovieId == _movie.Id && f.RelativePath == _origRelativePath)
                           .Should().HaveCount(1);
            AllStoredModels.Should().HaveCount(1);
        }

        [Test]
        public void should_not_delete_rows_with_the_same_path_for_another_movie()
        {
            var otherMovie = GivenExtraFile(2, _origRelativePath, ".nfo-orig");
            GivenExtraFile(_movie.Id, _nfoRelativePath, ".nfo");

            Subject.RenameOtherExtraFile(_movie, _nfoPath);

            AllStoredModels.Should().HaveCount(2);
            AllStoredModels.Should().Contain(f => f.Id == otherMovie.Id && f.MovieId == 2 && f.RelativePath == _origRelativePath);
        }

        [Test]
        public void should_leave_a_single_row_for_the_orig_path_when_the_orig_file_is_missing()
        {
            // A tracked -orig row whose file was deleted from disk before the next rescan.
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FileExists(It.Is<string>(p => p.EndsWith("-orig"))))
                  .Returns(false);

            GivenExtraFile(_movie.Id, _origRelativePath, ".nfo-orig");
            var nfo = GivenExtraFile(_movie.Id, _nfoRelativePath, ".nfo");

            Subject.RenameOtherExtraFile(_movie, _nfoPath);

            AllStoredModels.Should().HaveCount(1);
            AllStoredModels.Single().Id.Should().Be(nfo.Id);
            AllStoredModels.Single().RelativePath.Should().Be(_origRelativePath);

            Action lookup = () => Mocker.Resolve<IOtherExtraFileService>().FindByPath(_movie.Id, _origRelativePath);
            lookup.Should().NotThrow();
        }
    }
}
