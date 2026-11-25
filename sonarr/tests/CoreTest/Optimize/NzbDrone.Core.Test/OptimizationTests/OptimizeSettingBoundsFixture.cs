using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Optimization;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.OptimizationTests
{
    // The documented bounds and defaults of every Optimize page value (the table in the notes and READMEs), pinned to
    // literal numbers so a changed bound fails here instead of moving the other tests along with it.
    [TestFixture]
    public class OptimizeSettingBoundsFixture : TestBase
    {
        // Name, default, page minimum, page maximum, environment maximum (when larger than the page maximum).
        private static readonly object[][] Table =
        {
            new object[] { "IndexerRateLimitMs", 2000, 0L, 10000L, 86400000L },
            new object[] { "FfprobeTimeoutMinutes", 15, 0L, 1440L, null },
            new object[] { "GrabSpacingMs", 2000, 0L, 60000L, null },
            new object[] { "CfCacheCapacity", 50000, 0L, 1000000L, null },
            new object[] { "CfCacheLifetimeMinutes", 360, 1L, 10080L, null },
            new object[] { "CfCacheMaxKeyChars", 4096, 256L, 65536L, null },
            new object[] { "CfCacheSweepSeconds", 60, 1L, 3600L, null },
            new object[] { "FfprobeCacheCapacity", 1000, 0L, 100000L, null },
            new object[] { "FfprobeCacheMaxMb", 32, 0L, 1024L, null },
            new object[] { "FfprobeCacheMaxEntryKb", 4096, 1L, 131072L, null },
            new object[] { "FfprobeCacheIdleMinutes", 360, 1L, 10080L, null },
            new object[] { "FfprobeCacheFailureMinutes", 10, 0L, 10L, null },
            new object[] { "FfprobeCacheSettleSeconds", 60, 60L, 3600L, null },
            new object[] { "FfprobeCacheUnsettledSeconds", 60, 0L, 60L, null },
            new object[] { "FfprobeCacheSweepSeconds", 60, 1L, 3600L, null }
        };

        private static IEnumerable<TestCaseData> IntegerBounds()
        {
            return Table.Select(row => new TestCaseData(row).SetName("{m}(" + row[0] + ")"));
        }

        [TestCaseSource(nameof(IntegerBounds))]
        public void integer_value_should_have_the_documented_default_and_bounds(string name, int defaultValue, long min, long max, long? environmentMax)
        {
            var definition = OptimizeSettingDefinitions.Get(name);

            definition.Should().NotBeNull();
            definition.Type.Should().Be(OptimizeValueType.Integer);
            definition.DefaultValue.Should().Be(defaultValue);
            definition.Min.Should().Be(min);
            definition.Max.Should().Be(max);
            definition.EnvironmentMax.Should().Be(environmentMax);

            definition.TryParse(min.ToString(), false, out _, out _).Should().BeTrue();
            definition.TryParse(max.ToString(), false, out _, out _).Should().BeTrue();
            definition.TryParse((max + 1).ToString(), false, out _, out _).Should().BeFalse();
            definition.TryParse((environmentMax ?? max).ToString(), true, out _, out _).Should().BeTrue();
            definition.TryParse(((environmentMax ?? max) + 1).ToString(), true, out _, out _).Should().BeFalse();

            if (min > 0)
            {
                definition.TryParse((min - 1).ToString(), false, out _, out _).Should().BeFalse();
                definition.TryParse((min - 1).ToString(), true, out _, out _).Should().BeFalse();
            }
        }

        [Test]
        public void text_and_boolean_values_should_have_the_documented_defaults()
        {
            OptimizeSettingDefinitions.Get("CacheHitHeader").DefaultValue.Should().Be(string.Empty);
            OptimizeSettingDefinitions.Get("CacheHitValue").DefaultValue.Should().Be("HIT");
            OptimizeSettingDefinitions.Get("DecisionMemoEnabled").DefaultValue.Should().Be(true);
        }

        [Test]
        public void table_should_cover_every_integer_value()
        {
            OptimizeSettingDefinitions.All.Where(d => d.Type == OptimizeValueType.Integer).Select(d => d.Name)
                .Should().BeEquivalentTo(Table.Select(row => (string)row[0]));
            OptimizeSettingDefinitions.All.Should().HaveCount(18);
        }

        // Values below these warn on the page (risky), values above the others warn about memory.
        [TestCase("IndexerRateLimitMs", 1999, true)]
        [TestCase("IndexerRateLimitMs", 2000, false)]
        [TestCase("GrabSpacingMs", 1999, true)]
        [TestCase("GrabSpacingMs", 2000, false)]
        [TestCase("FfprobeTimeoutMinutes", 0, true)]
        [TestCase("FfprobeTimeoutMinutes", 1, false)]
        [TestCase("CfCacheCapacity", 200001, true)]
        [TestCase("CfCacheCapacity", 200000, false)]
        [TestCase("CfCacheMaxKeyChars", 16385, true)]
        [TestCase("FfprobeCacheCapacity", 20001, true)]
        [TestCase("FfprobeCacheMaxMb", 257, true)]
        [TestCase("FfprobeCacheMaxMb", 256, false)]
        public void risky_values_should_carry_a_warning(string name, int value, bool risky)
        {
            OptimizeSettingDefinitions.Get(name).IsRisky(value).Should().Be(risky);
        }
    }
}
