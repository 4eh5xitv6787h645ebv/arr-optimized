using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using System.Reflection;
using FluentValidation;
using FluentValidation.TestHelper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Optimize;
using Radarr.Api.V3.Config;

namespace NzbDrone.Api.Test.v3.Config
{
    // GET/PUT /api/v3/config/optimize round trip through the controller and the real settings service (in-memory
    // Config table), and the field validation the API returns as 400.
    [TestFixture]
    [NonParallelizable]
    public class OptimizeConfigControllerFixture
    {
        private Dictionary<string, string> _store;
        private OptimizeSettingsService _service;
        private OptimizeConfigController _controller;

        [SetUp]
        public void Setup()
        {
            OptimizeSettings.Load(_ => null, 1);

            _store = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var configService = new Mock<IConfigService>();
            configService.Setup(s => s.GetOptimizeValue(It.IsAny<string>()))
                .Returns<string>(key => _store.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null);
            configService.Setup(s => s.SetOptimizeValue(It.IsAny<string>(), It.IsAny<string>()))
                .Callback<string, string>((key, value) => _store[key] = value ?? string.Empty);
            configService.SetupGet(s => s.CheckForFinishedDownloadInterval).Returns(1);

            _service = new OptimizeSettingsService(configService.Object, new Mock<IVideoFileInfoReader>().Object, LogManager.GetCurrentClassLogger());
            _service.Reload();

            _controller = new OptimizeConfigController(_service)
            {
                Url = new Mock<IUrlHelper>().Object
            };
        }

        [TearDown]
        public void TearDown()
        {
            OptimizeSettings.Load(_ => null, 1);
        }

        private OptimizeConfigResource Put(OptimizeConfigResource resource)
        {
            var result = _controller.SaveOptimizeConfig(resource);

            return ((Accepted<OptimizeConfigResource>)result.Result).Value;
        }

        [Test]
        public void get_should_return_defaults_as_empty_and_every_tunable()
        {
            var resource = _controller.GetOptimizeConfig();

            resource.Id.Should().Be(1);
            resource.IndexerRateLimitMs.Should().BeNull();
            resource.CacheHitHeader.Should().BeNull();
            resource.FfprobeTimeoutMinutes.Should().BeNull();
            resource.CustomFormatCacheCapacity.Should().BeNull();
            resource.DecisionBatchMemo.Should().BeTrue();
            resource.ReleaseProfileCache.Should().BeTrue();

            resource.Tunables.Select(t => t.Name).Should().BeEquivalentTo(OptimizeTunables.All.Select(t => t.Name));
            resource.Tunables.Single(t => t.Name == OptimizeTunables.FfprobeTimeoutMinutes).Value.Should().Be(15L);
            _controller.GetOptimizeSettings().Should().HaveCount(OptimizeTunables.All.Count);
        }

        [Test]
        public void every_editable_tunable_should_have_a_resource_property()
        {
            OptimizeConfigResourceMapper.ValuePropertyNames.Select(n => n.ToLowerInvariant())
                .Should().BeEquivalentTo(OptimizeTunables.Editable.Select(t => t.Name.ToLowerInvariant()));
        }

        [Test]
        public void put_should_save_apply_and_return_the_new_values()
        {
            var resource = _controller.GetOptimizeConfig();
            resource.IndexerRateLimitMs = 400;
            resource.CacheHitHeader = "X-Search-Cache";
            resource.FfprobeCacheEntries = 0;
            resource.DecisionBatchMemo = false;

            var saved = Put(resource);

            saved.IndexerRateLimitMs.Should().Be(400);
            saved.CacheHitHeader.Should().Be("X-Search-Cache");
            saved.FfprobeCacheEntries.Should().Be(0);
            saved.DecisionBatchMemo.Should().BeFalse();
            saved.CustomFormatCacheCapacity.Should().BeNull();
            saved.Tunables.Single(t => t.Name == OptimizeTunables.IndexerRateLimitMs).Source.Should().Be("database");

            IndexerRateLimitOptions.Current.BaseRateLimit.Should().Be(TimeSpan.FromMilliseconds(400));
            OptimizeSettings.Current.FfprobeCache.Enabled.Should().BeFalse();
            OptimizeSettings.Current.DecisionBatchMemo.Should().BeFalse();

            var again = _controller.GetOptimizeConfig();
            again.IndexerRateLimitMs.Should().Be(400);
            again.DecisionBatchMemo.Should().BeFalse();

            // Reset to default: the default value clears the saved value (as does an explicit null).
            again.IndexerRateLimitMs = 2000;
            again.DecisionBatchMemo = true;
            again.FfprobeCacheEntries = 1000;

            var reset = Put(again);
            reset.IndexerRateLimitMs.Should().BeNull();
            reset.FfprobeCacheEntries.Should().BeNull();
            reset.Tunables.Single(t => t.Name == OptimizeTunables.IndexerRateLimitMs).Source.Should().Be("default");
            IndexerRateLimitOptions.Current.BaseRateLimit.Should().Be(TimeSpan.FromSeconds(2));
        }

