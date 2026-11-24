// Expiry only bounds memory if no cached match can outlive a change to what it was computed from.
// Each change below must be reflected on the very next call, with no expiry involved.
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Tv;

public static partial class Tests
{
    static List<CustomFormat> LanguageFormats() => new()
    {
        new CustomFormat("original", new LanguageSpecification { Value = Language.Original.Id }) { Id = 1 },
        new CustomFormat("not-original", new LanguageSpecification { Value = Language.Original.Id, Negate = true }) { Id = 2 },
        new CustomFormat("except-original", new LanguageSpecification { Value = Language.Original.Id, ExceptLanguage = true }) { Id = 3 },
        new CustomFormat("english", new LanguageSpecification { Value = Language.English.Id }) { Id = 4 },
        new CustomFormat("1080p", new ResolutionSpecification { Value = 1080 }) { Id = 5 },
        new CustomFormat("web", new SourceSpecification { Value = (int)QualitySource.Web }) { Id = 6 },
        new CustomFormat("season-pack", new ReleaseTypeSpecification { Value = (int)ReleaseType.SeasonPack }) { Id = 7 }
    };

    static void Test_SeriesOriginalLanguageChange()
    {
        UseFakeClock();
        var formats = LanguageFormats();
        var series = new Series { OriginalLanguage = Language.English };
        var input = Input(1);
        input.Series = series;
        Equal(input, formats);
        series.OriginalLanguage = Language.Japanese;  // in-place metadata refresh
        Equal(input, formats, "Original-language change served a stale match");
        series.OriginalLanguage = Language.Unknown;
        Equal(input, formats, "Unknown original language served a stale match");
        series.OriginalLanguage = null;
        Equal(input, formats);
        input.Series = new Series { OriginalLanguage = Language.English }; // a different series object
        Equal(input, formats);
        input.Series = null;
        Equal(input, formats, "Missing series served a stale match");
        input.EpisodeInfo = null;
        Equal(input, formats, "Missing episode info served a stale match");
    }

    static void Test_SeriesAndEpisodeMetadataOutsideTheKeyCannotChangeMatches()
    {
        // Specifications read only: release title/group/quality, filename, size, languages,
        // indexer flags, release type and the series' original language. Changing anything else
        // must leave the oracle result unchanged, so a cached result for it stays correct.
        var formats = Formats(AllSpecs()).Concat(LanguageFormats().Select(f => { f.Id += 100; return f; })).ToList();
        var input = Input(1);
        var before = Oracle(input, formats).Select(f => f.Id).ToList();
        input.Series.Title = "Renamed";
        input.Series.Year = 1999;
        input.Series.Tags = new HashSet<int> { 1, 2 };
        input.Series.SeriesType = SeriesTypes.Anime;
        input.Series.QualityProfileId = 99;
        input.EpisodeInfo.SeriesTitle = "Other";
        input.EpisodeInfo.SeasonNumbers = new List<int> { 9 };
        input.EpisodeInfo.EpisodeNumbers = new[] { 9 };
        input.EpisodeInfo.AbsoluteEpisodeNumbers = new[] { 99 };
        Check(Oracle(input, formats).Select(f => f.Id).SequenceEqual(before), "A non-key field changed the native result");
        Equal(input, formats);
    }

    static void Test_LanguageListChangesInPlace()
    {
        var formats = LanguageFormats();
        var input = Input(1);
        Equal(input, formats);
        input.Languages.Add(Language.Japanese);
        Equal(input, formats, "In-place language addition served a stale match");
        input.Languages.Clear();
        Equal(input, formats, "In-place language removal served a stale match");
        input.Languages = null;
        Equal(input, formats);
    }

    static void Test_QualityChanges()
    {
        var formats = LanguageFormats();
        var input = Input(1);
        Equal(input, formats);
        input.EpisodeInfo.Quality = new QualityModel(Quality.Bluray2160p);
        Equal(input, formats, "Quality change served a stale match");
        input.EpisodeInfo.Quality.Quality = Quality.HDTV1080p;
        Equal(input, formats, "In-place quality change served a stale match");

        // Quality definition edits (title, sizes) do not touch Resolution/Source. Even if a Quality
        // object's resolution or source were mutated in place, the key reads the current values.
        var q = new Quality { Id = 9001, Name = "custom", Source = QualitySource.Web, Resolution = 1080 };
        input.EpisodeInfo.Quality = new QualityModel(q);
        Equal(input, formats);
        q.Resolution = 720;
        Equal(input, formats, "Resolution mutation served a stale match");
        q.Source = QualitySource.Bluray;
        Equal(input, formats, "Source mutation served a stale match");
    }

    static void Test_CustomFormatEditsAndProfileScoreEdits()
    {
        var formats = LanguageFormats();
        var input = Input(1);
        var matched = Parse(input, formats);
        var profile = new QualityProfile
        {
            FormatItems = formats.Select(f => new ProfileFormatItem { Format = f, Score = 10 }).ToList()
        };
        var score = profile.CalculateCustomFormatScore(matched);
        Check(score == matched.Count * 10, "Unexpected score");
        // Profile score edits are applied after matching: the cache holds no scores.
        profile.FormatItems.ForEach(i => i.Score = 7);
        Check(profile.CalculateCustomFormatScore(Parse(input, formats)) == matched.Count * 7, "Profile score edit not applied");
        // Custom format edits: replace a definition with a new object (as an API edit does) and edit in place.
        formats[4] = new CustomFormat("1080p", new ResolutionSpecification { Value = 720 }) { Id = 5 };
        Equal(input, formats, "Replaced custom format served a stale match");
        ((ResolutionSpecification)formats[4].Specifications[0]).Value = 1080;
        Equal(input, formats, "In-place custom format edit served a stale match");
        formats[4].Specifications.Add(new SizeSpecification { Min = 100, Max = 200, Required = true });
        Equal(input, formats, "Added condition served a stale match");
        formats[4].Name = "zzz";
        Equal(input, formats, "Renamed format served stale ordering");
        formats.Insert(0, new CustomFormat("new", new SizeSpecification { Min = 0, Max = 1 }) { Id = 50 });
        Equal(input, formats, "Added format ignored");
        formats.RemoveAt(0);
        Equal(input, formats, "Removed format still matched");
    }

    static void Test_ReleaseTypeAndFlagsAndSize()
    {
        var formats = LanguageFormats().Concat(new[]
        {
            new CustomFormat("big", new SizeSpecification { Min = 1, Max = 5 }) { Id = 20 },
            new CustomFormat("freeleech", new IndexerFlagSpecification { Value = (int)IndexerFlags.Freeleech }) { Id = 21 }
        }).ToList();
        var input = Input(2L * 1024 * 1024 * 1024);
        Equal(input, formats);
        input.ReleaseType = ReleaseType.SeasonPack;
        Equal(input, formats, "Release type change served a stale match");
        input.IndexerFlags = IndexerFlags.Freeleech;
        Equal(input, formats, "Indexer flag change served a stale match");
        input.Size = 10L * 1024 * 1024 * 1024;
        Equal(input, formats, "Size change served a stale match");
        input.Filename = "file.mkv";
        Equal(input, formats);
    }
}
