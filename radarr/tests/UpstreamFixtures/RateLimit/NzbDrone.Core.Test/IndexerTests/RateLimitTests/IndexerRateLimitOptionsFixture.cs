using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.IndexerTests.RateLimitTests
{
    [TestFixture]
    [NonParallelizable]
    public class IndexerRateLimitOptionsFixture : CoreTest
    {
        private Dictionary<string, string> _variables;

        [SetUp]
        public void Setup()
        {
            _variables = new Dictionary<string, string>();
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable(IndexerRateLimitOptions.RateLimitVariable, null);
            Environment.SetEnvironmentVariable(IndexerRateLimitOptions.CacheHitHeaderVariable, null);
            Environment.SetEnvironmentVariable(IndexerRateLimitOptions.CacheHitValueVariable, null);
            IndexerRateLimitOptions.Current = null;
        }

        private IndexerRateLimitOptions Parse()
        {
            return IndexerRateLimitOptions.FromEnvironment(n => _variables.TryGetValue(n, out var v) ? v : null, TestLogger);
        }

        [Test]
        public void unset_should_be_stock()
        {
            var options = Parse();

            options.BaseRateLimit.Should().Be(TimeSpan.FromSeconds(2));
            options.BaseRateLimit.Ticks.Should().Be(20_000_000);
            options.RefundsCacheHits.Should().BeFalse();
            options.CacheHitHeader.Should().BeNull();
            options.IsStock.Should().BeTrue();
        }

        [Test]
        public void stock_constant_should_equal_upstream_value()
        {
            IndexerRateLimitOptions.Stock.BaseRateLimit.Should().Be(TimeSpan.FromSeconds(2));
            IndexerRateLimitOptions.Stock.IsStock.Should().BeTrue();
        }

        [TestCase("", 2000)]
        [TestCase("   ", 2000)]
        [TestCase("0", 0)]
        [TestCase("250", 250)]
        [TestCase(" 1500 ", 1500)]
        [TestCase("2000", 2000)]
        [TestCase("86400000", 86400000)]
        public void should_parse_rate_limit(string value, long expectedMilliseconds)
        {
            _variables[IndexerRateLimitOptions.RateLimitVariable] = value;

            var options = Parse();

            options.BaseRateLimit.Should().Be(TimeSpan.FromMilliseconds(expectedMilliseconds));
            options.RefundsCacheHits.Should().BeFalse();
        }

        [TestCase("-1")]
        [TestCase("+5")]
        [TestCase("abc")]
        [TestCase("1.5")]
        [TestCase("1,000")]
        [TestCase("2s")]
        [TestCase("86400001")]
        [TestCase("99999999999999999999999")]
        public void invalid_rate_limit_should_warn_and_keep_stock(string value)
        {
            _variables[IndexerRateLimitOptions.RateLimitVariable] = value;

            var options = Parse();

            options.BaseRateLimit.Should().Be(TimeSpan.FromSeconds(2));
            options.IsStock.Should().BeTrue();
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void header_should_enable_refund_with_default_value()
        {
            _variables[IndexerRateLimitOptions.CacheHitHeaderVariable] = " X-Search-Cache ";

            var options = Parse();

            options.RefundsCacheHits.Should().BeTrue();
            options.CacheHitHeader.Should().Be("X-Search-Cache");
            options.CacheHitValue.Should().Be("HIT");
            options.BaseRateLimit.Should().Be(TimeSpan.FromSeconds(2));
            options.IsStock.Should().BeFalse();
        }

        [Test]
        public void should_use_configured_hit_value()
        {
            _variables[IndexerRateLimitOptions.CacheHitHeaderVariable] = "X-Cache";
            _variables[IndexerRateLimitOptions.CacheHitValueVariable] = "fresh-hit";

            var options = Parse();

            options.CacheHitHeader.Should().Be("X-Cache");
            options.CacheHitValue.Should().Be("fresh-hit");
        }

        [TestCase("X Search Cache")]
        [TestCase("X-Search-Cache:")]
        [TestCase("X-Säarch")]
        [TestCase("X-Search\nCache")]
        public void invalid_header_should_warn_and_stay_off(string header)
        {
            _variables[IndexerRateLimitOptions.CacheHitHeaderVariable] = header;

            var options = Parse();

            options.RefundsCacheHits.Should().BeFalse();
            options.IsStock.Should().BeTrue();
            ExceptionVerification.ExpectedWarns(1);
        }

        [TestCase("HIT, MISS")]
        [TestCase("a;b")]
        public void invalid_hit_value_should_warn_and_use_default(string value)
        {
            _variables[IndexerRateLimitOptions.CacheHitHeaderVariable] = "X-Search-Cache";
            _variables[IndexerRateLimitOptions.CacheHitValueVariable] = value;

            var options = Parse();

            options.RefundsCacheHits.Should().BeTrue();
            options.CacheHitValue.Should().Be("HIT");
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void hit_value_without_header_should_warn_and_stay_off()
        {
            _variables[IndexerRateLimitOptions.CacheHitValueVariable] = "HIT";

            var options = Parse();

            options.RefundsCacheHits.Should().BeFalse();
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void current_should_read_process_environment_once()
        {
            Environment.SetEnvironmentVariable(IndexerRateLimitOptions.RateLimitVariable, "300");
            Environment.SetEnvironmentVariable(IndexerRateLimitOptions.CacheHitHeaderVariable, "X-Search-Cache");
            IndexerRateLimitOptions.Current = null;

            var first = IndexerRateLimitOptions.Current;
            first.BaseRateLimit.Should().Be(TimeSpan.FromMilliseconds(300));
            first.CacheHitHeader.Should().Be("X-Search-Cache");

            Environment.SetEnvironmentVariable(IndexerRateLimitOptions.RateLimitVariable, "900");

            IndexerRateLimitOptions.Current.Should().BeSameAs(first);
            IndexerRateLimitOptions.Current.BaseRateLimit.Should().Be(TimeSpan.FromMilliseconds(300));
        }

        [Test]
        public void current_should_be_stock_without_environment()
        {
            Environment.SetEnvironmentVariable(IndexerRateLimitOptions.RateLimitVariable, null);
            Environment.SetEnvironmentVariable(IndexerRateLimitOptions.CacheHitHeaderVariable, null);
            Environment.SetEnvironmentVariable(IndexerRateLimitOptions.CacheHitValueVariable, null);
            IndexerRateLimitOptions.Current = null;

            IndexerRateLimitOptions.Current.IsStock.Should().BeTrue();
            IndexerRateLimitOptions.Current.BaseRateLimit.Should().Be(TimeSpan.FromSeconds(2));
        }

        [TestCase(2000, null)]
        [TestCase(2000, "X-Search-Cache")]
        [TestCase(5000, null)]
        [TestCase(86400000, null)]
        public async Task grab_should_be_the_stock_call_unless_base_is_lowered(int milliseconds, string header)
        {
            var service = new Mock<IRateLimitService>(MockBehavior.Strict);
            service.Setup(s => s.WaitAndPulseAsync("indexer.local", TimeSpan.FromSeconds(2))).Returns(Task.CompletedTask);

            await IndexerRateLimitOptions.WaitForGrabAsync(service.Object, "indexer.local", new IndexerRateLimitOptions(TimeSpan.FromMilliseconds(milliseconds), header, null));

            service.Verify(s => s.WaitAndPulseAsync("indexer.local", TimeSpan.FromSeconds(2)), Times.Once());
            service.VerifyNoOtherCalls();
        }

        [Test]
        public async Task grab_with_stock_options_should_be_the_stock_call()
        {
            var service = new Mock<IRateLimitService>(MockBehavior.Strict);
            service.Setup(s => s.WaitAndPulseAsync("indexer.local", TimeSpan.FromSeconds(2))).Returns(Task.CompletedTask);

            await IndexerRateLimitOptions.WaitForGrabAsync(service.Object, "indexer.local", IndexerRateLimitOptions.Stock);

            service.Verify(s => s.WaitAndPulseAsync("indexer.local", TimeSpan.FromSeconds(2)), Times.Once());
            service.VerifyNoOtherCalls();
            IndexerRateLimitOptions.GrabRateLimit.Ticks.Should().Be(20_000_000);
        }

        [TestCase(0)]
        [TestCase(500)]
        [TestCase(1999)]
        public async Task grab_with_lowered_base_should_pulse_host_with_base_only(int milliseconds)
        {
            var calls = new List<string>();
            var service = new Mock<IRateLimitService>(MockBehavior.Strict);
            service.Setup(s => s.WaitAndPulseAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()))
                .Callback((string key, TimeSpan interval) => calls.Add(key + "=" + (long)interval.TotalMilliseconds))
                .Returns(Task.CompletedTask);

            await IndexerRateLimitOptions.WaitForGrabAsync(service.Object, "indexer.local", new IndexerRateLimitOptions(TimeSpan.FromMilliseconds(milliseconds), null, null));

            // Grab-to-grab spacing (stock 2 s) is kept by the grab path itself, counted from when the previous grab was
            // released; the only rate-limit call is the last wait: "<host>" with the configured base.
            calls.Should().Equal("indexer.local=" + milliseconds);
        }
    }
}
