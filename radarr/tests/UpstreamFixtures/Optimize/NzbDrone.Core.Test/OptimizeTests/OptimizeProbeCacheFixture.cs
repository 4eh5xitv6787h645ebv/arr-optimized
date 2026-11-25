using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Optimize;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common.Categories;

namespace NzbDrone.Core.Test.OptimizeTests
{
    // The ffprobe result cache only caches: at every allowed limit a lookup returns the result stored for exactly
    // that path and file stamp, or misses; it never exceeds its limits; changed limits apply at once; and with the
    // cache off every read runs ffprobe, as on stock.
    [TestFixture]
    [NonParallelizable]
    public class OptimizeProbeCacheFixture : CoreTest<VideoFileInfoReader>
    {
        private static readonly DateTime SettledWrite = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        private Func<long> _originalClock;
        private long _now;

        [SetUp]
        public void Setup()
        {
            OptimizeSettings.Reset();
            _originalClock = MediaInfoProbeCache.Clock;
            _now = 1_000_000;
            MediaInfoProbeCache.Clock = () => Interlocked.Read(ref _now);
        }

        [TearDown]
        public void TearDown()
        {
            MediaInfoProbeCache.Clock = _originalClock;
            OptimizeSettings.Reset();
        }

        private static void Use(params (string Name, long Value)[] values)
        {
            var settings = OptimizeSettings.Current;

            foreach (var (name, value) in values)
            {
                settings = settings.With(name, value);
            }

            OptimizeSettings.Current = settings;
        }

        private static MediaInfoModel Model(int width, int rawLength = 2)
        {
            return new MediaInfoModel
            {
                Width = width,
                RawStreamData = new string('x', rawLength),
                AudioStreams = new List<MediaInfoAudioStreamModel> { new MediaInfoAudioStreamModel { Language = "eng", Channels = 2 } },
                SubtitleStreams = new List<MediaInfoSubtitleStreamModel>()
            };
        }

        [Test]
        [Pairwise]
        public void lookups_should_only_return_the_result_for_the_same_file_stamp(
            [Values(1, 1000, 100_000)] long entries,
            [Values(1, 32, 1024)] long megabytes,
            [Values(1, 4096, 131_072)] long entryKilobytes,
            [Values(0, 5, 10)] long failureMinutes,
            [Values(1, 360, 10_080)] long idleMinutes,
            [Values(60, 600, 3600)] long settleSeconds,
            [Values(0, 30, 60)] long unsettledSeconds,
            [Values(1, 60, 3600)] long sweepSeconds)
        {
            Use((OptimizeTunables.FfprobeCacheEntries, entries),
                (OptimizeTunables.FfprobeCacheMaxMegabytes, megabytes),
                (OptimizeTunables.FfprobeCacheMaxEntryKilobytes, entryKilobytes),
                (OptimizeTunables.FfprobeCacheFailureMinutes, failureMinutes),
                (OptimizeTunables.FfprobeCacheIdleMinutes, idleMinutes),
                (OptimizeTunables.FfprobeCacheSettleSeconds, settleSeconds),
                (OptimizeTunables.FfprobeCacheUnsettledSeconds, unsettledSeconds),
                (OptimizeTunables.FfprobeCacheSweepSeconds, sweepSeconds));

            var limits = OptimizeSettings.Current.FfprobeCache;
            var cache = new MediaInfoProbeCache();
            var random = new Random(42);
            var truth = new Dictionary<string, (long Length, int Width)>();

            for (var i = 0; i < 3000; i++)
            {
                _now += random.Next(0, 120_000);

                var path = "/movies/" + random.Next(0, 60);
                var length = random.Next(1, 4);

                if (random.Next(3) == 0)
                {
                    var width = i;
                    var model = random.Next(10) == 0 ? null : Model(width, random.Next(0, 3) == 0 ? 200_000 : 50);
                    var written = random.Next(4) == 0 ? DateTime.UtcNow : SettledWrite;

                    cache.Add(path, new MediaInfoProbeCache.FileStamp(length, written), model, DateTime.UtcNow);
                    truth[path + "|" + length + "|" + written.Ticks] = (length, model?.Width ?? -1);
                }
                else
                {
                    var written = SettledWrite;

                    if (cache.TryGet(path, new MediaInfoProbeCache.FileStamp(length, written), out var cached))
                    {
                        truth.TryGetValue(path + "|" + length + "|" + written.Ticks, out var expected).Should().BeTrue();
                        (cached?.Width ?? -1).Should().Be(expected.Width);
                    }
                }

                cache.Count.Should().BeLessOrEqualTo(limits.Capacity);
                cache.Bytes.Should().BeLessOrEqualTo(limits.MaxBytes);
            }
        }

