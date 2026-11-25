using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Optimize;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.OptimizeTests
{
    // The custom-format match cache only caches: at every allowed capacity, expiry and input limit (including 0 =
    // off) every release gets exactly the custom formats the stock grouping rule gives, also while the limits change
    // between and during evaluations.
    [TestFixture]
    [NonParallelizable]
    public class OptimizeCustomFormatCacheFixture : CoreTest<CustomFormatCalculationService>
    {
        private List<CustomFormat> _formats;
        private List<RemoteMovie> _releases;

        [SetUp]
        public void Setup()
        {
            OptimizeSettings.Reset();
            ClearCache();

            _formats = BuildFormats();
            Mocker.GetMock<ICustomFormatService>().Setup(s => s.All()).Returns(() => _formats);
            _releases = BuildReleases();
        }

        [TearDown]
        public void TearDown()
        {
            ClearCache();
            OptimizeSettings.Reset();
        }

        private static void ClearCache()
        {
            OptimizeSettings.Current = OptimizeSettings.Current.With(OptimizeTunables.CustomFormatCacheCapacity, 0L);
            CustomFormatMatchCache.ApplyLimits();
            OptimizeSettings.Reset();
        }

        private static void Use(long capacity, long expiryMinutes, long maxInput)
        {
            OptimizeSettings.Current = OptimizeSettings.Current
                .With(OptimizeTunables.CustomFormatCacheCapacity, capacity)
                .With(OptimizeTunables.CustomFormatCacheExpiryMinutes, expiryMinutes)
                .With(OptimizeTunables.CustomFormatCacheMaxInputChars, maxInput);
            CustomFormatMatchCache.ApplyLimits();
        }

        private static List<CustomFormat> BuildFormats()
        {
            CustomFormat Format(int id, string name, params ICustomFormatSpecification[] specs)
            {
                return new CustomFormat(name, specs) { Id = id };
            }

            return new List<CustomFormat>
            {
                Format(1, "x265", new ReleaseTitleSpecification { Value = @"[xh]\.?265|hevc" }),
                Format(2, "Remux 2160p", new SourceSpecification { Value = (int)QualitySource.BLURAY, Required = true },
                    new QualityModifierSpecification { Value = (int)Modifier.REMUX, Required = true },
                    new ResolutionSpecification { Value = (int)Resolution.R2160p, Required = true }),
                Format(3, "Not English", new LanguageSpecification { Value = Language.English.Id, Negate = true }),
                Format(4, "Original language", new LanguageSpecification { Value = Language.Original.Id }),
                Format(5, "Big", new SizeSpecification { Min = 10, Max = 200 }),
                Format(6, "Freeleech", new IndexerFlagSpecification { Value = (int)IndexerFlags.G_Freeleech }),
                Format(7, "Group A or B", new ReleaseGroupSpecification { Value = "^(GRPA|GRPB)$" }, new ReleaseGroupSpecification { Value = "^GRPC$", Negate = true, Required = true }),
                Format(8, "Directors cut", new EditionSpecification { Value = "director" }),
                Format(9, "Older than 2000", new YearSpecification { Min = 1900, Max = 1999 }),
                Format(10, "HDR not 720", new ReleaseTitleSpecification { Value = @"\bHDR\b", Required = true }, new ResolutionSpecification { Value = (int)Resolution.R720p, Negate = true }),
                Format(11, "Optional mix", new ReleaseTitleSpecification { Value = "DTS" }, new ReleaseTitleSpecification { Value = "TrueHD" }, new SizeSpecification { Min = 0, Max = 5 })
            };
        }

        private static List<RemoteMovie> BuildReleases()
        {
            var titles = new[] { "Movie.2160p.BluRay.REMUX.HEVC.HDR.TrueHD-GRPA", "Movie.720p.WEB.x264.DTS-GRPC", "Movie.Directors.Cut.1080p.BluRay.x265-GRPB", "Movie.HDR.720p.WEB-DL-NOGRP" };
            var sources = new[] { QualitySource.BLURAY, QualitySource.WEBDL, QualitySource.TV };
            var resolutions = new[] { Resolution.R2160p, Resolution.R1080p, Resolution.R720p };
            var languages = new[] { new List<Language> { Language.English }, new List<Language> { Language.French }, new List<Language> { Language.English, Language.German } };
            var releases = new List<RemoteMovie>();
            var index = 0;

            foreach (var title in titles)
            {
                foreach (var source in sources)
                {
                    foreach (var resolution in resolutions)
                    {
                        index++;

                        var movie = new Movie
                        {
                            Id = index,
                            MovieMetadata = new MovieMetadata { Year = index % 2 == 0 ? 1995 : 2015, OriginalLanguage = index % 3 == 0 ? Language.French : Language.English }
                        };

                        releases.Add(new RemoteMovie
                        {
                            Movie = movie,
                            Languages = languages[index % languages.Length],
                            Release = new ReleaseInfo { Title = title, IndexerFlags = index % 4 == 0 ? IndexerFlags.G_Freeleech : 0, Size = index * 1_500_000_000L },
                            ParsedMovieInfo = new ParsedMovieInfo
                            {
                                SimpleReleaseTitle = title + (index % 5 == 0 ? new string('x', 5000) : string.Empty),
                                ReleaseTitle = title,
                                ReleaseGroup = title.Split('-').Last(),
                                Edition = title.Contains("Directors") ? "Directors Cut" : null,
                                Year = index % 7 == 0 ? 0 : 1990 + index,
                                Quality = new QualityModel(new Quality { Id = index, Name = "q" + index, Source = source, Resolution = (int)resolution, Modifier = index % 3 == 1 ? Modifier.REMUX : Modifier.NONE }),
                                Languages = languages[index % languages.Length]
                            }
                        });
                    }
                }
            }

            return releases;
        }

        // Stock CustomFormatCalculationService rule: group by specification type; a group matches unless a required
        // specification fails or no specification matches; a format matches when all groups match; sorted by name.
        private List<string> Stock(RemoteMovie release)
        {
            var input = new CustomFormatInput
            {
                MovieInfo = release.ParsedMovieInfo,
                Movie = release.Movie,
                Size = release.Release.Size,
                Languages = release.Languages,
                IndexerFlags = release.Release.IndexerFlags
            };

            return _formats.Where(f => f.Specifications.GroupBy(s => s.GetType())
                    .Select(g => new SpecificationMatchesGroup { Matches = g.ToDictionary(s => s, s => s.IsSatisfiedBy(input)) })
                    .All(g => g.DidMatch))
                .OrderBy(f => f.Name)
                .Select(f => f.Name)
                .ToList();
        }

        private List<string> Cached(RemoteMovie release)
        {
            return Subject.ParseCustomFormat(release, release.Release.Size).Select(f => f.Name).ToList();
        }

        [Test]
        public void sample_should_exercise_matches_and_misses()
        {
            var results = _releases.Select(Stock).ToList();

            results.Should().Contain(r => r.Count <= 1);
            results.Should().Contain(r => r.Count >= 3);
            results.Select(r => string.Join(",", r)).Distinct().Count().Should().BeGreaterThan(15);
            _formats.Select(f => f.Name).Should().OnlyContain(name => results.Any(r => r.Contains(name)), "every format matches some release");
        }

        [Test]
        public void should_equal_stock_at_every_allowed_setting(
            [Values(0, 1, 2, 50_000, 1_000_000)] long capacity,
            [Values(1, 360, 10_080)] long expiryMinutes,
            [Values(256, 4096, 65_536)] long maxInput)
        {
            Use(capacity, expiryMinutes, maxInput);

            for (var pass = 0; pass < 3; pass++)
            {
                foreach (var release in _releases)
                {
                    Cached(release).Should().Equal(Stock(release), release.Release.Title);
                }
            }

            if (capacity == 0)
            {
                CustomFormatMatchCache.Count.Should().Be(0);
            }
            else
            {
                CustomFormatMatchCache.Count.Should().BeInRange(1, (int)capacity);
            }
        }

        [Test]
        public void disabled_cache_should_take_the_uncached_path_and_drop_entries()
        {
            Use(50_000, 360, 4096);
            _releases.ForEach(r => Cached(r));
            CustomFormatMatchCache.Count.Should().BeGreaterThan(0);

            var hits = CustomFormatMatchCache.ReadCounter(ref CustomFormatMatchCache.Hits);
            var misses = CustomFormatMatchCache.ReadCounter(ref CustomFormatMatchCache.Misses);

            Use(0, 360, 4096);
            CustomFormatMatchCache.Count.Should().Be(0);

            foreach (var release in _releases)
            {
                Cached(release).Should().Equal(Stock(release));
            }

            CustomFormatMatchCache.Count.Should().Be(0);
            CustomFormatMatchCache.ReadCounter(ref CustomFormatMatchCache.Hits).Should().Be(hits);
            CustomFormatMatchCache.ReadCounter(ref CustomFormatMatchCache.Misses).Should().Be(misses);
        }

        [Test]
        public void shrinking_should_trim_at_once_and_keep_the_most_recent()
        {
            Use(50_000, 360, 65_536);
            _releases.ForEach(r => Cached(r));
            var full = CustomFormatMatchCache.Count;
            full.Should().BeGreaterThan(10);

            Use(5, 360, 65_536);
            CustomFormatMatchCache.Count.Should().Be(5);

            var hits = CustomFormatMatchCache.ReadCounter(ref CustomFormatMatchCache.Hits);

            // The five most recently used releases are the last five evaluated.
            foreach (var release in _releases.Skip(_releases.Count - 5))
            {
                Cached(release).Should().Equal(Stock(release));
            }

            CustomFormatMatchCache.ReadCounter(ref CustomFormatMatchCache.Hits).Should().Be(hits + 5);
        }

        [Test]
        public void changing_limits_during_concurrent_evaluations_should_never_change_a_result()
        {
            var expected = _releases.ToDictionary(r => r, Stock);
            var capacities = new long[] { 0, 1, 3, 50_000, 1_000_000 };
            var failures = 0;
            var stop = 0;

            var readers = Enumerable.Range(0, 4).Select(seed => Task.Run(() =>
            {
                var random = new Random(seed);

                while (Volatile.Read(ref stop) == 0)
                {
                    var release = _releases[random.Next(_releases.Count)];

                    if (!Cached(release).SequenceEqual(expected[release]))
                    {
                        Interlocked.Increment(ref failures);
                    }
                }
            })).ToArray();

            for (var i = 0; i < 200; i++)
            {
                Use(capacities[i % capacities.Length], i % 2 == 0 ? 1 : 360, i % 3 == 0 ? 256 : 65_536);
                CustomFormatMatchCache.Count.Should().BeLessOrEqualTo((int)Math.Max(capacities[i % capacities.Length], 0) + 4);
                Thread.Sleep(1);
            }

            Volatile.Write(ref stop, 1);
            Task.WaitAll(readers);

            failures.Should().Be(0);
        }

        [Test]
        public void shorter_expiry_should_drop_idle_entries_at_once()
        {
            var originalClock = CustomFormatMatchCache.Clock;
            var now = 1_000_000L;

            try
            {
                CustomFormatMatchCache.Clock = () => now;
                Use(50_000, 360, 65_536);
                _releases.ForEach(r => Cached(r));
                CustomFormatMatchCache.Count.Should().Be(_releases.Count);

                now += 2 * 60_000;
                Use(50_000, 1, 65_536);
                CustomFormatMatchCache.Count.Should().Be(0);

                foreach (var release in _releases)
                {
                    Cached(release).Should().Equal(Stock(release));
                }
            }
            finally
            {
                CustomFormatMatchCache.Clock = originalClock;
            }
        }
    }
}
