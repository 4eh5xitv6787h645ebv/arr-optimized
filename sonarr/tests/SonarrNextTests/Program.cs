// Tests for the sonarr-next patch. A tiny runner (no test framework) so the project only needs
// the patched Sonarr.Core.dll plus the upstream image's managed dependencies.
using System.Collections;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Data.Sqlite;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Tv;

var binDir = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(binDir, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
return Tests.Run(args.Skip(1).ToArray());

public static partial class Tests
{
    static Assembly Core => typeof(CustomFormat).Assembly;
    static Type CacheType => Core.GetType("NzbDrone.Core.CustomFormats.CustomFormatMatchCache", true);
    static MethodInfo ParseMethod => typeof(CustomFormatCalculationService).GetMethod("ParseCustomFormat", BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(CustomFormatInput), typeof(List<CustomFormat>) });
    static int _checks;

    public static int Run(string[] only)
    {
        Console.WriteLine($"Sonarr.Core: {Core.Location}");
        var tests = typeof(Tests).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(m => m.Name.StartsWith("Test_")).OrderBy(m => m.MetadataToken).ToList();
        var failed = 0;
        foreach (var t in tests.Where(t => only.Length == 0 || only.Any(o => t.Name.Contains(o))))
        {
            ResetCache();
            var sw = Stopwatch.StartNew();
            var before = _checks;
            try
            {
                t.Invoke(null, null);
                Console.WriteLine($"PASS {t.Name} ({_checks - before} checks, {sw.ElapsedMilliseconds} ms)");
            }
            catch (TargetInvocationException e)
            {
                failed++;
                Console.WriteLine($"FAIL {t.Name}: {e.InnerException}");
            }
        }

        Console.WriteLine($"{tests.Count - failed}/{tests.Count} tests passed, {_checks} checks");
        return failed == 0 ? 0 : 1;
    }

    // ---------- helpers ----------
    static void Check(bool ok, string why)
    {
        _checks++;
        if (!ok)
        {
            throw new Exception(why);
        }
    }

    static object Static(string name) => CacheType.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(null);
    static void SetStatic(string name, object value) => CacheType.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(null, value);
    static long Counter(string name) => (long)Static(name);
    static int Capacity => (int)Static("Capacity");
    static long Lifetime => (long)Static("SlidingLifetimeMs");

    static long _now = 1_000_000;
    static void UseFakeClock() => SetStatic("Clock", new Func<long>(() => _now));

    static void ResetCache()
    {
        SetStatic("_plan", null);
        SetStatic("Clock", new Func<long>(() => Environment.TickCount64));
        _now = 1_000_000;
    }

    static int CachedEntries()
    {
        var plan = Static("_plan");
        return plan == null ? 0 : ((IDictionary)plan.GetType().GetField("Results", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plan)!).Count;
    }

    static List<CustomFormat> Parse(CustomFormatInput input, List<CustomFormat> formats) => (List<CustomFormat>)ParseMethod.Invoke(null, new object[] { input, formats })!;

    // The stock algorithm (Sonarr v5 CustomFormatCalculationService.ParseCustomFormat before the patch).
    static List<CustomFormat> Oracle(CustomFormatInput input, List<CustomFormat> formats) => formats.Where(f => f.Specifications.GroupBy(s => s.GetType()).All(g =>
    {
        var values = g.Select(s => (s.Required, Match: s.IsSatisfiedBy(input))).ToArray();
        return !values.Any(v => v.Required && !v.Match) && values.Any(v => v.Match);
    })).OrderBy(f => f.Name).ToList();

    static void Equal(CustomFormatInput input, List<CustomFormat> formats, string why = "Cached decision differs from native truth table")
    {
        var actual = Parse(input, formats);
        var expected = Oracle(input, formats);
        Check(actual.Count == expected.Count && actual.Zip(expected).All(p => ReferenceEquals(p.First, p.Second)), why);
    }

    static CustomFormatInput Input(long size, string title = "Example.S01E01.1080p.WEB-DL.H264-Group")
    {
        return new CustomFormatInput
        {
            EpisodeInfo = Parser.ParseTitle(title),
            Series = new Series { OriginalLanguage = Language.English },
            Size = size,
            Languages = new List<Language> { Language.English }
        };
    }

    static List<ICustomFormatSpecification> AllSpecs() => new()
    {
        new ReleaseTitleSpecification { Value = "example" }, new ReleaseGroupSpecification { Value = "group" },
        new LanguageSpecification { Value = Language.Original.Id }, new SizeSpecification { Min = 0, Max = 10 },
        new ResolutionSpecification { Value = 1080 }, new SourceSpecification { Value = 3 },
        new IndexerFlagSpecification { Value = 1 }, new ReleaseTypeSpecification { Value = 0 }
    };

    static List<CustomFormat> Formats(List<ICustomFormatSpecification> specs) => specs.Select((s, i) => new CustomFormat("format" + i, s) { Id = i + 1 }).ToList();

    static List<CustomFormat> SizeFormats() => new() { new CustomFormat("size", new SizeSpecification { Min = 0, Max = 10 }) { Id = 1 } };
}
