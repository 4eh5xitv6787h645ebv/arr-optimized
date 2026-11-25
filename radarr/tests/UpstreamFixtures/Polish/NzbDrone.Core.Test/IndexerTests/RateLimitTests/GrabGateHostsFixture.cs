using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Movies;

namespace NzbDrone.Core.Test.IndexerTests.RateLimitTests
{
    // Grabs are paced per download host, on stock (the "<host>" rate-limit key) and with a lowered limit (one grab gate
    // per host). Grabs to two different hosts must not wait for each other; a second grab to the first host still
    // waits 2 s. Real DownloadService and RateLimitService; the download client is a mock that records when each grab
    // goes out. Grab URLs are never fetched, so the hosts need not exist.
    [TestFixture]
    [NonParallelizable]
    public class GrabGateHostsFixture : CoreTest<DownloadService>
    {
        private const string RateLimitVariable = "ARR_INDEXER_RATE_LIMIT_MS";

        private ConcurrentQueue<(string Url, double Seconds)> _grabs;
        private Stopwatch _clock;

        [SetUp]
        public void Setup()
        {
            SetRateLimit(null);

            _clock = Stopwatch.StartNew();
            _grabs = new ConcurrentQueue<(string, double)>();

            Mocker.SetConstant<IRateLimitService>(new RateLimitService(new CacheManager(), TestLogger));

            var downloadClient = new Mock<IDownloadClient>();
            downloadClient.SetupGet(c => c.Definition).Returns(new DownloadClientDefinition { Id = 1, Name = "Grab client" });
            downloadClient.SetupGet(c => c.Protocol).Returns(DownloadProtocol.Torrent);
            downloadClient.Setup(c => c.Download(It.IsAny<RemoteMovie>(), It.IsAny<IIndexer>()))
                .Callback<RemoteMovie, IIndexer>((r, i) => _grabs.Enqueue((r.Release.DownloadUrl, _clock.Elapsed.TotalSeconds)))
                .ReturnsAsync("id");
            Mocker.GetMock<IProvideDownloadClient>().Setup(p => p.Get(1)).Returns(downloadClient.Object);
        }

        [TearDown]
        public void TearDown()
        {
            SetRateLimit(null);
        }

        private static void SetRateLimit(string value)
        {
            Environment.SetEnvironmentVariable(RateLimitVariable, value);

            // Patched Core reads the environment once; make it read again. No-op on stock Core.
            typeof(HttpIndexerBase<>).Assembly.GetType("NzbDrone.Core.Indexers.IndexerRateLimitOptions")?
                .GetProperty("Current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?
                .SetValue(null, null);
        }

        private Task Grab(string host)
        {
            var remote = new RemoteMovie
            {
                Movie = Builder<Movie>.CreateNew().Build(),
                Release = new ReleaseInfo { Title = "Grab", DownloadUrl = $"http://{host}:9/download/{Guid.NewGuid()}", DownloadProtocol = DownloadProtocol.Torrent }
            };

            return Subject.DownloadReport(remote, 1);
        }

        private List<double> GrabTimes()
        {
            var times = _grabs.Select(g => g.Seconds).ToList();
            TestContext.Progress.WriteLine("{0}: grabs at [{1}] s", TestContext.CurrentContext.Test.Name, string.Join(", ", times.Select(t => t.ToString("0.000"))));

            return times;
        }

        [TestCase(null)]
        [TestCase("500")]
        [TestCase("0")]
        public async Task grabs_to_two_hosts_should_not_wait_for_each_other(string rateLimit)
        {
            SetRateLimit(rateLimit);

            await Grab("127.0.0.1");
            await Grab("localhost");
            await Grab("127.0.0.1");

            var times = GrabTimes();

            times.Should().HaveCount(3);
            (times[1] - times[0]).Should().BeLessThan(1.0, "a grab to another host does not wait for the first host");
            (times[2] - times[0]).Should().BeGreaterOrEqualTo(1.9, "a second grab to the first host still waits 2 s");
        }

        [TestCase(null)]
        [TestCase("500")]
        public async Task concurrent_grabs_to_two_hosts_should_not_wait_for_each_other(string rateLimit)
        {
            SetRateLimit(rateLimit);

            // Prime both hosts, then grab from both at once: each waits only for its own host.
            await Task.WhenAll(Task.Run(() => Grab("127.0.0.1")), Task.Run(() => Grab("localhost")));
            await Task.WhenAll(Task.Run(() => Grab("127.0.0.1")), Task.Run(() => Grab("localhost")));

            var byHost = _grabs.GroupBy(g => new Uri(g.Url).Host).ToDictionary(g => g.Key, g => g.Select(x => x.Seconds).OrderBy(s => s).ToList());
            GrabTimes();

            byHost.Should().HaveCount(2);

            foreach (var host in byHost.Values)
            {
                host.Should().HaveCount(2);
                (host[1] - host[0]).Should().BeInRange(1.9, 2.6);
            }

            // Both second grabs went out together, about 2 s after the first pair, not 2 s after each other.
            Math.Abs(byHost["127.0.0.1"][1] - byHost["localhost"][1]).Should().BeLessThan(1.0);
        }
    }
}
