using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Optimize;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Test.IndexerTests;

namespace NzbDrone.Core.Test.OptimizeTests
{
    // Saving on the Optimize page applies at once (no restart), the default is never stored, environment variables
    // lock their value, and the settings list and status come from the running app.
    [TestFixture]
    [NonParallelizable]
    public class OptimizeSettingsServiceFixture : CoreTest<OptimizeSettingsService>
    {
        private Dictionary<string, string> _store;

        [SetUp]
        public void Setup()
        {
            OptimizeSettings.Reset();
            _store = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            Mocker.GetMock<IConfigService>()
                  .Setup(s => s.GetOptimizeValue(It.IsAny<string>()))
                  .Returns<string>(key => _store.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null);

            Mocker.GetMock<IConfigService>()
                  .Setup(s => s.SetOptimizeValue(It.IsAny<string>(), It.IsAny<string>()))
                  .Callback<string, string>((key, value) => _store[key] = value ?? string.Empty);

            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.CheckForFinishedDownloadInterval)
                  .Returns(1);

            Mocker.SetConstant<IVideoFileInfoReader>(new VideoFileInfoReader(Mocker.Resolve<NzbDrone.Common.Disk.IDiskProvider>(), TestLogger));
        }

        [TearDown]
        public void TearDown()
        {
            OptimizeSettings.Reset();
        }

        private static string Key(string name)
        {
            return OptimizeTunables.Get(name).ConfigKey;
        }

        [Test]
        public void save_should_apply_without_restart()
        {
            Subject.Reload();
            IndexerRateLimitOptions.Current.BaseRateLimit.Should().Be(TimeSpan.FromSeconds(2));

            Subject.Save(new Dictionary<string, object>
            {
                { OptimizeTunables.IndexerRateLimitMs, 500 },
                { OptimizeTunables.CacheHitHeader, "X-Search-Cache" },
                { OptimizeTunables.FfprobeTimeoutMinutes, 3 },
                { OptimizeTunables.GrabSpacingMs, 2500 }
            });

            IndexerRateLimitOptions.Current.BaseRateLimit.Should().Be(TimeSpan.FromMilliseconds(500));
            IndexerRateLimitOptions.Current.CacheHitHeader.Should().Be("X-Search-Cache");
            IndexerRateLimitOptions.Current.GrabSpacing.Should().Be(TimeSpan.FromMilliseconds(2500));
            VideoFileInfoReader.ProbeTimeout.Should().Be(TimeSpan.FromMinutes(3));
            Mocker.Resolve<TestIndexer>().RateLimit.Should().Be(TimeSpan.FromMilliseconds(500));

            _store[Key(OptimizeTunables.IndexerRateLimitMs)].Should().Be("500");
        }

        [Test]
        public void save_default_or_null_should_clear_the_saved_value()
        {
            Subject.Save(new Dictionary<string, object> { { OptimizeTunables.CustomFormatCacheCapacity, 1000 } });
            Subject.Current[OptimizeTunables.CustomFormatCacheCapacity].Source.Should().Be(OptimizeValueSource.Database);

            Subject.Save(new Dictionary<string, object> { { OptimizeTunables.CustomFormatCacheCapacity, 50_000 } });
            _store[Key(OptimizeTunables.CustomFormatCacheCapacity)].Should().BeEmpty();
            Subject.Current[OptimizeTunables.CustomFormatCacheCapacity].Source.Should().Be(OptimizeValueSource.Default);

            Subject.Save(new Dictionary<string, object> { { OptimizeTunables.GrabSpacingMs, 3000 } });
            Subject.Save(new Dictionary<string, object> { { OptimizeTunables.GrabSpacingMs, null } });
            _store[Key(OptimizeTunables.GrabSpacingMs)].Should().BeEmpty();
            IndexerRateLimitOptions.Current.IsStock.Should().BeTrue();
        }

        [Test]
        public void save_should_leave_values_it_was_not_given()
        {
            Subject.Save(new Dictionary<string, object> { { OptimizeTunables.FfprobeCacheEntries, 10 } });
            Subject.Save(new Dictionary<string, object> { { OptimizeTunables.FfprobeTimeoutMinutes, 20 } });

            Subject.Current.FfprobeCache.Capacity.Should().Be(10);
            Subject.Current.FfprobeTimeout.Should().Be(TimeSpan.FromMinutes(20));
        }

