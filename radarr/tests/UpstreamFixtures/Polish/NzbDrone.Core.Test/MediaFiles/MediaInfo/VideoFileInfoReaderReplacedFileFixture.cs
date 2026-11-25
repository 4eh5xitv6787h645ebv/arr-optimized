using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Movies;
using NzbDrone.Test.Common.Categories;

namespace NzbDrone.Core.Test.MediaFiles.MediaInfo
{
    // A file that the app replaces at the same library path must not get the media info of the file it replaced. With
    // "Change File Date" the new file gets the same last write time as the old one (the air date), so when the sizes
    // also match the file stamp alone does not tell them apart. Size and last write time are pinned here through the
    // disk provider mock, which is that worst case. The fixture reaches the event handlers through IHandle<T>, so it
    // compiles against the build without them too; there every test that expects a new probe gets the old file's info.
    [TestFixture]
    [DiskAccessTest]
    [NonParallelizable]
    public class VideoFileInfoReaderReplacedFileFixture : CoreTest<VideoFileInfoReader>
    {
        private static readonly DateTime AirDate = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        private string _movieFolder;

        [SetUp]
        public void Setup()
        {
            _movieFolder = Path.Combine(GetTempFilePath(), "Movie Title (2024)");
            Directory.CreateDirectory(_movieFolder);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FileExists(It.IsAny<string>()))
                  .Returns(true);

