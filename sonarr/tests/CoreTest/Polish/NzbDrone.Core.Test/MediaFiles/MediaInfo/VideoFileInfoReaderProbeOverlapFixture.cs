using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common.Categories;

namespace NzbDrone.Core.Test.MediaFiles.MediaInfo
{
    // The app replaces a file while ffprobe is still reading the old one. The reader takes the cache generation before
    // ffprobe starts, so an invalidation that lands while ffprobe runs keeps the old result out of the cache. The path is
    // a FIFO: opening its write end blocks until ffprobe has opened it, so the event below is published during the probe.
    [TestFixture]
    [DiskAccessTest]
    [NonParallelizable]
    public class VideoFileInfoReaderProbeOverlapFixture : CoreTest<VideoFileInfoReader>
    {
        private static readonly DateTime AirDate = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        private string _folder;

        [SetUp]
        public void Setup()
        {
            if (OsInfo.IsWindows)
            {
                Assert.Ignore("Needs mkfifo");
            }

            _folder = Path.Combine(GetTempFilePath(), "Series Title");
            Directory.CreateDirectory(_folder);

            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileExists(It.IsAny<string>())).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.GetFileSize(It.IsAny<string>())).Returns(1000);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileGetLastWrite(It.IsAny<string>())).Returns(AirDate);
        }

        private static string SamplePath(string name)
        {
            return Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "Media", name);
        }

        private void Publish<TEvent>(TEvent message)
            where TEvent : class, IEvent
        {
            (Subject as IHandle<TEvent>)?.Handle(message);
        }

        [Test]
        public void invalidation_while_ffprobe_runs_should_keep_the_old_result_out_of_the_cache()
        {
            var relative = "Series Title - S01E01.ts";
            var path = Path.Combine(_folder, relative);

            using (var mkfifo = Process.Start("mkfifo", new[] { path }))
            {
                mkfifo.WaitForExit();
                mkfifo.ExitCode.Should().Be(0);
            }

            var probe = Task.Run(() => Subject.GetMediaInfo(path));

            try
            {
                using var writer = new FileStream(path, FileMode.Open, FileAccess.Write);

                // ffprobe has the old file open: the app deletes it (upgrade) and tells the reader.
                Publish(new EpisodeFileDeletedEvent(new EpisodeFile { Id = 5, SeriesId = 1, RelativePath = relative, Path = path }, DeleteMediaFileReason.Upgrade));

                using var sample = File.OpenRead(SamplePath("ffprobe_cache_mpeg2.ts"));
                sample.CopyTo(writer);
            }
            catch (IOException)
            {
                // ffprobe may close its end before it has read everything.
            }

            probe.Wait(TimeSpan.FromSeconds(60)).Should().BeTrue();
            probe.Result.VideoFormat.Should().Be("mpeg2video");

            // The new file at the same path, with the same pinned size and date.
            File.Delete(path);
            File.Copy(SamplePath("ffprobe_cache_h264.mkv"), path);
            File.SetLastWriteTimeUtc(path, AirDate);

            Subject.GetMediaInfo(path).VideoFormat.Should().Be("h264");
        }
    }
}
