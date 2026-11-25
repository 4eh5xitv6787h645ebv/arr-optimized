using System;
using System.Diagnostics;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Optimize;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.OptimizeTests
{
    // Wall-clock timing of the grab spacing with real delays (moved here from OptimizeSwitchesFixture). The windows can
    // be missed on a loaded machine without anything being wrong, so CI runs this fixture in the step that is retried
    // once and does not block publishing, like IndexerRateLimitTimingFixture.
    [TestFixture]
    [NonParallelizable]
    public class OptimizeGrabSpacingTimingFixture : TestBase
    {
        [SetUp]
        public void Setup()
        {
            OptimizeSettings.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            OptimizeSettings.Reset();
        }

        // Lowered base (100 ms) below the grab spacing (600 ms): each grab pulses the host with the base interval only,
        // and grabs are spaced by the grab spacing setting (not the stock 2 s).
        [Test]
        public async Task lowered_base_should_space_grabs_by_the_grab_spacing_and_pulse_the_base()
        {
            var service = new Mock<IRateLimitService>(MockBehavior.Strict);
            service.Setup(s => s.WaitAndPulseAsync("grab.local", TimeSpan.FromMilliseconds(100))).Returns(Task.CompletedTask);

            var lowered = new IndexerRateLimitOptions(TimeSpan.FromMilliseconds(100), null, null, TimeSpan.FromMilliseconds(600));

            var watch = Stopwatch.StartNew();
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

            var watch = Stopwatch.StartNew();
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