            // Same size and same last write time for every file: the worst case for the file stamp.
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFileSize(It.IsAny<string>()))
                  .Returns(1000);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FileGetLastWrite(It.IsAny<string>()))
                  .Returns(AirDate);
        }

        private static string SamplePath(string name)
        {
            return Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "Media", name);
        }

        private string RelativePath(string name = "Movie Title (2024).mkv")
        {
            return name;
        }

        private string PlaceSample(string sample, string relativePath)
        {
            var path = Path.Combine(_movieFolder, relativePath);

            // Overwrite in place with the pinned size and last write time: only the event tells the cache that the
            // file is new.
            File.Copy(SamplePath(sample), path, true);
            File.SetLastWriteTimeUtc(path, AirDate);

            return path;
        }

        private void Publish<TEvent>(TEvent message)
            where TEvent : class, IEvent
        {
            (Subject as IHandle<TEvent>)?.Handle(message);
        }

        private Movie Movie => new Movie { Id = 1, Path = _movieFolder };

        private MovieFile MovieFile(string relativePath, string fullPath = null)
        {
            return new MovieFile { Id = 5, MovieId = 1, RelativePath = relativePath, Path = fullPath };
        }

        [Test]
        public void import_of_a_new_file_at_the_same_path_should_probe_it_again()
        {
            var relative = RelativePath();
            var path = PlaceSample("ffprobe_cache_h264.mkv", relative);

            Subject.GetMediaInfo(path).VideoFormat.Should().Be("h264");

            // Upgrade: the old file goes, the new one is imported to the same path and gets the same date.
            PlaceSample("ffprobe_cache_mpeg2.ts", relative);
            Publish(new MovieFileImportedEvent(new LocalMovie { Movie = Movie, Path = "/downloads/new.ts" }, MovieFile(relative), new List<DeletedMovieFile>(), true, null));

            Subject.GetMediaInfo(path).VideoFormat.Should().Be("mpeg2video");
        }

        [Test]
        public void deleting_the_file_should_drop_its_media_info()
        {
            var relative = RelativePath();
            var path = PlaceSample("ffprobe_cache_h264.mkv", relative);

            Subject.GetMediaInfo(path).VideoFormat.Should().Be("h264");

            // MediaFileService.Delete sets Path to the full path before it publishes the event.
            Publish(new MovieFileDeletedEvent(MovieFile(relative, path), DeleteMediaFileReason.Upgrade));
            PlaceSample("ffprobe_cache_mpeg2.ts", relative);

            Subject.GetMediaInfo(path).VideoFormat.Should().Be("mpeg2video");
        }

        [Test]
        public void renaming_should_drop_media_info_of_both_paths()
        {
            var oldRelative = RelativePath("old name.mkv");
            var newRelative = RelativePath("new name.mkv");
            var oldPath = PlaceSample("ffprobe_cache_h264.mkv", oldRelative);
            var newPath = PlaceSample("ffprobe_cache_noaudio.mkv", newRelative);

            Subject.GetMediaInfo(oldPath).VideoFormat.Should().Be("h264");
            Subject.GetMediaInfo(newPath).AudioStreams.Should().BeEmpty();

            // The file at the new name is replaced by the renamed one; a different file then appears at the old name.
            File.Delete(newPath);
            File.Move(oldPath, newPath);
            File.SetLastWriteTimeUtc(newPath, AirDate);
            Publish(new MovieFileRenamedEvent(Movie, MovieFile(newRelative), oldPath));
            PlaceSample("ffprobe_cache_mpeg2.ts", oldRelative);

            Subject.GetMediaInfo(newPath).VideoFormat.Should().Be("h264");
            Subject.GetMediaInfo(newPath).AudioStreams.Should().NotBeEmpty();
            Subject.GetMediaInfo(oldPath).VideoFormat.Should().Be("mpeg2video");
        }

        [Test]
        public void events_for_other_paths_should_keep_cached_media_info()
        {
            var relative = RelativePath();
            var other = RelativePath("Other Movie (2024).mkv");
            var path = PlaceSample("ffprobe_cache_h264.mkv", relative);
            var otherPath = PlaceSample("ffprobe_cache_h264.mkv", other);

            Subject.GetMediaInfo(path);
            Subject.GetMediaInfo(otherPath);

            // Changed in place without the app knowing (same size, same time): the cached result is still used,
            // which shows that the entry for this path survived the events for the other path.
            PlaceSample("ffprobe_cache_mpeg2.ts", relative);
            Publish(new MovieFileImportedEvent(new LocalMovie { Movie = Movie }, MovieFile(other), new List<DeletedMovieFile>(), true, null));
            Publish(new MovieFileDeletedEvent(MovieFile(other, otherPath), DeleteMediaFileReason.Manual));

            Subject.GetMediaInfo(path).VideoFormat.Should().Be("h264");
        }

        [Test]
        public void event_with_a_differently_written_path_should_drop_the_media_info()
        {
            var relative = RelativePath();
            var path = PlaceSample("ffprobe_cache_h264.mkv", relative);

            Subject.GetMediaInfo(path).VideoFormat.Should().Be("h264");

            PlaceSample("ffprobe_cache_mpeg2.ts", relative);
            Publish(new MovieFileDeletedEvent(MovieFile(relative, path.Replace("/Movie Title (2024)/", "/Movie Title (2024)//")), DeleteMediaFileReason.Manual));

            Subject.GetMediaInfo(path).VideoFormat.Should().Be("mpeg2video");
        }

        [Test]
        public void events_without_paths_should_be_ignored()
        {
            var path = PlaceSample("ffprobe_cache_h264.mkv", RelativePath());

            Subject.GetMediaInfo(path);

            Publish(new MovieFileImportedEvent(new LocalMovie(), new MovieFile(), new List<DeletedMovieFile>(), true, null));
            Publish(new MovieFileDeletedEvent(new MovieFile(), DeleteMediaFileReason.Manual));
            Publish(new MovieFileRenamedEvent(new Movie(), new MovieFile(), null));

            Subject.GetMediaInfo(path).VideoFormat.Should().Be("h264");
        }

        [Test]
        public void probe_of_a_file_replaced_while_it_was_probed_should_not_be_cached()
        {
            var relative = RelativePath();
            var path = PlaceSample("ffprobe_cache_h264.mkv", relative);
            var sizeReads = 0;

            // The stamp is read before and after ffprobe runs. Replace the file (and tell the reader) between the two
            // reads, as an import running at the same time would: the old file's result must not be stored for the path.
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFileSize(It.IsAny<string>()))
                  .Returns<string>(p =>
                  {
                      if (++sizeReads == 2)
                      {
                          PlaceSample("ffprobe_cache_mpeg2.ts", relative);
                          Publish(new MovieFileDeletedEvent(MovieFile(relative, path), DeleteMediaFileReason.Upgrade));
                      }

                      return 1000;
                  });

            Subject.GetMediaInfo(path).VideoFormat.Should().Be("h264");

            Subject.GetMediaInfo(path).VideoFormat.Should().Be("mpeg2video");
        }

        [Test]
        public void unchanged_file_should_still_be_a_cache_hit()
        {
            var relative = RelativePath();
            var path = PlaceSample("ffprobe_cache_h264.mkv", relative);

            Subject.GetMediaInfo(path);

            // Only proves a hit if the content change below is not seen.
            PlaceSample("ffprobe_cache_mpeg2.ts", relative);

            Subject.GetMediaInfo(path).VideoFormat.Should().Be("h264");
        }
    }
}
