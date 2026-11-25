using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Optimization;
using Sonarr.Api.V3.Config;

namespace NzbDrone.Api.Test.v3.Config
{
    // GET/PUT round trip of /api/v3/config/optimize through the controller and the real OptimizeSettingsService,
    // with the stored config values in memory.
    [TestFixture]
    [NonParallelizable]
    public class OptimizeConfigControllerFixture
    {
        private Dictionary<string, string> _saved;
        private Mock<IConfigService> _configService;
        private TestController _controller;

        private class TestController : OptimizeConfigController
        {
            public TestController(IConfigService configService, IOptimizeSettingsService optimizeSettingsService)
                : base(configService, optimizeSettingsService)
            {
            }

            public List<ValidationFailure> Validate(OptimizeConfigResource resource)
            {
                return SharedValidator.Validate(resource).Errors.ToList();
            }
        }

        [SetUp]
        public void Setup()
        {
            Environment.SetEnvironmentVariable(IndexerRateLimitOptions.RateLimitVariable, null);
            _saved = new Dictionary<string, string>();
            _configService = new Mock<IConfigService>();

            _configService.Setup(c => c.GetOptimizeValue(It.IsAny<string>()))
                .Returns<string>(n => _saved.TryGetValue(n, out var v) && !string.IsNullOrEmpty(v) ? v : null);
            _configService.Setup(c => c.SaveOptimizeValues(It.IsAny<IDictionary<string, string>>()))
                .Callback<IDictionary<string, string>>(values =>
                {
                    foreach (var pair in values)
                    {
                        _saved[pair.Key] = pair.Value;
                    }
                });

            var service = new OptimizeSettingsService(_configService.Object, LogManager.GetLogger("test"));
            service.Refresh();

            _controller = new TestController(_configService.Object, service);
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable(IndexerRateLimitOptions.RateLimitVariable, null);
            new OptimizeSettingsService(new Mock<IConfigService>().Object, LogManager.GetLogger("test")).Refresh();
        }

        [Test]
        public void resource_should_have_a_value_for_every_setting()
        {
            var properties = typeof(OptimizeConfigResource).GetProperties().Select(p => p.Name).ToList();

            properties.Should().Contain(OptimizeSettingDefinitions.All.Select(d => d.Name));
        }

        [Test]
        public void get_should_return_unsaved_values_as_null_and_the_settings_list()
        {
            var resource = _controller.GetConfig();

            resource.Id.Should().Be(1);
            resource.IndexerRateLimitMs.Should().BeNull();
            resource.CacheHitHeader.Should().BeNull();
            resource.FfprobeTimeoutMinutes.Should().BeNull();
            resource.DecisionMemoEnabled.Should().BeTrue();
            resource.Settings.Should().Contain(s => s.Name == "IndexerRateLimitMs" && (int)s.Value == 2000 && s.Source == "default");
            resource.Settings.Should().Contain(s => s.Name == "FfprobeTimeoutMinutes" && (int)s.Value == 15);
        }

        [Test]
        public void put_then_get_should_round_trip_and_apply_at_once()
        {
            var resource = _controller.GetConfig();
            resource.IndexerRateLimitMs = 500;
            resource.CacheHitHeader = "X-Search-Cache";
            resource.FfprobeTimeoutMinutes = 0;
            resource.CfCacheCapacity = 1000;
            resource.DecisionMemoEnabled = false;

            _controller.Validate(resource).Should().BeEmpty();
            var result = _controller.SaveConfig(resource);

            var accepted = result.Result as AcceptedAtActionResult;
            accepted.Should().NotBeNull();
            var returned = accepted.Value as OptimizeConfigResource;
            returned.IndexerRateLimitMs.Should().Be(500);

            var reloaded = _controller.GetConfig();
            reloaded.IndexerRateLimitMs.Should().Be(500);
            reloaded.CacheHitHeader.Should().Be("X-Search-Cache");
            reloaded.FfprobeTimeoutMinutes.Should().Be(0);
            reloaded.CfCacheCapacity.Should().Be(1000);
            reloaded.DecisionMemoEnabled.Should().BeFalse();
            reloaded.Settings.Single(s => s.Name == "IndexerRateLimitMs").Source.Should().Be("saved");

            _saved["IndexerRateLimitMs"].Should().Be("500");
            IndexerRateLimitOptions.Current.BaseRateLimit.Should().Be(TimeSpan.FromMilliseconds(500));
            IndexerRateLimitOptions.Current.CacheHitHeader.Should().Be("X-Search-Cache");

            // Reset to default: null.
            reloaded.IndexerRateLimitMs = null;
            reloaded.CacheHitHeader = null;
            _controller.SaveConfig(reloaded);

            _controller.GetConfig().IndexerRateLimitMs.Should().BeNull();
            IndexerRateLimitOptions.Current.BaseRateLimit.Should().Be(TimeSpan.FromSeconds(2));
            IndexerRateLimitOptions.Current.RefundsCacheHits.Should().BeFalse();
        }

