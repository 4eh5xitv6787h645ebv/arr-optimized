using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Optimize;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;
using NzbDrone.Test.Common.Categories;

namespace NzbDrone.Core.Test.OptimizeTests
{
    // Status counters of the cache-hit refund: a refunded hit, and a hit whose slot is kept because another request
    // reserved the next one meanwhile.
    [TestFixture]
    [NonParallelizable]
    public class OptimizeRefundCountersFixture : CoreTest
    {
        private const string Host = "prowlarr.local";
        private const string IndexerKey = "5";

        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

        private ConcurrentDictionary<string, DateTime> _store;
        private DateTime _now;
        private IndexerCacheHitRateLimiter _limiter;

        [SetUp]
        public void Setup()
        {
            _store = new ConcurrentDictionary<string, DateTime>();
            _now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            _limiter = new IndexerCacheHitRateLimiter(_store, () => _now, TestLogger);

            IndexerRateLimitOptions.Current = new IndexerRateLimitOptions(IndexerRateLimitOptions.StockRateLimit, "X-Search-Cache", "HIT");
            IndexerCacheHitRateLimiter.Shared = _limiter;
        }

        [TearDown]
        public void TearDown()
        {
            IndexerRateLimitOptions.Current = null;
            IndexerCacheHitRateLimiter.Shared = null;
            OptimizeSettings.Reset();
        }

        private static HttpResponse Hit()
        {
            var headers = new HttpHeader();
            headers.Add("X-Search-Cache", "HIT");

            return new HttpResponse(new HttpRequest("http://" + Host + "/api"), headers, Array.Empty<byte>(), HttpStatusCode.OK);
        }

        private static Dictionary<string, long> Read()
        {
            return new Dictionary<string, long>
            {
                ["hits"] = OptimizeCounters.Read(ref OptimizeCounters.CacheHitResponses),
                ["refunds"] = OptimizeCounters.Read(ref OptimizeCounters.Refunds),
                ["kept"] = OptimizeCounters.Read(ref OptimizeCounters.RefundsKept),
                ["refundedTicks"] = OptimizeCounters.Read(ref OptimizeCounters.RefundedTicks),
                ["paced"] = OptimizeCounters.Read(ref OptimizeCounters.PacedRequests)
            };
        }

        private Task Execute(Action duringRequest = null)
        {
            var request = new HttpRequest("http://" + Host + "/api") { RateLimit = Interval, RateLimitKey = IndexerKey };
            var client = new Mock<IHttpClient>();
            client.Setup(c => c.ExecuteAsync(It.IsAny<HttpRequest>()))
                .ReturnsAsync((HttpRequest r) =>
                {
                    _limiter.PreRequest(r);
                    duringRequest?.Invoke();

                    return Hit();
                });

            return IndexerCacheHitRateLimiter.ExecuteAsync(client.Object, request);
        }

        [Test]
        public async Task refunded_hit_should_count_a_refund_and_its_time()
        {
            var before = Read();

            await Execute();

            var after = Read();
            after["hits"].Should().Be(before["hits"] + 1);
            after["refunds"].Should().Be(before["refunds"] + 1);
            after["kept"].Should().Be(before["kept"]);
            after["refundedTicks"].Should().Be(before["refundedTicks"] + Interval.Ticks);
            after["paced"].Should().Be(before["paced"] + 1);
        }

        [Test]
        public async Task hit_whose_next_slot_is_taken_should_count_as_kept()
        {
            var before = Read();

            // Another request to the same indexer reserves the next slot while this one is in flight.
            await Execute(() => _limiter.Reserve(Host, IndexerKey, Interval));

            var after = Read();
            after["hits"].Should().Be(before["hits"] + 1);
            after["refunds"].Should().Be(before["refunds"]);
            after["kept"].Should().Be(before["kept"] + 1);
            after["refundedTicks"].Should().Be(before["refundedTicks"]);
        }
    }

    // The ffprobe timeout counter on the status page counts every ffprobe run stopped by the timeout.
    [TestFixture]
    [DiskAccessTest]
    [NonParallelizable]
    [Platform(Exclude = "Win")]
    public class OptimizeFfprobeTimeoutCounterFixture : CoreTest<VideoFileInfoReader>
    {
        private string _fifo;

        [SetUp]
        public void Setup()
        {
            OptimizeSettings.Reset();

            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileExists(It.IsAny<string>())).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.GetFileSize(It.IsAny<string>())).Returns(1000);
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileGetLastWrite(It.IsAny<string>())).Returns(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));

            var folder = GetTempFilePath();
            Directory.CreateDirectory(folder);
            _fifo = Path.Combine(folder, "stalled.mkv");

            using var mkfifo = Process.Start("mkfifo", new[] { _fifo });
            mkfifo.WaitForExit();
            mkfifo.ExitCode.Should().Be(0);
        }

        [TearDown]
        public void TearDown()
        {
            KillFfprobeFor(_fifo);
            OptimizeSettings.Reset();
        }

        [Test]
        public void stopped_ffprobe_should_be_counted()
        {
            VideoFileInfoReader.ProbeTimeout = TimeSpan.FromSeconds(2);
            var before = OptimizeCounters.Read(ref OptimizeCounters.FfprobeTimeouts);

            var probe = Task.Run(() => Subject.GetMediaInfo(_fifo));

            try
            {
                probe.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
                probe.Result.Should().BeNull();
                OptimizeCounters.Read(ref OptimizeCounters.FfprobeTimeouts).Should().Be(before + 1);
                ExceptionVerification.ExpectedErrors(1);
            }
            finally
            {
                KillFfprobeFor(_fifo);
                probe.Wait(TimeSpan.FromSeconds(30));
                ExceptionVerification.IgnoreErrors();
            }
        }

        private static void KillFfprobeFor(string path)
        {
            if (path == null || !Directory.Exists("/proc"))
            {
                return;
            }

            foreach (var directory in Directory.GetDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), out var pid))
                {
                    continue;
                }

                try
                {
                    var commandLine = File.ReadAllText(Path.Combine(directory, "cmdline")).Replace('\0', ' ');

                    if (commandLine.Contains("ffprobe") && commandLine.Contains(path))
                    {
                        using var process = Process.GetProcessById(pid);
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }
                catch (Exception)
                {
                    // Gone already
                }
            }
        }
    }
}
