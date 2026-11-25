using System;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Optimize;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.OptimizeTests
{
    // Optimize values are stored in the Config table under "optimize..." keys and survive a new ConfigService
    // (a restart); the settings service reads them from there.
    [TestFixture]
    [NonParallelizable]
    public class OptimizeConfigServiceFixture : DbTest<ConfigService, Config>
    {
        [SetUp]
        public void Setup()
        {
            OptimizeSettings.Reset();
            Mocker.SetConstant<IConfigRepository>(Mocker.Resolve<ConfigRepository>());
        }

        // DbTest.TearDown is not virtual; NUnit runs both [TearDown] methods.
        [TearDown]
        public new void TearDown()
        {
            OptimizeSettings.Reset();
        }

        private ConfigService Restarted()
        {
            return new ConfigService(Mocker.Resolve<IConfigRepository>(), Mocker.Resolve<IEventAggregator>(), TestLogger);
        }

        [Test]
        public void values_should_round_trip_through_the_config_table()
        {
            Subject.OptimizeIndexerRateLimitMs.Should().BeNull();
            Subject.OptimizeCacheHitValue.Should().BeNull();

            Subject.OptimizeIndexerRateLimitMs = 500;
            Subject.OptimizeCacheHitHeader = "X-Cache";
            Subject.OptimizeFfprobeTimeoutMinutes = 0;
            Subject.SetOptimizeValue("OptimizeFfprobeCacheEntries", "10");

            var restarted = Restarted();
            restarted.OptimizeIndexerRateLimitMs.Should().Be(500);
            restarted.OptimizeCacheHitHeader.Should().Be("X-Cache");
            restarted.OptimizeFfprobeTimeoutMinutes.Should().Be(0);
            restarted.GetOptimizeValue("OptimizeFfprobeCacheEntries").Should().Be("10");

            Db.All<Config>().Should().Contain(c => c.Key == "optimizeindexerratelimitms" && c.Value == "500");

            restarted.OptimizeIndexerRateLimitMs = null;
            Restarted().OptimizeIndexerRateLimitMs.Should().BeNull();
        }

        [Test]
        public void other_keys_should_be_refused()
        {
            Assert.Throws<ArgumentException>(() => Subject.SetOptimizeValue("ApiKey", "x"));
            Assert.Throws<ArgumentException>(() => Subject.GetOptimizeValue("RssSyncInterval"));
        }

        [Test]
        public void settings_service_should_load_and_save_through_the_database()
        {
            Subject.SetOptimizeValue(OptimizeTunables.Get(OptimizeTunables.IndexerRateLimitMs).ConfigKey, "300");

            var service = new OptimizeSettingsService(Subject, new Mock<IVideoFileInfoReader>().Object, TestLogger);
            service.Reload();

            IndexerRateLimitOptions.Current.BaseRateLimit.Should().Be(TimeSpan.FromMilliseconds(300));

            service.Save(new System.Collections.Generic.Dictionary<string, object>
            {
                { OptimizeTunables.CustomFormatCacheCapacity, 1234 },
                { OptimizeTunables.IndexerRateLimitMs, null }
            });

            var restarted = Restarted();
            restarted.GetOptimizeValue(OptimizeTunables.Get(OptimizeTunables.CustomFormatCacheCapacity).ConfigKey).Should().Be("1234");
            restarted.OptimizeIndexerRateLimitMs.Should().BeNull();

            OptimizeSettings.Reset();
            new OptimizeSettingsService(restarted, new Mock<IVideoFileInfoReader>().Object, TestLogger).Reload();
            OptimizeSettings.Current.CustomFormatCacheCapacity.Should().Be(1234);
            IndexerRateLimitOptions.Current.IsStock.Should().BeTrue();
        }
    }
}