        // Like the stock config endpoints, a PUT changes only the values it carries; missing (null) values, the two
        // switches included, stay as they are.
        [Test]
        public void partial_put_should_leave_missing_values_unchanged()
        {
            var full = _controller.GetOptimizeConfig();
            full.IndexerRateLimitMs = 750;
            full.FfprobeTimeoutMinutes = 30;
            full.CustomFormatCacheCapacity = 1000;
            full.DecisionBatchMemo = false;
            Put(full);

            var saved = Put(new OptimizeConfigResource { Id = 1, GrabSpacingMs = 2500 });

            saved.GrabSpacingMs.Should().Be(2500);
            saved.IndexerRateLimitMs.Should().Be(750);
            saved.FfprobeTimeoutMinutes.Should().Be(30);
            saved.CustomFormatCacheCapacity.Should().Be(1000);
            saved.DecisionBatchMemo.Should().BeFalse();
            saved.ReleaseProfileCache.Should().BeTrue();
            OptimizeSettings.Current.ReleaseProfileCache.Should().BeTrue();
            OptimizeSettings.Current.DecisionBatchMemo.Should().BeFalse();

            saved = Put(new OptimizeConfigResource { Id = 1, CacheHitHeader = "X-Cache" });

            saved.CacheHitHeader.Should().Be("X-Cache");
            saved.GrabSpacingMs.Should().Be(2500);
            saved.IndexerRateLimitMs.Should().Be(750);
            saved.DecisionBatchMemo.Should().BeFalse();
            saved.ReleaseProfileCache.Should().BeTrue();

            // An explicit default resets just that value.
            saved = Put(new OptimizeConfigResource { Id = 1, IndexerRateLimitMs = 2000, DecisionBatchMemo = true, CacheHitHeader = string.Empty });

            saved.IndexerRateLimitMs.Should().BeNull();
            saved.DecisionBatchMemo.Should().BeTrue();
            saved.CacheHitHeader.Should().BeNull();
            saved.FfprobeTimeoutMinutes.Should().Be(30);
            saved.Tunables.Single(t => t.Name == OptimizeTunables.DecisionBatchMemo).Source.Should().Be("default");
        }

