using System;
using System.Collections.Generic;
using System.Threading;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Optimize;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.OptimizeTests
{
    // The sliding expiry of the custom-format match cache is checked at lookup time against the setting in effect,
    // without ApplyLimits, and the hit/miss/expiry counters follow every lookup.
    [TestFixture]
    [NonParallelizable]
    public class OptimizeCustomFormatExpiryFixture : CoreTest
    {
        private Func<long> _originalClock;
        private long _now;
        private List<CustomFormat> _formats;
        private int _uncachedCalls;

        [SetUp]
        public void Setup()
        {
            Clear();
            _uncachedCalls = 0;
            _originalClock = CustomFormatMatchCache.Clock;
            _now = 10_000_000;
            CustomFormatMatchCache.Clock = () => Interlocked.Read(ref _now);

            _formats = new List<CustomFormat>
            {
                new CustomFormat("x265", new ReleaseTitleSpecification { Value = @"[xh]\.?265|hevc" }) { Id = 1 },
                new CustomFormat("1080p", new ResolutionSpecification { Value = (int)Resolution.R1080p }) { Id = 2 }
            };
        }

        [TearDown]
        public void TearDown()
        {
            CustomFormatMatchCache.Clock = _originalClock;
            Clear();
        }

        private static void Clear()
        {
            OptimizeSettings.Current = OptimizeSettings.Current.With(OptimizeTunables.CustomFormatCacheCapacity, 0L);
            CustomFormatMatchCache.ApplyLimits();
            OptimizeSettings.Reset();
        }

        private static CustomFormatInput Input(string title)
        {
            return new CustomFormatInput
            {
                MovieInfo = new ParsedMovieInfo
                {
                    SimpleReleaseTitle = title,
                    ReleaseTitle = title,
                    ReleaseGroup = "GRP",
                    Quality = new QualityModel(Quality.Bluray1080p)
                },
                Size = 1_000_000_000,
                Languages = new List<Language> { Language.English }
            };
        }

        private List<CustomFormat> Parse(string title)
        {
            return CustomFormatMatchCache.Parse(Input(title), _formats, (i, f) =>
            {
                _uncachedCalls++;
                return new List<CustomFormat>();
            });
        }

        private static (long Hits, long Misses, long Expirations) Counters()
        {
            return (CustomFormatMatchCache.ReadCounter(ref CustomFormatMatchCache.Hits),
                CustomFormatMatchCache.ReadCounter(ref CustomFormatMatchCache.Misses),
                CustomFormatMatchCache.ReadCounter(ref CustomFormatMatchCache.Expirations));
        }

        [TestCase(1)]
        [TestCase(360)]
        [TestCase(720)]
        public void lookup_should_refuse_an_entry_idle_for_the_expiry_in_effect(long expiryMinutes)
        {
            OptimizeSettings.Current = OptimizeSettings.Current.With(OptimizeTunables.CustomFormatCacheExpiryMinutes, expiryMinutes);
            var expiryMs = expiryMinutes * 60_000;

            var start = Counters();
            Parse("Movie.2015.1080p.BluRay.x265-GRP").Should().HaveCount(2);
            Counters().Should().Be((start.Hits, start.Misses + 1, start.Expirations));

            // Within the expiry: a hit, which refreshes the entry.
            Interlocked.Add(ref _now, expiryMs - 1000);
            Parse("Movie.2015.1080p.BluRay.x265-GRP").Should().HaveCount(2);
            Counters().Should().Be((start.Hits + 1, start.Misses + 1, start.Expirations));

            // Idle for longer than the expiry: refused at lookup, evaluated again, same result.
            Interlocked.Add(ref _now, expiryMs + 1000);
            Parse("Movie.2015.1080p.BluRay.x265-GRP").Should().HaveCount(2);
            Counters().Should().Be((start.Hits + 1, start.Misses + 2, start.Expirations + 1));

            _uncachedCalls.Should().Be(0);
            CustomFormatMatchCache.Count.Should().Be(1);
        }

        [Test]
        public void shortened_expiry_should_refuse_an_entry_without_apply_limits()
        {
            Parse("Movie.2015.1080p.WEB.h264-GRP").Should().HaveCount(1);

            Interlocked.Add(ref _now, 10 * 60_000);

            // Only the snapshot changes; ApplyLimits is not called.
            OptimizeSettings.Current = OptimizeSettings.Current.With(OptimizeTunables.CustomFormatCacheExpiryMinutes, 5L);

            var start = Counters();
            Parse("Movie.2015.1080p.WEB.h264-GRP").Should().HaveCount(1);
            Counters().Should().Be((start.Hits, start.Misses + 1, start.Expirations + 1));
        }

        [Test]
        public void capacity_zero_should_take_the_uncached_path_and_count_nothing()
        {
            OptimizeSettings.Current = OptimizeSettings.Current.With(OptimizeTunables.CustomFormatCacheCapacity, 0L);

            var start = Counters();
            Parse("Movie.2015.1080p.BluRay.x265-GRP");
            Parse("Movie.2015.1080p.BluRay.x265-GRP");

            _uncachedCalls.Should().Be(2);
            Counters().Should().Be(start);
            CustomFormatMatchCache.Count.Should().Be(0);
        }
    }
}
