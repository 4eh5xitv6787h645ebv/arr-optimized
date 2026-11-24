// Capacity (50,000), least-recently-used eviction and the 6-hour sliding expiry.
using NzbDrone.Core.CustomFormats;
public static partial class Tests
{
    static int Batch => (int)Static("EvictionBatch");

    static void Test_CapacityIs50000AndEnforced()
    {
        Check(Capacity == 50000, "Capacity must be 50,000");
        var formats = SizeFormats();
        var evictions = Counter("Evictions");
        var peak = 0;
        for (var i = 0; i < Capacity + 1000; i++)
        {
            Parse(Input(i), formats);
            peak = Math.Max(peak, CachedEntries());
        }

        var evicted = Counter("Evictions") - evictions;
        Check(peak == Capacity, $"Peak {peak} entries, expected exactly {Capacity}");
        Check(evicted == 2 * Batch && CachedEntries() == Capacity + 1000 - evicted, $"Unexpected eviction count {evicted}");
        Equal(Input(Capacity + 999), formats);
    }

    static void Test_EvictionOrderIsLeastRecentlyUsed()
    {
        UseFakeClock();
        var formats = SizeFormats();
        for (var i = 0; i < Capacity; i++)
        {
            _now++;
            Parse(Input(i), formats);
        }

        // Touch the two oldest entries: 2 .. Batch+1 become the least recently used.
        _now++;
        Parse(Input(0), formats);
        _now++;
        Parse(Input(1), formats);
        _now++;
        Parse(Input(Capacity), formats); // full: evicts one batch of least recently used entries
        Check(CachedEntries() == Capacity - Batch + 1, $"Expected one batch of {Batch} evictions");
        var hits = Counter("Hits");
        Parse(Input(0), formats);
        Parse(Input(1), formats);
        Parse(Input(Batch + 2), formats);
        Parse(Input(Capacity - 1), formats);
        Check(Counter("Hits") == hits + 4, "A recently used entry was evicted");
        var misses = Counter("Misses");
        Parse(Input(2), formats);
        Parse(Input(Batch + 1), formats);
        Check(Counter("Misses") == misses + 2, "The least recently used entries were not the ones evicted");
    }

    static void Test_HitRefreshesSlidingExpiry()
    {
        UseFakeClock();
        var formats = SizeFormats();
        var input = Input(1);
        Parse(input, formats);
        var step = Lifetime - 60_000; // 5h59m
        for (var i = 0; i < 4; i++)
        {
            _now += step; // ~24h after creation in total; a fixed expiry would have expired long ago
            var hits = Counter("Hits");
            Equal(input, formats);
            Check(Counter("Hits") == hits + 1, $"Refreshed entry missed after {i + 1} refresh(es)");
        }
    }

    static void Test_IdleEntryExpiresAfterSixHours()
    {
        Check(Lifetime == 6L * 60 * 60 * 1000, "Sliding lifetime must be 6 hours");
        UseFakeClock();
        var formats = SizeFormats();
        var input = Input(1);
        Parse(input, formats);
        _now += Lifetime - 1;
        var hits = Counter("Hits");
        Equal(input, formats);
        Check(Counter("Hits") == hits + 1, "Entry expired before 6 hours idle");
        _now += Lifetime;
        var misses = Counter("Misses");
        var expirations = Counter("Expirations");
        Equal(input, formats);
        Check(Counter("Misses") == misses + 1 && Counter("Expirations") == expirations + 1, "Idle entry was reused after 6 hours");
    }

    static void Test_ExpiredEntriesAreDroppedBeforeEvictingLiveOnes()
    {
        UseFakeClock();
        var formats = SizeFormats();
        for (var i = 0; i < 100; i++)
        {
            Parse(Input(i), formats);
        }

        _now += Lifetime;
        Parse(Input(1000), formats); // inserting trims the idle head
        var live = Input(1001);
        Parse(live, formats);
        Check(CachedEntries() == 2, $"Expired entries were retained ({CachedEntries()})");
        var hits = Counter("Hits");
        Parse(live, formats);
        Check(Counter("Hits") == hits + 1, "Live entry lost");
    }

    static void Test_DefinitionChangeStartsEmptyGeneration()
    {
        var formats = SizeFormats();
        for (var i = 0; i < 100; i++)
        {
            Parse(Input(i), formats);
        }

        ((SizeSpecification)formats[0].Specifications[0]).Max = 20;
        Equal(Input(1), formats);
        Check(CachedEntries() == 1, "Old generation entries survived a definition change");
    }
}
