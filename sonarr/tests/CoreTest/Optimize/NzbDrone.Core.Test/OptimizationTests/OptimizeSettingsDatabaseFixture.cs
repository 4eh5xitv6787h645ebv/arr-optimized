using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Optimization;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.OptimizationTests
{
    // The real ConfigService on the test database: values are stored in the Config table under "optimize*" keys,
    // read back through ConfigService's cache, and the environment still wins.
    [TestFixture]
    [NonParallelizable]
    public class OptimizeSettingsDatabaseFixture : DbTest<OptimizeSettingsService, Config>
    {
        private Dictionary<string, string> _environment;

        [SetUp]
        public void Setup()
        {
            OptimizeSettings.Current = null;
            _environment = new Dictionary<string, string>();

            Mocker.SetConstant<IConfigRepository>(Mocker.Resolve<ConfigRepository>());
            Mocker.SetConstant<IConfigService>(Mocker.Resolve<ConfigService>());

            Subject.GetEnvironmentVariable = n => _environment.TryGetValue(n, out var v) ? v : null;
        }

        // DbTest.TearDown is not virtual; NUnit runs both [TearDown] methods.
        [TearDown]
        public new void TearDown()
        {
            OptimizeSettings.Current = null;
        }

        private OptimizeSettingsService NewService()
        {
            // A second service and config service on the same database, as after a restart.
            var service = new OptimizeSettingsService(new ConfigService(Mocker.Resolve<IConfigRepository>(), Mocker.Resolve<IEventAggregator>(), TestLogger), TestLogger);
            service.GetEnvironmentVariable = Subject.GetEnvironmentVariable;
            return service;
        }

        [Test]
        public void saved_values_should_be_stored_in_the_config_table_with_optimize_keys()
        {
            Subject.Refresh();
            Subject.Save(new Dictionary<string, object> { { "IndexerRateLimitMs", 750 }, { "CacheHitHeader", "X-Search-Cache" }, { "DecisionMemoEnabled", false } });

            var rows = Db.All<Config>().ToDictionary(c => c.Key, c => c.Value);
            rows["optimizeindexerratelimitms"].Should().Be("750");
            rows["optimizecachehitheader"].Should().Be("X-Search-Cache");
            rows["optimizedecisionmemoenabled"].Should().Be("false");

            Mocker.GetMock<IEventAggregator>().Verify(e => e.PublishEvent(It.IsAny<ConfigSavedEvent>()), Times.Once());
        }

        [Test]
        public void precedence_should_be_environment_then_database_then_default()
        {
            Subject.Refresh();
            Subject.Save(new Dictionary<string, object> { { "IndexerRateLimitMs", 750 }, { "GrabSpacingMs", 1500 } });

            // After a restart: the app's start-up event loads the database value.
            OptimizeSettings.Current = null;
            var restarted = NewService();
            restarted.Handle(new ApplicationStartedEvent());
            OptimizeSettings.Current.IndexerRateLimitMs.Should().Be(750);
            OptimizeSettings.Current.Resolved["IndexerRateLimitMs"].Source.Should().Be(OptimizeSettingSource.Saved);

            // The environment wins over the database.
            _environment[IndexerRateLimitOptions.RateLimitVariable] = "250";
            OptimizeSettings.Current = null;
            restarted = NewService();
            restarted.Handle(new ApplicationStartedEvent());
            OptimizeSettings.Current.IndexerRateLimitMs.Should().Be(250);
            OptimizeSettings.Current.Resolved["IndexerRateLimitMs"].Locked.Should().BeTrue();
            OptimizeSettings.Current.GrabSpacingMs.Should().Be(1500);

            // Neither: the default.
            _environment.Clear();
            restarted.Refresh();
            restarted.Save(new Dictionary<string, object> { { "IndexerRateLimitMs", null }, { "GrabSpacingMs", null } });
            OptimizeSettings.Current.IndexerRateLimitMs.Should().Be(2000);
            OptimizeSettings.Current.GrabSpacingMs.Should().Be(2000);
            OptimizeSettings.Current.Resolved.Values.Should().OnlyContain(r => r.Source == OptimizeSettingSource.Default);
            IndexerRateLimitOptions.Current.IsStock.Should().BeTrue();
        }

        // Values saved on the page apply again after a restart, from start-up on, before any other config is saved.
        [Test]
        public void saved_values_should_apply_after_a_restart_through_the_startup_event()
        {
            Subject.Refresh();
            Subject.Save(new Dictionary<string, object>
            {
                { "IndexerRateLimitMs", 750 }, { "CacheHitHeader", "X-Search-Cache" }, { "FfprobeTimeoutMinutes", 30 },
                { "CfCacheCapacity", 1234 }, { "DecisionMemoEnabled", false }
            });

            // A new process: nothing published yet, so only the environment and the defaults apply.
            OptimizeSettings.Current = null;
            OptimizeSettings.Current.IndexerRateLimitMs.Should().Be(2000);
            OptimizeSettings.Current = null;

            var restarted = NewService();
            restarted.Handle(new ApplicationStartedEvent());

            var current = OptimizeSettings.Current;
            current.IndexerRateLimitMs.Should().Be(750);
            current.RateLimit.CacheHitHeader.Should().Be("X-Search-Cache");
            current.FfprobeTimeout.Should().Be(TimeSpan.FromMinutes(30));
            current.CfCacheCapacity.Should().Be(1234);
            current.DecisionMemoEnabled.Should().BeFalse();
            current.Resolved["IndexerRateLimitMs"].Source.Should().Be(OptimizeSettingSource.Saved);
            IndexerRateLimitOptions.Current.BaseRateLimit.Should().Be(TimeSpan.FromMilliseconds(750));
            restarted.GetSettings().Single(s => s.Name == "FfprobeTimeoutMinutes").Source.Should().Be("saved");
        }

        [Test]
        public void saving_the_same_values_should_not_write_or_publish()
        {
            Subject.Refresh();
            Subject.Save(new Dictionary<string, object> { { "FfprobeTimeoutMinutes", 30 } });
            Subject.Save(new Dictionary<string, object> { { "FfprobeTimeoutMinutes", 30 } });

            Mocker.GetMock<IEventAggregator>().Verify(e => e.PublishEvent(It.IsAny<ConfigSavedEvent>()), Times.Once());
            OptimizeSettings.Current.FfprobeTimeout.Should().Be(TimeSpan.FromMinutes(30));
        }

        [Test]
        public void other_settings_saved_through_the_config_dictionary_should_not_touch_optimize_values()
        {
            Subject.Refresh();
            Subject.Save(new Dictionary<string, object> { { "CfCacheCapacity", 1234 } });

            var configService = Mocker.Resolve<IConfigService>();
            configService.SaveConfigDictionary(new Dictionary<string, object> { { "RssSyncInterval", 30 }, { "CfCacheCapacity", 99 } });
            Subject.Handle(new ConfigSavedEvent());

            configService.RssSyncInterval.Should().Be(30);
            OptimizeSettings.Current.CfCacheCapacity.Should().Be(1234);
        }
    }
}
