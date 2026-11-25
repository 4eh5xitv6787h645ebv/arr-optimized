using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.TPL;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Optimize;
using NzbDrone.Core.Profiles.Releases;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.OptimizeTests
{
    // On/off switches and the grab spacing: off (or 2 s) is exactly the stock behaviour.
    [TestFixture]
    [NonParallelizable]
    public class OptimizeSwitchesFixture : CoreTest<ReleaseProfileService>
    {
        [SetUp]
        public void Setup()
        {
            OptimizeSettings.Reset();
            Mocker.GetMock<IReleaseProfileRepository>().Setup(r => r.All()).Returns(() => new List<ReleaseProfile> { new ReleaseProfile { Id = 1, Name = "p" } });
        }

        [TearDown]
        public void TearDown()
        {
            OptimizeSettings.Reset();
        }

        [Test]
        public void release_profile_cache_off_should_read_the_repository_every_time()
        {
            Subject.All();
            Subject.All();
            Mocker.GetMock<IReleaseProfileRepository>().Verify(r => r.All(), Times.Once());

            OptimizeSettings.Current = OptimizeSettings.Current.With(OptimizeTunables.ReleaseProfileCache, false);

            Subject.All().Should().HaveCount(1);
            Subject.All().Should().HaveCount(1);
            Mocker.GetMock<IReleaseProfileRepository>().Verify(r => r.All(), Times.Exactly(3));

            // Turned on again: the list cached before is still valid (writes always clear it), so no extra read.
            OptimizeSettings.Current = OptimizeSettings.Current.With(OptimizeTunables.ReleaseProfileCache, true);
            Subject.All();
            Mocker.GetMock<IReleaseProfileRepository>().Verify(r => r.All(), Times.Exactly(3));
        }

        [Test]
        public void release_profile_writes_while_off_should_not_leave_an_old_list()
        {
            Subject.All();

            OptimizeSettings.Current = OptimizeSettings.Current.With(OptimizeTunables.ReleaseProfileCache, false);
            Mocker.GetMock<IReleaseProfileRepository>().Setup(r => r.All()).Returns(() => new List<ReleaseProfile> { new ReleaseProfile { Id = 1 }, new ReleaseProfile { Id = 2 } });
            Subject.Add(new ReleaseProfile { Id = 2 });

            OptimizeSettings.Current = OptimizeSettings.Current.With(OptimizeTunables.ReleaseProfileCache, true);
            Subject.All().Should().HaveCount(2);
        }

        [Test]
        public void batch_memo_off_should_load_on_every_use()
        {
            var loads = 0;
            Func<int, IndexerDefinition> load = id =>
            {
                loads++;
                return new IndexerDefinition { Id = id };
            };

            using (DecisionBatchCache.Begin())
            {
                DecisionBatchCache.Indexer(1, load);
                DecisionBatchCache.Indexer(1, load);
                loads.Should().Be(1);

                OptimizeSettings.Current = OptimizeSettings.Current.With(OptimizeTunables.DecisionBatchMemo, false);

                DecisionBatchCache.Indexer(1, load);
                DecisionBatchCache.Indexer(1, load);
                loads.Should().Be(3);
            }
        }

        [TestCase(2000, 2000)]
        [TestCase(5000, 2000)]
        [TestCase(2000, 1000)]
        [TestCase(2000, 0)]
        [TestCase(3000, 3000)]
        [TestCase(5000, 3000)]
        [TestCase(60000, 60000)]
        public async Task grab_should_be_one_pulse_with_the_grab_spacing_unless_base_is_below_it(int baseMs, int grabMs)
        {
            var spacing = TimeSpan.FromMilliseconds(grabMs);
            var service = new Mock<IRateLimitService>(MockBehavior.Strict);
            service.Setup(s => s.WaitAndPulseAsync("indexer.local", spacing)).Returns(Task.CompletedTask);

            await IndexerRateLimitOptions.WaitForGrabAsync(service.Object, "indexer.local",
                new IndexerRateLimitOptions(TimeSpan.FromMilliseconds(baseMs), null, null, spacing));

            service.Verify(s => s.WaitAndPulseAsync("indexer.local", spacing), Times.Once());
            service.VerifyNoOtherCalls();
        }

        [TestCase(2000, 2000, true)]
        [TestCase(2000, 3000, false)]
        [TestCase(2000, 1000, false)]
        [TestCase(1000, 2000, false)]
        public void is_stock_should_need_stock_base_and_grab_spacing(int baseMs, int grabMs, bool stock)
        {
            new IndexerRateLimitOptions(TimeSpan.FromMilliseconds(baseMs), null, null, TimeSpan.FromMilliseconds(grabMs)).IsStock.Should().Be(stock);
        }

        [Test]
        public async Task default_grab_spacing_should_be_the_stock_call()
        {
            OptimizeSettings.Current.RateLimit.GrabSpacing.Should().Be(TimeSpan.FromSeconds(2));

            var service = new Mock<IRateLimitService>(MockBehavior.Strict);
            service.Setup(s => s.WaitAndPulseAsync("indexer.local", TimeSpan.FromSeconds(2))).Returns(Task.CompletedTask);

            await IndexerRateLimitOptions.WaitForGrabAsync(service.Object, "indexer.local", IndexerRateLimitOptions.Current);

            service.Verify(s => s.WaitAndPulseAsync("indexer.local", TimeSpan.FromSeconds(2)), Times.Once());
            service.VerifyNoOtherCalls();
        }

        // Lowered base (100 ms) below the grab spacing (600 ms): each grab pulses the host with the base interval only,
        // and grabs are spaced by the grab spacing setting (not the stock 2 s).
        [Test]
        public async Task lowered_base_should_space_grabs_by_the_grab_spacing_and_pulse_the_base()
        {
            var service = new Mock<IRateLimitService>(MockBehavior.Strict);
            service.Setup(s => s.WaitAndPulseAsync("grab.local", TimeSpan.FromMilliseconds(100))).Returns(Task.CompletedTask);

            var lowered = new IndexerRateLimitOptions(TimeSpan.FromMilliseconds(100), null, null, TimeSpan.FromMilliseconds(600));

            var watch = System.Diagnostics.Stopwatch.StartNew();
            await IndexerRateLimitOptions.WaitForGrabAsync(service.Object, "grab.local", lowered);
            await IndexerRateLimitOptions.WaitForGrabAsync(service.Object, "grab.local", lowered);
            watch.Stop();

            watch.Elapsed.Should().BeGreaterOrEqualTo(TimeSpan.FromMilliseconds(550));
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(1500));

            service.Verify(s => s.WaitAndPulseAsync("grab.local", TimeSpan.FromMilliseconds(100)), Times.Exactly(2));
            service.VerifyNoOtherCalls();
        }

        // Stock base (2 s) with a larger grab spacing (3 s): the host is pulsed with the stock 2 s only, so the next
        // search is not held for 3 s, and grabs are 3 s apart.
        [Test]
        public async Task stock_base_with_larger_grab_spacing_should_pulse_the_base_and_space_grabs_by_the_setting()
        {
            var service = new Mock<IRateLimitService>(MockBehavior.Strict);
            service.Setup(s => s.WaitAndPulseAsync("grab3.local", TimeSpan.FromSeconds(2))).Returns(Task.CompletedTask);

            var options = new IndexerRateLimitOptions(TimeSpan.FromSeconds(2), null, null, TimeSpan.FromSeconds(3));

            var watch = System.Diagnostics.Stopwatch.StartNew();
            await IndexerRateLimitOptions.WaitForGrabAsync(service.Object, "grab3.local", options);
            var first = watch.Elapsed;
            await IndexerRateLimitOptions.WaitForGrabAsync(service.Object, "grab3.local", options);
            watch.Stop();

            first.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
            (watch.Elapsed - first).Should().BeGreaterOrEqualTo(TimeSpan.FromMilliseconds(2900));
            (watch.Elapsed - first).Should().BeLessThan(TimeSpan.FromMilliseconds(3900));

            service.Verify(s => s.WaitAndPulseAsync("grab3.local", TimeSpan.FromSeconds(2)), Times.Exactly(2));
            service.VerifyNoOtherCalls();
        }
    }
}
