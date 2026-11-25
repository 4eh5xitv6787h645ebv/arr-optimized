using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Languages;
using NzbDrone.Core.MediaFiles.MediaInfo;
using NzbDrone.Core.Optimize;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.OptimizeTests
{
    // A Save on the Optimize page (and a ConfigSavedEvent) applies changed cache limits to the cache contents at once,
    // and the status page reports the caches' own numbers.
    [TestFixture]
    [NonParallelizable]
    public class OptimizeApplyOnSaveFixture : CoreTest<OptimizeSettingsService>
    {
        private static readonly DateTime Written = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        private Dictionary<string, string> _store;
        private VideoFileInfoReader _reader;
        private List<CustomFormat> _formats;

        [SetUp]
        public void Setup()
        {
            ClearCustomFormatCache();
            _store = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            Mocker.GetMock<IConfigService>()
                  .Setup(s => s.GetOptimizeValue(It.IsAny<string>()))
                  .Returns<string>(key => _store.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null);

            Mocker.GetMock<IConfigService>()
                  .Setup(s => s.SetOptimizeValue(It.IsAny<string>(), It.IsAny<string>()))
                  .Callback<string, string>((key, value) => _store[key] = value ?? string.Empty);

            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.CheckForFinishedDownloadInterval)
                  .Returns(1);

            _reader = new VideoFileInfoReader(Mocker.Resolve<IDiskProvider>(), TestLogger);
            Mocker.SetConstant<IVideoFileInfoReader>(_reader);

            _formats = new List<CustomFormat>
            {
                new CustomFormat("x265", new ReleaseTitleSpecification { Value = @"[xh]\.?265|hevc" }) { Id = 1 }
            };

            Subject.Reload();
        }

        [TearDown]
        public void TearDown()
        {
            ClearCustomFormatCache();
        }

        private static void ClearCustomFormatCache()
        {
            OptimizeSettings.Current = OptimizeSettings.Current.With(OptimizeTunables.CustomFormatCacheCapacity, 0L);
            CustomFormatMatchCache.ApplyLimits();
            OptimizeSettings.Reset();
        }

        private static string Key(string name)
        {
            return OptimizeTunables.Get(name).ConfigKey;
        }

        private void FillCaches(int count)
        {
            for (var i = 0; i < count; i++)
            {
                var title = $"Movie.{2000 + i}.1080p.BluRay.x265-GRP";
                var input = new CustomFormatInput
                {
                    MovieInfo = new ParsedMovieInfo { SimpleReleaseTitle = title, ReleaseTitle = title, ReleaseGroup = "GRP", Quality = new QualityModel(Quality.Bluray1080p) },
                    Size = 1_000_000_000,
                    Languages = new List<Language> { Language.English }
                };

                CustomFormatMatchCache.Parse(input, _formats, (_, _) => throw new InvalidOperationException("the cache is on"));

                _reader.ProbeCache.Add("/movies/" + i, 1, Written, new MediaInfoModel { Width = i, RawStreamData = "{}" });
            }

            CustomFormatMatchCache.Count.Should().Be(count);
            _reader.ProbeCache.Count.Should().Be(count);
        }

        [Test]
        public void save_should_shrink_both_caches_at_once()
        {
            FillCaches(20);

            Subject.Save(new Dictionary<string, object>
            {
                { OptimizeTunables.CustomFormatCacheCapacity, 5 },
                { OptimizeTunables.FfprobeCacheEntries, 10 }
            });

            CustomFormatMatchCache.Count.Should().Be(5);
            _reader.ProbeCache.Count.Should().BeLessOrEqualTo(10);
            _reader.ProbeCache.Count.Should().BeGreaterThan(0);
        }

        [Test]
        public void save_should_empty_both_caches_when_turned_off()
        {
            FillCaches(10);

            Subject.Save(new Dictionary<string, object>
            {
                { OptimizeTunables.CustomFormatCacheCapacity, 0 },
                { OptimizeTunables.FfprobeCacheMaxMegabytes, 0 }
            });

            CustomFormatMatchCache.Count.Should().Be(0);
            _reader.ProbeCache.Count.Should().Be(0);
            _reader.ProbeCache.Bytes.Should().Be(0);
        }

        [Test]
        public void config_saved_event_should_apply_cache_limits_at_once()
        {
            FillCaches(10);

            _store[Key(OptimizeTunables.CustomFormatCacheCapacity)] = "0";
            _store[Key(OptimizeTunables.FfprobeCacheEntries)] = "0";
            Subject.Handle(new ConfigSavedEvent());

            CustomFormatMatchCache.Count.Should().Be(0);
            _reader.ProbeCache.Count.Should().Be(0);
        }

        [Test]
        public void status_should_report_the_cache_numbers()
        {
            FillCaches(3);

            _reader.ProbeCache.TryGet("/movies/1", 1, Written, out _).Should().BeTrue();
            _reader.ProbeCache.TryGet("/movies/2", 1, Written, out _).Should().BeTrue();
            _reader.ProbeCache.TryGet("/movies/missing", 1, Written, out _).Should().BeFalse();

            var counters = Subject.GetStatus().Counters.ToDictionary(c => c.Name, c => c.Value);

            counters["customFormatCacheEntries"].Should().Be(3);
            counters["customFormatCacheMisses"].Should().Be(CustomFormatMatchCache.ReadCounter(ref CustomFormatMatchCache.Misses));
            counters["ffprobeCacheEntries"].Should().Be(3);
            counters["ffprobeCacheHits"].Should().Be(2);
            counters["ffprobeCacheMisses"].Should().Be(1);
            counters["ffprobeCacheBytes"].Should().Be(_reader.ProbeCache.Bytes / 1024);
        }
    }
}
