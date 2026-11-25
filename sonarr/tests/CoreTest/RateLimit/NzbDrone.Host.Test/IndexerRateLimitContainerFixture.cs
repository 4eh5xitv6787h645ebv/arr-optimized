using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DryIoc;
using DryIoc.Microsoft.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Common.Options;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Extensions;
using NzbDrone.Core.Indexers;
using NzbDrone.Host;
using NzbDrone.SignalR;
using NzbDrone.Test.Common;
using IServiceProvider = System.IServiceProvider;

namespace NzbDrone.App.Test
{
    // Goes into src/NzbDrone.Host.Test: checks the real container wiring of the opt-in indexer rate limiter.
    [TestFixture]
    [NonParallelizable]
    public class IndexerRateLimitContainerFixture : TestBase
    {
        private IServiceProvider _container;

        [SetUp]
        public void SetUp()
        {
            var args = new StartupContext("first", "second");

            var container = new Container(rules => rules.WithNzbDroneRules())
                .AutoAddServices(Bootstrap.ASSEMBLIES)
                .AddNzbDroneLogger()
                .AddDummyDatabase()
                .AddStartupContext(args);

            container.RegisterInstance<IHostLifetime>(new Mock<IHostLifetime>().Object);
            container.RegisterInstance<IBroadcastSignalRMessage>(new Mock<IBroadcastSignalRMessage>().Object);
            container.RegisterInstance<IOptions<PostgresOptions>>(new Mock<IOptions<PostgresOptions>>().Object);
            container.RegisterInstance<IOptions<AuthOptions>>(new Mock<IOptions<AuthOptions>>().Object);
            container.RegisterInstance<IOptions<AppOptions>>(new Mock<IOptions<AppOptions>>().Object);
            container.RegisterInstance<IOptions<ServerOptions>>(new Mock<IOptions<ServerOptions>>().Object);
            container.RegisterInstance<IOptions<UpdateOptions>>(new Mock<IOptions<UpdateOptions>>().Object);
            container.RegisterInstance<IOptions<LogOptions>>(new Mock<IOptions<LogOptions>>().Object);

            _container = container.GetServiceProvider();
        }

        [TearDown]
        public void TearDown()
        {
            SharedProperty.SetValue(null, null);
        }

        private static PropertyInfo SharedProperty => typeof(IndexerCacheHitRateLimiter).GetProperty("Shared", BindingFlags.NonPublic | BindingFlags.Static);

        [Test]
        public void limiter_should_be_an_http_interceptor_sharing_the_rate_limit_store()
        {
            SharedProperty.SetValue(null, null);

            var httpClient = _container.GetRequiredService<IHttpClient>();
            httpClient.Should().NotBeNull();

            // Building HttpClient alone must publish the limiter (HttpClient materialises its interceptors in its
            // constructor), so it exists before the first indexer request. Read before anything else resolves it.
            var shared = SharedProperty.GetValue(null);
            shared.Should().NotBeNull();

            var limiter = _container.GetRequiredService<IEnumerable<IHttpRequestInterceptor>>().OfType<IndexerCacheHitRateLimiter>().Single();

            // Built through its public constructor (the one that publishes the shared instance).
            shared.Should().BeSameAs(limiter);

            var store = _container.GetRequiredService<ICacheManager>()
                .GetCache<ConcurrentDictionary<string, DateTime>>(typeof(RateLimitService), "rateLimitStore")
                .Get("rateLimitStore", () => null);

            store.Should().NotBeNull();

            typeof(IndexerCacheHitRateLimiter).GetField("_rateLimitStore", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(limiter).Should().BeSameAs(store);

            typeof(RateLimitService).GetField("_rateLimitStore", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(_container.GetRequiredService<IRateLimitService>()).Should().BeSameAs(store);
        }
    }
}
