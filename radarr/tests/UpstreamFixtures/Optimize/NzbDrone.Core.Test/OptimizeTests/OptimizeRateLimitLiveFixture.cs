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
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Optimize;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Test.IndexerTests;

namespace NzbDrone.Core.Test.OptimizeTests
{
    // Black-box timing, like IndexerRateLimitTimingFixture: a real HttpClient with the Core interceptors, the real
    // RateLimitService and a local HTTP server. One indexer instance and one HttpClient for the whole test: the
    // spacing follows each Save on the Optimize page without a restart or a new indexer.
    [TestFixture]
    [NonParallelizable]
    public class OptimizeRateLimitLiveFixture : CoreTest<TestIndexer>
    {
        private FakeIndexerServer _server;
        private Dictionary<string, string> _store;
        private OptimizeSettingsService _settingsService;

        [SetUp]
        public void Setup()
        {
            OptimizeSettings.Reset();
            _server = new FakeIndexerServer();

            UseRealHttp();

            var cacheManager = new CacheManager();
            var limiter = new IndexerCacheHitRateLimiter(cacheManager, TestLogger);
            var rateLimitService = new RateLimitService(cacheManager, TestLogger);
            var httpClient = new HttpClient(new IHttpRequestInterceptor[] { limiter }, cacheManager, rateLimitService, Mocker.Resolve<IHttpDispatcher>(), TestLogger);
            Mocker.SetConstant<IHttpClient>(httpClient);

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

            // The Optimize settings service with an in-memory Config table.
            _store = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var configService = new Mock<IConfigService>();
            configService.Setup(s => s.GetOptimizeValue(It.IsAny<string>()))
                .Returns<string>(key => _store.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null);
            configService.Setup(s => s.SetOptimizeValue(It.IsAny<string>(), It.IsAny<string>()))
                .Callback<string, string>((key, value) => _store[key] = value ?? string.Empty);
            configService.SetupGet(s => s.CheckForFinishedDownloadInterval).Returns(1);

            _settingsService = new OptimizeSettingsService(configService.Object, new Mock<IVideoFileInfoReader>().Object, TestLogger);
            _settingsService.Reload();
        }

        [TearDown]
        public void TearDown()
        {
            _server?.Dispose();
            IndexerCacheHitRateLimiter.Shared = null;
            OptimizeSettings.Reset();
        }

        private void Save(string name, object value)
        {
            _settingsService.Save(new Dictionary<string, object> { { name, value } });
        }

        private async Task<List<double>> FetchPages(int count)
        {
            var start = _server.ArrivalSeconds.Count;

            var chain = new IndexerPageableRequestChain();
            chain.Add(Enumerable.Range(0, count).Select(i => new IndexerRequest(_server.Url + "api?t=search&offset=" + i + "&n=" + Guid.NewGuid(), HttpAccept.Rss)).ToList());

            var generator = new Mock<IIndexerRequestGenerator>();
            generator.Setup(g => g.GetRecentRequests()).Returns(chain);
            Subject._requestGenerator = generator.Object;

            await Subject.FetchRecent();

            var times = _server.ArrivalSeconds.Skip(start).ToList();
            times.Should().HaveCount(count);

            var gaps = times.Zip(times.Skip(1), (a, b) => b - a).ToList();
            TestContext.Progress.WriteLine("{0}: gaps [{1}] s", TestContext.CurrentContext.Test.Name, string.Join(", ", gaps.Select(g => g.ToString("0.000"))));

            return gaps;
        }

        [Test]
        public async Task spacing_should_follow_each_save_without_restart()
        {
            _server.CacheHeader = "MISS";

            // Stock: 2 s between requests.
            (await FetchPages(3)).Should().OnlyContain(g => g >= 1.9 && g < 3.0);

            // Lowered on the page: 200 ms, same indexer instance and HttpClient.
            Save(OptimizeTunables.IndexerRateLimitMs, 200);
            Subject.RateLimit.Should().Be(TimeSpan.FromMilliseconds(200));
            (await FetchPages(4)).Should().OnlyContain(g => g >= 0.15 && g < 1.0);

            // Back to the default: 2 s again.
            Save(OptimizeTunables.IndexerRateLimitMs, null);
            (await FetchPages(3)).Should().OnlyContain(g => g >= 1.9 && g < 3.0);
        }

        [Test]
        public async Task cache_hit_header_should_apply_on_save_and_count_refunds()
        {
            _server.CacheHeader = "HIT";

            // Header not set: hits still wait 2 s.
            (await FetchPages(3)).Should().OnlyContain(g => g >= 1.9 && g < 3.0);

            var refunds = OptimizeCounters.Read(ref OptimizeCounters.Refunds);
            var hits = OptimizeCounters.Read(ref OptimizeCounters.CacheHitResponses);
            var paced = OptimizeCounters.Read(ref OptimizeCounters.PacedRequests);

            Save(OptimizeTunables.CacheHitHeader, "X-Search-Cache");
            (await FetchPages(4)).Should().OnlyContain(g => g < 1.0);

            OptimizeCounters.Read(ref OptimizeCounters.CacheHitResponses).Should().Be(hits + 4);
            OptimizeCounters.Read(ref OptimizeCounters.Refunds).Should().BeGreaterOrEqualTo(refunds + 3);
            OptimizeCounters.Read(ref OptimizeCounters.PacedRequests).Should().Be(paced + 4);
            OptimizeCounters.Read(ref OptimizeCounters.RefundedTicks).Should().BeGreaterThan(0);

            // A different hit value: the responses are no longer hits.
            Save(OptimizeTunables.CacheHitValue, "FRESH");
            (await FetchPages(3)).Should().OnlyContain(g => g >= 1.9 && g < 3.0);

            OptimizeCounters.Read(ref OptimizeCounters.PacedRequestsWaited).Should().BeGreaterThan(0);
            OptimizeCounters.Read(ref OptimizeCounters.PacedWaitTicks).Should().BeGreaterThan(TimeSpan.FromSeconds(3).Ticks);
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
