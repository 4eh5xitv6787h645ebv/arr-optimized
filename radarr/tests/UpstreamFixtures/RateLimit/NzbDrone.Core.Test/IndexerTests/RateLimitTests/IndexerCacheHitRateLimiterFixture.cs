using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.IndexerTests.RateLimitTests
{
    [TestFixture]
    [NonParallelizable]
    public class IndexerCacheHitRateLimiterFixture : CoreTest
    {
        private const string Host = "prowlarr.local";
        private const string IndexerKey = "5";
        private const string CombinedKey = Host + "-" + IndexerKey;

        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

        private ConcurrentDictionary<string, DateTime> _store;
        private DateTime _now;
        private IndexerCacheHitRateLimiter _limiter;
        private IndexerRateLimitOptions _hitOptions;

        [SetUp]
        public void Setup()
        {
            _store = new ConcurrentDictionary<string, DateTime>();
            _now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            _limiter = new IndexerCacheHitRateLimiter(_store, () => _now, TestLogger);
            _hitOptions = new IndexerRateLimitOptions(IndexerRateLimitOptions.StockRateLimit, "X-Search-Cache", "HIT");
        }

        [TearDown]
        public void TearDown()
        {
            IndexerRateLimitOptions.Current = null;
            IndexerCacheHitRateLimiter.Shared = null;
        }

        private void Advance(int milliseconds)
        {
            _now = _now.AddMilliseconds(milliseconds);
        }

        private IndexerCacheHitRateLimiter.Reservation Reserve()
        {
            return _limiter.Reserve(Host, IndexerKey, Interval);
        }

        private static HttpResponse ResponseWith(string header, string value, HttpStatusCode status = HttpStatusCode.OK)
        {
            var headers = new HttpHeader();

            if (header != null)
            {
                headers.Add(header, value);
            }

            return new HttpResponse(new HttpRequest("http://" + Host + "/api"), headers, Array.Empty<byte>(), status);
        }

        [Test]
        public void first_request_should_not_wait_and_should_reserve_one_interval()
        {
            var reservation = Reserve();

            reservation.Delay.Should().Be(TimeSpan.Zero);
            reservation.Previous.Should().BeNull();
            reservation.Reserved.Should().Be(_now + Interval);
            _store[CombinedKey].Should().Be(_now + Interval);
            _store[Host].Should().Be(_now + Interval);
        }

        [Test]
        public void sequential_requests_without_refund_should_be_spaced_like_stock()
        {
            Reserve();
            Advance(100);

            var second = Reserve();

            second.Delay.Should().Be(Interval - TimeSpan.FromMilliseconds(100));
            second.Reserved.Should().Be(_now - TimeSpan.FromMilliseconds(100) + Interval + Interval);
        }

        [Test]
        public void sequential_hit_should_release_slot_for_next_request()
        {
            var first = Reserve();
            Advance(100);

            _limiter.Refund(first).Should().BeTrue();
            _store.ContainsKey(CombinedKey).Should().BeFalse();

            Advance(5);
            var second = Reserve();

            second.Delay.Should().BeLessOrEqualTo(TimeSpan.Zero);
            second.Reserved.Should().Be(_now + Interval);
        }

        [Test]
        public void refund_should_restore_value_from_before_the_request()
        {
            var earlier = _now - TimeSpan.FromSeconds(30);
            _store[CombinedKey] = earlier;

            var reservation = Reserve();
            reservation.Previous.Should().Be(earlier);

            _limiter.Refund(reservation).Should().BeTrue();

            _store[CombinedKey].Should().Be(earlier);
        }

        [Test]
        public void refund_should_not_touch_shared_host_key()
        {
            var reservation = Reserve();
            var hostValue = _store[Host];

            _limiter.Refund(reservation).Should().BeTrue();

            _store[Host].Should().Be(hostValue);
        }

        [Test]
        public void many_sequential_hits_should_never_wait()
        {
            for (var i = 0; i < 20; i++)
            {
                var reservation = Reserve();
                reservation.Delay.Should().BeLessOrEqualTo(TimeSpan.Zero);

                Advance(30);
                _limiter.Refund(reservation).Should().BeTrue();
                Advance(1);
            }
        }

        [Test]
        public void hit_should_not_release_a_slot_another_request_reserved_after_it()
        {
            // Mirror store without any refund, to compare with.
            var stockStore = new ConcurrentDictionary<string, DateTime>();
            var stock = new IndexerCacheHitRateLimiter(stockStore, () => _now, TestLogger);

            var first = Reserve();
            stock.Reserve(Host, IndexerKey, Interval);
            Advance(1);

            var second = Reserve();
            stock.Reserve(Host, IndexerKey, Interval);
            second.Delay.Should().Be(Interval - TimeSpan.FromMilliseconds(1));

            // First response is a hit, but the second request has already taken the next slot.
            Advance(50);
            _limiter.Refund(first).Should().BeFalse();
            _store[CombinedKey].Should().Be(second.Reserved);

            Advance(5);
            var third = Reserve();
            var stockThird = stock.Reserve(Host, IndexerKey, Interval);

            third.Delay.Should().Be(stockThird.Delay);
            third.Reserved.Should().Be(stockThird.Reserved);
            _store.Should().BeEquivalentTo(stockStore);
        }

        [Test]
        public void concurrent_hits_refunded_newest_first_should_unwind_completely()
        {
            var first = Reserve();
            Advance(1);
            var second = Reserve();

            _limiter.Refund(second).Should().BeTrue();
            _store[CombinedKey].Should().Be(first.Reserved);

            _limiter.Refund(first).Should().BeTrue();
            _store.ContainsKey(CombinedKey).Should().BeFalse();
        }

        [Test]
        public void concurrent_hits_refunded_oldest_first_should_keep_older_slot()
        {
            var first = Reserve();
            Advance(1);
            var second = Reserve();

            _limiter.Refund(first).Should().BeFalse();
            _limiter.Refund(second).Should().BeTrue();

            // Back to the first reservation: conservative, never earlier than without the second request.
            _store[CombinedKey].Should().Be(first.Reserved);

            Advance(10);
            var third = Reserve();
            third.Delay.Should().Be(first.Reserved - _now);
        }

        [Test]
        public void hit_after_a_miss_should_keep_the_miss_slot()
        {
            var miss = Reserve();
            Advance(1);
            var hit = Reserve();

            Advance((int)hit.Delay.TotalMilliseconds + 100);
            _limiter.Refund(hit).Should().BeTrue();

            _store[CombinedKey].Should().Be(miss.Reserved);
        }

        [Test]
        public void refund_should_fail_after_a_stock_pulse_on_the_same_key()
        {
            // e.g. a .torrent download from the same indexer goes through RateLimitService with the same key.
            var reservation = Reserve();
            var stockPulse = new IndexerCacheHitRateLimiter(_store, () => _now, TestLogger);
            Advance(10);
            stockPulse.Reserve(Host, IndexerKey, TimeSpan.FromMilliseconds(1));

            _limiter.Refund(reservation).Should().BeFalse();
        }

        [Test]
        public void randomized_interleavings_should_never_release_a_kept_reservation()
        {
            var random = new Random(1234);

            for (var run = 0; run < 200; run++)
            {
                _store.Clear();
                var open = new List<IndexerCacheHitRateLimiter.Reservation>();
                var kept = new List<IndexerCacheHitRateLimiter.Reservation>();

                for (var step = 0; step < 40; step++)
                {
                    Advance(random.Next(0, 1500));

                    if (open.Count == 0 || random.Next(2) == 0)
                    {
                        var reservation = _limiter.Reserve(Host, IndexerKey, TimeSpan.FromMilliseconds(random.Next(1, 3) * 1000));

                        // Never earlier than any reservation that was kept.
                        foreach (var k in kept)
                        {
                            (_now + reservation.Delay).Should().BeOnOrAfter(k.Reserved);
                        }

                        open.Add(reservation);
                    }
                    else
                    {
                        var index = random.Next(open.Count);
                        var done = open[index];
                        open.RemoveAt(index);

                        if (random.Next(3) == 0)
                        {
                            kept.Add(done);
                        }
                        else if (!_limiter.Refund(done))
                        {
                            kept.Add(done);
                        }
                    }

                    if (_store.TryGetValue(CombinedKey, out var current))
                    {
                        foreach (var k in kept)
                        {
                            current.Should().BeOnOrAfter(k.Reserved);
                        }
                    }
                    else
                    {
                        kept.Should().BeEmpty();
                    }
                }
            }
        }

        [Test]
        public void threaded_reserve_with_fixed_clock_should_not_lose_reservations()
        {
            // Fixed clock: every reservation must land exactly one interval after the previous one, so a lost update
            // between threads (a non-atomic read-modify-write) shows up as a final value short of start + n * interval.
            const int count = 200000;
            var store = new ConcurrentDictionary<string, DateTime>();
            var limiter = new IndexerCacheHitRateLimiter(store, () => _now, TestLogger);

            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                limiter.Reserve(Host, IndexerKey, TimeSpan.FromTicks(1));
            });

            store[CombinedKey].Should().Be(_now.AddTicks(count));
            store[Host].Should().Be(_now.AddTicks(1));
        }

        [Test]
        public void threaded_reserve_and_refund_with_fixed_clock_should_count_every_kept_reservation()
        {
            // Fixed clock and a 1-tick interval: each kept reservation adds exactly one tick and each successful refund
            // takes back exactly the tick its own reservation added, so the entry must equal start + (reserved - refunded)
            // ticks. A refund that overwrote a later reservation (no compare-and-swap), or a reservation lost to a race,
            // breaks the count. Every kept reservation must also still be covered by the entry.
            const int count = 200000;
            var store = new ConcurrentDictionary<string, DateTime>();
            var limiter = new IndexerCacheHitRateLimiter(store, () => _now, TestLogger);
            var kept = new ConcurrentBag<IndexerCacheHitRateLimiter.Reservation>();
            var refunded = 0;

            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                var reservation = limiter.Reserve(Host, IndexerKey, TimeSpan.FromTicks(1));

                if (i % 2 == 0 && limiter.Refund(reservation))
                {
                    Interlocked.Increment(ref refunded);
                }
                else
                {
                    kept.Add(reservation);
                }
            });

            refunded.Should().BeGreaterThan(0);
            kept.Should().HaveCount(count - refunded);

            var final = store.TryGetValue(CombinedKey, out var value) ? value : _now;

            final.Should().Be(_now.AddTicks(count - refunded));
            final.Should().BeOnOrAfter(kept.Max(k => k.Reserved));
        }

        [Test]
        public void reserve_should_match_rate_limit_service_exactly()
        {
            // Future store values make the result independent of the clock, so RateLimitService (which reads
            // DateTime.UtcNow itself) can be compared tick for tick. The returned tasks are left waiting.
            var random = new Random(42);
            var cacheManager = new CacheManager();
            var service = new RateLimitService(cacheManager, TestLogger);
            var stockStore = cacheManager.GetCache<ConcurrentDictionary<string, DateTime>>(typeof(RateLimitService), "rateLimitStore")
                .Get("rateLimitStore", () => new ConcurrentDictionary<string, DateTime>());
            var store = new ConcurrentDictionary<string, DateTime>();
            var limiter = new IndexerCacheHitRateLimiter(store, () => DateTime.UtcNow, TestLogger);
            var future = DateTime.UtcNow.AddDays(2);

            for (var i = 0; i < 500; i++)
            {
                var interval = TimeSpan.FromMilliseconds(random.Next(1, 6000));
                var subKey = random.Next(4).ToString();
                var combinedKey = Host + "-" + subKey;

                stockStore.Clear();
                store.Clear();

                if (random.Next(4) != 0)
                {
                    stockStore[Host] = store[Host] = future.AddMilliseconds(random.Next(0, 100000));
                }

                if (random.Next(4) != 0)
                {
                    stockStore[combinedKey] = store[combinedKey] = future.AddMilliseconds(random.Next(-50000, 100000));
                }

                if (!store.ContainsKey(Host))
                {
                    // Without a future host value "now" decides the result; covered by the next test.
                    stockStore[Host] = store[Host] = future;
                }

                _ = service.WaitAndPulseAsync(Host, subKey, interval);
                limiter.Reserve(Host, subKey, interval);

                store.Should().BeEquivalentTo(stockStore);
            }
        }

        [Test]
        public void reserve_on_empty_store_should_match_rate_limit_service()
        {
            var cacheManager = new CacheManager();
            var service = new RateLimitService(cacheManager, TestLogger);
            var stockStore = cacheManager.GetCache<ConcurrentDictionary<string, DateTime>>(typeof(RateLimitService), "rateLimitStore")
                .Get("rateLimitStore", () => new ConcurrentDictionary<string, DateTime>());
            var store = new ConcurrentDictionary<string, DateTime>();
            var limiter = new IndexerCacheHitRateLimiter(store, () => DateTime.UtcNow, TestLogger);

            service.WaitAndPulseAsync(Host, IndexerKey, Interval).Wait();
            var reservation = limiter.Reserve(Host, IndexerKey, Interval);

            reservation.Delay.Should().BeLessOrEqualTo(TimeSpan.Zero);
            store.Keys.Should().BeEquivalentTo(stockStore.Keys);

            foreach (var key in store.Keys)
            {
                store[key].Should().BeCloseTo(stockStore[key], TimeSpan.FromMilliseconds(250));
            }
        }

        [Test]
        public void public_constructor_should_share_rate_limit_service_store()
        {
            var cacheManager = new CacheManager();
            var service = new RateLimitService(cacheManager, TestLogger);
            var limiter = new IndexerCacheHitRateLimiter(cacheManager, TestLogger);

            IndexerCacheHitRateLimiter.Shared.Should().BeSameAs(limiter);

            limiter.Reserve(Host, IndexerKey, Interval);

            var stockStore = cacheManager.GetCache<ConcurrentDictionary<string, DateTime>>(typeof(RateLimitService), "rateLimitStore")
                .Get("rateLimitStore", () => null);
            stockStore.Should().ContainKey(CombinedKey);
            stockStore.Should().ContainKey(Host);
            service.Should().NotBeNull();
        }

        [Test]
        public void public_constructor_first_should_still_share_store()
        {
            var cacheManager = new CacheManager();
            var limiter = new IndexerCacheHitRateLimiter(cacheManager, TestLogger);
            var service = new RateLimitService(cacheManager, TestLogger);

            service.WaitAndPulseAsync(Host, IndexerKey, Interval).Wait();

            // The service's pulse is visible to the limiter: the next request has to wait.
            limiter.Reserve(Host, IndexerKey, Interval).Delay.Should().BeGreaterThan(TimeSpan.FromSeconds(1));
        }

        [TestCase("X-Search-Cache", "HIT", true)]
        [TestCase("x-search-cache", "hit", true)]
        [TestCase("X-Search-Cache", " Hit ", true)]
        [TestCase("X-Search-Cache", "HIT, HIT", true)]
        [TestCase("X-Search-Cache", "MISS", false)]
        [TestCase("X-Search-Cache", "UNCACHED", false)]
        [TestCase("X-Search-Cache", "BYPASS", false)]
        [TestCase("X-Search-Cache", "HIT, MISS", false)]
        [TestCase("X-Search-Cache", "HITS", false)]
        [TestCase("X-Search-Cache", "", false)]
        [TestCase("X-Other", "HIT", false)]
        [TestCase(null, null, false)]
        public void should_detect_cache_hit(string header, string value, bool expected)
        {
            IndexerCacheHitRateLimiter.IsCacheHit(ResponseWith(header, value), _hitOptions).Should().Be(expected);
        }

        [Test]
        public void should_not_treat_error_response_as_hit()
        {
            IndexerCacheHitRateLimiter.IsCacheHit(ResponseWith("X-Search-Cache", "HIT", HttpStatusCode.InternalServerError), _hitOptions).Should().BeFalse();
        }

        [Test]
        public void should_not_detect_hit_when_disabled()
        {
            IndexerCacheHitRateLimiter.IsCacheHit(ResponseWith("X-Search-Cache", "HIT"), IndexerRateLimitOptions.Stock).Should().BeFalse();
        }

        [Test]
        public async Task execute_should_pass_request_through_unchanged_when_disabled()
        {
            IndexerRateLimitOptions.Current = IndexerRateLimitOptions.Stock;
            IndexerCacheHitRateLimiter.Shared = _limiter;

            var request = new HttpRequest("http://" + Host + "/api") { RateLimit = Interval, RateLimitKey = IndexerKey };
            var client = new Mock<IHttpClient>();
            TimeSpan seen = default;
            client.Setup(c => c.ExecuteAsync(It.IsAny<HttpRequest>()))
                .Callback((HttpRequest r) => seen = r.RateLimit)
                .ReturnsAsync((HttpRequest r) => ResponseWith("X-Search-Cache", "HIT"));

            await IndexerCacheHitRateLimiter.ExecuteAsync(client.Object, request);

            client.Verify(c => c.ExecuteAsync(request), Times.Once());
            seen.Should().Be(Interval);
            _store.Should().BeEmpty();
        }

        [Test]
        public async Task execute_should_pass_request_through_when_no_shared_limiter()
        {
            IndexerRateLimitOptions.Current = _hitOptions;
            IndexerCacheHitRateLimiter.Shared = null;

            var request = new HttpRequest("http://" + Host + "/api") { RateLimit = Interval, RateLimitKey = IndexerKey };
            var client = new Mock<IHttpClient>();
            TimeSpan seen = default;
            client.Setup(c => c.ExecuteAsync(It.IsAny<HttpRequest>()))
                .Callback((HttpRequest r) => seen = r.RateLimit)
                .ReturnsAsync((HttpRequest r) => ResponseWith("X-Search-Cache", "HIT"));

            await IndexerCacheHitRateLimiter.ExecuteAsync(client.Object, request);

            seen.Should().Be(Interval);
        }

        private Mock<IHttpClient> GivenClient(int hops, string cacheValue, List<TimeSpan> seenPerHop)
        {
            var client = new Mock<IHttpClient>();
            client.Setup(c => c.ExecuteAsync(It.IsAny<HttpRequest>()))
                .ReturnsAsync((HttpRequest r) =>
                {
                    // What HttpClient does: interceptors run before each hop, then it paces by r.RateLimit.
                    for (var i = 0; i < hops; i++)
                    {
                        r = _limiter.PreRequest(r);
                        seenPerHop.Add(r.RateLimit);
                    }

                    return ResponseWith("X-Search-Cache", cacheValue);
                });

            return client;
        }

        [TestCase("HIT", false)]
        [TestCase("MISS", true)]
        public async Task execute_should_pace_first_hop_and_refund_only_hits(string cacheValue, bool keepsSlot)
        {
            IndexerRateLimitOptions.Current = _hitOptions;
            IndexerCacheHitRateLimiter.Shared = _limiter;

            var request = new HttpRequest("http://" + Host + "/api") { RateLimit = Interval, RateLimitKey = IndexerKey };
            var seen = new List<TimeSpan>();
            var client = GivenClient(1, cacheValue, seen);

            await IndexerCacheHitRateLimiter.ExecuteAsync(client.Object, request);

            seen.Should().Equal(TimeSpan.Zero);
            request.RateLimit.Should().Be(Interval);
            _store.ContainsKey(CombinedKey).Should().Be(keepsSlot);
            _store.ContainsKey(Host).Should().BeTrue();
        }

        [Test]
        public async Task redirect_hops_should_get_their_interval_back_and_not_refund()
        {
            IndexerRateLimitOptions.Current = _hitOptions;
            IndexerCacheHitRateLimiter.Shared = _limiter;

            var request = new HttpRequest("http://" + Host + "/api") { RateLimit = Interval, RateLimitKey = IndexerKey };
            var seen = new List<TimeSpan>();
            var client = GivenClient(3, "HIT", seen);

            await IndexerCacheHitRateLimiter.ExecuteAsync(client.Object, request);

            seen.Should().Equal(TimeSpan.Zero, Interval, Interval);
            request.RateLimit.Should().Be(Interval);
            _store.ContainsKey(CombinedKey).Should().BeTrue();
        }

        [Test]
        public async Task failed_request_should_keep_slot_and_restore_rate_limit()
        {
            IndexerRateLimitOptions.Current = _hitOptions;
            IndexerCacheHitRateLimiter.Shared = _limiter;

            var request = new HttpRequest("http://" + Host + "/api") { RateLimit = Interval, RateLimitKey = IndexerKey };
            var client = new Mock<IHttpClient>();
            client.Setup(c => c.ExecuteAsync(It.IsAny<HttpRequest>()))
                .ThrowsAsync(new WebException("boom"));

            Func<Task> act = () => IndexerCacheHitRateLimiter.ExecuteAsync(client.Object, request);

            await act.Should().ThrowAsync<WebException>();
            request.RateLimit.Should().Be(Interval);
            _store.ContainsKey(CombinedKey).Should().BeTrue();
        }
    }
}
