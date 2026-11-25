using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;
using NzbDrone.Test.Common.Categories;

namespace NzbDrone.Core.Test.MediaFiles.MediaInfo
{
    // Behaviour of the ffprobe result cache in VideoFileInfoReader, seen only through GetMediaInfo, so the fixture
    // also compiles against an unpatched tree. There the tests that expect a cache hit fail (every call probes).
    // The test clock of the cache is resolved by name and only used when it exists.
    [TestFixture]
    [DiskAccessTest]
    [NonParallelizable]
    public class VideoFileInfoReaderCacheFixture : CoreTest<VideoFileInfoReader>
    {
        private const string CacheTypeName = "NzbDrone.Core.MediaFiles.MediaInfo.MediaInfoProbeCache";

        private static readonly DateTime SettledWrite = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        private long? _fixedLength;
        private DateTime? _fixedLastWrite;
        private long _clockOffset;
        private object _originalClock;
        private object _originalUtcNow;
        private int _sizeReads;
        private Func<int, long> _lengthByRead;
        private Func<int, DateTime> _lastWriteByRead;

        [SetUp]
        public void Setup()
        {
            _fixedLength = null;
            _fixedLastWrite = null;
            _clockOffset = 0;
            _sizeReads = 0;
            _lengthByRead = null;
            _lastWriteByRead = null;

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FileExists(It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFileSize(It.IsAny<string>()))
                  .Returns<string>(p =>
                  {
                      // GetFileSize is read first, then FileGetLastWrite: _sizeReads numbers the stamp reads
                      _sizeReads++;

                      return _lengthByRead?.Invoke(_sizeReads) ?? _fixedLength ?? new FileInfo(p).Length;
                  });

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FileGetLastWrite(It.IsAny<string>()))
                  .Returns<string>(p => _lastWriteByRead?.Invoke(_sizeReads) ?? _fixedLastWrite ?? new FileInfo(p).LastWriteTimeUtc);

            var clock = ClockField;

            if (clock != null)
            {
                _originalClock = clock.GetValue(null);
                Func<long> testClock = () => Environment.TickCount64 + _clockOffset;
                clock.SetValue(null, testClock);
            }

            _originalUtcNow = UtcNowField?.GetValue(null);
        }

        [TearDown]
        public void TearDown()
        {
            ClockField?.SetValue(null, _originalClock);
            UtcNowField?.SetValue(null, _originalUtcNow);
        }

        private static FieldInfo ClockField => CacheField("Clock");

        private static FieldInfo UtcNowField => CacheField("UtcNow");

        private static FieldInfo CacheField(string name) => typeof(VideoFileInfoReader).Assembly
            .GetType(CacheTypeName)?
            .GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        private void AdvanceClock(TimeSpan by)
        {
            if (ClockField == null)
            {
                Assert.Fail("No media info cache clock in this build");
            }

            _clockOffset += (long)by.TotalMilliseconds;
        }

