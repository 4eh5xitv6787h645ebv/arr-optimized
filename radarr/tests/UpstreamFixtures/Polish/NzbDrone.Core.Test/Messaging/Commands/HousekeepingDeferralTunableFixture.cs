using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Housekeeping;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Optimize;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Messaging.Commands
{
    // The Optimize page finds the housekeeping maximum wait by reflection (the page and the bound came from separate
    // patches). In this build both are present, so the page must list it, read-only, with the constant's value, and the
    // status must describe the bounded wait instead of an indefinite one.
    [TestFixture]
    [NonParallelizable]
    public class HousekeepingDeferralTunableFixture : CoreTest<OptimizeSettingsService>
    {
        [SetUp]
        public void Setup()
        {
            OptimizeSettings.Reset();
            Mocker.GetMock<IConfigService>().SetupGet(s => s.CheckForFinishedDownloadInterval).Returns(1);
            Mocker.SetConstant<IVideoFileInfoReader>(new VideoFileInfoReader(Mocker.Resolve<NzbDrone.Common.Disk.IDiskProvider>(), TestLogger));
        }

        [TearDown]
        public void TearDown()
        {
            OptimizeSettings.Reset();
        }

        [Test]
        public void optimize_page_should_list_the_housekeeping_max_deferral()
        {
            var tunable = OptimizeTunables.Get(OptimizeTunables.HousekeepingMaxDeferral);

            tunable.Should().NotBeNull();
            tunable.Editable.Should().BeFalse();
            tunable.DefaultValue.Should().Be((long)HousekeepingCommand.MaxDeferralMinutes);
            tunable.ReadOnlyValue(OptimizeSettings.Current).Should().Be((long)HousekeepingCommand.MaxDeferral.TotalMinutes);
            OptimizeTunables.All.Should().Contain(tunable);
            OptimizeTunables.Editable.Should().NotContain(tunable);
        }

        [Test]
        public void settings_list_should_hold_the_housekeeping_deferral_row_with_its_value()
        {
            var row = Subject.GetSettings().SingleOrDefault(s => s.Name == "housekeepingMaxDeferral");

            row.Should().NotBeNull("GET /api/v3/config/optimize/settings lists the housekeeping deferral");
            row.Value.Should().Be(30L);
            row.DefaultValue.Should().Be(30L);
            row.Unit.Should().Be("minutes");
            row.Editable.Should().BeFalse();
            row.Source.Should().Be("fixed");
            row.HowToChange.Should().Contain("HousekeepingCommand.MaxDeferral");
        }

        [Test]
        public void status_should_describe_the_bounded_housekeeping_wait()
        {
            var feature = Subject.GetStatus().Features.Single(f => f.Name == "Exclusive housekeeping");

            feature.Active.Should().BeTrue();
            feature.Description.Should().Contain("HousekeepingCommand.MaxDeferralMinutes (30 minutes)");
            feature.Description.Should().Contain("for at most");
            feature.Description.Should().NotContain("start only when no other command runs");
            feature.State.Should().Be("On, waits at most 30 minutes");
        }
    }
}
