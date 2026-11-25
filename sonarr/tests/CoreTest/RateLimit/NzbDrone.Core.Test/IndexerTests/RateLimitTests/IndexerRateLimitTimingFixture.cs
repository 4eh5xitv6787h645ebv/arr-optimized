using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FizzWare.NBuilder;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.IndexerTests.RateLimitTests
{
    // Black-box timing harness: a real HttpClient (with every IHttpRequestInterceptor in the Core assembly, as the
    // container builds it), the real RateLimitService and a local HTTP server that answers with an
    // X-Search-Cache header. Uses only stock types (the options are reloaded through reflection when the patched
    // type exists), so it compiles and runs against the unpatched Core as well.
    [TestFixture]
    [NonParallelizable]
    public class IndexerRateLimitTimingFixture : CoreTest<TestIndexer>
    {
        private const string RateLimitVariable = "ARR_INDEXER_RATE_LIMIT_MS";
        private const string HeaderVariable = "ARR_INDEXER_CACHE_HIT_HEADER";
        private const string ValueVariable = "ARR_INDEXER_CACHE_HIT_VALUE";

        private FakeIndexerServer _server;
        private ConcurrentQueue<double> _grabSeconds;

        [SetUp]
        public void Setup()
        {
            SetEnvironment(null, null, null);

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

            // Grabs go through the real DownloadService with the same RateLimitService; the download client is a mock.
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

            // One release per page and a page size of one: every request in the chain is fetched (paging).
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

        [TearDown]
        public void TearDown()
        {
            _server?.Dispose();
            SetEnvironment(null, null, null);
            SetPatchedStatic("NzbDrone.Core.Indexers.IndexerCacheHitRateLimiter", "Shared", null);
        }

        private static void SetEnvironment(string rateLimit, string header, string value)
        {
            Environment.SetEnvironmentVariable(RateLimitVariable, rateLimit);
            Environment.SetEnvironmentVariable(HeaderVariable, header);
            Environment.SetEnvironmentVariable(ValueVariable, value);

            // Patched Core reads the environment once; make it read again. No-op on stock Core.
            SetPatchedStatic("NzbDrone.Core.Indexers.IndexerRateLimitOptions", "Current", null);
        }

        private static void SetPatchedStatic(string typeName, string property, object value)
        {
            var type = typeof(HttpIndexerBase<>).Assembly.GetType(typeName);
            type?.GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, value);
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

        private void GivenPages(int count, bool allowAutoRedirect = false)
        {
            var chain = new IndexerPageableRequestChain();
            chain.Add(Enumerable.Range(0, count).Select(i =>
            {
                var request = new IndexerRequest(_server.Url + "api?t=search&offset=" + i, HttpAccept.Rss);

                if (allowAutoRedirect)
                {
                    request.HttpRequest.AllowAutoRedirect = true;
                }

                return request;
            }).ToList());

            var generator = new Mock<IIndexerRequestGenerator>();
            generator.Setup(g => g.GetRecentRequests()).Returns(chain);
            Subject._requestGenerator = generator.Object;
        }

        private Task Grab()
        {
            var remote = new RemoteEpisode
            {
                Series = Builder<Series>.CreateNew().Build(),
                Episodes = Builder<Episode>.CreateListOfSize(1).Build().ToList(),
                Release = new ReleaseInfo { Title = "Grab", DownloadUrl = _server.Url + "download/1", DownloadProtocol = DownloadProtocol.Torrent }
            };

            return Mocker.Resolve<DownloadService>().DownloadReport(remote, 1);
        }

        private List<double> Gaps()
        {
            var times = _server.ArrivalSeconds;
            var gaps = times.Zip(times.Skip(1), (a, b) => b - a).ToList();

            TestContext.Progress.WriteLine("{0}: {1} requests, gaps [{2}] s, total {3:0.000} s",
                TestContext.CurrentContext.Test.Name, times.Count, string.Join(", ", gaps.Select(g => g.ToString("0.000"))), times.Last() - times.First());

            return gaps;
        }

        [Test]
        public async Task stock_should_space_pages_two_seconds_apart()
        {
            _server.CacheHeader = "HIT";
            GivenPages(4);

            await Subject.FetchRecent();

            _server.ArrivalSeconds.Should().HaveCount(4);
            Gaps().Should().OnlyContain(g => g >= 1.9 && g < 3.0);
        }

        [Test]
        public async Task hit_header_should_remove_wait_between_pages()
        {
            SetEnvironment(null, "X-Search-Cache", null);
            _server.CacheHeader = "HIT";
            GivenPages(4);

            await Subject.FetchRecent();

            _server.ArrivalSeconds.Should().HaveCount(4);
            Gaps().Should().OnlyContain(g => g < 0.5);
        }

        [Test]
        public async Task hit_header_should_remove_wait_between_sequential_searches()
        {
            SetEnvironment(null, "X-Search-Cache", "hit");
            _server.CacheHeader = "HIT";
            GivenPages(1);

            for (var i = 0; i < 4; i++)
            {
                await Subject.FetchRecent();
            }

            _server.ArrivalSeconds.Should().HaveCount(4);
            Gaps().Should().OnlyContain(g => g < 0.5);
        }

        [Test]
        public async Task miss_should_still_space_pages_two_seconds_apart()
        {
            SetEnvironment(null, "X-Search-Cache", null);
            _server.CacheHeader = "MISS";
            GivenPages(4);

            await Subject.FetchRecent();

            _server.ArrivalSeconds.Should().HaveCount(4);
            Gaps().Should().OnlyContain(g => g >= 1.9 && g < 3.0);
        }

        [Test]
        public async Task missing_header_should_still_space_pages_two_seconds_apart()
        {
            SetEnvironment(null, "X-Search-Cache", null);
            _server.CacheHeader = null;
            GivenPages(3);

            await Subject.FetchRecent();

            _server.ArrivalSeconds.Should().HaveCount(3);
            Gaps().Should().OnlyContain(g => g >= 1.9 && g < 3.0);
        }

        [Test]
        public async Task only_the_request_after_a_hit_should_skip_the_wait()
        {
            SetEnvironment(null, "X-Search-Cache", null);
            _server.CacheHeaderSequence = new[] { "HIT", "MISS", "HIT", "HIT" };
            GivenPages(5);

            await Subject.FetchRecent();

            _server.ArrivalSeconds.Should().HaveCount(5);
            var gaps = Gaps();
            gaps[0].Should().BeLessThan(0.5);
            gaps[1].Should().BeInRange(1.9, 3.0);
            gaps[2].Should().BeLessThan(0.5);
            gaps[3].Should().BeLessThan(0.5);
        }

        [TestCase("HIT", 0.0)]
        [TestCase("MISS", 2.0)]
        public async Task concurrent_requests_should_keep_reserved_slot(string cacheHeader, double expectedThirdGap)
        {
            SetEnvironment(null, "X-Search-Cache", null);
            _server.CacheHeader = cacheHeader;
            GivenPages(1);

            // Two searches to the same indexer at once: the second has its slot reserved before the first answer
            // arrives, so it waits 2 s whatever the first answer says. Only then can the third go straight away.
            await Task.WhenAll(Subject.FetchRecent(), Subject.FetchRecent());
            await Subject.FetchRecent();

            _server.ArrivalSeconds.Should().HaveCount(3);
            var gaps = Gaps();
            gaps[0].Should().BeInRange(1.9, 3.0);
            gaps[1].Should().BeInRange(Math.Max(0, expectedThirdGap - 0.1), expectedThirdGap + 0.5);
        }

        [Test]
        public async Task rate_limit_variable_should_replace_base_interval()
        {
            SetEnvironment("500", null, null);
            _server.CacheHeader = "MISS";
            GivenPages(4);

            await Subject.FetchRecent();

            _server.ArrivalSeconds.Should().HaveCount(4);
            Gaps().Should().OnlyContain(g => g >= 0.45 && g < 1.5);
        }

        [Test]
        public async Task zero_rate_limit_should_not_wait()
        {
            SetEnvironment("0", null, null);
            _server.CacheHeader = "MISS";
            GivenPages(4);

            await Subject.FetchRecent();

            _server.ArrivalSeconds.Should().HaveCount(4);
            Gaps().Should().OnlyContain(g => g < 0.5);
        }

        [Test]
        public async Task invalid_variables_should_be_stock()
        {
            SetEnvironment("fast", "X Search Cache", null);
            _server.CacheHeader = "HIT";
            GivenPages(3);

            await Subject.FetchRecent();

            _server.ArrivalSeconds.Should().HaveCount(3);
            Gaps().Should().OnlyContain(g => g >= 1.9 && g < 3.0);

            // Stock ignores the variables silently; patched Core warns once per invalid variable. The third warning
            // is the RSS gap warning every paged FetchRecent in this harness logs, on stock as well.
            if (typeof(HttpIndexerBase<>).Assembly.GetType("NzbDrone.Core.Indexers.IndexerRateLimitOptions") != null)
            {
                NzbDrone.Test.Common.ExceptionVerification.ExpectedWarns(3);
            }
        }

        [Test]
        public async Task third_request_while_second_sleeps_must_wait_for_second_slot()
        {
            SetEnvironment(null, "X-Search-Cache", null);
            _server.CacheHeader = "HIT";
            GivenPages(1);

            // A and B start together, so B reserves the slot after A's before A's HIT arrives and keeps it. C starts as
            // soon as A has returned (B still sleeping): it must not get A's slot back but queue after B's.
            var a = Subject.FetchRecent();
            var b = Subject.FetchRecent();
            await a;
            var c = Subject.FetchRecent();
            await Task.WhenAll(b, c);

            _server.ArrivalSeconds.Should().HaveCount(3);
            var gaps = Gaps();
            gaps[0].Should().BeInRange(1.9, 3.0);
            gaps[1].Should().BeGreaterOrEqualTo(1.9);
        }

        [TestCase(null)]
        [TestCase("X-Search-Cache")]
        public async Task redirected_request_should_be_paced_like_stock_and_not_refunded(string header)
        {
            // Real HttpClient following a 302 (Radarr always enables auto redirect for indexer requests): every hop is
            // paced 2 s apart, and a HIT on the redirect target does not release the slot.
            SetEnvironment(null, header, null);
            _server.CacheHeader = "HIT";
            _server.RedirectFirst = true;

            // HttpClient rewrites the request URL to the redirect target, so each search gets fresh requests.
            GivenPages(1, allowAutoRedirect: true);
            await Subject.FetchRecent();
            GivenPages(1, allowAutoRedirect: true);
            await Subject.FetchRecent();

            _server.ArrivalSeconds.Should().HaveCount(4);
            Gaps().Should().OnlyContain(g => g >= 1.9 && g < 3.0);
        }

        [Test]
        public async Task stock_grab_between_searches_should_keep_stock_spacing()
        {
            _server.CacheHeader = "MISS";
            GivenPages(1);

            await Subject.FetchRecent();
            await Grab();
            await Subject.FetchRecent();

            _server.ArrivalSeconds.Should().HaveCount(2);
            Gaps()[0].Should().BeInRange(1.9, 3.0);

            // Stock: the grab waits for the search's 2 s on "<host>", the next search then goes straight away.
            GrabGaps()[0].Should().BeInRange(1.9, 3.0);
        }

        [TestCase("500", 0.45)]
        [TestCase("0", 0.0)]
        public async Task lowered_rate_limit_grab_should_not_hold_next_search_longer_than_the_limit(string rateLimit, double expected)
        {
            SetEnvironment(rateLimit, null, null);
            _server.CacheHeader = "MISS";
            GivenPages(1);

            await Subject.FetchRecent();
            await Grab();
            await Subject.FetchRecent();

            _server.ArrivalSeconds.Should().HaveCount(2);

            // Unpatched grab pacing (2 s pulse of "<host>") holds the second search until 2 s after the first.
            Gaps()[0].Should().BeInRange(expected, expected + 0.45);
            GrabGaps()[0].Should().BeInRange(expected, expected + 0.45);
        }

        [Test]
        public async Task hit_with_lowered_rate_limit_grab_should_not_hold_next_search_longer_than_the_limit()
        {
            SetEnvironment("500", "X-Search-Cache", null);
            _server.CacheHeader = "HIT";
            GivenPages(1);

            await Subject.FetchRecent();
            await Grab();
            await Subject.FetchRecent();

            _server.ArrivalSeconds.Should().HaveCount(2);
            Gaps()[0].Should().BeInRange(0.45, 0.9);
        }

        [Test]
        public async Task hit_header_alone_grab_should_wait_for_search_like_stock()
        {
            // Documented limitation: the refund releases the indexer's own key only; a grab right after a search still
            // waits for the search's 2 s on "<host>", exactly as on stock.
            SetEnvironment(null, "X-Search-Cache", null);
            _server.CacheHeader = "HIT";
            GivenPages(1);

            await Subject.FetchRecent();
            await Grab();
            await Subject.FetchRecent();

            _server.ArrivalSeconds.Should().HaveCount(2);
            Gaps()[0].Should().BeInRange(1.9, 3.0);
            GrabGaps()[0].Should().BeInRange(1.9, 3.0);
        }

        [TestCase(null)]
        [TestCase("500")]
        [TestCase("0")]
        public async Task grabs_from_one_host_should_stay_two_seconds_apart(string rateLimit)
        {
            SetEnvironment(rateLimit, null, null);

            await Grab();
            await Grab();
            await Grab();

            var grabs = _grabSeconds.ToList();
            var gaps = grabs.Zip(grabs.Skip(1), (x, y) => y - x).ToList();
            TestContext.Progress.WriteLine("{0}: grab gaps [{1}] s", TestContext.CurrentContext.Test.Name, string.Join(", ", gaps.Select(g => g.ToString("0.000"))));

            gaps.Should().HaveCount(2);
            gaps.Should().OnlyContain(g => g >= 1.9 && g < 3.0);
        }

        [TestCase(null, 5.8, 6.8)]
        [TestCase("500", 4.8, 5.6)]
        public async Task sweep_with_grabs_should_never_be_slower_than_stock(string rateLimit, double min, double max)
        {
            // Backlog/season/movie searches run search, process (here 1 s), grab, next search. Stock: grabs at 2, 4,
            // 6 s. With 500 ms the grabs (still 2 s apart from each other) go at 1, 3, 5 s and no search waits after a
            // grab. Unpatched grab pacing with 500 ms: searches at 0, 2.5, 5.0 and the last grab at 6.0 s (8.5 s vs
            // stock 8.0 s for 4 cycles), i.e. slower than stock.
            SetEnvironment(rateLimit, null, null);
            _server.CacheHeader = "MISS";
            GivenPages(1);

            var clock = Stopwatch.StartNew();

            for (var i = 0; i < 3; i++)
            {
                await Subject.FetchRecent();
                await Task.Delay(1000);
                await Grab();
            }

            var total = clock.Elapsed.TotalSeconds;
            TestContext.Progress.WriteLine("{0}: 3 search+grab cycles took {1:0.000} s, search gaps [{2}]", TestContext.CurrentContext.Test.Name, total, string.Join(", ", Gaps().Select(g => g.ToString("0.000"))));

            total.Should().BeInRange(min, max);
        }

        // Regression (integration review): a search followed by several grabs from the same host, as when a season
        // search grabs several releases in one pass. Grabs must stay 2 s apart, counted from when each grab is
        // actually released, and the first grab must still wait for the search's own interval on "<host>".
        [TestCase(null, 2.0)]
        [TestCase("1500", 1.5)]
        [TestCase("1000", 1.0)]
        [TestCase("500", 0.5)]
        [TestCase("0", 0.0)]
        public async Task search_then_grabs_should_keep_grabs_two_seconds_apart(string rateLimit, double searchToGrab)
        {
            SetEnvironment(rateLimit, null, null);
            _server.CacheHeader = "MISS";
            GivenPages(1);

            await Subject.FetchRecent();
            await Grab();
            await Grab();
            await Grab();

            var grabs = _grabSeconds.ToList();
            var gaps = grabs.Zip(grabs.Skip(1), (x, y) => y - x).ToList();
            TestContext.Progress.WriteLine("{0}: search at {1:0.000}, grabs at [{2}], grab gaps [{3}] s", TestContext.CurrentContext.Test.Name,
                _server.ArrivalSeconds.First(), string.Join(", ", grabs.Select(g => g.ToString("0.000"))), string.Join(", ", gaps.Select(g => g.ToString("0.000"))));

            gaps.Should().HaveCount(2);
            gaps.Should().OnlyContain(g => g >= 1.9 && g < 3.0);
            GrabGaps()[0].Should().BeInRange(searchToGrab - 0.1, searchToGrab + 0.45);
        }

        private List<double> GrabGaps()
        {
            // Time from each search arrival to the grab that followed it.
            var searches = _server.ArrivalSeconds;
            var grabs = _grabSeconds.ToList();
            var gaps = grabs.Select(g => g - searches.Where(s => s <= g).DefaultIfEmpty(g).Max()).ToList();

            TestContext.Progress.WriteLine("{0}: search -> grab [{1}] s", TestContext.CurrentContext.Test.Name, string.Join(", ", gaps.Select(g => g.ToString("0.000"))));

            return gaps;
        }

        private sealed class FakeIndexerServer : IDisposable
        {
            private const string Body = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><rss version=\"2.0\"><channel><title>t</title></channel></rss>";

            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly ConcurrentQueue<double> _arrivals = new ConcurrentQueue<double>();
            private int _count;

            public FakeIndexerServer()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/";
                Task.Run(AcceptLoop);
            }

            public string Url { get; }
            public string CacheHeader { get; set; }
            public string[] CacheHeaderSequence { get; set; }
            public bool RedirectFirst { get; set; }
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
                    var requestText = received.ToString();

                    if (RedirectFirst && !requestText.Contains("final=1"))
                    {
                        var path = requestText.Split(' ')[1];
                        var redirect = Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: " + path + "&final=1\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(redirect, 0, redirect.Length);
                        await stream.FlushAsync();
                        return;
                    }

                    var index = Interlocked.Increment(ref _count) - 1;
                    var header = CacheHeaderSequence != null ? CacheHeaderSequence[Math.Min(index, CacheHeaderSequence.Length - 1)] : CacheHeader;

                    var body = Encoding.UTF8.GetBytes(Body);
                    var head = "HTTP/1.1 200 OK\r\nContent-Type: application/rss+xml\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n" +
                               (header != null ? "X-Search-Cache: " + header + "\r\n" : string.Empty) + "\r\n";

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
