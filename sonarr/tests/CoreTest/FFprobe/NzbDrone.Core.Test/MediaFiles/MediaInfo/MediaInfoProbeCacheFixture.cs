using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.MediaInfo
{
    // Mechanics of the internal MediaInfoProbeCache (patched trees only: the type does not exist in stock).
    [TestFixture]
    [NonParallelizable]
    public class MediaInfoProbeCacheFixture : TestBase
    {
        private static readonly DateTime SettledWrite = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        private Func<long> _originalClock;
        private long _now;

        [SetUp]
        public void Setup()
        {
            _originalClock = MediaInfoProbeCache.Clock;
            _now = 1_000_000;
            MediaInfoProbeCache.Clock = () => _now;
        }

        [TearDown]
        public void TearDown()
        {
            MediaInfoProbeCache.Clock = _originalClock;
        }

        private static MediaInfoModel Model(int width)
        {
            return new MediaInfoModel
            {
                Width = width,
                RawStreamData = "{}",
                AudioStreams = new List<MediaInfoAudioStreamModel> { new MediaInfoAudioStreamModel { Language = "eng", Channels = 2 } },
                SubtitleStreams = new List<MediaInfoSubtitleStreamModel> { new MediaInfoSubtitleStreamModel { Language = "eng", Forced = true } }
            };
        }

        [Test]
        public void should_never_hold_more_than_capacity_and_evict_least_recently_used()
        {
            var cache = new MediaInfoProbeCache();

            for (var i = 0; i < MediaInfoProbeCache.Capacity; i++)
            {
                _now++;
                cache.Add("/tv/" + i, 1, SettledWrite, Model(i));
            }

            // Touch the oldest entry, so it is the most recently used one
            _now++;
            cache.TryGet("/tv/0", 1, SettledWrite, out _).Should().BeTrue();

            _now++;
            cache.Add("/tv/new", 1, SettledWrite, Model(-1));

            cache.Count.Should().BeLessOrEqualTo(MediaInfoProbeCache.Capacity);
            cache.TryGet("/tv/0", 1, SettledWrite, out _).Should().BeTrue();
            cache.TryGet("/tv/new", 1, SettledWrite, out _).Should().BeTrue();
            cache.TryGet("/tv/1", 1, SettledWrite, out _).Should().BeFalse();
            cache.TryGet("/tv/" + (MediaInfoProbeCache.Capacity - 1), 1, SettledWrite, out var last).Should().BeTrue();
            last.Width.Should().Be(MediaInfoProbeCache.Capacity - 1);
        }

        [Test]
        public void sweep_should_drop_idle_and_expired_entries()
        {
            var cache = new MediaInfoProbeCache();

            cache.Add("/tv/ok", 1, SettledWrite, Model(1));
            cache.Add("/tv/failed", 1, SettledWrite, null);
            cache.Count.Should().Be(2);

            _now += MediaInfoProbeCache.FailureLifetimeMs;
            cache.Add("/tv/other", 1, SettledWrite, Model(2));
            cache.Count.Should().Be(2);

            _now += MediaInfoProbeCache.IdleLifetimeMs;
            cache.Add("/tv/last", 1, SettledWrite, Model(3));
            cache.Count.Should().Be(1);
        }

        [Test]
        public void failure_of_unsettled_file_should_not_be_stored()
        {
            var cache = new MediaInfoProbeCache();

            cache.Add("/tv/new", 1, DateTime.UtcNow.AddSeconds(-30), null);
            cache.Add("/tv/future", 1, DateTime.UtcNow.AddHours(1), null);

            cache.Count.Should().Be(0);
        }

        [Test]
        public void stored_model_and_returned_models_should_be_independent()
        {
            var cache = new MediaInfoProbeCache();
            var original = Model(7);

            cache.Add("/tv/a", 1, SettledWrite, original);
            original.Width = 0;
            original.AudioStreams[0].Language = "xxx";
            original.SubtitleStreams.Clear();

            cache.TryGet("/tv/a", 1, SettledWrite, out var first).Should().BeTrue();
            first.Width.Should().Be(7);
            first.AudioStreams[0].Language.Should().Be("eng");
            first.SubtitleStreams.Should().ContainSingle();

            first.AudioStreams[0].Channels = 99;

            cache.TryGet("/tv/a", 1, SettledWrite, out var second).Should().BeTrue();
            second.AudioStreams[0].Channels.Should().Be(2);
            second.AudioStreams[0].Should().NotBeSameAs(first.AudioStreams[0]);
        }

        [Test]
        public void counters_should_count_hits_and_misses()
        {
            var cache = new MediaInfoProbeCache();

            cache.TryGet("/tv/a", 1, SettledWrite, out _).Should().BeFalse();
            cache.Add("/tv/a", 1, SettledWrite, Model(1));
            cache.TryGet("/tv/a", 1, SettledWrite, out _).Should().BeTrue();
            cache.TryGet("/tv/a", 2, SettledWrite, out _).Should().BeFalse();

            cache.Hits.Should().Be(1);
            cache.Misses.Should().Be(2);
            cache.Stores.Should().Be(1);
        }

        // Rebase guard: Copy() shares every member except the two stream lists. If upstream adds a member that
        // holds mutable state (a list, an array, a class), get-only or not, public or not, Copy() must learn about
        // it. MemberwiseClone copies fields, so every instance field of the type and its base types is checked
        // (auto-properties included, through their backing fields).
        [TestCase(typeof(MediaInfoModel))]
        [TestCase(typeof(MediaInfoAudioStreamModel))]
        [TestCase(typeof(MediaInfoSubtitleStreamModel))]
        public void model_members_should_be_immutable_values_or_the_known_stream_lists(Type type)
        {
            var allowed = new[]
            {
                typeof(string), typeof(int), typeof(long), typeof(decimal), typeof(bool), typeof(bool?), typeof(TimeSpan), typeof(HdrFormat)
            };

            var fields = new List<FieldInfo>();

            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                fields.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
            }

            fields.Should().NotBeEmpty();

            var shared = fields
                .Where(f => !allowed.Contains(f.FieldType))
                .Select(f => f.Name)
                .ToList();

            if (type == typeof(MediaInfoModel))
            {
                shared.Should().BeEquivalentTo("<AudioStreams>k__BackingField", "<SubtitleStreams>k__BackingField");
            }
            else
            {
                shared.Should().BeEmpty();
            }
        }

        [Test]
        public void entries_should_be_bounded_by_estimated_memory()
        {
            var cache = new MediaInfoProbeCache();

            // ffprobe JSON of an mkv with many font attachments and chapters (about 110 KB, measured)
            var raw = new string('x', 111_520);

            for (var i = 0; i < MediaInfoProbeCache.Capacity; i++)
            {
                _now++;
                var model = Model(i);
                model.RawStreamData = raw;
                cache.Add("/anime/" + i, 1, SettledWrite, model);

                cache.Bytes.Should().BeLessOrEqualTo(MediaInfoProbeCache.MaxBytes);
            }

            cache.Count.Should().BeLessThan(MediaInfoProbeCache.Capacity);
            cache.Count.Should().BeGreaterThan(100);
            cache.Bytes.Should().Be(cache.Count * MediaInfoProbeCache.EstimateSize(new MediaInfoModel { RawStreamData = raw, AudioStreams = Model(0).AudioStreams, SubtitleStreams = Model(0).SubtitleStreams }));

            cache.TryGet("/anime/" + (MediaInfoProbeCache.Capacity - 1), 1, SettledWrite, out var last).Should().BeTrue();
            last.RawStreamData.Should().Be(raw);
            cache.TryGet("/anime/0", 1, SettledWrite, out _).Should().BeFalse();
        }

        [Test]
        public void huge_result_should_not_be_cached()
        {
            var cache = new MediaInfoProbeCache();
            var model = Model(1);
            model.RawStreamData = new string('x', (int)(MediaInfoProbeCache.MaxEntryBytes / 2) + 1);

            cache.Add("/tv/a", 1, SettledWrite, Model(2));
            cache.Add("/tv/a", 1, SettledWrite, model);

            cache.Count.Should().Be(0);
            cache.Bytes.Should().Be(0);
        }

        [Test]
        public void bytes_should_follow_replacements_and_removals()
        {
            var cache = new MediaInfoProbeCache();
            var size = MediaInfoProbeCache.EstimateSize(Model(1));

            cache.Add("/tv/a", 1, SettledWrite, Model(1));
            cache.Add("/tv/a", 1, SettledWrite, Model(1));
            cache.Add("/tv/b", 1, SettledWrite, Model(1));
            cache.Bytes.Should().Be(2 * size);

            // A lookup with another stamp drops the stale entry
            cache.TryGet("/tv/a", 2, SettledWrite, out _).Should().BeFalse();
            cache.Bytes.Should().Be(size);

            cache.Clear();
            cache.Bytes.Should().Be(0);
        }

        [Test]
        public void idle_entries_should_be_released_by_lookups_without_further_probes()
        {
            var cache = new MediaInfoProbeCache();

            for (var i = 0; i < 500; i++)
            {
                _now++;
                cache.Add("/tv/" + i, 1, SettledWrite, Model(i));
            }

            _now += MediaInfoProbeCache.IdleLifetimeMs + 1;

            cache.TryGet("/tv/other", 1, SettledWrite, out _).Should().BeFalse();

            cache.Count.Should().Be(0);
            cache.Bytes.Should().Be(0);
        }

        [Test]
        public void settle_rule_should_be_judged_when_the_probe_started()
        {
            var cache = new MediaInfoProbeCache();
            var stamp = new MediaInfoProbeCache.FileStamp(1, SettledWrite);

            // Probe started 10 s after the write and ended (much) later: still a recently written file
            cache.Add("/tv/failed", stamp, null, SettledWrite.AddSeconds(10));
            cache.Count.Should().Be(0);

            cache.Add("/tv/ok", stamp, Model(1), SettledWrite.AddSeconds(10));
            _now += MediaInfoProbeCache.UnsettledLifetimeMs;
            cache.TryGet("/tv/ok", stamp, out _).Should().BeFalse();

            cache.Add("/tv/failed", stamp, null, SettledWrite.AddSeconds(60));
            cache.TryGet("/tv/failed", stamp, out var failed).Should().BeTrue();
            failed.Should().BeNull();
        }

        [Test]
        public void symlink_target_time_should_be_part_of_the_stamp_and_the_settle_rule()
        {
            var cache = new MediaInfoProbeCache();
            var linkWrite = SettledWrite;
            var targetWrite = SettledWrite.AddDays(30);
            var stamp = new MediaInfoProbeCache.FileStamp(1, linkWrite, targetWrite);

            cache.Add("/tv/link", stamp, Model(1), targetWrite.AddMinutes(5));

            cache.TryGet("/tv/link", stamp, out _).Should().BeTrue();
            cache.TryGet("/tv/link", new MediaInfoProbeCache.FileStamp(1, linkWrite, targetWrite.AddTicks(1)), out _).Should().BeFalse();

            // Link old, target written 10 s before the probe: not settled, so a failure is not kept
            cache.Add("/tv/link2", new MediaInfoProbeCache.FileStamp(1, linkWrite, targetWrite), null, targetWrite.AddSeconds(10));
            cache.TryGet("/tv/link2", new MediaInfoProbeCache.FileStamp(1, linkWrite, targetWrite), out _).Should().BeFalse();
        }
    }
}
