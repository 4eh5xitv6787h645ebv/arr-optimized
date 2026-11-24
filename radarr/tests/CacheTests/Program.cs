// Tests for the radarr-next Core patch. Run against the PATCHED Radarr.Core.dll:
//   dotnet run -c Release -p:CoreBin=<patched dir> -p:StockBin=<stock bin> -- <stock bin> [--memory]
// Every cached result is compared with a native truth table (the stock grouping rule
// evaluated directly on the live specification objects).
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Movies;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(Path.GetFullPath(args[0]), name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
return Tests.Run(args.Contains("--memory"));

public static class Tests
{
    const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static readonly Type Cache = typeof(CustomFormat).Assembly.GetType("NzbDrone.Core.CustomFormats.CustomFormatMatchCache", true);
    static readonly Type Batch = typeof(CustomFormat).Assembly.GetType("NzbDrone.Core.DecisionEngine.DecisionBatchCache", false);
    static readonly MethodInfo ParseMethod = typeof(CustomFormatCalculationService).GetMethod("ParseCustomFormat", Any, new[] { typeof(CustomFormatInput), typeof(List<CustomFormat>) });
    // Tolerant lookups so the suite can also run (and fail meaningfully) against older builds.
    static readonly int Capacity = (int?)Cache.GetField("Capacity", Any)?.GetValue(null) ?? 4096;
    static readonly long SlidingMs = (long?)Cache.GetField("SlidingMs", Any)?.GetValue(null) ?? 600_000;
    static long _now;
    static int _passed, _failed;

    public static int Run(bool memory)
    {
        Console.WriteLine($"Core: {typeof(CustomFormat).Assembly.Location}");
        Console.WriteLine($"Capacity={Capacity}, sliding expiry={TimeSpan.FromMilliseconds(SlidingMs)}");
        Test("rebase guard: CustomFormatInput shape is the one the cache key covers", InputShape);
        Test("rebase guard: specification types and their settings are the ones the plan compares", SpecificationShape);
        Test("truth table: every input dimension, including in-place edits", TruthTable);
        Test("definition edits invalidate immediately (values, required/negate, order, membership, names)", DefinitionMutations);
        Test("every scalar definition property participates in invalidation", ScalarProperties);
        Test("unknown specification types are never cached", UnknownBypass);
        Test("returned lists are copies", ResultIsolation);
        Test("10,000 concurrent evaluations stay equivalent", Concurrency);
        Test("year: parsed year 0 falls back to the movie year (metadata year edit)", YearFallback);
        Test("movie original-language edit is never served stale", OriginalLanguageEdit);
        Test("different movie objects with the same release text are keyed by year/original language", ReusedMovieMetadata);
        Test("oversized inputs (>4096 chars) are evaluated but not retained", Oversized);
        Test($"capacity: never more than {Capacity:N0} entries", CapacityLimit);
        Test("eviction order at the cap is least-recently-used", EvictionOrder);
        Test("sliding expiry: a hit refreshes the entry", RefreshOnHit);
        Test("idle expiry: an entry idle for the full window is recalculated", IdleExpiry);
        Test("idle entries are purged from the tail on insert", PurgeOnInsert);
        Test("a stale in-flight plan cannot populate the new generation", OldPlanCannotPopulate);
        Test("decision batch memo: scope, null results, nesting and thread isolation", BatchMemo);
        Test("decision batch memo: indexer writes mid-batch invalidate (update, racing load, delete via Find/Get)", BatchMemoInvalidation);
        if (memory)
        {
            Test("memory per entry at 50k (typical and worst case)", Memory);
        }

        Console.WriteLine($"\n{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    static void Test(string name, Action body)
    {
        Reset();
        try
        {
            body();
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception e)
        {
            _failed++;
            Console.WriteLine($"FAIL {name}: {(e is TargetInvocationException t ? t.InnerException : e)}");
        }
        finally
        {
            SetClock(null);
        }
    }

    // ---- helpers ----
    static void Check(bool ok, string why)
    {
        if (!ok)
        {
            throw new Exception(why);
        }
    }

    static long Counter(string name) => (long)Cache.GetField(name, Any).GetValue(null);
    static object Plan() => Cache.GetField("_plan", Any).GetValue(null);
    static int Entries() => ((IDictionary)Plan().GetType().GetField("Results", Any).GetValue(Plan())).Count;

    static void Reset() => Cache.GetField("_plan", Any).SetValue(null, null);

    static void SetClock(long? start)
    {
        var field = Cache.GetField("Clock", Any);
        if (field == null)
        {
            if (start != null)
            {
                throw new Exception("build has no injectable clock");
            }

            return;
        }

        if (start == null)
        {
            field.SetValue(null, (Func<long>)(() => Environment.TickCount64));
            return;
        }

        _now = start.Value;
        field.SetValue(null, (Func<long>)(() => _now));
    }

    static List<CustomFormat> Parse(CustomFormatInput input, List<CustomFormat> formats) =>
        (List<CustomFormat>)ParseMethod.Invoke(null, new object[] { input, formats });

    static List<CustomFormat> Oracle(CustomFormatInput input, List<CustomFormat> formats) =>
        formats.Where(f => f.Specifications.GroupBy(s => s.GetType()).All(g =>
        {
            var values = g.Select(s => (s.Required, Match: s.IsSatisfiedBy(input))).ToArray();
            return !values.Any(v => v.Required && !v.Match) && values.Any(v => v.Match);
        })).OrderBy(f => f.Name).ToList();

    static void Equal(CustomFormatInput input, List<CustomFormat> formats)
    {
        var expected = Oracle(input, formats).Select(f => f.Id).ToArray();
        Check(Parse(input, formats).Select(f => f.Id).SequenceEqual(expected), "cached match differs from native truth table");
        Check(Parse(input, formats).Select(f => f.Id).SequenceEqual(expected), "second (cached) match differs from native truth table");
    }

    static bool IsHit(CustomFormatInput input, List<CustomFormat> formats)
    {
        var hits = Counter("Hits");
        Parse(input, formats);
        return Counter("Hits") > hits;
    }

    static CustomFormatInput Input(long size, string title = "Example.Movie.2020.1080p.WEB-DL.H264-Group")
    {
        var movie = new Movie { Title = "Example", Year = 2020 };
        movie.MovieMetadata.Value.OriginalLanguage = Language.English;
        return new CustomFormatInput
        {
            MovieInfo = Parser.ParseMovieTitle(title),
            Movie = movie,
            Size = size,
            Languages = new List<Language> { Language.English }
        };
    }

    static List<ICustomFormatSpecification> AllSpecs() => new List<ICustomFormatSpecification>
    {
        new ReleaseTitleSpecification { Value = "example" }, new ReleaseGroupSpecification { Value = "group" },
        new LanguageSpecification { Value = Language.Original.Id }, new SizeSpecification { Min = 0, Max = 10 },
        new ResolutionSpecification { Value = 1080 }, new SourceSpecification { Value = 3 },
        new IndexerFlagSpecification { Value = 1 }, new EditionSpecification { Value = "extended" },
        new YearSpecification { Min = 2010, Max = 2030 }, new QualityModifierSpecification { Value = 0 }
    };

    static List<CustomFormat> Formats(List<ICustomFormatSpecification> specs) =>
        specs.Select((s, i) => new CustomFormat("format" + i, s) { Id = i + 1 }).ToList();

    static List<CustomFormat> SizeOnly() => new List<CustomFormat> { new CustomFormat("size", new SizeSpecification { Min = 0, Max = 10 }) { Id = 1 } };

    // ---- tests ----
    // If upstream changes any of these, the cache key / definition comparison must be reviewed
    // (and Known/Same/InputKey updated) before shipping. Unknown spec types already bypass the
    // cache, but a new input field or a new setting on a known type would not be detected.
    static void InputShape()
    {
        var expected = new[] { "Filename", "IndexerFlags", "Languages", "Movie", "MovieInfo", "Size" };
        var actual = typeof(CustomFormatInput).GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();
        Check(actual.SequenceEqual(expected), "CustomFormatInput properties changed: " + string.Join(",", actual));
    }

    static void SpecificationShape()
    {
        var expected = new Dictionary<string, string>
        {
            ["EditionSpecification"] = "Value",
            ["IndexerFlagSpecification"] = "Value",
            ["LanguageSpecification"] = "ExceptLanguage,Value",
            ["QualityModifierSpecification"] = "Value",
            ["ReleaseGroupSpecification"] = "Value",
            ["ReleaseTitleSpecification"] = "Value",
            ["ResolutionSpecification"] = "Value",
            ["SizeSpecification"] = "Max,Min",
            ["SourceSpecification"] = "Value",
            ["YearSpecification"] = "Max,Min",
        };
        var common = new HashSet<string> { "Name", "Negate", "Required" };
        var types = typeof(CustomFormat).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(ICustomFormatSpecification).IsAssignableFrom(t))
            .ToDictionary(t => t.Name, t => string.Join(",", t.GetProperties().Where(p => p.CanWrite && !common.Contains(p.Name)).Select(p => p.Name).OrderBy(n => n)));
        Check(types.Count == expected.Count && expected.All(e => types.TryGetValue(e.Key, out var v) && v == e.Value),
            "specification types/settings changed: " + string.Join("; ", types.OrderBy(t => t.Key).Select(t => $"{t.Key}={t.Value}")));
    }

    static void TruthTable()
    {
        var formats = Formats(AllSpecs());
        var input = Input(100);
        Equal(input, formats);
        Check(IsHit(input, formats), "identical input missed the cache");
        for (var i = 0; i < 5000; i++)
        {
            var x = Input(i % 31);
            x.Filename = i % 2 == 0 ? "example" : null;
            x.IndexerFlags = (IndexerFlags)(i % 8);
            x.Languages = i % 5 == 0 ? null : i % 7 == 0 ? new List<Language>() : new List<Language> { (Language)(i % 4) };
            x.Movie.MovieMetadata.Value.OriginalLanguage = (Language)(i % 3);
            x.Movie.MovieMetadata.Value.Year = 2000 + (i % 40);
            x.MovieInfo.SimpleReleaseTitle = "title" + i % 3;
            x.MovieInfo.ReleaseGroup = i % 2 == 0 ? "group" : null;
            x.MovieInfo.Edition = i % 2 == 0 ? "extended" : null;
            x.MovieInfo.Year = i % 3 == 0 ? 0 : i % 2 == 0 ? 2020 : 2000;
            x.MovieInfo.Quality.Quality = i % 3 == 0 ? Quality.SDTV : i % 3 == 1 ? Quality.HDTV720p : Quality.Bluray1080p;
            if (i % 11 == 0)
            {
                x.Movie = null;
            }

            Equal(x, formats);
        }

        // In-place edits to an input whose result is already cached.
        var y = Input(5);
        Equal(y, formats);
        y.MovieInfo.ReleaseGroup = "other";
        Equal(y, formats);
        y.Languages.Add(Language.French);
        Equal(y, formats);
        y.IndexerFlags = (IndexerFlags)1;
        Equal(y, formats);
        y.Size = 50L * 1024 * 1024 * 1024;
        Equal(y, formats);
    }

    static void DefinitionMutations()
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
        formats[1].Specifications = formats[1].Specifications.Select(s => s.Clone()).ToList();
        Equal(input, formats);
    }

    static void ScalarProperties()
    {
        var specs = AllSpecs();
        var formats = Formats(specs);
        var input = Input(100);
        Equal(input, formats);
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

    public class UnknownSpecification : ResolutionSpecification
    {
        public int Calls;

        public override bool IsSatisfiedBy(CustomFormatInput input)
        {
            Calls++;
            return true;
        }
    }

    static void UnknownBypass()
    {
        var unknown = new UnknownSpecification();
        var custom = new List<CustomFormat> { new CustomFormat("unknown", unknown) { Id = 10 } };
        var input = Input(1);
        Parse(input, custom);
        var calls = unknown.Calls;
        Parse(input, custom);
        Check(unknown.Calls > calls, "unknown specification was cached");
    }

    static void ResultIsolation()
    {
        var formats = Formats(AllSpecs());
        var input = Input(100);
        Parse(input, formats).Clear();
        Equal(input, formats);
        Parse(input, formats).Add(new CustomFormat("injected") { Id = 77 });
        Equal(input, formats);
    }

    static void Concurrency()
    {
        var formats = SizeOnly();
        Parallel.For(0, 10000, i => Equal(Input(i % 500), formats));
    }

    static void YearFallback()
    {
        var formats = new List<CustomFormat> { new CustomFormat("year", new YearSpecification { Min = 2010, Max = 2030 }) { Id = 1 } };
        // Titles without a parseable year leave ParsedMovieInfo.Year at 0.
        var input = Input(1, "Example.Movie.2020.1080p.WEB-DL-Group");
        input.MovieInfo.SimpleReleaseTitle = "Example Movie 1080p WEB-DL-Group";
        input.MovieInfo.Year = 0;
        input.Movie.MovieMetadata.Value.Year = 2020;
        Equal(input, formats);
        Check(Parse(input, formats).Count == 1, "expected year match via movie year");

        // Metadata refresh changes the movie's year; same release title, same parsed year 0.
        input.Movie.MovieMetadata.Value.Year = 2000;
        Equal(input, formats);
        Check(Parse(input, formats).Count == 0, "stale year match served after metadata year change");

        // A different movie (same release text) must not share the entry either.
        var other = Input(1, "Example.Movie.2020.1080p.WEB-DL-Group");
        other.MovieInfo.SimpleReleaseTitle = "Example Movie 1080p WEB-DL-Group";
        other.MovieInfo.Year = 0;
        other.Movie.MovieMetadata.Value.Year = 2015;
        Equal(other, formats);
    }

    static void OriginalLanguageEdit()
    {
        var formats = new List<CustomFormat> { new CustomFormat("original", new LanguageSpecification { Value = Language.Original.Id }) { Id = 1 } };
        var input = Input(1);
        input.Languages = new List<Language> { Language.French };
        input.Movie.MovieMetadata.Value.OriginalLanguage = Language.English;
        Equal(input, formats);
        Check(Parse(input, formats).Count == 0, "unexpected match");
        input.Movie.MovieMetadata.Value.OriginalLanguage = Language.French;
        Equal(input, formats);
        Check(Parse(input, formats).Count == 1, "stale original-language result");
        input.Movie.MovieMetadata.Value.OriginalLanguage = Language.Unknown;
        Equal(input, formats);
    }

    static void ReusedMovieMetadata()
    {
        // The exact-ID shortcut hands the searched Movie object to every release. A different
        // Movie object with different metadata must never share a cached result.
        var formats = Formats(AllSpecs());
        var a = Input(3);
        a.MovieInfo.Year = 0;
        var b = Input(3);
        b.MovieInfo.Year = 0;
        b.Movie.MovieMetadata.Value.Year = 1999;
        b.Movie.MovieMetadata.Value.OriginalLanguage = Language.Japanese;
        Equal(a, formats);
        Equal(b, formats);
        Equal(a, formats);
    }

    static void Oversized()
    {
        var formats = SizeOnly();
        var input = Input(1);
        input.MovieInfo.SimpleReleaseTitle = new string('x', 4097);
        Equal(input, formats);
        Check(Plan() == null || Entries() == 0, "oversized input was retained");
        Check(!IsHit(input, formats), "oversized input was served from cache");
    }

    static void CapacityLimit()
    {
        var formats = SizeOnly();
        var evictions = Counter("Evictions");
        for (var i = 0; i < Capacity + 1000; i++)
        {
            Parse(Input(i), formats);
        }

        Check(Entries() == Capacity, $"expected {Capacity} entries, found {Entries()}");
        Check(Counter("Evictions") - evictions == 1000, "expected exactly 1000 evictions");
    }

    static void EvictionOrder()
    {
        var formats = SizeOnly();
        SetClock(1_000_000);
        for (var i = 0; i < Capacity; i++)
        {
            Parse(Input(i), formats);
            _now++;
        }

        Check(Entries() == Capacity, "cache not full");

        // Touch the oldest entry: it becomes most recent and must survive the next eviction.
        Check(IsHit(Input(0), formats), "entry 0 missing before eviction");
        _now++;
        Parse(Input(Capacity), formats); // evicts the least recently used: entry 1
        Check(Entries() == Capacity, "cap exceeded");
        Check(IsHit(Input(0), formats), "recently used entry 0 was evicted");
        Check(IsHit(Input(2), formats), "entry 2 evicted before entry 1");
        Check(!IsHit(Input(1), formats), "least recently used entry 1 was not evicted");
    }

    static void RefreshOnHit()
    {
        var formats = SizeOnly();
        var input = Input(1);
        SetClock(10_000);
        Parse(input, formats);
        _now += SlidingMs - 60_000;
        Check(IsHit(input, formats), "hit before expiry failed");
        _now += SlidingMs - 60_000; // ~12h after insert, but only ~6h-1min after the last hit
        Check(IsHit(input, formats), "hit did not refresh the entry");
        var expirations = Counter("Expirations");
        _now += SlidingMs;
        Check(!IsHit(input, formats), "entry idle for the full window was served");
        Check(Counter("Expirations") == expirations + 1, "expiration not counted");
        Check(IsHit(input, formats), "recalculated entry was not stored again");
    }

    static void IdleExpiry()
    {
        var formats = SizeOnly();
        SetClock(10_000);
        var a = Input(1);
        var b = Input(2);
        Parse(a, formats);
        Parse(b, formats);
        _now += SlidingMs - 1;
        Check(IsHit(a, formats), "entry expired early");
        _now += 1;
        Check(!IsHit(b, formats), "entry idle for exactly the window was served");
        Equal(b, formats);
    }

    static void PurgeOnInsert()
    {
        var formats = SizeOnly();
        SetClock(10_000);
        for (var i = 0; i < 100; i++)
        {
            Parse(Input(i), formats);
        }

        _now += SlidingMs + 1;
        Parse(Input(1000), formats);
        Check(Entries() == 1, $"idle entries not purged ({Entries()} remain)");
    }

    static void OldPlanCannotPopulate()
    {
        // Simulate: plan A computed, definitions change (plan B), result from A arrives late.
        var formats = SizeOnly();
        Parse(Input(1), formats);
        var planA = Plan();
        formats[0].Specifications[0].Negate = true;
        Parse(Input(2), formats);
        Check(!ReferenceEquals(planA, Plan()), "plan not replaced");
        Equal(Input(1), formats);
    }

    static void BatchMemo()
    {
        var begin = Batch.GetMethod("Begin", Any);
        var indexer = Batch.GetMethod("Indexer", Any);
        var invalidate = Batch.GetMethod("Invalidate", Any);
        Check(Batch.GetMethod("MovieHistory", Any) == null, "movie history must not be memoized (review fix: concurrent history writes)");
        Check(invalidate != null, "generation invalidation missing (review fix: concurrent indexer writes)");
        var loads = 0;
        Func<int, IndexerDefinition> load = id => { loads++; return id == 404 ? null : new IndexerDefinition { Id = id }; };
        IndexerDefinition Get(int id) => (IndexerDefinition)indexer.Invoke(null, new object[] { id, load });

        Get(1);
        Get(1);
        Check(loads == 2, "lookups outside a scope must not be memoized");

        loads = 0;
        using (var scope = (IDisposable)begin.Invoke(null, null))
        {
            var a = Get(1);
            var b = Get(1);
            Get(2);
            Check(loads == 2 && ReferenceEquals(a, b), "lookups inside a scope must be memoized per id");
            Get(404);
            Get(404);
            Check(loads == 4, "null (missing) results must not be memoized");

            var otherThread = Task.Run(() => { Get(1); return loads; }).Result;
            Check(otherThread == 5, "scope leaked to another thread");

            using (var inner = (IDisposable)begin.Invoke(null, null))
            {
                Get(1);
                Check(loads == 6, "nested scope must start empty");
            }

            Get(1);
            Check(loads == 6, "outer scope not restored after nested scope");
        }

        loads = 0;
        Get(1);
        Check(loads == 1, "scope not cleared on dispose");
    }

    // Review finding 3: an indexer edited or deleted mid-batch must be re-read like stock.
    static void BatchMemoInvalidation()
    {
        var begin = Batch.GetMethod("Begin", Any);
        var indexer = Batch.GetMethod("Indexer", Any);
        var invalidate = Batch.GetMethod("Invalidate", Any);
        Check(invalidate != null, "no generation invalidation");
        var version = 0;
        var loads = 0;
        var deleted = false;
        Func<int, IndexerDefinition> find = id => { loads++; return deleted ? null : new IndexerDefinition { Id = id, Name = "v" + version }; };
        Func<int, IndexerDefinition> get = id =>
        {
            loads++;
            if (deleted)
            {
                throw new NzbDrone.Core.Datastore.ModelNotFoundException(typeof(IndexerDefinition), id);
            }

            return new IndexerDefinition { Id = id, Name = "v" + version };
        };
        IndexerDefinition Call(Func<int, IndexerDefinition> f, int id) => (IndexerDefinition)indexer.Invoke(null, new object[] { id, f });

        using (var scope = (IDisposable)begin.Invoke(null, null))
        {
            Check(Call(get, 1).Name == "v0", "first read");
            version = 1;
            Check(Call(get, 1).Name == "v0", "memo hit expected before any write event");
            invalidate.Invoke(null, null);
            Check(Call(get, 1).Name == "v1", "update not seen after invalidation");
            Check(Call(find, 1).Name == "v1" && loads == 2, "Find must share the refreshed entry");

            // A write that lands while a lookup is loading: the stored entry is dropped next time.
            Func<int, IndexerDefinition> racing = id => { var d = get(id); version = 2; invalidate.Invoke(null, null); return d; };
            Check(Call(racing, 7).Name == "v1", "racing load returns what it read");
            Check(Call(get, 7).Name == "v2", "entry loaded during a write was served after the write");

            // Delete: Find returns null (not memoized) and Get throws like stock, never a cached null.
            deleted = true;
            invalidate.Invoke(null, null);
            Check(Call(find, 1) == null, "deleted indexer: Find must return null");
            var threw = false;
            try
            {
                Call(get, 1);
            }
            catch (TargetInvocationException e) when (e.InnerException is NzbDrone.Core.Datastore.ModelNotFoundException)
            {
                threw = true;
            }

            Check(threw, "deleted indexer: Get must throw ModelNotFoundException like stock");
            deleted = false;
            Check(Call(get, 1).Name == "v2" && Call(find, 1).Name == "v2", "re-added indexer");
        }

        // The event handler bumps the generation for add, update and delete.
        var handler = typeof(CustomFormat).Assembly.GetType("NzbDrone.Core.DecisionEngine.DecisionBatchCacheInvalidator", true);
        var instance = Activator.CreateInstance(handler);
        var generation = Batch.GetProperty("Generation", Any);
        var events = new object[]
        {
            new NzbDrone.Core.ThingiProvider.Events.ProviderAddedEvent<IIndexer>(new IndexerDefinition()),
            new NzbDrone.Core.ThingiProvider.Events.ProviderUpdatedEvent<IIndexer>(new IndexerDefinition()),
            new NzbDrone.Core.ThingiProvider.Events.ProviderDeletedEvent<IIndexer>(1)
        };
        foreach (var e in events)
        {
            var before = (long)generation.GetValue(null);
            handler.GetMethod("Handle", new[] { e.GetType() }).Invoke(instance, new[] { e });
            Check((long)generation.GetValue(null) == before + 1, $"{e.GetType().Name} did not invalidate");
        }
    }

    static void Memory()
    {
        // Typical: realistic parsed release titles against a 200-format set where 6 match.
        var formats = Enumerable.Range(0, 200).Select(i => new CustomFormat($"cf{i:000}",
            i < 6 ? new SizeSpecification { Min = 0, Max = 1000 } : (ICustomFormatSpecification)new ReleaseTitleSpecification { Value = $"\\bnomatch{i}\\b" }) { Id = i + 1 }).ToList();
        var groups = new[] { "FLUX", "NTb", "SPARKS", "FraMeSToR", "BHDStudio", "playWEB", "TEPES", "CtrlHD" };
        var editions = new[] { "", ".Extended", ".Directors.Cut", ".IMAX", ".Remastered" };
        var res = new[] { "720p.WEB-DL.DDP5.1.H.264", "1080p.BluRay.DTS.x264", "2160p.UHD.BluRay.REMUX.HDR.HEVC.TrueHD.Atmos.7.1", "1080p.WEBRip.x265.10bit.AAC5.1" };
        var typical = Measure(formats, i =>
        {
            var title = $"Movie.Title.Number.{i}.{1950 + (i % 75)}{editions[i % editions.Length]}.{res[i % res.Length]}-{groups[i % groups.Length]}";
            return Input(1L + i, title);
        });

        // Worst case: every entry retains the maximum 4,096 characters of text and matches all 200 formats.
        var all = Enumerable.Range(0, 200).Select(i => new CustomFormat($"cf{i:000}", new SizeSpecification { Min = 0, Max = 1000 }) { Id = i + 1 }).ToList();
        var worst = Measure(all, i =>
        {
            var input = Input(1L + i);
            input.MovieInfo.SimpleReleaseTitle = i.ToString("D8") + new string('x', 4096 - 8 - 3 - 1);
            input.MovieInfo.ReleaseGroup = "GRP";
            input.MovieInfo.Edition = null;
            return input;
        });
        Console.WriteLine($"  typical: {typical / Capacity:N0} B/entry, {typical / 1048576.0:N1} MiB at {Capacity:N0} entries");
        Console.WriteLine($"  worst:   {worst / Capacity:N0} B/entry, {worst / 1048576.0:N1} MiB at {Capacity:N0} entries");
    }

    // Managed bytes retained by a full cache: inputs are created, evaluated and dropped one by
    // one, so only what the cache keeps (key, text, indices, node) remains reachable.
    static long Measure(List<CustomFormat> formats, Func<int, CustomFormatInput> make)
    {
        Reset();
        Parse(make(Capacity + 1), formats); // build the plan (not part of per-entry cost)
        Reset();
        Parse(make(Capacity + 2), formats);
        var plan = Plan();
        var before = GC.GetTotalMemory(true);
        for (var i = 0; i < Capacity - 1; i++)
        {
            Parse(make(i), formats);
        }

        var after = GC.GetTotalMemory(true);
        Check(ReferenceEquals(plan, Plan()) && Entries() == Capacity, $"expected a full cache, found {Entries()}");
        return (after - before) * Capacity / (Capacity - 1);
    }
}
