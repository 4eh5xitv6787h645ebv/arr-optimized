using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Dapper;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Common.Reflection;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Sabnzbd;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Indexers.Torznab;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.Test.PerfNext
{
    // Equivalence for the per-closed-type cache of settings types in ProviderRepository: every row
    // gets the same settings type and values as stock's per-row FindTypeByName lookup, including
    // unknown contracts, other casing, blank contracts and blank settings.
    [TestFixture]
    public class ProviderConfigTypesFixture : DbTest<IndexerRepository, IndexerDefinition>
    {
        private void GivenIndexer(string name, IProviderConfig settings, string contractOverride = null, string settingsOverride = null)
        {
            var model = Builder<IndexerDefinition>.CreateNew()
                .With(d => d.Id = 0)
                .With(d => d.Name = name)
                .With(d => d.Implementation = "Newznab")
                .BuildNew();
            model.Settings = settings;
            model = Subject.Insert(model);

            using var conn = Db.OpenConnection();
            if (contractOverride != null)
            {
                conn.Execute("UPDATE \"Indexers\" SET \"ConfigContract\" = @c WHERE \"Id\" = @id", new { c = contractOverride == "<null>" ? null : contractOverride, id = model.Id });
            }

            if (settingsOverride != null)
            {
                conn.Execute("UPDATE \"Indexers\" SET \"Settings\" = @s WHERE \"Id\" = @id", new { s = settingsOverride == "<null>" ? null : settingsOverride, id = model.Id });
            }
        }

        private static Type StockSettingsType(string contract, bool blankBody)
        {
            var impType = typeof(IProviderConfig).Assembly.FindTypeByName(contract);

            return blankBody || impType == null ? typeof(NullConfig) : impType;
        }

        [Test]
        public void settings_types_match_stock_lookup()
        {
            GivenIndexer("newznab", new NewznabSettings { BaseUrl = "http://nzb.example", ApiKey = "abc" });
            GivenIndexer("torznab", new TorznabSettings { BaseUrl = "http://tor.example", ApiKey = "def", MinimumSeeders = 3 });
            GivenIndexer("lowercase", new NewznabSettings { BaseUrl = "http://lower.example" }, contractOverride: "newznabsettings");
            GivenIndexer("unknown", new NewznabSettings { BaseUrl = "http://unknown.example" }, contractOverride: "NoSuchSettings");
            GivenIndexer("empty contract", new NewznabSettings { BaseUrl = "http://empty.example" }, contractOverride: "");
            GivenIndexer("null contract", new NewznabSettings { BaseUrl = "http://null.example" }, contractOverride: "<null>");
            GivenIndexer("blank body", new NewznabSettings { BaseUrl = "http://blank.example" }, settingsOverride: "  ");
            GivenIndexer("null body", new NewznabSettings { BaseUrl = "http://nullbody.example" }, settingsOverride: "<null>");

            // Read several times so later reads come from the cache.
            for (var pass = 0; pass < 3; pass++)
            {
                var rows = Subject.All().OrderBy(r => r.Id).ToList();
                rows.Should().HaveCount(8);

                using var conn = Db.OpenConnection();
                var raw = conn.Query<(int Id, string ConfigContract, string Settings)>("SELECT \"Id\", \"ConfigContract\", \"Settings\" FROM \"Indexers\" ORDER BY \"Id\"").ToList();

                foreach (var (row, stored) in rows.Zip(raw))
                {
                    row.Id.Should().Be(stored.Id);
                    var expected = StockSettingsType(stored.ConfigContract, string.IsNullOrWhiteSpace(stored.Settings));

                    // Assigning Settings rewrites ConfigContract to the settings type name, as in stock.
                    row.Settings.GetType().Should().Be(expected, row.Name);
                    row.ConfigContract.Should().Be(expected.Name, row.Name);
                }

                rows.Single(r => r.Name == "newznab").Settings.As<NewznabSettings>().BaseUrl.Should().Be("http://nzb.example");
                rows.Single(r => r.Name == "torznab").Settings.As<TorznabSettings>().MinimumSeeders.Should().Be(3);
                rows.Single(r => r.Name == "lowercase").Settings.As<NewznabSettings>().BaseUrl.Should().Be("http://lower.example");
            }

            Subject.Get(IdOf("torznab")).Settings.Should().BeOfType<TorznabSettings>();
        }

        private int IdOf(string name) => Subject.All().Single(r => r.Name == name).Id;

        [Test]
        public void cache_is_per_provider_type()
        {
            GivenIndexer("newznab", new NewznabSettings { BaseUrl = "http://nzb.example" });
            Subject.All().Single().Settings.Should().BeOfType<NewznabSettings>();

            var clients = Mocker.Resolve<DownloadClientRepository>();
            var client = Builder<DownloadClientDefinition>.CreateNew()
                .With(d => d.Id = 0)
                .With(d => d.Implementation = "Sabnzbd")
                .BuildNew();
            client.Settings = new SabnzbdSettings { Host = "sab.example", Port = 8080 };
            clients.Insert(client);

            clients.All().Single().Settings.Should().BeOfType<SabnzbdSettings>();
            Subject.All().Single().Settings.Should().BeOfType<NewznabSettings>();
        }

        // Mechanics: the settings type is looked up once per contract and then taken from the cache.
        // Reaches the private static ConfigTypes field of ProviderRepository<IndexerDefinition> by name.
        private static ConcurrentDictionary<string, Type> IndexerConfigTypes()
        {
            var field = typeof(ProviderRepository<IndexerDefinition>).GetField("ConfigTypes", BindingFlags.Static | BindingFlags.NonPublic);
            field.Should().NotBeNull("ProviderRepository<T>.ConfigTypes caches the settings type per contract");

            return (ConcurrentDictionary<string, Type>)field.GetValue(null);
        }

        [Test]
        public void settings_type_is_taken_from_the_cache()
        {
            var cache = IndexerConfigTypes();
            const string contract = "PerfNextCachedContractSettings";

            GivenIndexer("cached", new NewznabSettings { BaseUrl = "http://cached.example" }, contractOverride: contract);

            try
            {
                // The first read fills the cache: an unknown contract maps to no type, as in stock.
                Subject.All().Single().Settings.Should().BeOfType<NullConfig>();
                cache.Should().ContainKey(contract);
                cache[contract].Should().BeNull();

                // Later reads use the cached entry instead of scanning the assembly again.
                cache[contract] = typeof(NewznabSettings);
                Subject.All().Single().Settings.As<NewznabSettings>().BaseUrl.Should().Be("http://cached.example");
            }
            finally
            {
                cache.TryRemove(contract, out _);
            }
        }

        [Test]
        [Explicit]
        [Category("PerfNextBenchmark")]
        public void benchmark_indexer_reads()
        {
            for (var i = 0; i < 20; i++)
            {
                GivenIndexer("indexer " + i, new NewznabSettings { BaseUrl = "http://nzb" + i + ".example", ApiKey = "k" });
            }

            Subject.All();

            var best = double.MaxValue;
            for (var round = 0; round < 5; round++)
            {
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < 200; i++)
                {
                    Subject.All();
                }

                sw.Stop();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }

            TestContext.Progress.WriteLine($"PERFNEXT IndexerRepository.All() x200 (20 indexers): best of 5 = {best:F1} ms ({best * 1000 / 200:F0} us per call)");
        }
    }
}
