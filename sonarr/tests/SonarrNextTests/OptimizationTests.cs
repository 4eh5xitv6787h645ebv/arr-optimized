// DecisionScope memo and the in-memory SQLite LIKE used for blocklist rows.
using System.Data.Common;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;
using NzbDrone.Core.Datastore;

public static partial class Tests
{
    static Type ScopeType => Core.GetType("NzbDrone.Core.DecisionEngine.DecisionScope", true);
    static Type LikeType => Core.GetType("NzbDrone.Core.Blocklisting.SqliteLike", true);

    static IDisposable BeginScope() => (IDisposable)ScopeType.GetMethod("Begin")!.Invoke(null, null)!;
    static bool ScopeActive => (bool)ScopeType.GetProperty("Active")!.GetValue(null)!;

    static T Memo<T>(string kind, int id, Func<T> load)
        where T : class => (T)ScopeType.GetMethod("Memo")!.MakeGenericMethod(typeof(T)).Invoke(null, new object[] { kind, id, load })!;

    static void Test_DecisionScopeMemoizesOnlyInsideABatch()
    {
        var loads = 0;
        Func<List<int>> load = () => { loads++; return new List<int> { loads }; };
        Memo("k", 1, load);
        Memo("k", 1, load);
        Check(loads == 2 && !ScopeActive, "Memo outside a batch must not cache");
        using (BeginScope())
        {
            var a = Memo("k", 1, load);
            var b = Memo("k", 1, load);
            Check(ReferenceEquals(a, b) && loads == 3, "Memo inside a batch must load once");
            Memo("k", 2, load);
            Memo("other", 1, load);
            Check(loads == 5, "Different keys must load separately");
            Check(Memo<string>("null", 1, () => { loads++; return null; }) == null && Memo<string>("null", 1, () => { loads++; return null; }) == null && loads == 7, "Null results must not be memoized");
            using (BeginScope())
            {
                Memo("k", 1, load);
                Check(loads == 7, "Nested batch must share the outer memo");
            }

            Check(ScopeActive, "Disposing a nested scope ended the outer batch");
            var thrown = 0;
            for (var i = 0; i < 2; i++)
            {
                try
                {
                    Memo<string>("throws", 1, () => throw new InvalidOperationException());
                }
                catch (TargetInvocationException)
                {
                    thrown++;
                }
            }

            Check(thrown == 2, "Exceptions must propagate every time and never be cached");
        }

        Check(!ScopeActive, "Batch did not end");
        Memo("k", 1, load);
        Check(loads == 8, "Memo leaked past the batch");
        var other = 0;
        using (BeginScope())
        {
            var t = new Thread(() => { Memo("k", 1, load); Memo("k", 1, load); other = loads; });
            t.Start();
            t.Join();
        }

        Check(other == 10, "A batch on one thread must not memoize for other threads");
    }

    static Func<string, string, bool?> _like;
    static bool? Like(string value, string needle)
    {
        _like ??= LikeType.GetMethod("ContainsLike")!.CreateDelegate<Func<string, string, bool?>>();
        return _like(value, needle);
    }

    // The SQLite connections the emulation is compared against:
    //  - Microsoft.Data.Sqlite with its bundled e_sqlite3 (3.41.2), and
    //  - System.Data.SQLite.dll as shipped in the Sonarr image (the provider Sonarr itself uses, taken from
    //    the bin directory given to the runner), with whatever libe_sqlite3.so resolves for it. Inside the
    //    Sonarr image (tests/README) that is Sonarr's own /app/sonarr/bin/libe_sqlite3.so (3.53.4).
    // SONARR_NEXT_SQLITE_NATIVE=<path to a libsqlite3/libe_sqlite3.so> points System.Data.SQLite at another
    // native build (e.g. the host's libsqlite3). SONARR_NEXT_EXPECT_SQLITE=<version> makes the test fail
    // unless System.Data.SQLite reports exactly that sqlite_version().
    static List<(string Name, DbConnection Conn)> LikeConnections()
    {
        SQLitePCL.Batteries_V2.Init();
        var list = new List<(string, DbConnection)>();
        var ms = new SqliteConnection("Data Source=:memory:");
        ms.Open();
        list.Add(("Microsoft.Data.Sqlite", ms));

        Assembly sds;
        try
        {
            sds = Assembly.Load("System.Data.SQLite");
        }
        catch (FileNotFoundException)
        {
            Check(Environment.GetEnvironmentVariable("SONARR_NEXT_EXPECT_SQLITE") == null, "System.Data.SQLite.dll not found in the bin directory");
            Console.WriteLine("  System.Data.SQLite.dll not in the bin directory; skipped");
            return list;
        }

        var native = Environment.GetEnvironmentVariable("SONARR_NEXT_SQLITE_NATIVE");
        if (!string.IsNullOrEmpty(native))
        {
            NativeLibrary.SetDllImportResolver(sds, (name, _, _) => name.Contains("sqlite", StringComparison.OrdinalIgnoreCase) ? NativeLibrary.Load(native) : IntPtr.Zero);
        }

        var conn = (DbConnection)Activator.CreateInstance(sds.GetType("System.Data.SQLite.SQLiteConnection", true), "Data Source=:memory:")!;
        conn.Open();
        list.Add(("System.Data.SQLite (Sonarr)", conn));
        return list;
    }

