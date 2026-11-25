using System;
using System.Collections.Generic;
using System.Threading;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Optimize;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.OptimizeTests
{
    // Every time limit of the ffprobe result cache follows the setting in effect at the lookup or store, without
    // ApplyLimits: settle time, recently-written lifetime, idle lifetime and sweep interval, on a fixed clock.
    [TestFixture]
    [NonParallelizable]
    public class OptimizeProbeCacheLimitsFixture : CoreTest
    {
        private static readonly DateTime Written = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        private static readonly MediaInfoProbeCache.FileStamp Stamp = new MediaInfoProbeCache.FileStamp(1, Written);

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

        private void Advance(long milliseconds)
        {
            Interlocked.Add(ref _now, milliseconds);
        }

        // A probe that started less than the settle time after the last write is "recently written": with a
        // recently-written lifetime of 0 it is not stored. One that started at or after the settle time is stored.
        [TestCase(60)]
        [TestCase(61)]
        [TestCase(300)]
        [TestCase(3600)]
        public void settle_time_should_follow_the_setting(long settleSeconds)
        {
            Use((OptimizeTunables.FfprobeCacheSettleSeconds, settleSeconds), (OptimizeTunables.FfprobeCacheUnsettledSeconds, 0));

            var cache = new MediaInfoProbeCache();

            cache.Add("/movies/early", Stamp, Model(1), Written.AddSeconds(settleSeconds - 1));
            cache.Add("/movies/settled", Stamp, Model(2), Written.AddSeconds(settleSeconds));

            cache.TryGet("/movies/early", Stamp, out _).Should().BeFalse();
            cache.TryGet("/movies/settled", Stamp, out var model).Should().BeTrue();
            model.Width.Should().Be(2);
        }

        [TestCase(10)]
        [TestCase(30)]
        [TestCase(60)]
        public void recently_written_result_should_be_reused_for_the_unsettled_lifetime(long unsettledSeconds)
        {
            Use((OptimizeTunables.FfprobeCacheUnsettledSeconds, unsettledSeconds));

            var cache = new MediaInfoProbeCache();
            cache.Add("/movies/new", Stamp, Model(1), Written.AddSeconds(1));

            Advance((unsettledSeconds * 1000) - 1000);
            cache.TryGet("/movies/new", Stamp, out var model).Should().BeTrue();
            model.Width.Should().Be(1);

            Advance(2000);
            cache.TryGet("/movies/new", Stamp, out _).Should().BeFalse();
        }

        // The settle time, recently-written lifetime and failure lifetime have the Sonarr image's bounds, on the page, in
        // the API validation and for the shared ARR_FFPROBE_CACHE_* environment variables: they only allow values at
        // least as strict as the tested defaults (settle 60..3600 s, recently written 0..60 s, failure 0..10 min).
        [TestCase(OptimizeTunables.FfprobeCacheSettleSeconds, "ARR_FFPROBE_CACHE_SETTLE_SECONDS", 60, 3600)]
        [TestCase(OptimizeTunables.FfprobeCacheUnsettledSeconds, "ARR_FFPROBE_CACHE_UNSETTLED_SECONDS", 0, 60)]
        [TestCase(OptimizeTunables.FfprobeCacheFailureMinutes, "ARR_FFPROBE_CACHE_FAILURE_MINUTES", 0, 10)]
        public void strictness_limits_should_have_the_sonarr_bounds(string name, string variable, long min, long max)
        {
            var tunable = OptimizeTunables.Get(name);

            tunable.EnvironmentVariable.Should().Be(variable);
            tunable.Min.Should().Be(min);
            tunable.Max.Should().Be(max);
            tunable.EnvironmentMin.Should().BeNull();
            tunable.EnvironmentMax.Should().BeNull();
            ((long)tunable.DefaultValue).Should().BeInRange(min, max);

            foreach (var fromEnvironment in new[] { false, true })
            {
                tunable.TryParse(min.ToString(), fromEnvironment, out _).Should().BeNull();
                tunable.TryParse(max.ToString(), fromEnvironment, out _).Should().BeNull();
                tunable.TryParse((max + 1).ToString(), fromEnvironment, out _).Should().Contain($"from {min} to {max}");

                if (min > 0)
                {
                    tunable.TryParse((min - 1).ToString(), fromEnvironment, out _).Should().Contain($"from {min} to {max}");
                }
            }
        }

        // One .env shared by both containers: values the Sonarr image rejects are rejected here too (ignored with a
        // warning, not locked), and the defaults stay in effect.
        [Test]
        public void out_of_range_environment_values_should_be_ignored_like_in_the_sonarr_image()
        {
            var variables = new Dictionary<string, string>
            {
                ["ARR_FFPROBE_CACHE_SETTLE_SECONDS"] = "10",
                ["ARR_FFPROBE_CACHE_UNSETTLED_SECONDS"] = "3600",
                ["ARR_FFPROBE_CACHE_FAILURE_MINUTES"] = "1440"
            };

            var environment = OptimizeEnvironment.Read(n => variables.TryGetValue(n, out var v) ? v : null, TestLogger);
            var settings = OptimizeSettings.Build(environment, _ => null, 1);

            foreach (var name in new[] { OptimizeTunables.FfprobeCacheSettleSeconds, OptimizeTunables.FfprobeCacheUnsettledSeconds, OptimizeTunables.FfprobeCacheFailureMinutes })
            {
                settings.IsLocked(name).Should().BeFalse(name);
                settings[name].Source.Should().Be(OptimizeValueSource.Default, name);
                settings[name].EnvironmentError.Should().NotBeNullOrWhiteSpace(name);
            }

            settings.FfprobeCache.SettleTime.Should().Be(MediaInfoProbeCache.SettleTime);
            settings.FfprobeCache.UnsettledLifetimeMs.Should().Be(MediaInfoProbeCache.UnsettledLifetimeMs);
            settings.FfprobeCache.FailureLifetimeMs.Should().Be(MediaInfoProbeCache.FailureLifetimeMs);
            NzbDrone.Test.Common.ExceptionVerification.ExpectedWarns(3);
        }

        // A value saved before the bounds were aligned (for example a 10 s settle time) is ignored like any other
        // invalid saved value: the default applies.
        [Test]
        public void saved_values_outside_the_bounds_should_fall_back_to_the_default()
        {
            var saved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [OptimizeTunables.Get(OptimizeTunables.FfprobeCacheSettleSeconds).ConfigKey] = "10",
                [OptimizeTunables.Get(OptimizeTunables.FfprobeCacheUnsettledSeconds).ConfigKey] = "300",
                [OptimizeTunables.Get(OptimizeTunables.FfprobeCacheFailureMinutes).ConfigKey] = "60"
            };

            var settings = OptimizeSettings.Build(OptimizeEnvironment.Empty, k => saved.TryGetValue(k, out var v) ? v : null, 1);

            settings.FfprobeCache.SettleTime.Should().Be(MediaInfoProbeCache.SettleTime);
            settings.FfprobeCache.UnsettledLifetimeMs.Should().Be(MediaInfoProbeCache.UnsettledLifetimeMs);
            settings.FfprobeCache.FailureLifetimeMs.Should().Be(MediaInfoProbeCache.FailureLifetimeMs);
            settings[OptimizeTunables.FfprobeCacheSettleSeconds].Source.Should().Be(OptimizeValueSource.Default);
        }

        // The idle check of a lookup uses the setting in effect, without a sweep or ApplyLimits in between.
        [TestCase(2)]
        [TestCase(720)]
        public void idle_entry_should_be_missed_after_the_idle_lifetime(long idleMinutes)
        {
            Use((OptimizeTunables.FfprobeCacheIdleMinutes, idleMinutes), (OptimizeTunables.FfprobeCacheSweepSeconds, 3600));

            var cache = new MediaInfoProbeCache();
            cache.Add("/movies/idle", Stamp, Model(1), Written.AddDays(1));

            // A hit refreshes the entry.
            Advance((idleMinutes * 60_000) - 1000);
            cache.TryGet("/movies/idle", Stamp, out _).Should().BeTrue();

            Advance((idleMinutes * 60_000) - 1000);
            cache.TryGet("/movies/idle", Stamp, out _).Should().BeTrue();

            Advance((idleMinutes * 60_000) + 1000);
            cache.TryGet("/movies/idle", Stamp, out _).Should().BeFalse();
        }

        // A short sweep interval removes an expired entry on the next lookup of any path.
        [Test]
        public void short_sweep_interval_should_sweep_on_the_next_lookup()
        {
            Use((OptimizeTunables.FfprobeCacheSweepSeconds, 10), (OptimizeTunables.FfprobeCacheUnsettledSeconds, 20));

            var cache = new MediaInfoProbeCache();
            cache.Add("/movies/new", Stamp, Model(1), Written.AddSeconds(1));
            cache.Count.Should().Be(1);

            Advance(30_000);
            cache.TryGet("/movies/other", Stamp, out _).Should().BeFalse();

            cache.Count.Should().Be(0);
        }

        // A long sweep interval leaves an expired entry until the interval has passed (it is never returned: the
        // lookup itself checks the expiry).
        [Test]
        public void long_sweep_interval_should_not_sweep_before_it_has_passed()
        {
            Use((OptimizeTunables.FfprobeCacheSweepSeconds, 3600), (OptimizeTunables.FfprobeCacheUnsettledSeconds, 20));

            var cache = new MediaInfoProbeCache();
            cache.Add("/movies/new", Stamp, Model(1), Written.AddSeconds(1));

            Advance(120_000);
            cache.TryGet("/movies/other", Stamp, out _).Should().BeFalse();
            cache.Count.Should().Be(1);
            cache.TryGet("/movies/new", Stamp, out _).Should().BeFalse();

            cache.Add("/movies/keep", Stamp, Model(2), Written.AddDays(1));
            Advance(3_600_000);
            cache.TryGet("/movies/other", Stamp, out _).Should().BeFalse();
            cache.Count.Should().Be(1);
            cache.TryGet("/movies/keep", Stamp, out _).Should().BeTrue();
        }

        // Change from next3 (documented): a failed probe of a recently written file is still not remembered, but it now
        // also removes the older result for that path, so a lookup never returns anything but the latest probe.
        [Test]
        public void recent_failure_should_remove_the_older_result_for_the_same_file()
        {
            var cache = new MediaInfoProbeCache();
            cache.Add("/movies/changing", Stamp, Model(7), Written.AddSeconds(1));
            cache.TryGet("/movies/changing", Stamp, out _).Should().BeTrue();

            cache.Add("/movies/changing", Stamp, null, Written.AddSeconds(2));

            cache.TryGet("/movies/changing", Stamp, out _).Should().BeFalse();
            cache.Count.Should().Be(0);
        }
    }
}
