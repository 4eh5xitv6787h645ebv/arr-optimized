// Managed memory retained by the match cache at 50,000 entries, typical and worst case.
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Tv;

public static partial class Tests
{
    static long Retained(Func<int, CustomFormatInput> make, List<CustomFormat> formats)
    {
        ResetCache();
        Parse(make(-1), formats); // build the plan first so it is not counted
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalMemory(true);
        for (var i = 0; i < Capacity - 1; i++)
        {
            Parse(make(i), formats);
        }

        // Inputs are garbage now; what survives is what the cache keeps: entries, keys and the
        // key strings (title, filename, group, language list) that the dropped releases shared.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var after = GC.GetTotalMemory(true);
        Check(CachedEntries() == Capacity, "Cache not full");
        return (after - before) * Capacity / (Capacity - 1);
    }

    static CustomFormatInput Raw(string title, string group, int languages, long size)
    {
        return new CustomFormatInput
        {
            EpisodeInfo = new ParsedEpisodeInfo { ReleaseTitle = title, ReleaseGroup = group, Quality = new QualityModel(Quality.WEBDL1080p) },
            Series = new Series { OriginalLanguage = Language.English },
            Size = size,
            Languages = Enumerable.Range(0, languages).Select(i => Language.All[i + 1]).ToList()
        };
    }

    static void Test_MemoryPerEntryAt50000()
    {
        // A realistic definition set size does not change per-entry cost much: matches are stored as int indices.
        var formats = Formats(AllSpecs());
        var typical = Retained(i => Raw($"Some.Show.Name.S{i % 30:00}E{i % 99:00}.Episode.Title.{i}.1080p.WEB-DL.DDP5.1.H.264-GRP{i % 50}", $"GRP{i % 50}", 1, 1_000_000_000L + i), formats);
        var worst = Retained(i => Raw(new string((char)('a' + (i % 26)), 3990) + i.ToString("D10"), new string('g', 60) + i.ToString("D10"), 2, i), formats);
        var bytesTypical = typical / (double)Capacity;
        var bytesWorst = worst / (double)Capacity;
        Console.WriteLine($"  typical: {bytesTypical:F0} B/entry, {typical / 1048576.0:F1} MiB at {Capacity:N0}");
        Console.WriteLine($"  worst (4,096-char key text): {bytesWorst:F0} B/entry, {worst / 1048576.0:F1} MiB at {Capacity:N0}");
        Check(bytesTypical > 0 && bytesWorst > bytesTypical, "Implausible measurement");
    }
}
