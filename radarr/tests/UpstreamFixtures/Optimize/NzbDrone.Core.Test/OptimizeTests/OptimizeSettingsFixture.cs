using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Optimize;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.OptimizeTests
{
    // Precedence (environment > saved > default), bounds and parsing of every Optimize setting.
    [TestFixture]
    [NonParallelizable]
    public class OptimizeSettingsFixture : CoreTest
    {
        private Dictionary<string, string> _environment;
        private Dictionary<string, string> _saved;

        [SetUp]
        public void Setup()
        {
            _environment = new Dictionary<string, string>();
            _saved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            OptimizeSettings.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            OptimizeSettings.Reset();
        }

        private OptimizeSettings Build()
        {
            var environment = OptimizeEnvironment.Read(name => _environment.TryGetValue(name, out var value) ? value : null, TestLogger);

            return OptimizeSettings.Build(environment, key => _saved.TryGetValue(key, out var value) ? value : null, 1);
        }

        private static string Key(string name)
        {
            return OptimizeTunables.Get(name).ConfigKey;
        }

        [Test]
        public void nothing_set_should_be_stock_and_the_tested_defaults()
        {
            var settings = Build();

            settings.RateLimit.IsStock.Should().BeTrue();
            settings.RateLimit.BaseRateLimit.Should().Be(TimeSpan.FromSeconds(2));
            settings.RateLimit.GrabSpacing.Should().Be(TimeSpan.FromSeconds(2));
            settings.RateLimit.RefundsCacheHits.Should().BeFalse();
            settings.FfprobeTimeout.Should().Be(TimeSpan.FromMinutes(15));
            settings.CustomFormatCacheCapacity.Should().Be(CustomFormatMatchCache.Capacity);
            settings.CustomFormatCacheSlidingMs.Should().Be(CustomFormatMatchCache.SlidingMs);
            settings.CustomFormatCacheMaxInputChars.Should().Be(CustomFormatMatchCache.MaxTextLength);
            settings.DecisionBatchMemo.Should().BeTrue();
            settings.ReleaseProfileCache.Should().BeTrue();

            settings.FfprobeCache.Capacity.Should().Be(MediaInfoProbeCache.Capacity);
            settings.FfprobeCache.MaxBytes.Should().Be(MediaInfoProbeCache.MaxBytes);
            settings.FfprobeCache.MaxEntryBytes.Should().Be(MediaInfoProbeCache.MaxEntryBytes);
            settings.FfprobeCache.FailureLifetimeMs.Should().Be(MediaInfoProbeCache.FailureLifetimeMs);
            settings.FfprobeCache.IdleLifetimeMs.Should().Be(MediaInfoProbeCache.IdleLifetimeMs);
            settings.FfprobeCache.SettleTime.Should().Be(MediaInfoProbeCache.SettleTime);
            settings.FfprobeCache.UnsettledLifetimeMs.Should().Be(MediaInfoProbeCache.UnsettledLifetimeMs);
            settings.FfprobeCache.SweepIntervalMs.Should().Be(MediaInfoProbeCache.SweepIntervalMs);

            foreach (var tunable in OptimizeTunables.Editable)
            {
                settings[tunable.Name].Source.Should().Be(OptimizeValueSource.Default, tunable.Name);
                settings[tunable.Name].Value.Should().Be(tunable.DefaultValue, tunable.Name);
            }
        }

        [Test]
        public void saved_value_should_override_default()
        {
            _saved[Key(OptimizeTunables.IndexerRateLimitMs)] = "500";
            _saved[Key(OptimizeTunables.CacheHitHeader)] = "X-Search-Cache";
            _saved[Key(OptimizeTunables.FfprobeTimeoutMinutes)] = "0";

            var settings = Build();

            settings.RateLimit.BaseRateLimit.Should().Be(TimeSpan.FromMilliseconds(500));
            settings.RateLimit.CacheHitHeader.Should().Be("X-Search-Cache");
            settings.RateLimit.CacheHitValue.Should().Be("HIT");
            settings.FfprobeTimeout.Should().Be(TimeSpan.Zero);
            settings[OptimizeTunables.IndexerRateLimitMs].Source.Should().Be(OptimizeValueSource.Database);
            settings.IsLocked(OptimizeTunables.IndexerRateLimitMs).Should().BeFalse();
        }

        [Test]
        public void environment_should_override_saved_value_and_lock_it()
        {
            _saved[Key(OptimizeTunables.IndexerRateLimitMs)] = "500";
            _environment[IndexerRateLimitOptions.RateLimitVariable] = "250";

            var settings = Build();

            settings.RateLimit.BaseRateLimit.Should().Be(TimeSpan.FromMilliseconds(250));
            settings[OptimizeTunables.IndexerRateLimitMs].Source.Should().Be(OptimizeValueSource.Environment);
            settings[OptimizeTunables.IndexerRateLimitMs].SavedValue.Should().Be(500L);
            settings.IsLocked(OptimizeTunables.IndexerRateLimitMs).Should().BeTrue();
        }

        [Test]
        public void invalid_environment_should_be_ignored_and_reported()
        {
            _saved[Key(OptimizeTunables.CustomFormatCacheCapacity)] = "1000";
            _environment["ARR_CF_CACHE_CAPACITY"] = "-5";

            var settings = Build();

            settings.CustomFormatCacheCapacity.Should().Be(1000);
            settings[OptimizeTunables.CustomFormatCacheCapacity].Source.Should().Be(OptimizeValueSource.Database);
            settings[OptimizeTunables.CustomFormatCacheCapacity].EnvironmentText.Should().Be("-5");
            settings[OptimizeTunables.CustomFormatCacheCapacity].EnvironmentError.Should().NotBeNullOrWhiteSpace();
            settings.IsLocked(OptimizeTunables.CustomFormatCacheCapacity).Should().BeFalse();
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void invalid_saved_value_should_fall_back_to_default()
        {
            _saved[Key(OptimizeTunables.GrabSpacingMs)] = "abc";
            _saved[Key(OptimizeTunables.FfprobeCacheEntries)] = "100001";

            var settings = Build();

            settings.RateLimit.GrabSpacing.Should().Be(TimeSpan.FromSeconds(2));
            settings.FfprobeCache.Capacity.Should().Be(MediaInfoProbeCache.Capacity);
            settings[OptimizeTunables.GrabSpacingMs].Source.Should().Be(OptimizeValueSource.Default);
        }

        // The environment keeps next3's wider range (up to a day); a saved value must be in the page range (0..10000),
        // so a hand-edited database value above it is ignored like any other invalid saved value.
        [TestCase("50000")]
        [TestCase("86400000")]
        [TestCase("10001")]
        public void saved_rate_limit_outside_the_page_range_should_fall_back_to_default(string saved)
        {
            _saved[Key(OptimizeTunables.IndexerRateLimitMs)] = saved;

            var settings = Build();

            settings.RateLimit.BaseRateLimit.Should().Be(TimeSpan.FromSeconds(2));
            settings[OptimizeTunables.IndexerRateLimitMs].Source.Should().Be(OptimizeValueSource.Default);
            settings[OptimizeTunables.IndexerRateLimitMs].SavedValue.Should().BeNull();
        }

        [Test]
        public void saved_rate_limit_at_the_page_maximum_should_be_used()
        {
            _saved[Key(OptimizeTunables.IndexerRateLimitMs)] = "10000";

            Build().RateLimit.BaseRateLimit.Should().Be(TimeSpan.FromSeconds(10));
        }

        [Test]
        public void environment_rate_limit_should_keep_the_next3_range_but_the_page_should_not()
        {
            var tunable = OptimizeTunables.Get(OptimizeTunables.IndexerRateLimitMs);

            tunable.TryParse("86400000", true, out var fromEnvironment).Should().BeNull();
            fromEnvironment.Should().Be(86_400_000L);
            tunable.TryParse("10001", false, out _).Should().NotBeNull();
            tunable.TryParse("10000", false, out _).Should().BeNull();

            _environment[IndexerRateLimitOptions.RateLimitVariable] = "86400000";
            Build().RateLimit.BaseRateLimit.Should().Be(TimeSpan.FromDays(1));
        }

        [Test]
        public void each_value_should_follow_its_own_source()
        {
            _environment[IndexerRateLimitOptions.CacheHitValueVariable] = "FROMCACHE";
            _saved[Key(OptimizeTunables.CacheHitHeader)] = "X-Cache";

            var settings = Build();

            settings.RateLimit.CacheHitHeader.Should().Be("X-Cache");
            settings.RateLimit.CacheHitValue.Should().Be("FROMCACHE");
            settings[OptimizeTunables.CacheHitHeader].Source.Should().Be(OptimizeValueSource.Database);
            settings[OptimizeTunables.CacheHitValue].Source.Should().Be(OptimizeValueSource.Environment);
        }

        public static IEnumerable<TestCaseData> IntegerTunables()
        {
            return OptimizeTunables.Editable.Where(t => t.Kind == OptimizeValueKind.Integer).Select(t => new TestCaseData(t.Name).SetName("bounds_" + t.Name));
        }

        [TestCaseSource(nameof(IntegerTunables))]
        public void integer_bounds_should_be_inclusive(string name)
        {
            var tunable = OptimizeTunables.Get(name);

            tunable.TryParse(tunable.Min.ToString(), false, out var min).Should().BeNull();
            min.Should().Be(tunable.Min);
            tunable.TryParse(tunable.Max.ToString(), false, out var max).Should().BeNull();
            max.Should().Be(tunable.Max);
            tunable.TryParse((tunable.Max + 1).ToString(), false, out _).Should().NotBeNull();
            tunable.TryParse("-1", false, out _).Should().NotBeNull();
            tunable.TryParse("1.5", false, out _).Should().NotBeNull();
            tunable.TryParse("1,000", false, out _).Should().NotBeNull();

            if (tunable.Min > 0)
            {
                tunable.TryParse((tunable.Min - 1).ToString(), false, out _).Should().NotBeNull();
            }

            ((long)tunable.DefaultValue).Should().BeInRange(tunable.Min, tunable.Max);
        }

        [TestCase("X-Search-Cache", true)]
        [TestCase("x_cache", true)]
        [TestCase("X Cache", false)]
        [TestCase("X-Cache:", false)]
        [TestCase("Ünicode", false)]
        public void cache_hit_header_should_be_a_header_name(string header, bool valid)
        {
            var error = OptimizeTunables.Get(OptimizeTunables.CacheHitHeader).TryParse(header, false, out _);

            (error == null).Should().Be(valid);
        }

        [TestCase("HIT", true)]
        [TestCase("hit", true)]
        [TestCase("HIT,MISS", false)]
        [TestCase("HIT;x", false)]
        [TestCase("HIT\u0001", false)]
        public void cache_hit_value_should_be_a_single_value(string value, bool valid)
        {
            var error = OptimizeTunables.Get(OptimizeTunables.CacheHitValue).TryParse(value, false, out _);

            (error == null).Should().Be(valid);
        }

        [TestCase("true", true)]
        [TestCase("1", true)]
        [TestCase("Yes", true)]
        [TestCase("on", true)]
        [TestCase("False", false)]
        [TestCase("0", false)]
        [TestCase("no", false)]
        [TestCase("OFF", false)]
        public void booleans_should_parse(string text, bool expected)
        {
            OptimizeTunables.Get(OptimizeTunables.DecisionBatchMemo).TryParse(text, true, out var value).Should().BeNull();
            value.Should().Be(expected);
            OptimizeTunables.Get(OptimizeTunables.DecisionBatchMemo).TryParse("yes please", true, out _).Should().NotBeNull();
        }

        [Test]
        public void ffprobe_max_entry_should_never_exceed_the_memory_limit()
        {
            _saved[Key(OptimizeTunables.FfprobeCacheMaxMegabytes)] = "1";
            _saved[Key(OptimizeTunables.FfprobeCacheMaxEntryKilobytes)] = "1048576";

            Build().FfprobeCache.MaxEntryBytes.Should().Be(1024 * 1024);
        }

        [Test]
        public void every_tunable_should_have_a_description_and_editable_ones_an_environment_variable()
        {
            OptimizeTunables.All.Select(t => t.Name).Should().OnlyHaveUniqueItems();

            foreach (var tunable in OptimizeTunables.All)
            {
                tunable.Label.Should().NotBeNullOrWhiteSpace(tunable.Name);
                tunable.Description.Should().NotBeNullOrWhiteSpace(tunable.Name);

                if (tunable.Editable)
                {
                    tunable.EnvironmentVariable.Should().StartWith("ARR_", tunable.Name);
                    tunable.ConfigKey.Should().StartWith("Optimize", tunable.Name);
                }
                else
                {
                    tunable.HowToChange.Should().NotBeNullOrWhiteSpace(tunable.Name);
                    tunable.ReadOnlyValue.Should().NotBeNull(tunable.Name);
                }
            }
        }

        // Every value-type constant of the optimization classes is listed on the Optimize page (by its "Class.Member"
        // name, or as the environment variable of a listed tunable), so the page cannot miss a tunable.
        [TestCase(typeof(IndexerRateLimitOptions))]
        [TestCase(typeof(MediaInfoProbeCache))]
        [TestCase(typeof(CustomFormatMatchCache))]
        [TestCase(typeof(OptimizeTunables))]
        public void every_constant_should_be_in_the_settings_list(Type type)
        {
            var listed = OptimizeTunables.All.SelectMany(t => t.Constants).ToHashSet();
            var variables = OptimizeTunables.All.Select(t => t.EnvironmentVariable).Where(v => v != null).ToHashSet();

            var fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => f.IsLiteral || f.IsInitOnly)
                .Where(f => f.FieldType == typeof(int) || f.FieldType == typeof(long) || f.FieldType == typeof(TimeSpan) || f.FieldType == typeof(string))
                .ToList();

            fields.Should().NotBeEmpty();

            foreach (var field in fields)
            {
                var member = $"{type.Name}.{field.Name}";
                var value = field.GetValue(null) as string;

                // The tunable names declared in OptimizeTunables are identifiers, not values.
                if (type == typeof(OptimizeTunables) && value != null && !value.StartsWith("ARR_"))
                {
                    continue;
                }

                var isListed = listed.Contains(member) || (value != null && variables.Contains(value));

                isListed.Should().BeTrue($"{member} should be listed on the Optimize page");
            }
        }

        [Test]
        public void ffprobe_timeout_should_be_listed_with_its_default()
        {
            var listed = OptimizeTunables.All.SelectMany(t => t.Constants).ToHashSet();

            listed.Should().Contain("VideoFileInfoReader.DefaultProbeTimeout");
            listed.Should().Contain("VideoFileInfoReader.ProbeTimeout");
            VideoFileInfoReader.DefaultProbeTimeout.Should().Be(TimeSpan.FromMinutes((long)OptimizeTunables.Get(OptimizeTunables.FfprobeTimeoutMinutes).DefaultValue));
        }

        [Test]
        public void defaults_should_equal_the_constants_they_replace()
        {
            long Default(string name) => (long)OptimizeTunables.Get(name).DefaultValue;

            Default(OptimizeTunables.IndexerRateLimitMs).Should().Be((long)IndexerRateLimitOptions.StockRateLimit.TotalMilliseconds);
            Default(OptimizeTunables.GrabSpacingMs).Should().Be((long)IndexerRateLimitOptions.GrabRateLimit.TotalMilliseconds);
            Default(OptimizeTunables.CustomFormatCacheCapacity).Should().Be(50_000);
            Default(OptimizeTunables.CustomFormatCacheExpiryMinutes).Should().Be(360);
            Default(OptimizeTunables.CustomFormatCacheMaxInputChars).Should().Be(4096);
            Default(OptimizeTunables.FfprobeCacheEntries).Should().Be(1000);
            Default(OptimizeTunables.FfprobeCacheMaxMegabytes).Should().Be(32);
            Default(OptimizeTunables.FfprobeCacheMaxEntryKilobytes).Should().Be(4096);
            Default(OptimizeTunables.FfprobeCacheFailureMinutes).Should().Be(10);
            Default(OptimizeTunables.FfprobeCacheIdleMinutes).Should().Be(360);
            Default(OptimizeTunables.FfprobeCacheSettleSeconds).Should().Be(60);
            Default(OptimizeTunables.FfprobeCacheUnsettledSeconds).Should().Be(60);
            Default(OptimizeTunables.FfprobeCacheSweepSeconds).Should().Be(60);
            Default(OptimizeTunables.FfprobeTimeoutMinutes).Should().Be(15);
        }

        [Test]
        public void current_should_read_process_environment()
        {
            try
            {
                Environment.SetEnvironmentVariable("ARR_FFPROBE_TIMEOUT_MINUTES", "7");
                Environment.SetEnvironmentVariable("ARR_GRAB_SPACING_MS", "3000");
                OptimizeSettings.Reset();

                OptimizeSettings.Current.FfprobeTimeout.Should().Be(TimeSpan.FromMinutes(7));
                VideoFileInfoReader.ProbeTimeout.Should().Be(TimeSpan.FromMinutes(7));
                IndexerRateLimitOptions.Current.GrabSpacing.Should().Be(TimeSpan.FromSeconds(3));
                OptimizeSettings.Current.IsLocked(OptimizeTunables.FfprobeTimeoutMinutes).Should().BeTrue();
            }
            finally
            {
                Environment.SetEnvironmentVariable("ARR_FFPROBE_TIMEOUT_MINUTES", null);
                Environment.SetEnvironmentVariable("ARR_GRAB_SPACING_MS", null);
                OptimizeSettings.Reset();
            }
        }
    }
}
