// Review fixes for the DecisionScope memo (patch 0003): null results are never memoized, so a deleted
// indexer behaves and logs as on stock, and a write to a memoized table during a batch is seen by
// the next lookup. The NUnit fixtures in tests/CoreTest cover the same with real repositories.
using System.Reflection;
using NLog;
using NLog.Config;
using NLog.Targets;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.DecisionEngine.Specifications.RssSync;
using NzbDrone.Core.Download.Aggregation.Aggregators;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;

public class IndexerFactoryProxy : DispatchProxy
{
    public Func<MethodInfo, object[], object> Handler;

    protected override object Invoke(MethodInfo targetMethod, object[] args) => Handler(targetMethod, args);
}

public static partial class Tests
{
    static void TableChanged(string table) => ScopeType.GetMethod("TableChanged")!.Invoke(null, new object[] { table });

    static void Test_MemoReloadsAfterTableWrite()
    {
        var loads = 0;
        Func<List<int>> load = () => { loads++; return new List<int> { loads }; };
        using (BeginScope())
        {
            var a = Memo("ReviewT", 1, load);
            TableChanged("ReviewOther");
            Check(ReferenceEquals(Memo("ReviewT", 1, load), a) && loads == 1, "A write to another table must not invalidate");
            TableChanged("ReviewT");
            var b = Memo("ReviewT", 1, load);
            Check(!ReferenceEquals(a, b) && loads == 2, "A write to the memoized table must reload");
            Check(ReferenceEquals(Memo("ReviewT", 1, load), b) && loads == 2, "The reloaded value must be memoized again");

            // A write that commits while the loader runs: the stored value must not be trusted afterwards.
            var c = Memo("ReviewT", 2, () => { loads++; TableChanged("ReviewT"); return new List<int> { loads }; });
            var d = Memo("ReviewT", 2, load);
            Check(!ReferenceEquals(c, d) && loads == 4, "A write during the load must force a reload");

            // After a write the loader may return null: nothing is served from the stale entry.
            TableChanged("ReviewT");
            Check(Memo<List<int>>("ReviewT", 2, () => { loads++; return null; }) == null && loads == 5, "Stale entry served after write");
            Check(Memo<List<int>>("ReviewT", 2, () => { loads++; return null; }) == null && loads == 6, "Null was memoized after write");
        }
    }

    static IIndexerFactory Factory(Func<int, IndexerDefinition> find, Func<int, IndexerDefinition> get, List<string> calls)
    {
        var proxy = DispatchProxy.Create<IIndexerFactory, IndexerFactoryProxy>();
        ((IndexerFactoryProxy)(object)proxy).Handler = (m, a) =>
        {
            calls.Add(m.Name);
            return m.Name switch
            {
                "Find" => find((int)a[0]),
                "Get" => get((int)a[0]),
                _ => throw new NotSupportedException(m.Name)
            };
        };
        return proxy;
    }

    // Same order as a batch: aggregation, then the specifications, for two releases from the indexer.
    static (bool Tag, bool Seed, List<string> Logs) RunIndexerSpecs(IIndexerFactory factory, bool inBatch)
    {
        var target = new MemoryTarget("review") { Layout = "${level}|${logger}|${message}" };
        var config = new LoggingConfiguration();
        config.AddRuleForAllLevels(target, "review.*");
        LogManager.Configuration = config;
        try
        {
            var aggregate = new AggregateLanguages(factory, LogManager.GetLogger("review.aggregate"));
            var tag = new IndexerTagSpecification(LogManager.GetLogger("review.tag"), factory);
            var seed = new TorrentSeedingSpecification(factory, LogManager.GetLogger("review.seed"));
            var info = new ReleaseDecisionInformation(false, null);
            bool tagOk = true, seedOk = true;
            using (inBatch ? BeginScope() : null)
            {
                for (var i = 0; i < 2; i++)
                {
                    var release = new TorrentInfo { Seeders = 50, IndexerId = 7, Title = "Series.Title.S01E01.1080p.WEB-DL-Grp", DownloadProtocol = DownloadProtocol.Torrent };
                    var remote = new RemoteEpisode
                    {
                        Series = new Series { Title = "Series Title", Tags = new HashSet<int> { 1 }, OriginalLanguage = Language.English },
                        Release = release,
                        Episodes = new List<Episode> { new Episode { Id = 1, Title = "Pilot" } },
                        ParsedEpisodeInfo = new ParsedEpisodeInfo { ReleaseTitle = release.Title, Languages = new List<Language> { Language.English }, ReleaseTokens = "" }
                    };
                    aggregate.Aggregate(remote);
                    tagOk &= tag.IsSatisfiedBy(remote, info).Accepted;
                    seedOk &= seed.IsSatisfiedBy(remote, info).Accepted;
                }
            }

            return (tagOk, seedOk, target.Logs.ToList());
        }
        finally
        {
            LogManager.Configuration = null;
        }
    }

    // Review finding: AggregateLanguages (Find → null) and the two specifications (Get → throws) share the
    // memo key. A memoized null made both specifications throw NullReferenceException (DecisionError).
    static void Test_DeletedIndexerInBatchBehavesLikeStock()
    {
        var calls = new List<string>();
        var factory = Factory(_ => null, id => throw new ModelNotFoundException(typeof(IndexerDefinition), id), calls);

        var stock = RunIndexerSpecs(factory, false);
        Exception error = null;
        (bool Tag, bool Seed, List<string> Logs) batch = default;
        try
        {
            batch = RunIndexerSpecs(factory, true);
        }
        catch (Exception e)
        {
            error = e;
        }

        Check(error == null, $"Batch threw {error?.GetType().Name}");
        Check(stock.Tag && stock.Seed, "Stock path rejected a release from a deleted indexer");
        Check(batch.Tag && batch.Seed, "Batch rejected a release from a deleted indexer");
        Check(stock.Logs.Contains("Debug|review.tag|Indexer with id 7 does not exist, skipping indexer tags check"), "Missing stock tag log line");
        Check(stock.Logs.Contains("Debug|review.seed|Indexer with id 7 does not exist, skipping seeders check"), "Missing stock seeders log line");
        Check(batch.Logs.SequenceEqual(stock.Logs), "Batch log lines differ from stock:\n" + string.Join("\n", batch.Logs) + "\n--- stock:\n" + string.Join("\n", stock.Logs));
    }

    static void Test_ExistingIndexerIsReadOncePerBatch()
    {
        var calls = new List<string>();
        var definition = new IndexerDefinition { Id = 7, Tags = new HashSet<int>() };
        var factory = Factory(_ => definition, _ => definition, calls);

        var stock = RunIndexerSpecs(factory, false);
        var stockCalls = calls.Count;
        calls.Clear();
        var batch = RunIndexerSpecs(factory, true);

        Check(stock.Tag && stock.Seed && batch.Tag && batch.Seed, "Release from an existing indexer rejected");
        Check(batch.Logs.SequenceEqual(stock.Logs), "Batch log lines differ from stock");
        Check(stockCalls == 6 && calls.Count == 1 && calls[0] == "Find", $"Expected 6 stock lookups and 1 batch lookup, got {stockCalls} and {calls.Count}");
    }
}
