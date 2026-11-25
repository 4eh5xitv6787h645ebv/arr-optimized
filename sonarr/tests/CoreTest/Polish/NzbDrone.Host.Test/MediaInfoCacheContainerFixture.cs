using System.Collections.Generic;
using System.Linq;
using DryIoc;
using DryIoc.Microsoft.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Common.Options;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Extensions;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Host;
using NzbDrone.SignalR;
using NzbDrone.Test.Common;
using IServiceProvider = System.IServiceProvider;

namespace NzbDrone.App.Test
{
    // Goes into src/NzbDrone.Host.Test: the ffprobe cache lives in the VideoFileInfoReader instance, so the file events
    // must reach the same instance that probes files. Checks the real container wiring.
    [TestFixture]
    public class MediaInfoCacheContainerFixture : TestBase
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

        private void ShouldReachTheReader<TEvent>(IVideoFileInfoReader reader)
            where TEvent : class, NzbDrone.Common.Messaging.IEvent
        {
            _container.GetRequiredService<IEnumerable<IHandle<TEvent>>>()
                .OfType<VideoFileInfoReader>()
                .Should().ContainSingle()
                .Which.Should().BeSameAs(reader);
        }

        [Test]
        public void file_events_should_reach_the_reader_that_holds_the_cache()
        {
            var reader = _container.GetRequiredService<IVideoFileInfoReader>();

            reader.Should().BeOfType<VideoFileInfoReader>();
            _container.GetRequiredService<IVideoFileInfoReader>().Should().BeSameAs(reader);

            ShouldReachTheReader<EpisodeImportedEvent>(reader);
            ShouldReachTheReader<EpisodeFileDeletedEvent>(reader);
            ShouldReachTheReader<EpisodeFileRenamedEvent>(reader);
        }
    }
}
