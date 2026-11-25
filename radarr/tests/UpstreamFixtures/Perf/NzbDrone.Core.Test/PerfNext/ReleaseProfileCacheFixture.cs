using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Profiles.Releases;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.PerfNext
{
    // Mechanics of the in-memory release profile list (the profiles are read from the database once
    // and again only after a profile is added, updated or deleted).
    [TestFixture]
    public class ReleaseProfileCacheFixture : CoreTest<ReleaseProfileService>
    {
        private List<ReleaseProfile> _stored;

        [SetUp]
        public void Setup()
        {
            _stored = new List<ReleaseProfile>
            {
                new ReleaseProfile { Id = 1, Enabled = true, Required = new List<string> { "x264" }, Tags = new HashSet<int>() }
            };

            Mocker.GetMock<IReleaseProfileRepository>()
                  .Setup(s => s.All())
                  .Returns(() => new List<ReleaseProfile>(_stored));
        }

        [Test]
        public void should_load_profiles_once()
        {
            Subject.EnabledForTags(new HashSet<int>(), 0).Should().HaveCount(1);
            Subject.AllForTags(new HashSet<int> { 1 }).Should().HaveCount(1);
            Subject.AllForTag(1).Should().HaveCount(0);
            Subject.All().Should().HaveCount(1);

            Mocker.GetMock<IReleaseProfileRepository>().Verify(v => v.All(), Times.Once());
        }

        [Test]
        public void should_return_changed_profiles_after_add_update_and_delete()
        {
            Subject.EnabledForTags(new HashSet<int>(), 0).Should().HaveCount(1);

            var added = new ReleaseProfile { Id = 2, Enabled = true, Tags = new HashSet<int>() };
            _stored.Add(added);
            Subject.Add(added);

            Subject.EnabledForTags(new HashSet<int>(), 0).Should().HaveCount(2);

            var disabled = new ReleaseProfile { Id = 1, Enabled = false, Tags = new HashSet<int>() };
            _stored[0] = disabled;
            Subject.Update(disabled);

            Subject.EnabledForTags(new HashSet<int>(), 0).Should().ContainSingle(p => p.Id == 2);

            _stored.Remove(added);
            Subject.Delete(2);

            Subject.EnabledForTags(new HashSet<int>(), 0).Should().BeEmpty();
        }

        [Test]
        public void should_reload_after_a_failed_write()
        {
            Subject.All().Should().HaveCount(1);

            // A write that fails after changing the table must not leave the old list in memory.
            Mocker.GetMock<IReleaseProfileRepository>()
                  .Setup(s => s.Update(It.IsAny<ReleaseProfile>()))
                  .Callback(() => _stored.Clear())
                  .Throws(new InvalidOperationException("event handler failed"));

            Assert.Throws<InvalidOperationException>(() => Subject.Update(new ReleaseProfile { Id = 1 }));

            Subject.All().Should().BeEmpty();
        }

        // A load that is still reading the old rows when a write commits must not leave those rows in
        // memory. Deterministic: the repository read is held on a gate while the update runs on another
        // thread. Without the shared lock the update (and its clear) finishes first and the load then
        // stores the old list; with it the clear waits for the load and runs after it.
        [Test]
        public void a_load_that_overlaps_a_write_does_not_keep_the_old_rows()
        {
            using var loading = new ManualResetEventSlim();
            using var gate = new ManualResetEventSlim();
            var calls = 0;

            Mocker.GetMock<IReleaseProfileRepository>()
                  .Setup(s => s.All())
                  .Returns(() =>
                  {
                      var rows = _stored.Select(p => new ReleaseProfile { Id = p.Id, Enabled = p.Enabled, Tags = new HashSet<int>(p.Tags) }).ToList();

                      if (Interlocked.Increment(ref calls) == 1)
                      {
                          loading.Set();
                          gate.Wait(TimeSpan.FromSeconds(30));
                      }

                      return rows;
                  });

            Mocker.GetMock<IReleaseProfileRepository>()
                  .Setup(s => s.Update(It.IsAny<ReleaseProfile>()))
                  .Returns<ReleaseProfile>(p =>
                  {
                      _stored[0] = p;
                      return p;
                  });

            var reader = Task.Run(() => Subject.All());
            loading.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();

            var writer = Task.Run(() => Subject.Update(new ReleaseProfile { Id = 1, Enabled = false, Tags = new HashSet<int>() }));

            // Give an unlocked clear every chance to finish before the load stores its rows.
            writer.Wait(TimeSpan.FromSeconds(2));
            gate.Set();

            reader.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
            writer.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();

            Subject.All().Should().ContainSingle().Which.Enabled.Should().BeFalse();
            Subject.EnabledForTags(new HashSet<int>(), 0).Should().BeEmpty();
        }

        [Test]
        public void callers_cannot_change_the_cached_list()
        {
            Subject.All().Clear();
            Subject.AllForTags(new HashSet<int>()).Clear();

            Subject.All().Should().HaveCount(1);
            Subject.EnabledForTags(new HashSet<int>(), 0).Should().HaveCount(1);
        }
    }

    // Equivalence against stock on the real repository and test database: after any sequence of
    // writes, every read returns the same profiles, in the same order, as stock's per-call query.
    [TestFixture]
    public class ReleaseProfileCacheEquivalenceFixture : DbTest<ReleaseProfileService, ReleaseProfile>
    {
        private ReleaseProfileRepository _repo;

        [SetUp]
        public void Setup()
        {
            _repo = Mocker.Resolve<ReleaseProfileRepository>();
            Mocker.SetConstant<IReleaseProfileRepository>(_repo);
        }

        // Stock v7.0.3.10918 ReleaseProfileService read paths, verbatim, over the same repository.
        private List<ReleaseProfile> StockAll() => _repo.All().ToList();
        private List<ReleaseProfile> StockAllForTag(int tagId) => _repo.All().Where(r => r.Tags.Contains(tagId)).ToList();
        private List<ReleaseProfile> StockAllForTags(HashSet<int> tagIds) => _repo.All().Where(r => r.Tags.Intersect(tagIds).Any() || r.Tags.Empty()).ToList();
        private List<ReleaseProfile> StockEnabledForTags(HashSet<int> tagIds, int indexerId) => StockAllForTags(tagIds)
            .Where(r => r.Enabled)
            .Where(r => r.IndexerId == indexerId || r.IndexerId == 0).ToList();

        private static ReleaseProfile RandomProfile(Random random, int id = 0)
        {
            var tags = new HashSet<int>();
            for (var t = random.Next(0, 3); t > 0; t--)
            {
                tags.Add(random.Next(1, 5));
            }

            return new ReleaseProfile
            {
                Id = id,
                Name = "profile " + random.Next(1000),
                Enabled = random.Next(4) != 0,
                Required = Enumerable.Range(0, random.Next(0, 3)).Select(i => "req" + random.Next(10)).ToList(),
                Ignored = Enumerable.Range(0, random.Next(0, 3)).Select(i => "ign" + random.Next(10)).ToList(),
                IndexerId = random.Next(3),
                Tags = tags
            };
        }

        private static object Shape(ReleaseProfile p) => new
        {
            p.Id,
            p.Name,
            p.Enabled,
            Required = string.Join("\u0001", p.Required),
            Ignored = string.Join("\u0001", p.Ignored),
            p.IndexerId,
            Tags = string.Join(",", p.Tags.OrderBy(t => t))
        };

        private void AssertSameAsStock()
        {
            Subject.All().Select(Shape).Should().Equal(StockAll().Select(Shape));

            for (var tag = 0; tag <= 5; tag++)
            {
                Subject.AllForTag(tag).Select(Shape).Should().Equal(StockAllForTag(tag).Select(Shape));
            }

            var tagSets = new[] { new HashSet<int>(), new HashSet<int> { 1 }, new HashSet<int> { 2, 3 }, new HashSet<int> { 1, 2, 3, 4 }, new HashSet<int> { 9 } };

            foreach (var tags in tagSets)
            {
                Subject.AllForTags(tags).Select(Shape).Should().Equal(StockAllForTags(tags).Select(Shape));

                for (var indexerId = 0; indexerId <= 3; indexerId++)
                {
                    Subject.EnabledForTags(tags, indexerId).Select(Shape).Should().Equal(StockEnabledForTags(tags, indexerId).Select(Shape));
                }
            }
        }

        [Test]
        public void reads_match_stock_through_random_writes()
        {
            var random = new Random(2026);

            AssertSameAsStock();

            for (var step = 0; step < 120; step++)
            {
                var existing = _repo.All().ToList();
                var op = existing.Count < 3 ? 0 : random.Next(3);

                switch (op)
                {
                    case 0:
                        Subject.Add(RandomProfile(random));
                        break;
                    case 1:
                        Subject.Update(RandomProfile(random, existing[random.Next(existing.Count)].Id));
                        break;
                    default:
                        Subject.Delete(existing[random.Next(existing.Count)].Id);
                        break;
                }

                AssertSameAsStock();
            }
        }

        [Test]
        public void reads_match_stock_when_writes_race_reads()
        {
            var random = new Random(7);
            for (var i = 0; i < 5; i++)
            {
                Subject.Add(RandomProfile(random));
            }

            using var stop = new CancellationTokenSource();
            var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    Subject.EnabledForTags(new HashSet<int> { 1, 2 }, 1);
                }
            })).ToArray();

            var writerRandom = new Random(8);
            for (var i = 0; i < 60; i++)
            {
                var existing = _repo.All().ToList();
                Subject.Update(RandomProfile(writerRandom, existing[writerRandom.Next(existing.Count)].Id));

                // Every write is visible to the next read, whatever the readers were doing.
                Subject.All().Select(Shape).Should().Equal(StockAll().Select(Shape));
            }

            stop.Cancel();
            Task.WaitAll(readers);

            AssertSameAsStock();
        }

        [Test]
        [Explicit]
        [Category("PerfNextBenchmark")]
        public void benchmark_enabled_for_tags()
        {
            var random = new Random(3);
            for (var i = 0; i < 5; i++)
            {
                Subject.Add(RandomProfile(random));
            }

            var tags = new HashSet<int> { 1, 2 };
            Subject.EnabledForTags(tags, 1);

            var best = double.MaxValue;
            for (var round = 0; round < 5; round++)
            {
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < 2000; i++)
                {
                    Subject.EnabledForTags(tags, 1);
                }

                sw.Stop();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }

            TestContext.Progress.WriteLine($"PERFNEXT EnabledForTags x2000 (5 profiles): best of 5 = {best:F1} ms ({best * 1000 / 2000:F1} us per call)");
        }
    }
}