    static string SqliteVersion(DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version()";
        return (string)cmd.ExecuteScalar();
    }

    static void Test_SqliteLikeMatchesRealSqlite()
    {
        var total = 0L;
        foreach (var (name, conn) in LikeConnections())
        {
            using (conn)
            {
                var version = SqliteVersion(conn);
                var expectVersion = Environment.GetEnvironmentVariable("SONARR_NEXT_EXPECT_SQLITE");
                if (name.StartsWith("System.Data.SQLite") && !string.IsNullOrEmpty(expectVersion))
                {
                    Check(version == expectVersion, $"System.Data.SQLite uses SQLite {version}, expected {expectVersion}");
                }

                var n = LikeAgainst(conn);
                total += n;
                Console.WriteLine($"  {name}: sqlite {version}, {n} comparisons");
            }
        }

        _checks += (int)Math.Min(total, int.MaxValue);
    }

    // Same expression BlocklistRepository.BlocklistedByTitle produces through WhereBuilderSqlite.
    static long LikeAgainst(DbConnection conn)
    {
        DbCommand cmd = null;
        DbParameter pv = null, pn = null;
        var compared = 0L;

        // null = SQL NULL; throws on SQL errors (e.g. "LIKE or GLOB pattern too complex"). A command is not
        // reused after an error (System.Data.SQLite reports misuse for it).
        bool? Sql(string v, string n)
        {
            if (cmd == null)
            {
                cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT (@v LIKE '%' || @n || '%')";
                pv = cmd.CreateParameter();
                pv.ParameterName = "@v";
                pn = cmd.CreateParameter();
                pn.ParameterName = "@n";
                cmd.Parameters.Add(pv);
                cmd.Parameters.Add(pn);
            }

            pv.Value = (object)v ?? DBNull.Value;
            pn.Value = (object)n ?? DBNull.Value;
            try
            {
                var r = cmd.ExecuteScalar();
                return r is DBNull or null ? null : Convert.ToInt64(r) == 1;
            }
            catch (DbException)
            {
                cmd.Dispose();
                cmd = null;
                throw;
            }
        }

        static string Show(string s) => s == null ? "<null>" : s.Length > 200 ? $"<{s.Length} chars> " + Escape(s[..200]) : Escape(s);
        static string Escape(string s) => string.Concat(s.Select(c => c < 0x20 || c > 0x7e ? $"\\u{(int)c:X4}" : c.ToString()));

        // The emulation must equal SQLite whenever it answers; null (defer to the database) is always allowed
        // for the inputs it routes to SQL, and is required when SQLite raises an error.
        void Compare(string v, string n, bool mayDefer = false)
        {
            compared++;
            var actual = Like(v, n);
            bool? expected;
            try
            {
                expected = Sql(v, n);
            }
            catch (DbException e)
            {
                Check(actual == null, $"SQLite failed ({e.Message.Split('\n')[0]}) but emulation answered {actual} for '{Show(v)}' / '{Show(n)}'");
                return;
            }

            if (actual == null && (mayDefer || n == null))
            {
                return;
            }

            if (actual != (v == null ? false : expected))
            {
                Check(false, $"LIKE mismatch for '{Show(v)}' / '{Show(n)}': sqlite={expected?.ToString() ?? "NULL"} emulated={actual?.ToString() ?? "defer"}");
            }
        }

        var curated = new (string Value, string Needle)[]
        {
            ("Show.S01E01.1080p.WEB-DL-GRP", "show.s01e01"), ("Show.S01E01", "S01E01.1080p"), ("abc", ""), ("", ""), ("", "a"),
            ("Show_S01E01", "Show.S01E01"), ("Show.S01E01", "Show_S01E01"), ("Show%S01", "w%S"), ("ShowXYZS01", "w%S"),
            ("ÉCOLE", "école"), ("école", "école"), ("STRASSE", "straße"), ("İstanbul", "istanbul"), ("ΑΒΓ", "αβγ"),
            ("aaa", "aaaa"), ("ab", "_b"), ("b", "_b"), ("a\\b", "a\\b"), ("a[b]c", "[b]"), ("x", "%"), ("", "%"), ("", "_"),
            ("Mixed.Case.TITLE", "mIxEd.CaSe"), ("tab\tsep", "\t"), ("日本語タイトル", "本語"), ("ÄÖÜ", "äöü"), ("k", "K"),
            // sqlite3Utf8Read maps U+FFFE and U+FFFF to U+FFFD, so the three compare equal (re-review finding).
            ("Show.￾.S01E01", "show.�.s01"), ("Show.￿.S01E01", "￾"), ("�", "￿"), ("￾", "￾"),
            ("a�b", "a￾b"), ("a￿b", "A�B"), ("￾￿", "��"), ("x￾y", "x_y"), ("￿", "%�%"),
            ("￼", "�"), ("﻿", "￾"), ("x", "￾"),
            // Non-ASCII case variants never fold; ASCII always does.
            ("ΣΊΣΥΦΟΣ", "σίσυφος"), ("σίσυφος", "σίσυφοσ"), ("ς", "σ"), ("ẞ", "ß"), ("ss", "ß"), ("ı", "i"), ("I", "ı"), ("i̇", "İ"),
            ("ＡＢＣ", "ａｂｃ"), ("ＡＢＣ", "ABC"), ("K", "k"), ("K", "K"), ("ſ", "s"), ("Å", "Å"),
            // Combining marks compare by code point (no normalization).
            ("é", "é"), ("é", "é"), ("éx", "e_x"), ("éx", "e__x"), ("É", "é"),
            // Wildcards and backslash inside titles (no ESCAPE clause).
            ("100% Real", "100%"), ("100% Real", "100\\%"), ("a\\%b", "\\%"), ("a_b", "a\\_b"), ("a\\_b", "a\\_b"), ("%%", "%%%"), ("_", "__"),
        };
        foreach (var (v, n) in curated)
        {
            Compare(v, n);
        }

        Check(Like(null, "x") == false && Sql(null, "x") == null, "NULL value must not match");
        Check(Like("x", null) == null, "NULL needle must defer to the database");
        Check(Like("a\0b", "a") == null && Like("😀x", "x") == null && Like("x", "\ud800") == null && Like("x\udc00", "x") == null,
            "NUL and surrogates must defer to the database");

        // Pattern length limit (SQLITE_MAX_LIKE_PATTERN_LENGTH = 50,000 bytes, checked on the UTF-8 pattern).
        // The emulation answers only when even 3 bytes per character fit, i.e. needles up to 16,666 characters.
        Check(Sql("x", new string('a', 49998)) == false, "a 50,000-byte pattern must be accepted by this SQLite build");
        var threw = false;
        try
        {
            Sql("x", new string('a', 49999));
        }
        catch (DbException)
        {
            threw = true;
        }

        Check(threw, "a 50,001-byte pattern must be rejected by this SQLite build (limit differs from 50,000)");
        var cjk = new string('日', 16666);
        Check(Like("x" + cjk + "y", cjk) == true && Sql("x" + cjk + "y", cjk) == true, "16,666 three-byte characters (50,000-byte pattern) must be emulated");
        Check(Like(cjk + "日", cjk + "日") == null, "a needle over 16,666 characters must defer to the database");
        Compare(new string('a', 30000) + "b", new string('A', 16000) + "B");
        Compare(new string('a', 30000), new string('a', 16000) + "b");
        Compare(new string('a', 30000) + "_", new string('_', 16666));
        Compare("x" + new string('a', 49998), new string('a', 49998), mayDefer: true); // emulation defers, SQL answers
        Compare("x", new string('a', 49999)); // SQL raises, emulation must defer

        // Randomized comparison over a broad alphabet.
        string[] groups =
        {
            "aAbBkKsSiIzZeE09",                        // ASCII letters (folded) and digits
            ".-[] \t()+",                               // punctuation
            "%_\\",                                     // LIKE wildcards and backslash inside titles
            "�￾￿",                       // the replacement-character family
            "éÉèÈàÀÄäÖöÜüÅåØøÆæßẞſ",                    // Latin-1/extended letters with case variants
            "ΣσςΑαΩωΊί",                                // Greek, final sigma
            "İıiİ",                               // Turkish dotted/dotless forms
            "ＡａＢｂＫｋＺｚ",                          // fullwidth Latin
            "KÅΩ",                       // Kelvin, Angstrom, Ohm signs
            "̸̀́̇̈⃝",     // combining marks
            "\u0080 ÿĀ߿ࠀ﻿￼～", // UTF-8 length boundaries and specials
            "日本語タイ한국ЖжЯя",                       // CJK, Hangul, Cyrillic with case variants
        };
        var pool = groups.SelectMany(g => g).Distinct().ToArray();
        var family = "�￾￿";
        var rng = new Random(20261125);
        var tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");

        string Rand(int max, double asciiBias = 0)
        {
            var sb = new StringBuilder();
            var len = rng.Next(max + 1);
            for (var i = 0; i < len; i++)
            {
                var g = rng.NextDouble() < asciiBias ? groups[0] + groups[1] : groups[rng.Next(groups.Length)];
                sb.Append(g[rng.Next(g.Length)]);
            }

            return sb.ToString();
        }

        // A needle that is (mostly) a variant of part of the value: case changes (invariant and Turkish),
        // replacement-family swaps, wildcards in place of characters, and the odd random edit.
        string Variant(string v, int maxLen)
        {
            if (v.Length == 0)
            {
                return Rand(3);
            }

            var start = rng.Next(v.Length);
            var len = Math.Min(rng.Next(1, maxLen + 1), v.Length - start);
            var sb = new StringBuilder();
            foreach (var c in v.Substring(start, len))
            {
                var r = rng.Next(20);
                sb.Append(r switch
                {
                    0 => char.ToUpperInvariant(c).ToString(),
                    1 => char.ToLowerInvariant(c).ToString(),
                    2 => char.ToUpper(c, tr).ToString(),
                    3 => family.Contains(c) ? family[rng.Next(3)].ToString() : c.ToString(),
                    4 => "_",
                    5 => "%" + c,
                    6 => pool[rng.Next(pool.Length)].ToString(),
                    7 => "",
                    _ => c.ToString(),
                });
            }

            return rng.Next(4) == 0 ? sb.ToString().ToUpperInvariant() : sb.ToString();
        }

        // 1. Short random pairs from the whole alphabet (many coincidental matches).
        for (var i = 0; i < 1000000; i++)
        {
            var nd = Rand(4);
            var v = rng.Next(3) == 0 ? Rand(3) + nd.ToUpperInvariant() + Rand(3) : Rand(10);
            Compare(v, nd);
        }

        // 2. Needles derived from the value.
        for (var i = 0; i < 1000000; i++)
        {
            var v = Rand(40, rng.NextDouble());
            Compare(v, Variant(v, 12));
        }

        // 3. Release-like titles with injected characters.
        string[] stems = { "Show.Name.S01E01.1080p.WEB-DL.DDP5.1.H.264-GRP", "Series Title (2021) - S02E07 - 720p HDTV x264", "[SubGroup] Anime Title - 05 [1080p][ABCD1234]" };
        for (var i = 0; i < 300000; i++)
        {
            var sb = new StringBuilder(stems[rng.Next(stems.Length)]);
            for (var k = rng.Next(4); k > 0; k--)
            {
                sb.Insert(rng.Next(sb.Length + 1), pool[rng.Next(pool.Length)]);
            }

            var v = rng.Next(2) == 0 ? sb.ToString() : sb.ToString().ToLowerInvariant();
            Compare(v, rng.Next(3) == 0 ? Variant(v, v.Length) : Variant(v, 30));
        }

        // 4. Very long titles (up to 20,000 characters) and long needles.
        for (var i = 0; i < 10000; i++)
        {
            var v = Rand(rng.Next(2) == 0 ? 2000 : 20000, 0.7);
            Compare(v, Variant(v, rng.Next(2) == 0 ? 40 : 3000));
            Compare(v, Rand(6));
        }

        cmd?.Dispose();
        return compared;
    }
}