        [Test]
        public void invalid_value_should_be_rejected_and_change_nothing()
        {
            var values = new Dictionary<string, object>
            {
                { OptimizeTunables.IndexerRateLimitMs, 500 },
                { OptimizeTunables.CustomFormatCacheCapacity, 1_000_001 },
                { OptimizeTunables.CacheHitHeader, "bad header" }
            };

            var errors = Subject.Validate(values);

            errors.Keys.Should().BeEquivalentTo(OptimizeTunables.CustomFormatCacheCapacity, OptimizeTunables.CacheHitHeader);
            Assert.Throws<ArgumentException>(() => Subject.Save(values));
            _store.Should().BeEmpty();
            IndexerRateLimitOptions.Current.BaseRateLimit.Should().Be(TimeSpan.FromSeconds(2));
        }

        [Test]
        public void locked_values_should_be_ignored_on_save_and_not_validated()
        {
            try
            {
                Environment.SetEnvironmentVariable(IndexerRateLimitOptions.RateLimitVariable, "30000");
                OptimizeSettings.Reset();
                Subject.Reload();

                Subject.Current.IsLocked(OptimizeTunables.IndexerRateLimitMs).Should().BeTrue();

                // 30000 is above the page maximum, but it is the locked environment value the page sends back.
                Subject.Validate(new Dictionary<string, object> { { OptimizeTunables.IndexerRateLimitMs, 30000 } }).Should().BeEmpty();
                Subject.Save(new Dictionary<string, object> { { OptimizeTunables.IndexerRateLimitMs, 100 }, { OptimizeTunables.GrabSpacingMs, 3000 } });

                _store.ContainsKey(Key(OptimizeTunables.IndexerRateLimitMs)).Should().BeFalse();
                IndexerRateLimitOptions.Current.BaseRateLimit.Should().Be(TimeSpan.FromSeconds(30));
                IndexerRateLimitOptions.Current.GrabSpacing.Should().Be(TimeSpan.FromSeconds(3));

                var info = Subject.GetSettings().Single(s => s.Name == OptimizeTunables.IndexerRateLimitMs);
                info.Locked.Should().BeTrue();
                info.SourceText.Should().Be("Set by environment variable ARR_INDEXER_RATE_LIMIT_MS");
                info.Value.Should().Be(30000L);
            }
            finally
            {
                Environment.SetEnvironmentVariable(IndexerRateLimitOptions.RateLimitVariable, null);
                OptimizeSettings.Reset();
            }
        }

        [Test]
        public void config_saved_and_application_started_should_reload()
        {
            _store[Key(OptimizeTunables.IndexerRateLimitMs)] = "750";
            Subject.Handle(new ApplicationStartedEvent());
            IndexerRateLimitOptions.Current.BaseRateLimit.Should().Be(TimeSpan.FromMilliseconds(750));

            _store[Key(OptimizeTunables.IndexerRateLimitMs)] = "900";
            Subject.Handle(new ConfigSavedEvent());
            IndexerRateLimitOptions.Current.BaseRateLimit.Should().Be(TimeSpan.FromMilliseconds(900));
        }

        [Test]
        public void settings_list_should_hold_every_tunable_with_effective_and_default_values()
        {
            Subject.Save(new Dictionary<string, object> { { OptimizeTunables.FfprobeCacheEntries, 200 } });

            var list = Subject.GetSettings();

            list.Select(s => s.Name).Should().BeEquivalentTo(OptimizeTunables.All.Select(t => t.Name));

            var entries = list.Single(s => s.Name == OptimizeTunables.FfprobeCacheEntries);
            entries.Value.Should().Be(200L);
            entries.DefaultValue.Should().Be(1000L);
            entries.Source.Should().Be("database");
            entries.SourceText.Should().Be("Saved on this page");
            entries.EnvironmentVariable.Should().Be("ARR_FFPROBE_CACHE_CAPACITY");
            entries.Min.Should().Be(0);
            entries.Max.Should().Be(100_000);

            var eviction = list.Single(s => s.Name == OptimizeTunables.FfprobeCacheEvictionBatch);
            eviction.Editable.Should().BeFalse();
            eviction.Value.Should().Be(20L);
            eviction.DefaultValue.Should().Be(100L);
            eviction.HowToChange.Should().Contain("ffprobe cache entries");

            list.Where(s => !s.Editable).Should().OnlyContain(s => s.SourceText == "Fixed in this build" && !string.IsNullOrWhiteSpace(s.HowToChange));
            list.Where(s => s.Editable).Should().OnlyContain(s => s.HowToChange.Contains(s.EnvironmentVariable));
        }