        // PUT changes only the values present in the JSON body (as the app reads it, the same as the Sonarr image): a
        // value left out keeps its saved value, an explicit null resets it to the default.
        [Test]
        public void put_should_change_only_the_values_present_in_the_body()
        {
            Put(STJson.Deserialize<OptimizeConfigResource>("{\"id\":1,\"grabSpacingMs\":1000,\"cacheHitHeader\":\"X-Search-Cache\",\"decisionBatchMemo\":false}"));

            var partial = STJson.Deserialize<OptimizeConfigResource>("{\"id\":1,\"ffprobeTimeoutMinutes\":30}");
            partial.ToValues().Keys.Should().BeEquivalentTo(OptimizeTunables.FfprobeTimeoutMinutes);
            Put(partial);

            var reloaded = _controller.GetOptimizeConfig();
            reloaded.FfprobeTimeoutMinutes.Should().Be(30);
            reloaded.GrabSpacingMs.Should().Be(1000);
            reloaded.CacheHitHeader.Should().Be("X-Search-Cache");
            reloaded.DecisionBatchMemo.Should().BeFalse();

            var reset = STJson.Deserialize<OptimizeConfigResource>("{\"id\":1,\"grabSpacingMs\":null,\"cacheHitHeader\":null,\"decisionBatchMemo\":null}");
            reset.ToValues().Keys.Should().BeEquivalentTo(OptimizeTunables.GrabSpacingMs, OptimizeTunables.CacheHitHeader, OptimizeTunables.DecisionBatchMemo);
            new OptimizeConfigResourceValidator(_service).TestValidate(reset).ShouldNotHaveAnyValidationErrors();
            Put(reset);

            reloaded = _controller.GetOptimizeConfig();
            reloaded.GrabSpacingMs.Should().BeNull();
            reloaded.CacheHitHeader.Should().BeNull();
            reloaded.DecisionBatchMemo.Should().BeTrue();
            reloaded.Tunables.Single(t => t.Name == OptimizeTunables.GrabSpacingMs).Source.Should().Be("default");
            reloaded.Tunables.Single(t => t.Name == OptimizeTunables.DecisionBatchMemo).Source.Should().Be("default");
            reloaded.FfprobeTimeoutMinutes.Should().Be(30);
            IndexerRateLimitOptions.Current.GrabSpacing.Should().Be(TimeSpan.FromSeconds(2));
            IndexerRateLimitOptions.Current.RefundsCacheHits.Should().BeFalse();
            OptimizeSettings.Current.DecisionBatchMemo.Should().BeTrue();

            // The full resource the page sends back (every value, as returned by GET) keeps what is saved.
            var full = STJson.Deserialize<OptimizeConfigResource>(STJson.ToJson(reloaded));
            full.ToValues().Keys.Should().Contain(OptimizeTunables.FfprobeTimeoutMinutes);
            Put(full);
            _controller.GetOptimizeConfig().FfprobeTimeoutMinutes.Should().Be(30);
            _controller.GetOptimizeConfig().GrabSpacingMs.Should().BeNull();
        }

        // Locked values are ignored on PUT, also an explicit null: the environment value stays and nothing is saved.
        [Test]
        public void put_null_should_not_touch_a_locked_value()
        {
            try
            {
                Environment.SetEnvironmentVariable(IndexerRateLimitOptions.RateLimitVariable, "300");
                ResetEnvironment();
                _service.Reload();

                Put(STJson.Deserialize<OptimizeConfigResource>("{\"id\":1,\"indexerRateLimitMs\":null,\"grabSpacingMs\":2500}"));

                var resource = _controller.GetOptimizeConfig();
                resource.IndexerRateLimitMs.Should().Be(300);
                resource.GrabSpacingMs.Should().Be(2500);
                resource.Tunables.Single(t => t.Name == OptimizeTunables.IndexerRateLimitMs).Locked.Should().BeTrue();
                _store.ContainsKey(OptimizeTunables.Get(OptimizeTunables.IndexerRateLimitMs).ConfigKey).Should().BeFalse();
            }
            finally
            {
                Environment.SetEnvironmentVariable(IndexerRateLimitOptions.RateLimitVariable, null);
                ResetEnvironment();
                _service.Reload();
            }
        }

        // The environment is read once per process; the test hook that reads it again is internal to Radarr.Core.
        private static void ResetEnvironment()
        {
            typeof(OptimizeSettings).GetMethod("ResetEnvironment", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
        }

        // The controller wires the validator into the request pipeline: an invalid PUT is a 400 with field errors
        // (ValidationException) before the save runs, not a 500 from the service.
        [Test]
        public void put_pipeline_should_reject_invalid_values_with_field_errors()
        {
            var resource = _controller.GetOptimizeConfig();
            resource.IndexerRateLimitMs = 99_999_999_999;
            resource.CacheHitHeader = "bad header";

            var context = PutContext(resource);

            var exception = Assert.Throws<ValidationException>(() => _controller.OnActionExecuting(context));

            exception.Errors.Select(e => e.PropertyName).Should().BeEquivalentTo("IndexerRateLimitMs", "CacheHitHeader");
            OptimizeSettings.Current[OptimizeTunables.IndexerRateLimitMs].Source.Should().Be(OptimizeValueSource.Default);
        }

        [Test]
        public void put_pipeline_should_accept_valid_and_partial_values()
        {
            var context = PutContext(new OptimizeConfigResource { Id = 1, IndexerRateLimitMs = 10_000 });

            Assert.DoesNotThrow(() => _controller.OnActionExecuting(context));
        }

        private ActionExecutingContext PutContext(OptimizeConfigResource resource)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = "PUT";
            httpContext.Request.Path = "/api/v3/config/optimize/1";

            var descriptor = new ControllerActionDescriptor
            {
                MethodInfo = typeof(OptimizeConfigController).GetMethod(nameof(OptimizeConfigController.SaveOptimizeConfig)),
                ControllerTypeInfo = typeof(OptimizeConfigController).GetTypeInfo()
            };

            var actionContext = new ActionContext(httpContext, new RouteData(), descriptor);
            _controller.ControllerContext = new ControllerContext(actionContext);

            return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object> { ["resource"] = resource }, _controller);
        }

