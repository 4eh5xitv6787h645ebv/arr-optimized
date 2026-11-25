using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.BroadcastheNet;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Indexers.Torznab;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.IndexerTests.RateLimitTests
{
    [TestFixture]
    [NonParallelizable]
    public class HttpIndexerBaseRateLimitFixture : CoreTest<TestIndexer>
    {
        private HttpRequest _request;
        private List<(TimeSpan RateLimit, string Key, HttpRequest Request)> _seen;
        private string _cacheHeader;
        private Func<HttpRequest, HttpRequest> _preRequest;

        [SetUp]
        public void Setup()
        {
            IndexerRateLimitOptions.Current = IndexerRateLimitOptions.Stock;
            IndexerCacheHitRateLimiter.Shared = null;

            _seen = new List<(TimeSpan, string, HttpRequest)>();
            _cacheHeader = "HIT";
            _preRequest = r => r;

            Subject.Definition = new IndexerDefinition
            {
                Id = 5,
                Name = "Test",
                Settings = new TestIndexerSettings { MultiLanguages = new List<int>(), FailDownloads = new List<int>() }
            };

            _request = new HttpRequest("http://prowlarr.local/5/api?t=search");
            var chain = new IndexerPageableRequestChain();
            chain.Add(new[] { new IndexerRequest(_request) });

            var generator = new Mock<IIndexerRequestGenerator>();
            generator.Setup(g => g.GetRecentRequests()).Returns(chain);
            Subject._requestGenerator = generator.Object;

            var parser = new Mock<IParseIndexerResponse>();
            parser.Setup(p => p.ParseResponse(It.IsAny<IndexerResponse>())).Returns(new List<ReleaseInfo>());
            Subject._parser = parser.Object;

            Mocker.GetMock<IHttpClient>()
                .Setup(c => c.ExecuteAsync(It.IsAny<HttpRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((HttpRequest r, CancellationToken _) =>
                {
                    r = _preRequest(r);
                    _seen.Add((r.RateLimit, r.RateLimitKey, r));

                    var headers = new HttpHeader();
                    headers.Add("X-Search-Cache", _cacheHeader);
                    return new HttpResponse(r, headers, "<rss/>");
                });
        }

        [TearDown]
        public void TearDown()
        {
            IndexerRateLimitOptions.Current = null;
            IndexerCacheHitRateLimiter.Shared = null;
        }

        private static IndexerRateLimitOptions WithBase(int milliseconds, string header = null)
        {
            return new IndexerRateLimitOptions(TimeSpan.FromMilliseconds(milliseconds), header, null);
        }

        [Test]
        public void unset_should_be_stock_two_seconds()
        {
            Subject.RateLimit.Should().Be(TimeSpan.FromSeconds(2));
            Mocker.Resolve<Newznab>().RateLimit.Should().Be(TimeSpan.FromSeconds(2));
            Mocker.Resolve<Torznab>().RateLimit.Should().Be(TimeSpan.FromSeconds(2));
            Mocker.Resolve<BroadcastheNet>().RateLimit.Should().Be(TimeSpan.FromSeconds(5));
        }

        [TestCase(0)]
        [TestCase(250)]
        [TestCase(10000)]
        public void base_override_should_apply_to_indexers_without_their_own_limit(int milliseconds)
        {
            IndexerRateLimitOptions.Current = WithBase(milliseconds);

            Subject.RateLimit.Should().Be(TimeSpan.FromMilliseconds(milliseconds));
            Mocker.Resolve<Newznab>().RateLimit.Should().Be(TimeSpan.FromMilliseconds(milliseconds));
            Mocker.Resolve<Torznab>().RateLimit.Should().Be(TimeSpan.FromMilliseconds(milliseconds));
        }

        [TestCase(0)]
        [TestCase(250)]
        [TestCase(10000)]
        public void indexer_with_own_limit_should_keep_it(int milliseconds)
        {
            IndexerRateLimitOptions.Current = WithBase(milliseconds);

            Mocker.Resolve<BroadcastheNet>().RateLimit.Should().Be(TimeSpan.FromSeconds(5));
        }

        [Test]
        public async Task unset_should_hand_request_to_http_client_like_stock()
        {
            await Subject.FetchRecent();

            _seen.Should().HaveCount(1);
            _seen[0].Request.Should().BeSameAs(_request);
            _seen[0].RateLimit.Should().Be(TimeSpan.FromSeconds(2));
            _seen[0].Key.Should().Be("5");
            Mocker.GetMock<IHttpClient>().Verify(c => c.ExecuteAsync(_request, default), Times.Once());
        }

        [Test]
        public async Task base_override_should_reach_http_client()
        {
            IndexerRateLimitOptions.Current = WithBase(250);

            await Subject.FetchRecent();

            _seen.Single().RateLimit.Should().Be(TimeSpan.FromMilliseconds(250));
        }

        [Test]
        public async Task larger_limit_set_by_request_generator_should_be_kept()
        {
            IndexerRateLimitOptions.Current = WithBase(250);
            _request.RateLimit = TimeSpan.FromSeconds(3);

            await Subject.FetchRecent();

            _seen.Single().RateLimit.Should().Be(TimeSpan.FromSeconds(3));
        }

        [Test]
        public async Task header_without_shared_limiter_should_be_stock()
        {
            IndexerRateLimitOptions.Current = WithBase(2000, "X-Search-Cache");

            await Subject.FetchRecent();

            _seen.Single().RateLimit.Should().Be(TimeSpan.FromSeconds(2));
        }

        [TestCase("HIT", false)]
        [TestCase("MISS", true)]
        public async Task hit_refund_should_pace_in_core_and_release_only_hits(string cacheHeader, bool keepsSlot)
        {
            var store = new ConcurrentDictionary<string, DateTime>();
            var limiter = new IndexerCacheHitRateLimiter(store, () => DateTime.UtcNow, TestLogger);
            IndexerCacheHitRateLimiter.Shared = limiter;
            IndexerRateLimitOptions.Current = WithBase(2000, "X-Search-Cache");
            _preRequest = limiter.PreRequest;
            _cacheHeader = cacheHeader;

            await Subject.FetchRecent();

            // HttpClient sees no rate limit for the hop the limiter already paced, and the request gets it back after.
            _seen.Single().RateLimit.Should().Be(TimeSpan.Zero);
            _seen.Single().Key.Should().Be("5");
            _request.RateLimit.Should().Be(TimeSpan.FromSeconds(2));

            store.ContainsKey("prowlarr.local").Should().BeTrue();
            store.ContainsKey("prowlarr.local-5").Should().Be(keepsSlot);
        }
    }
}
