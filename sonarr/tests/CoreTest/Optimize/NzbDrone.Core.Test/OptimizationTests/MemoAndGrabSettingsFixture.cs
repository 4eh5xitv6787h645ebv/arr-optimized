using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Optimization;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.OptimizationTests
{
    // "Read memo per decision batch" switch and the grab spacing value.
    [TestFixture]
    [NonParallelizable]
    public class MemoAndGrabSettingsFixture : TestBase
    {
        [SetUp]
        public void Setup()
        {
            OptimizeSettings.Current = null;
        }

        [TearDown]
        public void TearDown()
        {
            OptimizeSettings.Current = null;
        }

        private static void Apply(Dictionary<string, object> values)
        {
            OptimizeSettings.Current = OptimizeSettings.Defaults.With(values);
        }

        private static int ReadTwice()
        {
            var loads = 0;

            using (DecisionScope.Begin())
            {
                DecisionScope.Memo("Indexers", 1, () =>
                {
                    loads++;
                    return new object();
                });
                DecisionScope.Memo("Indexers", 1, () =>
                {
                    loads++;
                    return new object();
                });
            }

            return loads;
        }

        [Test]
        public void memo_on_should_read_once_per_batch_and_count()
        {
            var batches = Interlocked.Read(ref DecisionScope.Batches);
            var hits = Interlocked.Read(ref DecisionScope.MemoHits);
            var loads = Interlocked.Read(ref DecisionScope.MemoLoads);

            ReadTwice().Should().Be(1);

            Interlocked.Read(ref DecisionScope.Batches).Should().Be(batches + 1);
            Interlocked.Read(ref DecisionScope.MemoHits).Should().Be(hits + 1);
            Interlocked.Read(ref DecisionScope.MemoLoads).Should().Be(loads + 1);

            var status = new OptimizeSettingsService(new Mock<IConfigService>().Object, TestLogger).GetStatus().Counters;
            status.Single(c => c.Name == "decisionBatches").Value.Should().Be(batches + 1);
            status.Single(c => c.Name == "memoHits").Value.Should().Be(hits + 1);
            status.Single(c => c.Name == "memoLoads").Value.Should().Be(loads + 1);
        }

        [Test]
        public void memo_off_should_read_every_time_like_stock()
        {
            Apply(new Dictionary<string, object> { { "DecisionMemoEnabled", false } });
            var batches = Interlocked.Read(ref DecisionScope.Batches);

            using (DecisionScope.Begin())
            {
                // Callers such as BlocklistService take their stock path when no batch is active.
                DecisionScope.Active.Should().BeFalse();
            }

            ReadTwice().Should().Be(2);
            Interlocked.Read(ref DecisionScope.Batches).Should().Be(batches);

            // Applies to the next batch without a restart.
            Apply(new Dictionary<string, object> { { "DecisionMemoEnabled", true } });
            ReadTwice().Should().Be(1);
        }

        private static List<string> GrabCalls(int baseMilliseconds, int grabMilliseconds)
        {
            var calls = new List<string>();
            var service = new Mock<IRateLimitService>();
            service.Setup(s => s.WaitAndPulseAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()))
                .Callback<string, TimeSpan>((key, interval) => calls.Add(key + "=" + (long)interval.TotalMilliseconds))
                .Returns(Task.CompletedTask);

            var options = new IndexerRateLimitOptions(TimeSpan.FromMilliseconds(baseMilliseconds), null, null, TimeSpan.FromMilliseconds(grabMilliseconds));
            IndexerRateLimitOptions.WaitForGrabAsync(service.Object, "dl.local", options).GetAwaiter().GetResult();

            return calls;
        }

        [Test]
        public void default_grab_spacing_should_be_the_stock_call()
        {
            OptimizeSettings.Current.GrabSpacingMs.Should().Be(2000);
            IndexerRateLimitOptions.Current.GrabSpacing.Should().Be(TimeSpan.FromSeconds(2));

            // Stock DownloadService: await _rateLimitService.WaitAndPulseAsync(url.Host, TimeSpan.FromSeconds(2));
            GrabCalls(2000, 2000).Should().Equal("dl.local=2000");
        }

        [TestCase(2000, 500, "dl.local=500")]
        [TestCase(2000, 0, "dl.local=0")]
        [TestCase(5000, 3000, "dl.local=3000")]

        // A lowered base that is still above the grab spacing: one pulse with the grab spacing, not the base.
        [TestCase(1000, 500, "dl.local=500")]
        [TestCase(1500, 1000, "dl.local=1000")]
        public void grab_spacing_at_or_below_the_base_should_be_one_pulse_with_the_spacing(int baseMilliseconds, int grabMilliseconds, string expected)
        {
            GrabCalls(baseMilliseconds, grabMilliseconds).Should().Equal(expected);
        }

        [Test]
        public void grab_spacing_above_the_base_should_pulse_the_host_with_the_base()
        {
            GrabCalls(500, 1000).Should().Equal("dl.local=500");
        }

        [Test]
        public void risky_grab_spacing_should_carry_the_warning()
        {
            var definition = OptimizeSettingDefinitions.Get("GrabSpacingMs");

            definition.IsRisky(1999).Should().BeTrue();
            definition.IsRisky(2000).Should().BeFalse();
            definition.Warning.Should().Contain("may rate-limit or ban you");
        }
    }
}