        private static string SamplePath(string name)
        {
            return Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "Media", name);
        }

        private string CopySample(string name, string targetName = null)
        {
            var folder = GetTempFilePath();
            Directory.CreateDirectory(folder);

            var path = Path.Combine(folder, targetName ?? name);
            File.Copy(SamplePath(name), path);
            File.SetLastWriteTimeUtc(path, SettledWrite);

            return path;
        }

        private static void ReplaceContent(string path, string sampleName)
        {
            File.Copy(SamplePath(sampleName), path, true);
        }

        private void UseFixedStamp(long length, DateTime lastWriteUtc)
        {
            _fixedLength = length;
            _fixedLastWrite = lastWriteUtc;
        }

        private MediaInfoModel FreshProbe(string path)
        {
            return new VideoFileInfoReader(Mocker.GetMock<IDiskProvider>().Object, TestLogger).GetMediaInfo(path);
        }

        [TestCase("H264_sample.mp4")]
        [TestCase("ffprobe_cache_h264.mkv")]
        [TestCase("ffprobe_cache_mpeg2.ts")]
        [TestCase("ffprobe_cache_hevc_pq.mp4")]
        [TestCase("ffprobe_cache_noaudio.mkv")]
        public void cached_media_info_should_equal_a_fresh_probe(string sample)
        {
            var path = CopySample(sample);

            var first = Subject.GetMediaInfo(path);
            var second = Subject.GetMediaInfo(path);
            var fresh = FreshProbe(path);

            fresh.Should().NotBeNull();
            fresh.RawStreamData.Should().NotBeNullOrWhiteSpace();
            first.Should().BeEquivalentTo(fresh, o => o.RespectingRuntimeTypes());
            second.Should().BeEquivalentTo(fresh, o => o.RespectingRuntimeTypes());
            second.Title.Should().Be(fresh.Title);
            second.RawStreamData.Should().Be(fresh.RawStreamData);
            second.Should().NotBeSameAs(first);
        }

        [Test]
        public void cached_media_info_should_keep_hdr_and_stream_details()
        {
            var hdr = CopySample("ffprobe_cache_hevc_pq.mp4");
            var mkv = CopySample("ffprobe_cache_h264.mkv");

            Subject.GetMediaInfo(hdr);
            Subject.GetMediaInfo(mkv);

            var hdrInfo = Subject.GetMediaInfo(hdr);
            var mkvInfo = Subject.GetMediaInfo(mkv);

            hdrInfo.VideoHdrFormat.Should().Be(HdrFormat.Hdr10);
            hdrInfo.VideoBitDepth.Should().Be(10);

            mkvInfo.Title.Should().Be("Cache Sample");
            mkvInfo.AudioStreams.Select(a => a.Language).Should().Equal("eng", "ger");
            mkvInfo.AudioStreams[0].Title.Should().Be("Stereo Commentary");
            mkvInfo.AudioStreams[1].Channels.Should().Be(6);
            mkvInfo.SubtitleStreams.Should().ContainSingle();
            mkvInfo.SubtitleStreams[0].Forced.Should().BeTrue();
            mkvInfo.SubtitleStreams[0].HearingImpaired.Should().BeTrue();
        }

        [Test]
        public void returned_media_info_should_not_share_state_with_the_cache()
        {
            var path = CopySample("ffprobe_cache_h264.mkv");
            var fresh = FreshProbe(path);

            var first = Subject.GetMediaInfo(path);
            first.Width = 1;
            first.Title = "changed";
            first.AudioStreams[0].Language = "xxx";
            first.AudioStreams.RemoveAt(1);
            first.SubtitleStreams[0].Forced = false;

            var second = Subject.GetMediaInfo(path);
            second.Should().BeEquivalentTo(fresh, o => o.RespectingRuntimeTypes());

            second.AudioStreams[0].Language = "yyy";
            second.SubtitleStreams.Clear();

            var third = Subject.GetMediaInfo(path);
            third.Should().BeEquivalentTo(fresh, o => o.RespectingRuntimeTypes());
        }

        [Test]
        public void unchanged_file_should_not_be_probed_again()
        {
            var path = CopySample("ffprobe_cache_h264.mkv", "episode.mkv");
            UseFixedStamp(1000, SettledWrite);

            Subject.GetMediaInfo(path).ContainerFormat.Should().Contain("matroska");

            // Different bytes, same path, size and last write time: only a cache can still answer "matroska"
            ReplaceContent(path, "ffprobe_cache_mpeg2.ts");

            Subject.GetMediaInfo(path).ContainerFormat.Should().Contain("matroska");
            Subject.GetRunTime(path).Should().Be(FreshProbeOf("ffprobe_cache_h264.mkv").RunTime);
        }

        private MediaInfoModel FreshProbeOf(string sample)
        {
            var saved = (_fixedLength, _fixedLastWrite);
            _fixedLength = null;
            _fixedLastWrite = null;

            var info = FreshProbe(CopySample(sample));

            (_fixedLength, _fixedLastWrite) = saved;

            return info;
        }

        [Test]
        public void size_change_should_probe_again()
        {
            var path = CopySample("ffprobe_cache_h264.mkv", "episode.mkv");
            UseFixedStamp(1000, SettledWrite);

            Subject.GetMediaInfo(path).ContainerFormat.Should().Contain("matroska");

            ReplaceContent(path, "ffprobe_cache_mpeg2.ts");
            UseFixedStamp(1001, SettledWrite);

            Subject.GetMediaInfo(path).ContainerFormat.Should().Be("mpegts");
        }

        [TestCase(1)]
        [TestCase(10000000)]
        public void last_write_change_should_probe_again(long ticks)
        {
            var path = CopySample("ffprobe_cache_h264.mkv", "episode.mkv");
            UseFixedStamp(1000, SettledWrite);

            Subject.GetMediaInfo(path).ContainerFormat.Should().Contain("matroska");

            ReplaceContent(path, "ffprobe_cache_mpeg2.ts");
            UseFixedStamp(1000, SettledWrite.AddTicks(ticks));

            Subject.GetMediaInfo(path).ContainerFormat.Should().Be("mpegts");
        }

        [Test]
        public void real_file_rewrite_should_probe_again()
        {
            var path = CopySample("ffprobe_cache_h264.mkv", "episode.mkv");

            Subject.GetMediaInfo(path).ContainerFormat.Should().Contain("matroska");

            ReplaceContent(path, "ffprobe_cache_mpeg2.ts");

            Subject.GetMediaInfo(path).ContainerFormat.Should().Be("mpegts");
        }

        [Test]
        public void other_path_with_same_size_and_last_write_should_be_probed()
        {
            var mkv = CopySample("ffprobe_cache_h264.mkv", "a.mkv");
            var ts = CopySample("ffprobe_cache_mpeg2.ts", "a.ts");
            UseFixedStamp(1000, SettledWrite);

            Subject.GetMediaInfo(mkv).ContainerFormat.Should().Contain("matroska");
            Subject.GetMediaInfo(ts).ContainerFormat.Should().Be("mpegts");
            Subject.GetMediaInfo(mkv).ContainerFormat.Should().Contain("matroska");
        }

        [Test]
        public void failed_probe_of_recently_written_file_should_not_be_remembered()
        {
            var path = CopySample("ffprobe_cache_h264.mkv", "episode.mkv");
            File.WriteAllBytes(path, new byte[4096]);
            UseFixedStamp(1000, DateTime.UtcNow.AddSeconds(-5));

            Subject.GetMediaInfo(path).Should().BeNull();

            ReplaceContent(path, "ffprobe_cache_h264.mkv");

            Subject.GetMediaInfo(path).ContainerFormat.Should().Contain("matroska");

            ExceptionVerification.ExpectedErrors(1);
        }

        [Test]
        public void failed_probe_of_settled_file_should_be_remembered_until_failure_lifetime()
        {
            var path = CopySample("ffprobe_cache_h264.mkv", "episode.mkv");
            File.WriteAllBytes(path, new byte[4096]);
            UseFixedStamp(1000, SettledWrite);

            Subject.GetMediaInfo(path).Should().BeNull();

            ReplaceContent(path, "ffprobe_cache_h264.mkv");

            Subject.GetMediaInfo(path).Should().BeNull("a failed probe of an unchanged, settled file is not repeated at once");

            AdvanceClock(TimeSpan.FromMinutes(9));
            Subject.GetMediaInfo(path).Should().BeNull();

            AdvanceClock(TimeSpan.FromMinutes(1));
            Subject.GetMediaInfo(path).ContainerFormat.Should().Contain("matroska");

            ExceptionVerification.ExpectedErrors(1);
        }

        [Test]
        public void success_of_recently_written_file_should_be_reused_only_briefly()
        {
            var path = CopySample("ffprobe_cache_h264.mkv", "episode.mkv");
            UseFixedStamp(1000, DateTime.UtcNow.AddSeconds(-5));

            Subject.GetMediaInfo(path).ContainerFormat.Should().Contain("matroska");

            ReplaceContent(path, "ffprobe_cache_mpeg2.ts");

            Subject.GetMediaInfo(path).ContainerFormat.Should().Contain("matroska", "the same import pass reuses the probe");

            AdvanceClock(TimeSpan.FromSeconds(61));

            Subject.GetMediaInfo(path).ContainerFormat.Should().Be("mpegts");
        }

        [Test]
        public void settled_success_should_be_kept_while_used_and_dropped_when_idle()
        {
            var path = CopySample("ffprobe_cache_h264.mkv", "episode.mkv");
            UseFixedStamp(1000, SettledWrite);

            Subject.GetMediaInfo(path).ContainerFormat.Should().Contain("matroska");

            ReplaceContent(path, "ffprobe_cache_mpeg2.ts");

            Subject.GetMediaInfo(path).ContainerFormat.Should().Contain("matroska");

            AdvanceClock(TimeSpan.FromHours(5));
            Subject.GetMediaInfo(path).ContainerFormat.Should().Contain("matroska");

            AdvanceClock(TimeSpan.FromHours(5));
            Subject.GetMediaInfo(path).ContainerFormat.Should().Contain("matroska");

            AdvanceClock(TimeSpan.FromHours(6));
            Subject.GetMediaInfo(path).ContainerFormat.Should().Be("mpegts");
        }

        // The stamp read after the probe differs from the one read before it: the file changed while ffprobe
        // read it, so the result must not be remembered under the earlier stamp.
        [TestCase(true)]
        [TestCase(false)]
        public void file_that_changes_during_the_probe_should_not_be_cached(bool sizeChanges)
        {
            var path = CopySample("ffprobe_cache_h264.mkv", "episode.mkv");
            UseFixedStamp(1000, SettledWrite);

            if (sizeChanges)
            {
                _lengthByRead = read => read == 1 ? 1000 : 1001;
            }
            else
            {
                _lastWriteByRead = read => read == 1 ? SettledWrite : SettledWrite.AddSeconds(1);
            }

            Subject.GetMediaInfo(path).ContainerFormat.Should().Contain("matroska");

            _lengthByRead = null;
            _lastWriteByRead = null;
            ReplaceContent(path, "ffprobe_cache_mpeg2.ts");

            Subject.GetMediaInfo(path).ContainerFormat.Should().Be("mpegts");
        }

        [Test]
        public void paths_differing_only_in_case_should_not_share_an_entry()
        {
            var upper = CopySample("ffprobe_cache_h264.mkv", "Episode.mkv");
            var lower = Path.Combine(Path.GetDirectoryName(upper), "episode.mkv");

            if (File.Exists(lower))
            {
                Assert.Ignore("Case-insensitive file system");
            }

            File.Copy(SamplePath("ffprobe_cache_mpeg2.ts"), lower);
            UseFixedStamp(1000, SettledWrite);

            Subject.GetMediaInfo(upper).ContainerFormat.Should().Contain("matroska");
            Subject.GetMediaInfo(lower).ContainerFormat.Should().Be("mpegts");
        }

        // The "recently written" rule is judged when the probe starts, not when it ends: a probe that takes long
        // (slow mount, timeout) must not turn a file written just before it into a settled one.
        [Test]
        public void failed_probe_of_file_written_just_before_a_slow_probe_should_not_be_remembered()
        {
            var path = CopySample("ffprobe_cache_h264.mkv", "episode.mkv");
            File.WriteAllBytes(path, new byte[4096]);
            UseFixedStamp(1000, SettledWrite);

            // Before the probe (one stamp read so far) the file is 10 s old; after it (second read) 20 minutes
            Func<DateTime> utcNow = () => _sizeReads < 2 ? SettledWrite.AddSeconds(10) : SettledWrite.AddMinutes(20);
            UtcNowField?.SetValue(null, utcNow);

            Subject.GetMediaInfo(path).Should().BeNull();

            ReplaceContent(path, "ffprobe_cache_h264.mkv");
            _sizeReads = 0;

            Subject.GetMediaInfo(path).ContainerFormat.Should().Contain("matroska");

            ExceptionVerification.ExpectedErrors(1);
        }

        // Symlinked libraries (debrid/rclone tools, import scripts). The real DiskProviderBase is used: its
        // GetFileSize follows the link, its FileGetLastWrite returns the link's own time.
        private VideoFileInfoReader RealDiskReader()
        {
            return new VideoFileInfoReader(new Mock<DiskProviderBase> { CallBase = true }.Object, TestLogger);
        }

        private static void Run(string file, params string[] args)
        {
            using var process = Process.Start(file, args);
            process.WaitForExit();
            process.ExitCode.Should().Be(0);
        }

        private static long PaddedSize => Math.Max(new FileInfo(SamplePath("ffprobe_cache_h264.mkv")).Length, new FileInfo(SamplePath("ffprobe_cache_mpeg2.ts")).Length) + 4096;

        private static void WritePadded(string path, string sample)
        {
            var result = new byte[PaddedSize];

            if (sample != null)
            {
                var bytes = File.ReadAllBytes(SamplePath(sample));
                Array.Copy(bytes, result, bytes.Length);
            }

            File.WriteAllBytes(path, result);
        }

        private (string Target, string Link) CreateLinkedFile(string sample)
        {
            var folder = GetTempFilePath();
            Directory.CreateDirectory(folder);

            var target = Path.Combine(folder, "target.mkv");
            var link = Path.Combine(folder, "episode.mkv");

            WritePadded(target, sample);
            File.CreateSymbolicLink(link, target);

            // The link itself was created long ago
            Run("touch", "-h", "-d", "2024-01-02T03:04:05", link);

            return (target, link);
        }

        [Test]
        [Platform(Exclude = "Win")]
        public void symlink_target_rewritten_with_same_size_should_probe_again()
        {
            var (target, link) = CreateLinkedFile("ffprobe_cache_h264.mkv");
            File.SetLastWriteTimeUtc(target, SettledWrite);
            var reader = RealDiskReader();

            reader.GetMediaInfo(link).ContainerFormat.Should().Contain("matroska");

            // Target rewritten in place with the same size: its time changes, the link's does not
            WritePadded(target, "ffprobe_cache_mpeg2.ts");

            reader.GetMediaInfo(link).ContainerFormat.Should().Be("mpegts");
        }

        [Test]
        [Platform(Exclude = "Win")]
        public void failed_probe_of_recently_written_symlink_target_should_not_be_remembered()
        {
            // Preallocated (all zero) target written just now, behind a link created long ago
            var (target, link) = CreateLinkedFile(null);
            var targetWrite = File.GetLastWriteTimeUtc(target);
            var reader = RealDiskReader();

            reader.GetMediaInfo(link).Should().BeNull();

            // Completed with the same size; even with the same target time the failure must not have been kept
            WritePadded(target, "ffprobe_cache_h264.mkv");
            File.SetLastWriteTimeUtc(target, targetWrite);

            reader.GetMediaInfo(link).ContainerFormat.Should().Contain("matroska");

            ExceptionVerification.ExpectedErrors(1);
        }

        // Regression (integration review): episode.mkv -> mid.mkv -> a.mkv, then mid.mkv is re-pointed at b.ts, which
        // has the same size and time as a.mkv. The first link and both targets look unchanged; only the final target's
        // path differs.
        [Test]
        [Platform(Exclude = "Win")]
        public void chain_middle_link_retargeted_should_probe_again()
        {
            var folder = GetTempFilePath();
            Directory.CreateDirectory(folder);

            var a = Path.Combine(folder, "a.mkv");
            var b = Path.Combine(folder, "b.ts");
            var mid = Path.Combine(folder, "mid.mkv");
            var link = Path.Combine(folder, "episode.mkv");

            WritePadded(a, "ffprobe_cache_h264.mkv");
            WritePadded(b, "ffprobe_cache_mpeg2.ts");
            File.SetLastWriteTimeUtc(a, SettledWrite);
            File.SetLastWriteTimeUtc(b, SettledWrite);
            Run("ln", "-s", a, mid);
            Run("ln", "-s", mid, link);
            Run("touch", "-h", "-d", "2024-01-02T03:04:05", link);
            var reader = RealDiskReader();

            reader.GetMediaInfo(link).ContainerFormat.Should().Contain("matroska");

            Run("ln", "-sfn", b, mid);

            reader.GetMediaInfo(link).ContainerFormat.Should().Be("mpegts");
        }

        [Test]
        [Platform(Exclude = "Win")]
        public void unchanged_symlinked_file_should_not_be_probed_again()
        {
            var (target, link) = CreateLinkedFile("ffprobe_cache_h264.mkv");
            File.SetLastWriteTimeUtc(target, SettledWrite);
            var reader = RealDiskReader();

            reader.GetMediaInfo(link).ContainerFormat.Should().Contain("matroska");

            // Different bytes behind the same link with the same size and the same time: only a cache answers "matroska"
            WritePadded(target, "ffprobe_cache_mpeg2.ts");
            File.SetLastWriteTimeUtc(target, SettledWrite);

            reader.GetMediaInfo(link).ContainerFormat.Should().Contain("matroska");
        }

        [Test]
        public void file_that_cannot_be_stat_ed_should_be_probed_without_cache()
        {
            var path = CopySample("ffprobe_cache_h264.mkv", "episode.mkv");

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFileSize(It.IsAny<string>()))
                  .Throws(new IOException("stat failed"));

            Subject.GetMediaInfo(path).ContainerFormat.Should().Contain("matroska");

            ReplaceContent(path, "ffprobe_cache_mpeg2.ts");

            Subject.GetMediaInfo(path).ContainerFormat.Should().Be("mpegts");
        }

        [Test]
        public void disc_and_streaming_extensions_should_still_return_null()
        {
            var path = CopySample("ffprobe_cache_h264.mkv", "movie.iso");

            Subject.GetMediaInfo(path).Should().BeNull();
            Subject.GetMediaInfo(path).Should().BeNull();
        }

        [Test]
        public void missing_file_should_still_throw()
        {
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FileExists(It.IsAny<string>()))
                  .Returns(false);

            Assert.Throws<FileNotFoundException>(() => Subject.GetMediaInfo("/missing/episode.mkv"));
        }
    }
}