        [Test]
        public void validator_should_report_each_invalid_field()
        {
            var validator = new OptimizeConfigResourceValidator(_service);
            var resource = _controller.GetOptimizeConfig();

            resource.IndexerRateLimitMs = 10_001;
            resource.CacheHitHeader = "bad header";
            resource.CacheHitValue = "HIT,MISS";
            resource.FfprobeCacheSettleSeconds = 4;
            resource.CustomFormatCacheCapacity = -1;

            var result = validator.TestValidate(resource);

            result.ShouldHaveValidationErrorFor("IndexerRateLimitMs");
            result.ShouldHaveValidationErrorFor("CacheHitHeader");
            result.ShouldHaveValidationErrorFor("CacheHitValue");
            result.ShouldHaveValidationErrorFor("FfprobeCacheSettleSeconds");
            result.ShouldHaveValidationErrorFor("CustomFormatCacheCapacity");
            result.Errors.Should().HaveCount(5);
        }

        // The ffprobe cache strictness limits have the Sonarr image's bounds.
        [Test]
        public void validator_should_use_the_sonarr_ffprobe_cache_bounds()
        {
            var validator = new OptimizeConfigResourceValidator(_service);
            var resource = _controller.GetOptimizeConfig();

            resource.FfprobeCacheSettleSeconds = 59;
            resource.FfprobeCacheUnsettledSeconds = 61;
            resource.FfprobeCacheFailureMinutes = 11;

            var result = validator.TestValidate(resource);
            result.ShouldHaveValidationErrorFor("FfprobeCacheSettleSeconds");
            result.ShouldHaveValidationErrorFor("FfprobeCacheUnsettledSeconds");
            result.ShouldHaveValidationErrorFor("FfprobeCacheFailureMinutes");
            result.Errors.Should().HaveCount(3);

            resource.FfprobeCacheSettleSeconds = 60;
            resource.FfprobeCacheUnsettledSeconds = 60;
            resource.FfprobeCacheFailureMinutes = 10;
            validator.TestValidate(resource).ShouldNotHaveAnyValidationErrors();

            resource.FfprobeCacheSettleSeconds = 3600;
            resource.FfprobeCacheUnsettledSeconds = 0;
            resource.FfprobeCacheFailureMinutes = 0;
            validator.TestValidate(resource).ShouldNotHaveAnyValidationErrors();
        }

        [Test]
        public void validator_should_accept_bounds_and_nulls()
        {
            var validator = new OptimizeConfigResourceValidator(_service);
            var resource = _controller.GetOptimizeConfig();

            resource.IndexerRateLimitMs = 0;
            resource.GrabSpacingMs = 60_000;
            resource.CustomFormatCacheCapacity = 1_000_000;
            resource.FfprobeCacheMaxMegabytes = 0;
            resource.FfprobeTimeoutMinutes = null;

            validator.TestValidate(resource).ShouldNotHaveAnyValidationErrors();
        }

        [Test]
        public void status_should_come_from_the_service()
        {
            var status = _controller.GetOptimizeStatus();

            status.Features.Should().NotBeEmpty();
            status.Counters.Should().Contain(c => c.Name == "customFormatCacheHits");
        }
    }
}