        [Test]
        public void shrinking_should_trim_at_once()
        {
            var cache = new MediaInfoProbeCache();

            for (var i = 0; i < 500; i++)
            {
                _now++;
                cache.Add("/movies/" + i, 1, SettledWrite, Model(i));
            }

            cache.Count.Should().Be(500);

            Use((OptimizeTunables.FfprobeCacheEntries, 100));
            cache.ApplyLimits();

            cache.Count.Should().BeLessOrEqualTo(100);
            cache.TryGet("/movies/499", 1, SettledWrite, out var newest).Should().BeTrue();
            newest.Width.Should().Be(499);
            cache.TryGet("/movies/0", 1, SettledWrite, out _).Should().BeFalse();
        }

        [Test]
        public void smaller_entry_limit_should_drop_larger_results_at_once()
        {
            var cache = new MediaInfoProbeCache();
            cache.Add("/movies/small", 1, SettledWrite, Model(1, 100));
            cache.Add("/movies/large", 1, SettledWrite, Model(2, 100_000));

            Use((OptimizeTunables.FfprobeCacheMaxEntryKilobytes, 16));
            cache.ApplyLimits();

            cache.TryGet("/movies/small", 1, SettledWrite, out _).Should().BeTrue();
            cache.TryGet("/movies/large", 1, SettledWrite, out _).Should().BeFalse();
        }

        [TestCase(OptimizeTunables.FfprobeCacheEntries)]
        [TestCase(OptimizeTunables.FfprobeCacheMaxMegabytes)]
        public void disabling_should_drop_everything_and_store_nothing(string name)
        {
            var cache = new MediaInfoProbeCache();
            cache.Add("/movies/1", 1, SettledWrite, Model(1));

            Use((name, 0));
            cache.ApplyLimits();

            cache.Count.Should().Be(0);
            cache.Bytes.Should().Be(0);

            cache.Add("/movies/2", 1, SettledWrite, Model(2));
            cache.Count.Should().Be(0);
            cache.TryGet("/movies/2", 1, SettledWrite, out _).Should().BeFalse();
        }

        [Test]
        public void zero_failure_lifetime_should_not_remember_failures()
        {
            Use((OptimizeTunables.FfprobeCacheFailureMinutes, 0));

            var cache = new MediaInfoProbeCache();
            cache.Add("/movies/broken", 1, SettledWrite, null);

            cache.TryGet("/movies/broken", 1, SettledWrite, out _).Should().BeFalse();
        }

        [Test]
        public void failure_lifetime_should_follow_the_setting()
        {
            Use((OptimizeTunables.FfprobeCacheFailureMinutes, 2));

            var cache = new MediaInfoProbeCache();
            cache.Add("/movies/broken", 1, SettledWrite, null);

            _now += 119_000;
            cache.TryGet("/movies/broken", 1, SettledWrite, out var model).Should().BeTrue();
            model.Should().BeNull();

            _now += 1000;
            cache.TryGet("/movies/broken", 1, SettledWrite, out _).Should().BeFalse();
        }

