using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.BroadcastheNet;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Optimization;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.OptimizationTests
{
    // Precedence (environment > saved > default), validation, saving and the list of all settings.
    [TestFixture]
    [NonParallelizable]
    public class OptimizeSettingsServiceFixture : CoreTest<OptimizeSettingsService>
    {
        private Dictionary<string, string> _saved;
        private Dictionary<string, string> _environment;

        [SetUp]
        public void Setup()
        {
            OptimizeSettings.Current = null;
            _saved = new Dictionary<string, string>();
            _environment = new Dictionary<string, string>();

            Mocker.GetMock<IConfigService>()
                .Setup(c => c.GetOptimizeValue(It.IsAny<string>()))
                .Returns<string>(n => _saved.TryGetValue(n, out var v) && !string.IsNullOrEmpty(v) ? v : null);

            Mocker.GetMock<IConfigService>()
                .Setup(c => c.SaveOptimizeValues(It.IsAny<IDictionary<string, string>>()))
                .Callback<IDictionary<string, string>>(values =>
                {
                    foreach (var pair in values)
                    {
                        _saved[pair.Key] = pair.Value;
                    }
                });

            Subject.GetEnvironmentVariable = n => _environment.TryGetValue(n, out var v) ? v : null;
        }

        [TearDown]
        public void TearDown()
        {
            OptimizeSettings.Current = null;
            CustomFormatMatchCache.Apply(OptimizeSettings.Current);
        }

        private static OptimizeSettingDefinition Definition(string name)
        {
            return OptimizeSettingDefinitions.Get(name);
        }

        // A valid saved value and a different valid environment value, both different from the default.
        private static (string Saved, string Environment) Samples(OptimizeSettingDefinition definition)
        {
            switch (definition.Type)
            {
                case OptimizeValueType.Integer:
                    var saved = definition.Min == Convert.ToInt64(definition.DefaultValue) ? definition.Min + 1 : definition.Min;
                    return (saved.ToString(), definition.Max.ToString());
                case OptimizeValueType.Boolean:
                    return ((!(bool)definition.DefaultValue).ToString(), (!(bool)definition.DefaultValue) ? "no" : "yes");
                default:
                    return definition.Name == "CacheHitHeader" ? ("X-Saved", "X-Environment") : ("SAVED", "ENVIRONMENT");
            }
        }

        private static object Parsed(OptimizeSettingDefinition definition, string text)
        {
            definition.TryParse(text, true, out var value, out _).Should().BeTrue();
            return value;
        }

        [Test]
        public void defaults_should_be_stock_and_equal_the_series_constants()
        {
            Subject.Refresh();
            var settings = OptimizeSettings.Current;

            settings.RateLimit.IsStock.Should().BeTrue();
            settings.RateLimit.BaseRateLimit.Should().Be(IndexerRateLimitOptions.StockRateLimit);
            settings.RateLimit.GrabSpacing.Should().Be(IndexerRateLimitOptions.GrabRateLimit);
            settings.RateLimit.CacheHitHeader.Should().BeNull();
            settings.RateLimit.CacheHitValue.Should().Be(IndexerRateLimitOptions.DefaultCacheHitValue);
            settings.FfprobeTimeout.Should().Be(TimeSpan.FromMinutes(15));
            settings.FfprobeTimeout.Should().Be(VideoFileInfoReader.DefaultProbeTimeout);
            settings.DecisionMemoEnabled.Should().BeTrue();

            settings.CfCacheCapacity.Should().Be(CustomFormatMatchCache.Capacity);
            settings.CfCacheLifetimeMs.Should().Be(CustomFormatMatchCache.SlidingLifetimeMs);
            settings.CfCacheMaxKeyChars.Should().Be(CustomFormatMatchCache.MaxKeyText);
            settings.CfCacheSweepMs.Should().Be(CustomFormatMatchCache.SweepIntervalMs);
            settings.CfCacheEvictionBatch.Should().Be(CustomFormatMatchCache.EvictionBatch);

            settings.ProbeCache.Enabled.Should().BeTrue();
            settings.ProbeCache.Capacity.Should().Be(MediaInfoProbeCache.Capacity);
            settings.ProbeCache.EvictionBatch.Should().Be(MediaInfoProbeCache.EvictionBatch);
            settings.ProbeCache.MaxBytes.Should().Be(MediaInfoProbeCache.MaxBytes);
            settings.ProbeCache.MaxEntryBytes.Should().Be(MediaInfoProbeCache.MaxEntryBytes);
            settings.ProbeCache.IdleLifetimeMs.Should().Be(MediaInfoProbeCache.IdleLifetimeMs);
            settings.ProbeCache.FailureLifetimeMs.Should().Be(MediaInfoProbeCache.FailureLifetimeMs);
            settings.ProbeCache.SettleTime.Should().Be(MediaInfoProbeCache.SettleTime);
            settings.ProbeCache.UnsettledLifetimeMs.Should().Be(MediaInfoProbeCache.UnsettledLifetimeMs);
            settings.ProbeCache.SweepIntervalMs.Should().Be(MediaInfoProbeCache.SweepIntervalMs);

            settings.Resolved.Values.Should().OnlyContain(r => r.Source == OptimizeSettingSource.Default && !r.Locked);
        }

        [Test]
        public void every_value_should_use_its_default_when_nothing_is_set()
        {
            Subject.Refresh();

            foreach (var definition in OptimizeSettingDefinitions.All)
            {
                var resolved = OptimizeSettings.Current.Resolved[definition.Name];
                resolved.Value.Should().Be(definition.DefaultValue, definition.Name);
                resolved.Source.Should().Be(OptimizeSettingSource.Default, definition.Name);
            }
        }

        [Test]
        public void saved_value_should_win_over_default()
        {
            foreach (var definition in OptimizeSettingDefinitions.All)
            {
                _saved[definition.Name] = Samples(definition).Saved;
            }

            Subject.Refresh();

            foreach (var definition in OptimizeSettingDefinitions.All)
            {
                var resolved = OptimizeSettings.Current.Resolved[definition.Name];
                resolved.Value.Should().Be(Parsed(definition, Samples(definition).Saved), definition.Name);
                resolved.Source.Should().Be(OptimizeSettingSource.Saved, definition.Name);
                resolved.Locked.Should().BeFalse();
            }
        }

        [Test]
        public void environment_should_win_over_saved_value_and_lock_it()
        {
            foreach (var definition in OptimizeSettingDefinitions.All)
            {
                _saved[definition.Name] = Samples(definition).Saved;
                _environment[definition.EnvironmentVariable] = Samples(definition).Environment;
            }

            Subject.Refresh();

            foreach (var definition in OptimizeSettingDefinitions.All)
            {
                var resolved = OptimizeSettings.Current.Resolved[definition.Name];
                resolved.Value.Should().Be(Parsed(definition, Samples(definition).Environment), definition.Name);
                resolved.Source.Should().Be(OptimizeSettingSource.Environment, definition.Name);
                resolved.Locked.Should().BeTrue();
                resolved.SavedValue.Should().Be(Parsed(definition, Samples(definition).Saved), definition.Name);
            }

            Subject.GetSettings().Where(s => s.Source == "environment").Should().HaveCount(OptimizeSettingDefinitions.All.Count)
                .And.OnlyContain(s => s.Locked && !s.Editable && s.HowToChange.Contains(s.EnvironmentVariable));
        }

        [Test]
        public void invalid_environment_value_should_be_ignored_with_a_warning()
        {
            _saved["IndexerRateLimitMs"] = "700";
            _environment[IndexerRateLimitOptions.RateLimitVariable] = "fast";
            _environment[OptimizeSettingDefinitions.GrabSpacingVariable] = "-1";

            Subject.Refresh();

            var rateLimit = OptimizeSettings.Current.Resolved["IndexerRateLimitMs"];
            rateLimit.Value.Should().Be(700);
            rateLimit.Source.Should().Be(OptimizeSettingSource.Saved);
            rateLimit.Locked.Should().BeFalse();
            rateLimit.EnvironmentError.Should().NotBeNullOrEmpty();

            OptimizeSettings.Current.GrabSpacingMs.Should().Be(2000);
            OptimizeSettings.Current.Resolved["GrabSpacingMs"].Source.Should().Be(OptimizeSettingSource.Default);

            ExceptionVerification.ExpectedWarns(2);
        }

        [Test]
        public void invalid_saved_value_should_use_the_default_with_a_warning()
        {
            _saved["CfCacheCapacity"] = "2000000";
            _saved["CacheHitHeader"] = "X Bad Header";

            Subject.Refresh();

            OptimizeSettings.Current.CfCacheCapacity.Should().Be(CustomFormatMatchCache.Capacity);
            OptimizeSettings.Current.Resolved["CfCacheCapacity"].SavedError.Should().NotBeNullOrEmpty();
            OptimizeSettings.Current.RateLimit.RefundsCacheHits.Should().BeFalse();

            ExceptionVerification.ExpectedWarns(2);
        }

        [Test]
        public void environment_may_exceed_the_page_range_for_the_indexer_rate_limit()
        {
            _environment[IndexerRateLimitOptions.RateLimitVariable] = "86400000";

            Subject.Refresh();

            OptimizeSettings.Current.RateLimit.BaseRateLimit.Should().Be(TimeSpan.FromDays(1));

            OptimizeSettings.Current = null;
            _environment.Clear();
            Subject.Refresh();

            Subject.Validate(new Dictionary<string, object> { { "IndexerRateLimitMs", 10001 } })
                .Should().ContainSingle(f => f.Name == "IndexerRateLimitMs" && f.Message.Contains("0 to 10000"));
        }

        [Test]
        public void environment_warnings_should_be_logged_once()
        {
            _environment[IndexerRateLimitOptions.RateLimitVariable] = "fast";

            Subject.Refresh();
            Subject.Handle(new ConfigSavedEvent());
            Subject.Refresh();

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void validation_should_accept_bounds_and_reject_values_outside_them()
        {
            Subject.Refresh();

            foreach (var definition in OptimizeSettingDefinitions.All.Where(d => d.Type == OptimizeValueType.Integer))
            {
                Subject.Validate(new Dictionary<string, object> { { definition.Name, (int)definition.Min } }).Should().BeEmpty(definition.Name);
                Subject.Validate(new Dictionary<string, object> { { definition.Name, (int)definition.Max } }).Should().BeEmpty(definition.Name);
                Subject.Validate(new Dictionary<string, object> { { definition.Name, null } }).Should().BeEmpty(definition.Name);

                Subject.Validate(new Dictionary<string, object> { { definition.Name, (int)definition.Max + 1 } })
                    .Should().ContainSingle(definition.Name).Which.Name.Should().Be(definition.Name);
                Subject.Validate(new Dictionary<string, object> { { definition.Name, (int)definition.Min - 1 } })
                    .Should().ContainSingle(definition.Name).Which.Name.Should().Be(definition.Name);
            }
        }

        [TestCase("CacheHitHeader", "X-Search-Cache", true)]
        [TestCase("CacheHitHeader", "X Search Cache", false)]
        [TestCase("CacheHitHeader", "X-Search-Cache:", false)]
        [TestCase("CacheHitHeader", "X-Säarch", false)]
        [TestCase("CacheHitValue", "HIT", true)]
        [TestCase("CacheHitValue", "HIT, MISS", false)]
        [TestCase("CacheHitValue", "a;b", false)]
        [TestCase("CacheHitValue", "a\tb", false)]
        public void validation_should_check_header_name_and_value(string name, string value, bool valid)
        {
            Subject.Refresh();

            Subject.Validate(new Dictionary<string, object> { { name, value } }).Should().HaveCount(valid ? 0 : 1);
        }

        [Test]
        public void save_should_store_values_and_apply_them_without_restart()
        {
            Subject.Refresh();
            IndexerRateLimitOptions.Current.BaseRateLimit.Should().Be(TimeSpan.FromSeconds(2));

            Subject.Save(new Dictionary<string, object> { { "IndexerRateLimitMs", 500 }, { "GrabSpacingMs", 1000 }, { "CacheHitHeader", "X-Search-Cache" } });

            _saved["IndexerRateLimitMs"].Should().Be("500");
            IndexerRateLimitOptions.Current.BaseRateLimit.Should().Be(TimeSpan.FromMilliseconds(500));
            IndexerRateLimitOptions.Current.GrabSpacing.Should().Be(TimeSpan.FromSeconds(1));
            IndexerRateLimitOptions.Current.CacheHitHeader.Should().Be("X-Search-Cache");
            Mocker.Resolve<Newznab>().RateLimit.Should().Be(TimeSpan.FromMilliseconds(500));
            Mocker.Resolve<BroadcastheNet>().RateLimit.Should().Be(TimeSpan.FromSeconds(5));

            Subject.Save(new Dictionary<string, object> { { "IndexerRateLimitMs", null }, { "GrabSpacingMs", null }, { "CacheHitHeader", string.Empty } });

            _saved["IndexerRateLimitMs"].Should().BeEmpty();
            IndexerRateLimitOptions.Current.IsStock.Should().BeTrue();
            Mocker.Resolve<Newznab>().RateLimit.Should().Be(TimeSpan.FromSeconds(2));
        }

        // A cleared cache hit header is logged as "off", not as an empty value.
        [Test]
        public void clearing_the_cache_hit_header_should_log_off()
        {
            Subject.Refresh();

            var messages = CaptureLog(() =>
            {
                Subject.Save(new Dictionary<string, object> { { "CacheHitHeader", "X-Cache" } });
                Subject.Save(new Dictionary<string, object> { { "CacheHitHeader", null } });
            });

            messages.Should().Contain("Optimize: Cache hit header = X-Cache (saved on the Optimize page, default off)");
            messages.Should().Contain("Optimize: Cache hit header = off (default)");
            messages.Should().NotContain(m => m.Contains(" =  ") || m.Contains("default empty"));
        }

        private static List<string> CaptureLog(Action action)
        {
            var target = new NLog.Targets.MemoryTarget { Name = "OptimizeSettingsServiceFixture", Layout = "${message}" };
            var rule = new NLog.Config.LoggingRule("*", NLog.LogLevel.Info, target);

            NLog.LogManager.Configuration.AddTarget(target);
            NLog.LogManager.Configuration.LoggingRules.Add(rule);
            NLog.LogManager.ReconfigExistingLoggers();

            try
            {
                action();
            }
            finally
            {
                NLog.LogManager.Configuration.LoggingRules.Remove(rule);
                NLog.LogManager.Configuration.RemoveTarget(target.Name);
                NLog.LogManager.ReconfigExistingLoggers();
            }

            return target.Logs.ToList();
        }

        [Test]
        public void save_should_reject_invalid_values()
        {
            Subject.Refresh();

            Assert.Throws<ArgumentException>(() => Subject.Save(new Dictionary<string, object> { { "FfprobeTimeoutMinutes", 5000 } }));

            _saved.Should().BeEmpty();
        }

        [Test]
        public void save_should_ignore_locked_values()
        {
            _environment[IndexerRateLimitOptions.RateLimitVariable] = "300";
            Subject.Refresh();

            // The locked value is ignored, even when it is invalid for the page.
            Subject.Validate(new Dictionary<string, object> { { "IndexerRateLimitMs", 99999 } }).Should().BeEmpty();
            Subject.Save(new Dictionary<string, object> { { "IndexerRateLimitMs", 99999 }, { "GrabSpacingMs", 3000 } });

            _saved.Should().NotContainKey("IndexerRateLimitMs");
            _saved["GrabSpacingMs"].Should().Be("3000");
            OptimizeSettings.Current.IndexerRateLimitMs.Should().Be(300);
            OptimizeSettings.Current.GrabSpacingMs.Should().Be(3000);
            Subject.GetValues()["IndexerRateLimitMs"].Should().Be(300);
        }

        [Test]
        public void boolean_equal_to_default_should_not_be_saved()
        {
            Subject.Refresh();

            Subject.Save(new Dictionary<string, object> { { "DecisionMemoEnabled", true } });
            _saved["DecisionMemoEnabled"].Should().BeEmpty();
            OptimizeSettings.Current.DecisionMemoEnabled.Should().BeTrue();

            Subject.Save(new Dictionary<string, object> { { "DecisionMemoEnabled", false } });
            _saved["DecisionMemoEnabled"].Should().Be("false");
            OptimizeSettings.Current.DecisionMemoEnabled.Should().BeFalse();
        }

        [Test]
        public void config_saved_event_should_apply_values_saved_elsewhere()
        {
            Subject.Refresh();
            _saved["FfprobeTimeoutMinutes"] = "1";

            Subject.Handle(new ConfigSavedEvent());

            VideoFileInfoReader.ProbeTimeout.Should().Be(TimeSpan.FromMinutes(1));
        }

        [Test]
        public void get_values_should_return_null_for_values_that_are_not_saved()
        {
            _saved["GrabSpacingMs"] = "2500";
            Subject.Refresh();

            var values = Subject.GetValues();

            values.Keys.Should().BeEquivalentTo(OptimizeSettingDefinitions.All.Select(d => d.Name));
            values["IndexerRateLimitMs"].Should().BeNull();
            values["GrabSpacingMs"].Should().Be(2500);
            values["DecisionMemoEnabled"].Should().Be(true);
        }

        [Test]
        public void settings_list_should_contain_every_value_with_current_default_and_source()
        {
            _saved["GrabSpacingMs"] = "500";
            Subject.Refresh();

            var list = Subject.GetSettings();

            list.Select(s => s.Name).Should().Contain(OptimizeSettingDefinitions.All.Select(d => d.Name));
            list.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.Label) && !string.IsNullOrWhiteSpace(s.Description) &&
                                           !string.IsNullOrWhiteSpace(s.HowToChange) && s.DefaultValue != null && s.Value != null);
            list.Where(s => !s.Editable && !s.Locked).Should().OnlyContain(s => s.Source == "derived" || s.Source == "fixed");

            var grab = list.Single(s => s.Name == "GrabSpacingMs");
            grab.Value.Should().Be(500);
            grab.DefaultValue.Should().Be(2000);
            grab.Source.Should().Be("saved");
            grab.CurrentValueRisky.Should().BeTrue();
            grab.Warning.Should().Contain("rate-limit or ban");

            list.Single(s => s.Name == "IndexerRateLimitMs").CurrentValueRisky.Should().BeFalse();
            list.Single(s => s.Name == "CfCacheEvictionBatch").Value.Should().Be(CustomFormatMatchCache.EvictionBatch);

            var likeLimit = list.Single(s => s.Name == "BlocklistLikePatternLimit");
            likeLimit.Value.Should().Be(50000);
            likeLimit.Editable.Should().BeFalse();
            likeLimit.HowToChange.Should().Contain("SqliteLike.MaxPatternBytes");
        }

        [Test]
        public void settings_list_should_name_every_constant_of_the_series()
        {
            Subject.Refresh();
            var list = Subject.GetSettings();
            var listed = list.SelectMany(s => s.Constants).ToHashSet();
            var variables = list.Select(s => s.EnvironmentVariable).Where(v => v != null).ToHashSet();

            // Constants that are not optimization tunables (stock media info schema revisions).
            var stock = new[] { "VideoFileInfoReader.MINIMUM_MEDIA_INFO_SCHEMA_REVISION", "VideoFileInfoReader.CURRENT_MEDIA_INFO_SCHEMA_REVISION" };
            var tunableTypes = new[] { typeof(int), typeof(long), typeof(TimeSpan), typeof(string) };

            var constants = new[] { typeof(IndexerRateLimitOptions), typeof(CustomFormatMatchCache), typeof(MediaInfoProbeCache), typeof(VideoFileInfoReader), typeof(SqliteLike) }
                .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(f => (f.IsLiteral || f.IsInitOnly) && tunableTypes.Contains(f.FieldType))
                    .Select(f => (Name: t.Name + "." + f.Name, Value: f.GetValue(null))))
                .Where(c => !stock.Contains(c.Name))
                .ToList();

            constants.Should().HaveCountGreaterThan(18);

            foreach (var constant in constants)
            {
                (listed.Contains(constant.Name) || variables.Contains(constant.Value as string)).Should().BeTrue(constant.Name + " is in the list");
            }

            // Every environment variable of the page is listed too.
            typeof(OptimizeSettingDefinitions).GetFields(BindingFlags.Static | BindingFlags.Public)
                .Where(f => f.IsLiteral && f.Name.EndsWith("Variable"))
                .Select(f => (string)f.GetValue(null))
                .Should().OnlyContain(v => variables.Contains(v));
        }

        [Test]
        public void status_should_list_built_in_optimizations_and_counters()
        {
            Subject.Refresh();

            var status = Subject.GetStatus();

            status.Optimizations.Should().HaveCountGreaterOrEqualTo(10);
            status.Optimizations.Should().OnlyContain(o => !string.IsNullOrWhiteSpace(o.Name) && !string.IsNullOrWhiteSpace(o.State));
            status.Counters.Select(c => c.Name).Should().Contain(new[]
            {
                "cfCacheEntries", "cfCacheHits", "cfCacheMisses", "ffprobeCacheEntries", "ffprobeCacheHits", "rateLimitRefunds",
                "rateLimitDelayedRequests", "rateLimitWaitMs", "memoHits", "decisionBatches"
            });
            status.StartedAt.Should().BeBefore(DateTime.UtcNow.AddSeconds(1));
        }
    }
}
