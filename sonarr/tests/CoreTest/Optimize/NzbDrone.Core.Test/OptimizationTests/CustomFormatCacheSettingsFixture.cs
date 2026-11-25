using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Optimization;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.OptimizationTests
{
    // The custom format match cache gives the stock matches at every allowed setting (cache off, smallest, default
    // and largest values), when entries expire, and when the settings change while entries are cached.
    [TestFixture]
    [NonParallelizable]
    public class CustomFormatCacheSettingsFixture : CoreTest<CustomFormatCalculationService>
    {
        private List<CustomFormat> _formats;
        private List<RemoteEpisode> _releases;
        private Func<long> _originalClock;
        private long _now;

        [SetUp]
        public void Setup()
        {
            _originalClock = CustomFormatMatchCache.Clock;
            _now = 1_000_000;
            CustomFormatMatchCache.Clock = () => Interlocked.Read(ref _now);

            Apply(new Dictionary<string, object> { { "CfCacheCapacity", 0 } });

            _formats = new List<CustomFormat>
            {
                Format(1, "x264", new ReleaseTitleSpecification { Value = "x264" }),
                Format(2, "HEVC", new ReleaseTitleSpecification { Value = "(x265|HEVC)" }),
                Format(3, "Group A", new ReleaseGroupSpecification { Value = "^GrpA$" }),
                Format(4, "Big", new SizeSpecification { Min = 2, Max = 100 }),
                Format(5, "1080p WEB", new ResolutionSpecification { Value = (int)Resolution.R1080p, Required = true }, new SourceSpecification { Value = (int)QualitySource.Web, Required = true }),
                Format(6, "Not 720p", new ResolutionSpecification { Value = (int)Resolution.R720p, Negate = true }),
                Format(7, "Repack", new ReleaseTitleSpecification { Value = "\\bREPACK\\b" }, new ReleaseTitleSpecification { Value = "\\bPROPER\\b" })
            };

            Mocker.GetMock<ICustomFormatService>().Setup(s => s.All()).Returns(_formats);

            var qualities = new[] { Quality.HDTV720p, Quality.WEBDL1080p, Quality.Bluray2160p, Quality.WEBRip720p };
            var groups = new[] { "GrpA", "GrpB", "grpa", null };
            var codecs = new[] { "x264", "x265", "HEVC", "XviD" };
            var series = new Series { Id = 1, Title = "Show", OriginalLanguage = Language.English };

            _releases = Enumerable.Range(0, 240).Select(i =>
            {
                var quality = qualities[i % qualities.Length];
                var group = groups[(i / 4) % groups.Length];
                var title = $"Show.S01E{i % 30 + 1:00}.{(i % 7 == 0 ? "REPACK." : string.Empty)}{quality.Name}.{codecs[(i / 3) % codecs.Length]}-{group ?? "nogrp"}";

                return new RemoteEpisode
                {
                    Series = series,
                    ParsedEpisodeInfo = new ParsedEpisodeInfo { ReleaseTitle = title, ReleaseGroup = group, Quality = new QualityModel(quality) },
                    Languages = new List<Language> { i % 5 == 0 ? Language.French : Language.English },
                    Release = new ReleaseInfo { Title = title }
                };
            }).ToList();
        }

        [TearDown]
        public void TearDown()
        {
            CustomFormatMatchCache.Clock = _originalClock;
            OptimizeSettings.Current = null;
            CustomFormatMatchCache.Apply(OptimizeSettings.Current);
        }

        private static CustomFormat Format(int id, string name, params ICustomFormatSpecification[] specifications)
        {
            return new CustomFormat(name, specifications) { Id = id };
        }

        private static void Apply(Dictionary<string, object> values)
        {
            var settings = OptimizeSettings.Defaults.With(values);
            OptimizeSettings.Current = settings;
            CustomFormatMatchCache.Apply(settings);
        }

        private long Size(int i)
        {
            return (i % 3 == 0 ? 5L : 1L) * 1024 * 1024 * 1024;
        }

        private List<string> Match()
        {
            return _releases.Select((r, i) => string.Join(",", Subject.ParseCustomFormat(r, Size(i)).Select(f => f.Name))).ToList();
        }

        private List<string> Stock()
        {
            var current = OptimizeSettings.Current;
            Apply(new Dictionary<string, object> { { "CfCacheCapacity", 0 } });
            var stock = Match();
            OptimizeSettings.Current = current;
            CustomFormatMatchCache.Apply(current);
            return stock;
        }

        [Test]
        public void cache_off_should_run_the_stock_evaluation_and_keep_nothing()
        {
            Apply(new Dictionary<string, object> { { "CfCacheCapacity", 0 } });
            var hits = Interlocked.Read(ref CustomFormatMatchCache.Hits);
            var misses = Interlocked.Read(ref CustomFormatMatchCache.Misses);

            var first = Match();
            var second = Match();

            second.Should().Equal(first);
            first.Should().Contain(m => m.Contains("x264")).And.Contain(m => m.Contains("1080p WEB")).And.Contain(m => m.Contains("Repack"));
            CustomFormatMatchCache.Count.Should().Be(0);
            Interlocked.Read(ref CustomFormatMatchCache.Hits).Should().Be(hits);
            Interlocked.Read(ref CustomFormatMatchCache.Misses).Should().Be(misses);
        }

        [TestCase(1, 1, 256)]
        [TestCase(7, 1, 256)]
        [TestCase(50000, 360, 4096)]
        [TestCase(1000000, 10080, 65536)]
        public void every_allowed_setting_should_match_like_stock(int capacity, int lifetimeMinutes, int maxKeyChars)
        {
            var stock = Stock();

            Apply(new Dictionary<string, object>
            {
                { "CfCacheCapacity", capacity },
                { "CfCacheLifetimeMinutes", lifetimeMinutes },
                { "CfCacheMaxKeyChars", maxKeyChars },
                { "CfCacheSweepSeconds", 1 }
            });

            var hits = Interlocked.Read(ref CustomFormatMatchCache.Hits);

            Match().Should().Equal(stock);
            Match().Should().Equal(stock);

            // Past the idle lifetime every entry expires; matches stay the same.
            Interlocked.Add(ref _now, (lifetimeMinutes * 60_000L) + 1);
            Match().Should().Equal(stock);

            CustomFormatMatchCache.Count.Should().BeLessOrEqualTo(Math.Max(capacity, 1));

            if (capacity >= _releases.Count)
            {
                Interlocked.Read(ref CustomFormatMatchCache.Hits).Should().BeGreaterThan(hits);
            }
        }

        [Test]
        public void shrinking_the_capacity_should_trim_the_cache_at_once()
        {
            var stock = Stock();

            Apply(new Dictionary<string, object> { { "CfCacheCapacity", 1000 } });
            Match().Should().Equal(stock);
            CustomFormatMatchCache.Count.Should().BeGreaterThan(50);

            Apply(new Dictionary<string, object> { { "CfCacheCapacity", 50 } });
            CustomFormatMatchCache.Count.Should().BeLessOrEqualTo(50);

            Match().Should().Equal(stock);
            CustomFormatMatchCache.Count.Should().BeLessOrEqualTo(50);
        }

        [Test]
        public void shortening_the_lifetime_should_drop_idle_entries_at_once()
        {
            Apply(new Dictionary<string, object> { { "CfCacheLifetimeMinutes", 360 } });
            Match();
            CustomFormatMatchCache.Count.Should().BeGreaterThan(0);

            Interlocked.Add(ref _now, 2 * 60_000L);
            Apply(new Dictionary<string, object> { { "CfCacheLifetimeMinutes", 1 } });

            CustomFormatMatchCache.Count.Should().Be(0);
        }

        // Idle entries are swept out when a new entry is added and the sweep interval has passed, not before.
        [TestCase(1, true)]
        [TestCase(60, true)]
        [TestCase(3600, false)]
        public void sweep_interval_setting_should_decide_when_idle_entries_are_dropped(int sweepSeconds, bool swept)
        {
            Apply(new Dictionary<string, object> { { "CfCacheLifetimeMinutes", 1 }, { "CfCacheSweepSeconds", sweepSeconds } });
            Match();
            var count = CustomFormatMatchCache.Count;
            count.Should().BeGreaterThan(100);

            // Past the idle lifetime, a release that is not cached yet adds an entry.
            Interlocked.Add(ref _now, 2 * 60_000L);
            var fresh = new RemoteEpisode
            {
                Series = _releases[0].Series,
                ParsedEpisodeInfo = new ParsedEpisodeInfo { ReleaseTitle = "Show.S02E01.WEBDL-1080p.x264-GrpA", ReleaseGroup = "GrpA", Quality = new QualityModel(Quality.WEBDL1080p) },
                Languages = new List<Language> { Language.English },
                Release = new ReleaseInfo { Title = "Show.S02E01.WEBDL-1080p.x264-GrpA" }
            };
            Subject.ParseCustomFormat(fresh, Size(0)).Select(f => f.Name).Should().Contain("x264");

            CustomFormatMatchCache.Count.Should().Be(swept ? 1 : count + 1);
        }

        [Test]
        public void turning_the_cache_off_and_on_should_drop_entries_and_still_match_like_stock()
        {
            var stock = Stock();

            Apply(new Dictionary<string, object> { { "CfCacheCapacity", 50000 } });
            Match().Should().Equal(stock);
            CustomFormatMatchCache.Count.Should().BeGreaterThan(0);

            Apply(new Dictionary<string, object> { { "CfCacheCapacity", 0 } });
            CustomFormatMatchCache.Count.Should().Be(0);
            Match().Should().Equal(stock);
            CustomFormatMatchCache.Count.Should().Be(0);

            Apply(new Dictionary<string, object> { { "CfCacheCapacity", 50000 } });
            Match().Should().Equal(stock);
            Match().Should().Equal(stock);
        }

        [Test]
        public void concurrent_matching_while_settings_change_should_match_like_stock()
        {
            var stock = Stock();
            var capacities = new[] { 0, 1, 10, 50000, 0, 200, 1000000 };
            var failures = 0;
            var stop = false;

            var workers = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    if (!Match().SequenceEqual(stock))
                    {
                        Interlocked.Increment(ref failures);
                    }
                }
            })).ToList();

            workers.ForEach(w => w.Start());

            foreach (var capacity in capacities.Concat(capacities))
            {
                Apply(new Dictionary<string, object> { { "CfCacheCapacity", capacity } });
                Thread.Sleep(30);
            }

            Volatile.Write(ref stop, true);
            workers.ForEach(w => w.Join());

            failures.Should().Be(0);
        }
    }
}
