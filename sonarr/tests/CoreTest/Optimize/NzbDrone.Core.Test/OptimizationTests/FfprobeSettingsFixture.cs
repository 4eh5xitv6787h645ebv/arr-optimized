using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Optimization;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.OptimizationTests
{
    // ffprobe cache and timeout settings: off means the stock path, every allowed value only caches (never another
    // file's result), and changed limits apply to the entries already cached.
    [TestFixture]
    [NonParallelizable]
    public class FfprobeSettingsFixture : CoreTest<VideoFileInfoReader>
    {
        private static readonly DateTime SettledWrite = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        private Func<long> _originalClock;
        private Func<DateTime> _originalUtcNow;
        private long _now;

        [SetUp]
        public void Setup()
        {
            OptimizeSettings.Current = null;
            _originalClock = MediaInfoProbeCache.Clock;
            _originalUtcNow = MediaInfoProbeCache.UtcNow;
            _now = 1_000_000;
            MediaInfoProbeCache.Clock = () => _now;
        }

        [TearDown]
        public void TearDown()
        {
            MediaInfoProbeCache.Clock = _originalClock;
            MediaInfoProbeCache.UtcNow = _originalUtcNow;
            OptimizeSettings.Current = null;
        }

        private static void Apply(Dictionary<string, object> values)
        {
            var settings = OptimizeSettings.Defaults.With(values);
            OptimizeSettings.Current = settings;
            MediaInfoProbeCache.ApplyAll(settings);
        }

        private static MediaInfoModel Model(int width, int rawChars = 2)
        {
            return new MediaInfoModel
            {
                Width = width,
                RawStreamData = new string('x', rawChars),
                AudioStreams = new List<MediaInfoAudioStreamModel> { new MediaInfoAudioStreamModel { Language = "eng", Channels = 2 } },
                SubtitleStreams = new List<MediaInfoSubtitleStreamModel>()
            };
        }

        [TestCase("FfprobeCacheCapacity")]
        [TestCase("FfprobeCacheMaxMb")]
        public void zero_should_turn_the_cache_off(string name)
        {
            Apply(new Dictionary<string, object> { { name, 0 } });
            var cache = new MediaInfoProbeCache();

            cache.Add("/tv/a.mkv", 1, SettledWrite, Model(1));

            cache.Count.Should().Be(0);
            cache.TryGet("/tv/a.mkv", 1, SettledWrite, out _).Should().BeFalse();
        }

        [Test]
        public void cache_off_should_probe_like_stock_without_reading_the_file_stamp()
        {
            Apply(new Dictionary<string, object> { { "FfprobeCacheCapacity", 0 } });
            var path = Path.Combine(TempFolder, "missing.mkv");

            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists(path)).Returns(true);

            Subject.GetMediaInfo(path).Should().BeNull();
            Subject.GetMediaInfo(path).Should().BeNull();

            Mocker.GetMock<IDiskProvider>().Verify(d => d.GetFileSize(It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IDiskProvider>().Verify(d => d.FileGetLastWrite(It.IsAny<string>()), Times.Never());
            ExceptionVerification.ExpectedErrors(2);
        }

        [Test]
        public void cache_on_should_read_the_file_stamp()
        {
            var path = Path.Combine(TempFolder, "missing.mkv");

            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileExists(path)).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.GetFileSize(path)).Returns(1);
            Mocker.GetMock<IDiskProvider>().Setup(d => d.FileGetLastWrite(path)).Returns(DateTime.UtcNow);

            Subject.GetMediaInfo(path).Should().BeNull();

            Mocker.GetMock<IDiskProvider>().Verify(d => d.GetFileSize(path), Times.AtLeastOnce());
            ExceptionVerification.ExpectedErrors(1);
        }

        [Test]
        public void timeout_should_follow_the_setting_and_zero_means_no_timeout()
        {
            VideoFileInfoReader.ProbeTimeout.Should().Be(TimeSpan.FromMinutes(15));

            Apply(new Dictionary<string, object> { { "FfprobeTimeoutMinutes", 1 } });
            VideoFileInfoReader.ProbeTimeout.Should().Be(TimeSpan.FromMinutes(1));

            Apply(new Dictionary<string, object> { { "FfprobeTimeoutMinutes", 0 } });
            VideoFileInfoReader.ProbeTimeout.Should().Be(TimeSpan.Zero);
        }

        // Smallest, default and largest values of every ffprobe cache setting.
        private static IEnumerable<TestCaseData> Limits()
        {
            yield return new TestCaseData(new Dictionary<string, object>
            {
                { "FfprobeCacheCapacity", 1 }, { "FfprobeCacheMaxMb", 1 }, { "FfprobeCacheMaxEntryKb", 1 }, { "FfprobeCacheIdleMinutes", 1 },
                { "FfprobeCacheFailureMinutes", 0 }, { "FfprobeCacheSettleSeconds", 60 }, { "FfprobeCacheUnsettledSeconds", 0 }, { "FfprobeCacheSweepSeconds", 1 }
            }).SetName("{m}(smallest)");

            // Smallest lifetimes with room for one result (the smallest largest-result value stores nothing).
            yield return new TestCaseData(new Dictionary<string, object>
            {
                { "FfprobeCacheCapacity", 1 }, { "FfprobeCacheMaxMb", 1 }, { "FfprobeCacheMaxEntryKb", 64 }, { "FfprobeCacheIdleMinutes", 1 },
                { "FfprobeCacheFailureMinutes", 0 }, { "FfprobeCacheSettleSeconds", 60 }, { "FfprobeCacheUnsettledSeconds", 0 }, { "FfprobeCacheSweepSeconds", 1 }
            }).SetName("{m}(smallest storing)");
            yield return new TestCaseData(new Dictionary<string, object>
            {
                { "FfprobeCacheCapacity", 5 }, { "FfprobeCacheFailureMinutes", 1 }, { "FfprobeCacheUnsettledSeconds", 1 }
            }).SetName("{m}(short lifetimes)");
            yield return new TestCaseData(new Dictionary<string, object>()).SetName("{m}(default)");
            yield return new TestCaseData(new Dictionary<string, object>
            {
                { "FfprobeCacheCapacity", 100000 }, { "FfprobeCacheMaxMb", 1024 }, { "FfprobeCacheMaxEntryKb", 131072 }, { "FfprobeCacheIdleMinutes", 10080 },
                { "FfprobeCacheFailureMinutes", 10 }, { "FfprobeCacheSettleSeconds", 3600 }, { "FfprobeCacheUnsettledSeconds", 60 }, { "FfprobeCacheSweepSeconds", 3600 }
            }).SetName("{m}(largest)");
            yield return new TestCaseData(new Dictionary<string, object>
            {
                { "FfprobeCacheCapacity", 100000 }, { "FfprobeCacheMaxMb", 1024 }, { "FfprobeCacheMaxEntryKb", 131072 }, { "FfprobeCacheIdleMinutes", 10080 },
                { "FfprobeCacheFailureMinutes", 10 }, { "FfprobeCacheSettleSeconds", 60 }, { "FfprobeCacheUnsettledSeconds", 60 }, { "FfprobeCacheSweepSeconds", 3600 }
            }).SetName("{m}(loosest)");

            var random = new Random(7);

            for (var i = 0; i < 6; i++)
            {
                yield return new TestCaseData(new Dictionary<string, object>
                {
                    { "FfprobeCacheCapacity", random.Next(1, 400) }, { "FfprobeCacheMaxMb", random.Next(1, 1025) }, { "FfprobeCacheMaxEntryKb", random.Next(1, 64) },
                    { "FfprobeCacheIdleMinutes", random.Next(1, 10081) }, { "FfprobeCacheFailureMinutes", random.Next(0, 11) },
                    { "FfprobeCacheSettleSeconds", random.Next(60, 3601) }, { "FfprobeCacheUnsettledSeconds", random.Next(0, 61) },
                    { "FfprobeCacheSweepSeconds", random.Next(1, 3601) }
                }).SetName("{m}(random " + i + ")");
            }
        }

        private sealed class SimulatedFile
        {
            public long Length;
            public DateTime LastWrite;
            public int Version;
        }

        private sealed class Stored
        {
            public int Version;
            public bool Failed;
            public long StoredAt;
            public DateTime ProbeStarted;
            public DateTime LastWrite;
        }

        // The cache matches on the file stamp only. Its premise (next3): a file can change under an unchanged stamp
        // only within the settle time (60 s) of its last write (coarse timestamps, attribute caches of network shares,
        // preallocated files). This simulation changes content under an unchanged stamp whenever that premise allows,
        // and checks every hit against what the content really is:
        // - a result for an older version of the file is only served within next3's own bounds (the probe started
        //   less than 60 s after the write, and it was stored less than 60 s ago);
        // - a remembered failure is only served for a file that was settled when probed (60 s after its write), and
        //   for less than next3's 10 minutes.
        // At every allowed setting; values that could break this (shorter settle time, longer unsettled or failure
        // lifetime) are rejected by validation (see bounds_should_only_allow_values_at_least_as_strict_as_next3).
        [TestCaseSource(nameof(Limits))]
        public void every_allowed_setting_should_serve_stale_results_no_longer_than_next3(Dictionary<string, object> values)
        {
            Apply(values);

            foreach (var name in values.Keys)
            {
                OptimizeSettings.Current.Resolved[name].Source.Should().Be(OptimizeSettingSource.Saved, name + " is an allowed value");
            }

            var epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            _now = 0;
            MediaInfoProbeCache.UtcNow = () => epoch.AddMilliseconds(_now);

            var cache = new MediaInfoProbeCache();
            var random = new Random(4242);
            var hazard = TimeSpan.FromSeconds(60);
            var files = Enumerable.Range(0, 25).Select(i => new SimulatedFile { Length = 1, LastWrite = epoch }).ToArray();
            var stored = new Dictionary<string, Stored>();
            var staleHits = 0;
            var hits = 0;

            // File system timestamps with a 2 s resolution.
            DateTime Stamp(long ms) => epoch.AddSeconds(Math.Floor(ms / 2000.0) * 2);

            for (var step = 0; step < 40000; step++)
            {
                _now += random.Next(0, 12000);
                var nowUtc = MediaInfoProbeCache.UtcNow();
                var index = random.Next(files.Length);
                var file = files[index];
                var path = "/tv/" + index + ".mkv";
                var action = random.Next(100);

                if (action < 8)
                {
                    // Rewritten: the size may stay the same and, within the timestamp resolution, so may the time.
                    file.LastWrite = Stamp(_now);
                    file.Length = random.Next(1, 3);
                    file.Version++;
                    continue;
                }

                if (action < 20)
                {
                    // Changed under an unchanged stamp, only while the premise allows it.
                    if (nowUtc - file.LastWrite < hazard)
                    {
                        file.Version++;
                    }

                    continue;
                }

                var stamp = new MediaInfoProbeCache.FileStamp(file.Length, file.LastWrite);

                if (cache.TryGet(path, stamp, out var found))
                {
                    hits++;
                    stored.Should().ContainKey(path);
                    var entry = stored[path];
                    entry.LastWrite.Should().Be(file.LastWrite);

                    if (found == null)
                    {
                        entry.Failed.Should().BeTrue();
                        (entry.ProbeStarted - entry.LastWrite).Should().BeGreaterOrEqualTo(hazard, "only failures of settled files are remembered");
                        (_now - entry.StoredAt).Should().BeLessThan(10 * 60_000, "a failure is remembered for at most next3's 10 minutes");
                    }
                    else
                    {
                        entry.Failed.Should().BeFalse();
                        found.Width.Should().Be(entry.Version, "a hit returns the last stored result of that path");

                        if (entry.Version != file.Version)
                        {
                            staleHits++;
                            (entry.ProbeStarted - entry.LastWrite).Should().BeLessThan(hazard, "settled results never go stale under the premise");
                            (_now - entry.StoredAt).Should().BeLessThan(60_000, "an unsettled result is reused for at most next3's 60 s");
                        }
                    }

                    continue;
                }

                // Probe: a failure now and then (a share that is briefly unavailable).
                var failed = random.Next(10) == 0;
                var model = failed ? null : Model(file.Version, random.Next(1, 1500));
                cache.Add(path, stamp, model, nowUtc);
                stored[path] = new Stored { Version = file.Version, Failed = failed, StoredAt = _now, ProbeStarted = nowUtc, LastWrite = file.LastWrite };

                var limits = OptimizeSettings.Current.ProbeCache;
                cache.Count.Should().BeLessOrEqualTo(limits.Capacity);
                cache.Bytes.Should().BeLessOrEqualTo(limits.MaxBytes);
            }

            TestContext.Progress.WriteLine("{0}: {1} hits, {2} within next3's unsettled window", TestContext.CurrentContext.Test.Name, hits, staleHits);
        }

        [TestCase("FfprobeCacheSettleSeconds", 59, false)]
        [TestCase("FfprobeCacheSettleSeconds", 60, true)]
        [TestCase("FfprobeCacheSettleSeconds", 3600, true)]
        [TestCase("FfprobeCacheUnsettledSeconds", 61, false)]
        [TestCase("FfprobeCacheUnsettledSeconds", 60, true)]
        [TestCase("FfprobeCacheUnsettledSeconds", 0, true)]
        [TestCase("FfprobeCacheFailureMinutes", 11, false)]
        [TestCase("FfprobeCacheFailureMinutes", 10, true)]
        [TestCase("FfprobeCacheFailureMinutes", 0, true)]
        public void bounds_should_only_allow_values_at_least_as_strict_as_next3(string name, int value, bool allowed)
        {
            var definition = OptimizeSettingDefinitions.Get(name);

            definition.TryParse(value.ToString(), false, out _, out _).Should().Be(allowed, "saved on the page");
            definition.TryParse(value.ToString(), true, out _, out _).Should().Be(allowed, "environment variable");
        }

        // The reviewer's case: a file written 6 s before the probe. Whatever the settle time, its result is reused for at
        // most the unsettled lifetime (60 s at most) and its failure is not remembered.
        [TestCase(60)]
        [TestCase(600)]
        [TestCase(3600)]
        public void recent_file_should_be_unsettled_at_every_allowed_settle_time(int settleSeconds)
        {
            Apply(new Dictionary<string, object> { { "FfprobeCacheSettleSeconds", settleSeconds } });
            var write = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            MediaInfoProbeCache.UtcNow = () => write.AddSeconds(6);
            var cache = new MediaInfoProbeCache();

            cache.Add("/tv/new.mkv", new MediaInfoProbeCache.FileStamp(1, write), Model(1), write.AddSeconds(6));
            cache.Add("/tv/failed.mkv", new MediaInfoProbeCache.FileStamp(1, write), null, write.AddSeconds(6));

            cache.TryGet("/tv/failed.mkv", 1, write, out _).Should().BeFalse();

            _now += 59_999;
            cache.TryGet("/tv/new.mkv", 1, write, out _).Should().BeTrue();

            _now += 1;
            cache.TryGet("/tv/new.mkv", 1, write, out _).Should().BeFalse();
        }

        [Test]
        public void unsettled_lifetime_setting_should_decide_how_long_a_recent_file_is_reused()
        {
            Apply(new Dictionary<string, object> { { "FfprobeCacheUnsettledSeconds", 10 } });
            var write = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var cache = new MediaInfoProbeCache();

            cache.Add("/tv/a.mkv", new MediaInfoProbeCache.FileStamp(1, write), Model(1), write.AddSeconds(2));

            _now += 9_999;
            cache.TryGet("/tv/a.mkv", 1, write, out _).Should().BeTrue();

            _now += 1;
            cache.TryGet("/tv/a.mkv", 1, write, out _).Should().BeFalse();

            // 0: not reused at all.
            Apply(new Dictionary<string, object> { { "FfprobeCacheUnsettledSeconds", 0 } });
            cache.Add("/tv/a.mkv", new MediaInfoProbeCache.FileStamp(1, write), Model(1), write.AddSeconds(2));
            cache.Count.Should().Be(0);
            cache.TryGet("/tv/a.mkv", 1, write, out _).Should().BeFalse();
        }

        // Idle entries of other files are swept out when the sweep interval has passed, not before.
        [TestCase(1, 0)]
        [TestCase(60, 0)]
        [TestCase(3600, 1)]
        public void sweep_interval_setting_should_decide_when_idle_entries_are_dropped(int sweepSeconds, int left)
        {
            Apply(new Dictionary<string, object> { { "FfprobeCacheSweepSeconds", sweepSeconds }, { "FfprobeCacheIdleMinutes", 1 } });
            var cache = new MediaInfoProbeCache();

            cache.Add("/tv/idle.mkv", 1, SettledWrite, Model(1));
            cache.Count.Should().Be(1);

            _now += 2 * 60_000;
            cache.TryGet("/tv/other.mkv", 1, SettledWrite, out _).Should().BeFalse();

            cache.Count.Should().Be(left);
        }

        [Test]
        public void status_should_sum_the_hits_of_every_cache()
        {
            var service = new OptimizeSettingsService(new Mock<IConfigService>().Object, TestLogger);
            MediaInfoProbeCache.Totals(out _, out _, out var hitsBefore, out var missesBefore);
            var statusBefore = service.GetStatus().Counters.Single(c => c.Name == "ffprobeCacheHits").Value;

            var first = new MediaInfoProbeCache();
            var second = new MediaInfoProbeCache();
            first.Add("/tv/a", 1, SettledWrite, Model(1));
            second.Add("/tv/b", 1, SettledWrite, Model(2));

            first.TryGet("/tv/a", 1, SettledWrite, out _).Should().BeTrue();
            first.TryGet("/tv/a", 1, SettledWrite, out _).Should().BeTrue();
            second.TryGet("/tv/b", 1, SettledWrite, out _).Should().BeTrue();
            second.TryGet("/tv/c", 1, SettledWrite, out _).Should().BeFalse();

            MediaInfoProbeCache.Totals(out var entries, out _, out var hits, out var misses);
            hits.Should().Be(hitsBefore + 3);
            misses.Should().Be(missesBefore + 1);
            entries.Should().BeGreaterOrEqualTo(2);

            var counters = service.GetStatus().Counters;
            counters.Single(c => c.Name == "ffprobeCacheHits").Value.Should().Be(statusBefore + 3);
            counters.Single(c => c.Name == "ffprobeCacheEntries").Value.Should().BeGreaterOrEqualTo(2);

            GC.KeepAlive(first);
            GC.KeepAlive(second);
        }

        [Test]
        public void shrinking_limits_should_trim_cached_entries_at_once()
        {
            var cache = new MediaInfoProbeCache();

            for (var i = 0; i < 100; i++)
            {
                _now++;
                cache.Add("/tv/" + i, 1, SettledWrite, Model(i, 600));
            }

            cache.Count.Should().Be(100);

            Apply(new Dictionary<string, object> { { "FfprobeCacheCapacity", 10 } });
            cache.Count.Should().BeLessOrEqualTo(10);

            // The most recently used entries are kept.
            cache.TryGet("/tv/99", 1, SettledWrite, out var kept).Should().BeTrue();
            kept.Width.Should().Be(99);

            // Entries larger than the new largest result are dropped.
            Apply(new Dictionary<string, object> { { "FfprobeCacheCapacity", 10 }, { "FfprobeCacheMaxEntryKb", 1 } });
            cache.Count.Should().Be(0);
        }

        [Test]
        public void turning_the_cache_off_should_clear_it()
        {
            var cache = new MediaInfoProbeCache();
            cache.Add("/tv/a", 1, SettledWrite, Model(1));
            cache.Count.Should().Be(1);

            Apply(new Dictionary<string, object> { { "FfprobeCacheMaxMb", 0 } });

            cache.Count.Should().Be(0);
            cache.Bytes.Should().Be(0);
        }

        [Test]
        public void lifetimes_should_apply_to_cached_entries()
        {
            var cache = new MediaInfoProbeCache();
            cache.Add("/tv/failed", 1, SettledWrite, null);
            cache.Add("/tv/ok", 1, SettledWrite, Model(1));

            cache.TryGet("/tv/failed", 1, SettledWrite, out var failed).Should().BeTrue();
            failed.Should().BeNull();

            // Failures not remembered any more: the next read probes again.
            Apply(new Dictionary<string, object> { { "FfprobeCacheFailureMinutes", 0 } });
            cache.TryGet("/tv/failed", 1, SettledWrite, out _).Should().BeFalse();

            // A shorter idle lifetime drops an entry idle for longer.
            _now += 2 * 60_000;
            cache.TryGet("/tv/ok", 1, SettledWrite, out _).Should().BeTrue();
            _now += 2 * 60_000;
            Apply(new Dictionary<string, object> { { "FfprobeCacheIdleMinutes", 1 } });
            cache.TryGet("/tv/ok", 1, SettledWrite, out _).Should().BeFalse();
        }

        [Test]
        public void failure_lifetime_should_decide_when_a_failed_probe_is_repeated()
        {
            Apply(new Dictionary<string, object> { { "FfprobeCacheFailureMinutes", 3 } });
            var cache = new MediaInfoProbeCache();
            cache.Add("/tv/failed", 1, SettledWrite, null);

            _now += (3 * 60_000) - 1;
            cache.TryGet("/tv/failed", 1, SettledWrite, out _).Should().BeTrue();

            _now += 1;
            cache.TryGet("/tv/failed", 1, SettledWrite, out _).Should().BeFalse();
        }

        [Test]
        public void settle_time_should_decide_whether_a_recent_file_is_kept()
        {
            var write = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            MediaInfoProbeCache.UtcNow = () => write.AddSeconds(90);

            // Written 90 s before the probe: settled with the default 60 s, so a failure is remembered.
            var cache = new MediaInfoProbeCache();
            cache.Add("/tv/a", 1, write, null);
            cache.TryGet("/tv/a", 1, write, out _).Should().BeTrue();

            // With a 120 s settle time the same file is unsettled: its failure is not remembered.
            Apply(new Dictionary<string, object> { { "FfprobeCacheSettleSeconds", 120 } });
            cache = new MediaInfoProbeCache();
            cache.Add("/tv/a", 1, write, null);
            cache.TryGet("/tv/a", 1, write, out _).Should().BeFalse();

            // A result (not a failure) of it is reused only for the unsettled lifetime.
            cache.Add("/tv/a", 1, write, Model(1));
            _now += 59_999;
            cache.TryGet("/tv/a", 1, write, out _).Should().BeTrue();
            _now += 1;
            cache.TryGet("/tv/a", 1, write, out _).Should().BeFalse();
        }
    }
}