        // PUT changes only the values present in the JSON body (as the app reads it): a value left out keeps its saved
        // value, an explicit null resets it to the default.
        [Test]
        public void put_should_change_only_the_values_present_in_the_body()
        {
            _controller.SaveConfig(STJson.Deserialize<OptimizeConfigResource>("{\"id\":1,\"grabSpacingMs\":1000,\"cacheHitHeader\":\"X-Search-Cache\",\"decisionMemoEnabled\":false}"));

            var partial = STJson.Deserialize<OptimizeConfigResource>("{\"id\":1,\"ffprobeTimeoutMinutes\":30}");
            partial.ToValues().Keys.Should().BeEquivalentTo("FfprobeTimeoutMinutes");
            _controller.Validate(partial).Should().BeEmpty();
            _controller.SaveConfig(partial);

            var reloaded = _controller.GetConfig();
            reloaded.FfprobeTimeoutMinutes.Should().Be(30);
            reloaded.GrabSpacingMs.Should().Be(1000);
            reloaded.CacheHitHeader.Should().Be("X-Search-Cache");
            reloaded.DecisionMemoEnabled.Should().BeFalse();

            _controller.SaveConfig(STJson.Deserialize<OptimizeConfigResource>("{\"id\":1,\"grabSpacingMs\":null}"));

            reloaded = _controller.GetConfig();
            reloaded.GrabSpacingMs.Should().BeNull();
            reloaded.Settings.Single(s => s.Name == "GrabSpacingMs").Source.Should().Be("default");
            reloaded.FfprobeTimeoutMinutes.Should().Be(30);
            reloaded.CacheHitHeader.Should().Be("X-Search-Cache");

            // The full resource the page sends (every value, as returned by GET) still works as before.
            var full = STJson.Deserialize<OptimizeConfigResource>(STJson.ToJson(reloaded));
            full.ToValues().Keys.Should().Contain("FfprobeTimeoutMinutes");
            _controller.SaveConfig(full);
            _controller.GetConfig().FfprobeTimeoutMinutes.Should().Be(30);
        }

        [Test]
        public void invalid_values_should_fail_validation_per_field()
        {
            var resource = _controller.GetConfig();
            resource.IndexerRateLimitMs = 20000;
            resource.CacheHitHeader = "X Search";
            resource.CfCacheCapacity = -1;
            resource.FfprobeCacheMaxMb = 2048;

            var errors = _controller.Validate(resource);

            errors.Select(e => e.PropertyName).Should().BeEquivalentTo("IndexerRateLimitMs", "CacheHitHeader", "CfCacheCapacity", "FfprobeCacheMaxMb");
            errors.Single(e => e.PropertyName == "IndexerRateLimitMs").ErrorMessage.Should().Contain("0 to 10000");
        }

        [Test]
        public void locked_value_should_be_reported_and_ignored_on_put()
        {
            Environment.SetEnvironmentVariable(IndexerRateLimitOptions.RateLimitVariable, "300");
            var service = new OptimizeSettingsService(_configService.Object, LogManager.GetLogger("test"));
            service.Refresh();
            _controller = new TestController(_configService.Object, service);

            var resource = _controller.GetConfig();
            resource.IndexerRateLimitMs.Should().Be(300);

            var info = resource.Settings.Single(s => s.Name == "IndexerRateLimitMs");
            info.Locked.Should().BeTrue();
            info.Editable.Should().BeFalse();
            info.Source.Should().Be("environment");
            info.EnvironmentVariable.Should().Be("ARR_INDEXER_RATE_LIMIT_MS");

            resource.IndexerRateLimitMs = 99999;
            _controller.Validate(resource).Should().BeEmpty();
            _controller.SaveConfig(resource);

            _saved.Should().NotContainKey("IndexerRateLimitMs");
            _controller.GetConfig().IndexerRateLimitMs.Should().Be(300);
            IndexerRateLimitOptions.Current.BaseRateLimit.Should().Be(TimeSpan.FromMilliseconds(300));
        }

        [Test]
        public void status_should_list_optimizations_and_counters()
        {
            var status = _controller.GetStatus();

            status.Optimizations.Should().NotBeEmpty();
            status.Counters.Should().Contain(c => c.Name == "cfCacheHits");
            _controller.GetSettings().Should().HaveCount(_controller.GetConfig().Settings.Count);
        }
    }
}
