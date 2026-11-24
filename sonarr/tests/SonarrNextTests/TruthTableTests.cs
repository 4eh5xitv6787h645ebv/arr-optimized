// Ported from sonarr-performance/cache-tests (5.0.0.2569): native truth-table equivalence under
// input and definition mutation, unknown-type bypass, result isolation and concurrency.
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

public static partial class Tests
{
    static void Test_IdenticalInputHitsCache()
    {
        var formats = Formats(AllSpecs());
        var input = Input(100);
        Equal(input, formats);
        var hits = Counter("Hits");
        Equal(input, formats);
        Check(Counter("Hits") > hits, "Identical input missed cache");
    }

    static void Test_ReturnedListIsACopy()
    {
        var formats = Formats(AllSpecs());
        var input = Input(100);
        Equal(input, formats);
        Parse(input, formats).Clear();
        Equal(input, formats, "Mutating a returned list changed the cached result");
    }

    static void Test_EveryInputDimensionIncludingInPlaceEdits()
    {
        var formats = Formats(AllSpecs());
        for (var i = 0; i < 5000; i++)
        {
            var x = Input(i % 31);
            x.Filename = i % 2 == 0 ? "example" : null;
            x.IndexerFlags = (IndexerFlags)(i % 8);
            x.Languages = i % 5 == 0 ? null : i % 7 == 0 ? new() : new() { (Language)(i % 4) };
            x.ReleaseType = (ReleaseType)(i % 3);
            x.Series.OriginalLanguage = (Language)(i % 3);
            x.EpisodeInfo.ReleaseTitle = "title" + (i % 3);
            x.EpisodeInfo.ReleaseGroup = i % 2 == 0 ? "group" : null;
            x.EpisodeInfo.Quality.Quality = i % 3 == 0 ? Quality.SDTV : Quality.HDTV720p;
            Equal(x, formats);
            Equal(x, formats);
        }
    }

    static void Test_DefinitionEditsInvalidateImmediately()
    {
        var specs = AllSpecs();
        var formats = Formats(specs);
        var input = Input(100);
        Equal(input, formats);
        foreach (var s in specs)
        {
            s.Negate = !s.Negate;
            Equal(input, formats);
            s.Required = !s.Required;
            Equal(input, formats);
        }

        ((ReleaseTitleSpecification)specs[0]).Value = "no-match";
        Equal(input, formats);
        ((LanguageSpecification)specs[2]).ExceptLanguage = true;
        Equal(input, formats);
        ((SizeSpecification)specs[3]).Max = 0;
        Equal(input, formats);
        formats.Reverse();
        Equal(input, formats);
        formats[0].Name = "a-first";
        Equal(input, formats);
        formats.RemoveAt(0);
        Equal(input, formats);
        formats.Add(new CustomFormat("empty") { Id = 999 });
        Equal(input, formats);
    }

    static void Test_EveryScalarDefinitionPropertyInvalidates()
    {
        var specs = AllSpecs();
        var formats = Formats(specs);
        var input = Input(100);
        foreach (var s in specs)
        {
            foreach (var p in s.GetType().GetProperties().Where(p => p.CanWrite && (p.PropertyType == typeof(int) || p.PropertyType == typeof(double))))
            {
                var original = p.GetValue(s);
                p.SetValue(s, p.PropertyType == typeof(int) ? (object)(s is LanguageSpecification ? Language.Japanese.Id : 12345) : 12345.0);
                Equal(input, formats);
                p.SetValue(s, original);
                Equal(input, formats);
            }
        }
    }

    static void Test_UnknownSpecificationIsNeverCached()
    {
        var unknown = new UnknownSpecification();
        var custom = new List<CustomFormat> { new CustomFormat("unknown", unknown) { Id = 10 } };
        var input = Input(100);
        Parse(input, custom);
        var calls = unknown.Calls;
        Parse(input, custom);
        Check(unknown.Calls > calls, "Unknown specification was cached");
    }

    static void Test_ConcurrentEvaluations()
    {
        var formats = SizeFormats();
        Parallel.For(0, 10000, i => Equal(Input(i), formats));
        Parallel.For(0, 10000, i => Equal(Input(i % 100), formats));
    }

    static void Test_OversizedInputIsCalculatedNotCached()
    {
        var formats = SizeFormats();
        var big = Input(5);
        big.EpisodeInfo.ReleaseTitle = new string('x', (int)Static("MaxKeyText") + 1);
        var misses = Counter("Misses");
        Equal(big, formats);
        Equal(big, formats);
        Check(Counter("Misses") == misses + 2 && CachedEntries() == 0, "Oversized input was cached");
    }
}

public class UnknownSpecification : ResolutionSpecification
{
    public int Calls;

    public override bool IsSatisfiedBy(CustomFormatInput input)
    {
        Interlocked.Increment(ref Calls);
        return true;
    }
}
