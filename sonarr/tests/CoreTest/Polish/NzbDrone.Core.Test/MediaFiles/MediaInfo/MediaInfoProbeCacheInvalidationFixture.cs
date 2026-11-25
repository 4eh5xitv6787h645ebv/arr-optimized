using System;
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.MediaInfo
{
    // MediaInfoProbeCache.Invalidate and the generation check in Add (patched trees only).
    [TestFixture]
    [NonParallelizable]
    public class MediaInfoProbeCacheInvalidationFixture : TestBase
    {
        private static readonly DateTime SettledWrite = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        private static readonly DateTime ProbeStarted = SettledWrite.AddHours(1);

        private static MediaInfoModel Model(int width)
        {
            return new MediaInfoModel
            {
                Width = width,
                RawStreamData = "{}",
                AudioStreams = new List<MediaInfoAudioStreamModel>(),
                SubtitleStreams = new List<MediaInfoSubtitleStreamModel>()
            };
        }

        private static MediaInfoProbeCache.FileStamp Stamp()
        {
            return new MediaInfoProbeCache.FileStamp(1000, SettledWrite);
        }

        [Test]
        public void invalidate_should_drop_only_that_path()
        {
            var cache = new MediaInfoProbeCache();
            cache.Add("/tv/a.mkv", Stamp(), Model(1), ProbeStarted);
            cache.Add("/tv/b.mkv", Stamp(), Model(2), ProbeStarted);
            var bytes = cache.Bytes;

            cache.Invalidate("/tv/a.mkv");

            cache.TryGet("/tv/a.mkv", Stamp(), out _).Should().BeFalse();
            cache.TryGet("/tv/b.mkv", Stamp(), out var b).Should().BeTrue();
            b.Width.Should().Be(2);
            cache.Count.Should().Be(1);
            cache.Bytes.Should().Be(bytes - MediaInfoProbeCache.EstimateSize(Model(1)));
        }

        [TestCase("/tv/show//a.mkv")]
        [TestCase("/tv/show/a.mkv/")]
        public void invalidate_should_find_a_differently_written_path(string written)
        {
            var cache = new MediaInfoProbeCache();
            cache.Add("/tv/show/a.mkv", Stamp(), Model(1), ProbeStarted);

            cache.Invalidate(written);

            cache.TryGet("/tv/show/a.mkv", Stamp(), out _).Should().BeFalse();
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void invalidate_without_a_path_should_do_nothing(string path)
        {
            var cache = new MediaInfoProbeCache();
            cache.Add("/tv/a.mkv", Stamp(), Model(1), ProbeStarted);
            var generation = cache.Generation;

            cache.Invalidate(path);

            cache.TryGet("/tv/a.mkv", Stamp(), out _).Should().BeTrue();
            cache.Generation.Should().Be(generation);
        }

        [Test]
        public void probe_that_overlapped_an_invalidation_should_not_be_stored()
        {
            var cache = new MediaInfoProbeCache();

            // GetMediaInfo reads the generation before it starts ffprobe; the app replaces the file meanwhile.
            var generation = cache.Generation;
            cache.Invalidate("/tv/a.mkv");
            cache.Add("/tv/a.mkv", Stamp(), Model(1), ProbeStarted, generation);

            cache.TryGet("/tv/a.mkv", Stamp(), out _).Should().BeFalse();
            cache.Count.Should().Be(0);
            cache.Bytes.Should().Be(0);
        }

        [Test]
        public void probe_with_the_current_generation_should_be_stored()
        {
            var cache = new MediaInfoProbeCache();
            cache.Invalidate("/tv/other.mkv");

            cache.Add("/tv/a.mkv", Stamp(), Model(1), ProbeStarted, cache.Generation);

            cache.TryGet("/tv/a.mkv", Stamp(), out _).Should().BeTrue();
        }
    }
}