        [Test]
        public void concurrent_lookups_while_limits_change_should_stay_consistent()
        {
            var cache = new MediaInfoProbeCache();
            var stop = 0;
            var failures = 0;

            var workers = Enumerable.Range(0, 4).Select(seed => Task.Run(() =>
            {
                var random = new Random(seed);

                while (Volatile.Read(ref stop) == 0)
                {
                    var id = random.Next(0, 300);
                    var path = "/movies/" + id;

                    if (random.Next(2) == 0)
                    {
                        cache.Add(path, id, SettledWrite, Model(id));
                    }
                    else if (cache.TryGet(path, id, SettledWrite, out var model) && model.Width != id)
                    {
                        Interlocked.Increment(ref failures);
                    }
                }
            })).ToArray();

            var sizes = new long[] { 0, 1, 50, 1000, 100_000 };

            for (var i = 0; i < 300; i++)
            {
                Use((OptimizeTunables.FfprobeCacheEntries, sizes[i % sizes.Length]));
                cache.ApplyLimits();
                Thread.Sleep(1);
            }

            Volatile.Write(ref stop, 1);
            Task.WaitAll(workers);

            failures.Should().Be(0);
            cache.Bytes.Should().BeGreaterOrEqualTo(0);
        }
    }

    // With the cache off, the reader runs ffprobe for every read, exactly like stock, and the results are the same.
    [TestFixture]
    [DiskAccessTest]
    [NonParallelizable]
    public class OptimizeProbeCacheReaderFixture : CoreTest<VideoFileInfoReader>
    {
        private string _path;

        [SetUp]
        public void Setup()
        {
            OptimizeSettings.Reset();

            var folder = GetTempFilePath();
            Directory.CreateDirectory(folder);
            _path = Path.Combine(folder, "H264_sample.mp4");
            File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "Media", "H264_sample.mp4"), _path);
            File.SetLastWriteTimeUtc(_path, new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));

            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileExists(It.IsAny<string>())).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.GetFileSize(It.IsAny<string>())).Returns<string>(p => new FileInfo(p).Length);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileGetLastWrite(It.IsAny<string>())).Returns<string>(p => new FileInfo(p).LastWriteTimeUtc);
        }

        [TearDown]
        public void TearDown()
        {
            OptimizeSettings.Reset();
        }

        [Test]
        public void cache_off_should_probe_every_time_with_the_same_result()
        {
            var cached = Subject.GetMediaInfo(_path);
            Subject.ProbeCache.Count.Should().Be(1);

            OptimizeSettings.Current = OptimizeSettings.Current.With(OptimizeTunables.FfprobeCacheEntries, 0L);
            Subject.ApplyCacheLimits();
            Subject.ProbeCache.Count.Should().Be(0);

            var misses = Subject.ProbeCache.Misses;
            var hits = Subject.ProbeCache.Hits;

            var first = Subject.GetMediaInfo(_path);
            var second = Subject.GetMediaInfo(_path);

            // Neither looked at the cache: both ran ffprobe.
            Subject.ProbeCache.Misses.Should().Be(misses);
            Subject.ProbeCache.Hits.Should().Be(hits);
            Subject.ProbeCache.Count.Should().Be(0);

            first.Should().BeEquivalentTo(cached);
            second.Should().BeEquivalentTo(cached);
        }

        [Test]
        public void zero_timeout_should_probe_without_a_timeout()
        {
            OptimizeSettings.Current = OptimizeSettings.Current.With(OptimizeTunables.FfprobeTimeoutMinutes, 0L);

            VideoFileInfoReader.ProbeTimeout.Should().Be(TimeSpan.Zero);

            var info = Subject.GetMediaInfo(_path);

            info.Should().NotBeNull();
            info.VideoFormat.Should().Be("h264");
        }

        [Test]
        public void default_timeout_should_be_fifteen_minutes()
        {
            VideoFileInfoReader.ProbeTimeout.Should().Be(TimeSpan.FromMinutes(15));
        }
    }
}