        // The list shows the value in effect: the max result size is capped by the cache memory limit, and a note
        // says so (the field itself keeps the saved value).
        [Test]
        public void settings_list_should_show_the_capped_max_result_size_in_effect()
        {
            Subject.Save(new Dictionary<string, object> { { OptimizeTunables.FfprobeCacheMaxEntryKilobytes, 131_072 } });

            var info = Subject.GetSettings().Single(s => s.Name == OptimizeTunables.FfprobeCacheMaxEntryKilobytes);

            Subject.Current.FfprobeCache.MaxEntryBytes.Should().Be(32L * 1024 * 1024);
            info.Value.Should().Be(32_768L);
            info.SavedValue.Should().Be(131_072L);
            info.Source.Should().Be("database");
            info.Note.Should().Contain("32768 KB").And.Contain("131072 KB").And.Contain("memory limit");

            Subject.Save(new Dictionary<string, object> { { OptimizeTunables.FfprobeCacheMaxMegabytes, 256 } });

            info = Subject.GetSettings().Single(s => s.Name == OptimizeTunables.FfprobeCacheMaxEntryKilobytes);
            info.Value.Should().Be(131_072L);
            info.Note.Should().BeNull();

            Subject.GetSettings().Where(s => s.Name != OptimizeTunables.FfprobeCacheMaxEntryKilobytes).Should().OnlyContain(s => s.Note == null);
        }

        [Test]
        public void risky_values_should_be_flagged()
        {
            Subject.Save(new Dictionary<string, object> { { OptimizeTunables.GrabSpacingMs, 500 }, { OptimizeTunables.CustomFormatCacheCapacity, 500_000 } });

            var list = Subject.GetSettings();

            list.Single(s => s.Name == OptimizeTunables.GrabSpacingMs).Risky.Should().BeTrue();
            list.Single(s => s.Name == OptimizeTunables.CustomFormatCacheCapacity).Risky.Should().BeTrue();
            list.Single(s => s.Name == OptimizeTunables.FfprobeCacheEntries).Risky.Should().BeFalse();
        }

        [Test]
        public void status_should_list_features_and_counters()
        {
            Subject.Reload();

            var status = Subject.GetStatus();

            status.Features.Should().Contain(f => f.Name == "Custom-format match cache" && f.Active);
            status.Features.Should().Contain(f => f.Name == "Cache hit refund" && !f.Active && f.State == "Off");
            status.Counters.Select(c => c.Name).Should().Contain(new[]
            {
                "customFormatCacheEntries", "customFormatCacheHits", "customFormatCacheMisses", "ffprobeCacheEntries", "ffprobeCacheHits",
                "rateLimitRefunds", "pacedRequestsWaited", "pacedWaitTime", "ffprobeTimeouts"
            });

            Subject.Save(new Dictionary<string, object> { { OptimizeTunables.CustomFormatCacheCapacity, 0 }, { OptimizeTunables.CacheHitHeader, "X-Cache" } });

            status = Subject.GetStatus();
            status.Features.Should().Contain(f => f.Name == "Custom-format match cache" && !f.Active);
            status.Features.Should().Contain(f => f.Name == "Cache hit refund" && f.Active && f.State == "On, X-Cache: HIT");
        }

        [Test]
        public void status_counters_should_follow_the_caches()
        {
            Subject.Reload();

            var before = Subject.GetStatus().Counters.ToDictionary(c => c.Name, c => c.Value);

            Interlocked.Increment(ref OptimizeCounters.Refunds);
            Interlocked.Increment(ref CustomFormatMatchCache.Hits);

            var after = Subject.GetStatus().Counters.ToDictionary(c => c.Name, c => c.Value);

            after["rateLimitRefunds"].Should().Be(before["rateLimitRefunds"] + 1);
            after["customFormatCacheHits"].Should().Be(before["customFormatCacheHits"] + 1);
        }
    }
}
