using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Optimization;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Test.IndexerTests;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.OptimizationTests
{
    // Values saved on the Optimize page change real request timing at once, without a restart: a real HttpClient
    // (with every IHttpRequestInterceptor of Core), the real RateLimitService and DownloadService, and a local HTTP
    // server, as in IndexerRateLimitTimingFixture. Saving goes through OptimizeSettingsService.Save.
    [TestFixture]
    [NonParallelizable]
    public class OptimizeLiveApplyTimingFixture : CoreTest<TestIndexer>
    {
        private FakeIndexerServer _server;
        private ConcurrentQueue<double> _grabSeconds;
        private Dictionary<string, string> _saved;
        private OptimizeSettingsService _settings;

        [SetUp]
        public void Setup()
        {
            OptimizeSettings.Current = null;
            _saved = new Dictionary<string, string>();

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

            _settings = new OptimizeSettingsService(Mocker.GetMock<IConfigService>().Object, TestLogger) { GetEnvironmentVariable = _ => null };
            _settings.Refresh();

            _server = new FakeIndexerServer();

            UseRealHttp();

            var cacheManager = new CacheManager();
            var interceptors = typeof(HttpIndexerBase<>).Assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && typeof(IHttpRequestInterceptor).IsAssignableFrom(t))
                .Select(t => CreateInterceptor(t, cacheManager))
                .ToList();

            var rateLimitService = new RateLimitService(cacheManager, TestLogger);
            var httpClient = new HttpClient(interceptors, cacheManager, rateLimitService, Mocker.Resolve<IHttpDispatcher>(), TestLogger);
            Mocker.SetConstant<IHttpClient>(httpClient);
            Mocker.SetConstant<IRateLimitService>(rateLimitService);

            _grabSeconds = new ConcurrentQueue<double>();

            var downloadClient = new Mock<IDownloadClient>();
            downloadClient.SetupGet(c => c.Definition).Returns(new DownloadClientDefinition { Id = 1, Name = "Timing client" });
            downloadClient.SetupGet(c => c.Protocol).Returns(DownloadProtocol.Torrent);
            downloadClient.Setup(c => c.Download(It.IsAny<RemoteEpisode>(), It.IsAny<IIndexer>()))
                .Callback(() => _grabSeconds.Enqueue(_server.Now))
                .ReturnsAsync("id");
            Mocker.GetMock<IProvideDownloadClient>().Setup(p => p.Get(1)).Returns(downloadClient.Object);

            Mocker.GetMock<IIndexerStatusService>()
                .Setup(s => s.GetLastRssSyncReleaseInfo(It.IsAny<int>()))
                .Returns(new ReleaseInfo { Title = "old", DownloadUrl = "http://old", PublishDate = new DateTime(2000, 1, 1) });

            Subject.Definition = new IndexerDefinition
            {
                Id = 7,
                Name = "Timing",
                Settings = new TestIndexerSettings { MultiLanguages = new List<int>(), FailDownloads = new List<int>() }
            };

            Subject._supportedPageSize = 1;

            var parser = new Mock<IParseIndexerResponse>();
            parser.Setup(p => p.ParseResponse(It.IsAny<IndexerResponse>()))
                .Returns(() =>
                {
                    var id = Guid.NewGuid().ToString();
                    return new List<ReleaseInfo> { new ReleaseInfo { Title = "Release " + id, Guid = id, DownloadUrl = "http://dl/" + id, PublishDate = DateTime.UtcNow } };
                });
            Subject._parser = parser.Object;
        }

        private IHttpRequestInterceptor CreateInterceptor(Type type, ICacheManager cacheManager)
        {
            var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
            var arguments = constructor.GetParameters().Select(p =>
            {
                if (p.ParameterType == typeof(ICacheManager))
                {
                    return cacheManager;
                }

                if (p.ParameterType == typeof(Logger))
                {
                    return (object)TestLogger;
                }

                throw new NotSupportedException($"{type.Name} needs {p.ParameterType.Name}");
            }).ToArray();

            return (IHttpRequestInterceptor)constructor.Invoke(arguments);
        }

        [TearDown]
        public void TearDown()
        {
            _server?.Dispose();
            OptimizeSettings.Current = null;
            IndexerCacheHitRateLimiter.Shared = null;
        }

        private void GivenPages(int count)
        {
            var chain = new IndexerPageableRequestChain();
            chain.Add(Enumerable.Range(0, count).Select(i => new IndexerRequest(_server.Url + "api?t=search&offset=" + i, HttpAccept.Rss)).ToList());

            var generator = new Mock<IIndexerRequestGenerator>();
            generator.Setup(g => g.GetRecentRequests()).Returns(chain);
            Subject._requestGenerator = generator.Object;
        }

        private async Task<List<double>> Phase(int pages)
        {
            var before = _server.ArrivalSeconds.Count;
            GivenPages(pages);

            await Subject.FetchRecent();

            var times = _server.ArrivalSeconds.Skip(before).ToList();
            times.Should().HaveCount(pages);

            var gaps = times.Zip(times.Skip(1), (a, b) => b - a).ToList();
            TestContext.Progress.WriteLine("{0}: page gaps [{1}] s", TestContext.CurrentContext.Test.Name, string.Join(", ", gaps.Select(g => g.ToString("0.000"))));

            return gaps;
        }

        private async Task<List<double>> Grabs(int count)
        {
            var before = _grabSeconds.Count;

            for (var i = 0; i < count; i++)
            {
                await Mocker.Resolve<DownloadService>().DownloadReport(new RemoteEpisode
                {
                    Series = Builder<Series>.CreateNew().Build(),
                    Episodes = Builder<Episode>.CreateListOfSize(1).Build().ToList(),
                    Release = new ReleaseInfo { Title = "Grab", DownloadUrl = _server.Url + "download/" + i, DownloadProtocol = DownloadProtocol.Torrent }
                }, 1);
            }

            var times = _grabSeconds.Skip(before).ToList();
            var gaps = times.Zip(times.Skip(1), (a, b) => b - a).ToList();
            TestContext.Progress.WriteLine("{0}: grab gaps [{1}] s", TestContext.CurrentContext.Test.Name, string.Join(", ", gaps.Select(g => g.ToString("0.000"))));

            return gaps;
        }

        [Test]
        public async Task indexer_request_spacing_saved_on_the_page_should_apply_to_the_next_requests()
        {
            _server.CacheHeader = "MISS";

            (await Phase(3)).Should().OnlyContain(g => g >= 1.9 && g < 3.0);

            _settings.Save(new Dictionary<string, object> { { "IndexerRateLimitMs", 500 } });

            (await Phase(4)).Should().OnlyContain(g => g >= 0.45 && g < 1.2);

            _settings.Save(new Dictionary<string, object> { { "IndexerRateLimitMs", null } });

            (await Phase(3)).Should().OnlyContain(g => g >= 1.9 && g < 3.0);
        }

        [Test]
        public async Task cache_hit_header_saved_on_the_page_should_refund_hits_and_count_them()
        {
            _server.CacheHeader = "HIT";

            (await Phase(3)).Should().OnlyContain(g => g >= 1.9 && g < 3.0);

            var refunds = Interlocked.Read(ref IndexerCacheHitRateLimiter.RefundCount);
            var hits = Interlocked.Read(ref IndexerCacheHitRateLimiter.CacheHitResponseCount);
            var paced = Interlocked.Read(ref IndexerCacheHitRateLimiter.PacedRequestCount);

            _settings.Save(new Dictionary<string, object> { { "CacheHitHeader", "X-Search-Cache" }, { "CacheHitValue", "hit" } });

            (await Phase(4)).Should().OnlyContain(g => g < 0.5);

            Interlocked.Read(ref IndexerCacheHitRateLimiter.CacheHitResponseCount).Should().Be(hits + 4);
            Interlocked.Read(ref IndexerCacheHitRateLimiter.RefundCount).Should().Be(refunds + 4);
            Interlocked.Read(ref IndexerCacheHitRateLimiter.PacedRequestCount).Should().Be(paced + 4);

            var status = _settings.GetStatus();
            status.Counters.Single(c => c.Name == "rateLimitRefunds").Value.Should().BeGreaterOrEqualTo(refunds + 4);
            status.Counters.Single(c => c.Name == "rateLimitRefundedMs").Value.Should().BeGreaterOrEqualTo(4 * 2000);

            _settings.Save(new Dictionary<string, object> { { "CacheHitHeader", string.Empty } });

            (await Phase(3)).Should().OnlyContain(g => g >= 1.9 && g < 3.0);
            Interlocked.Read(ref IndexerCacheHitRateLimiter.RefundCount).Should().Be(refunds + 4);
        }

        // With the cache hit header on, requests are paced by IndexerCacheHitRateLimiter: misses wait as on stock, and
        // those waits are counted ("Requests that waited", "Total wait").
        [Test]
        public async Task requests_that_wait_should_be_counted()
        {
            _server.CacheHeader = "MISS";
            _settings.Save(new Dictionary<string, object> { { "CacheHitHeader", "X-Search-Cache" } });

            var paced = Interlocked.Read(ref IndexerCacheHitRateLimiter.PacedRequestCount);
            var delayed = Interlocked.Read(ref IndexerCacheHitRateLimiter.DelayedRequestCount);
            var ticks = Interlocked.Read(ref IndexerCacheHitRateLimiter.DelayTicksTotal);
            var refunds = Interlocked.Read(ref IndexerCacheHitRateLimiter.RefundCount);
            var statusBefore = _settings.GetStatus().Counters.ToDictionary(c => c.Name, c => c.Value);

            var gaps = await Phase(4);
            gaps.Should().OnlyContain(g => g >= 1.9 && g < 3.0);

            // The first request of the phase may or may not wait; the next three do, about 2 s each.
            var waited = Interlocked.Read(ref IndexerCacheHitRateLimiter.DelayedRequestCount) - delayed;
            var waitedMs = (Interlocked.Read(ref IndexerCacheHitRateLimiter.DelayTicksTotal) - ticks) / TimeSpan.TicksPerMillisecond;

            Interlocked.Read(ref IndexerCacheHitRateLimiter.PacedRequestCount).Should().Be(paced + 4);
            waited.Should().BeInRange(3, 4);
            waitedMs.Should().BeInRange(3 * 1500, 4 * 2100);
            Interlocked.Read(ref IndexerCacheHitRateLimiter.RefundCount).Should().Be(refunds);

            var status = _settings.GetStatus().Counters.ToDictionary(c => c.Name, c => c.Value);
            (status["rateLimitPacedRequests"] - statusBefore["rateLimitPacedRequests"]).Should().Be(4);
            (status["rateLimitDelayedRequests"] - statusBefore["rateLimitDelayedRequests"]).Should().Be(waited);
            (status["rateLimitWaitMs"] - statusBefore["rateLimitWaitMs"]).Should().BeInRange(waitedMs - 1, waitedMs + 1);
        }

        [Test]
        public async Task grab_spacing_saved_on_the_page_should_apply_to_the_next_grabs()
        {
            (await Grabs(3)).Should().OnlyContain(g => g >= 1.9 && g < 3.0);

            _settings.Save(new Dictionary<string, object> { { "GrabSpacingMs", 500 } });

            var lowered = await Grabs(4);
            lowered.Skip(1).Should().OnlyContain(g => g >= 0.45 && g < 1.2);

            _settings.Save(new Dictionary<string, object> { { "GrabSpacingMs", null } });

            (await Grabs(3)).Should().OnlyContain(g => g >= 1.9 && g < 3.0);
        }

        private sealed class FakeIndexerServer : IDisposable
        {
            private const string Body = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><rss version=\"2.0\"><channel><title>t</title></channel></rss>";

            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly ConcurrentQueue<double> _arrivals = new ConcurrentQueue<double>();

            public FakeIndexerServer()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/";
                Task.Run(AcceptLoop);
            }

            public string Url { get; }
            public string CacheHeader { get; set; }
            public List<double> ArrivalSeconds => _arrivals.ToList();
            public double Now => _clock.Elapsed.TotalSeconds;

            private async Task AcceptLoop()
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client;

                    try
                    {
                        client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    }
                    catch
                    {
                        return;
                    }

                    _ = Task.Run(() => Serve(client));
                }
            }

            private async Task Serve(TcpClient client)
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    var received = new StringBuilder();

                    while (!received.ToString().Contains("\r\n\r\n"))
                    {
                        var read = await stream.ReadAsync(buffer, 0, buffer.Length);

                        if (read == 0)
                        {
                            return;
                        }

                        received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    _arrivals.Enqueue(_clock.Elapsed.TotalSeconds);

                    var body = Encoding.UTF8.GetBytes(Body);
                    var head = "HTTP/1.1 200 OK\r\nContent-Type: application/rss+xml\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n" +
                               (CacheHeader != null ? "X-Search-Cache: " + CacheHeader + "\r\n" : string.Empty) + "\r\n";

                    var headBytes = Encoding.ASCII.GetBytes(head);
                    await stream.WriteAsync(headBytes, 0, headBytes.Length);
                    await stream.WriteAsync(body, 0, body.Length);
                    await stream.FlushAsync();
                }
            }

            public void Dispose()
            {
                _cts.Cancel();
                _listener.Stop();
            }
        }
    }
}
