using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Profiles.Releases;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Profiles.Releases
{
    // Stock read new ReleaseProfile objects from the database on every call, so a caller that changed a returned
    // profile changed only its own copy. The in-memory profile list must keep that: every call returns profiles that
    // share nothing with the list or with other callers. The repository mock hands out new objects on every call, as
    // the database does, so these tests pass on stock too.
    [TestFixture]
    public class ReleaseProfileServiceCopyFixture : CoreTest<ReleaseProfileService>
    {
        private List<ReleaseProfile> _stored;

        [SetUp]
        public void Setup()
        {
            _stored = new List<ReleaseProfile>
            {
                new ReleaseProfile { Id = 1, Name = "Any", Enabled = true, Required = new List<string> { "x264" }, Ignored = new List<string> { "cam" }, IndexerId = 0, Tags = new HashSet<int>() },
                new ReleaseProfile { Id = 2, Name = "Tagged", Enabled = true, Required = new List<string>(), Ignored = new List<string> { "ts" }, IndexerId = 3, Tags = new HashSet<int> { 7 } },
                new ReleaseProfile { Id = 3, Name = "Off", Enabled = false, Required = new List<string> { "hevc" }, Ignored = new List<string>(), IndexerId = 0, Tags = new HashSet<int> { 7, 8 } }
            };

            Mocker.GetMock<IReleaseProfileRepository>()
                  .Setup(s => s.All())
                  .Returns(() => _stored.Select(Clone).ToList());
        }

        private static ReleaseProfile Clone(ReleaseProfile p)
        {
            return new ReleaseProfile
            {
                Id = p.Id,
                Name = p.Name,
                Enabled = p.Enabled,
                Required = p.Required.ToList(),
                Ignored = p.Ignored.ToList(),
                IndexerId = p.IndexerId,
                Tags = new HashSet<int>(p.Tags)
            };
        }

        private static void Mutate(ReleaseProfile profile)
        {
            profile.Name = "changed";
            profile.Enabled = !profile.Enabled;
            profile.IndexerId = 99;
            profile.Required.Add("added");
            profile.Ignored.Clear();
            profile.Tags.Add(42);
            profile.Tags.Remove(7);
        }

        private void ShouldMatchStored(IEnumerable<ReleaseProfile> profiles)
        {
            foreach (var profile in profiles)
            {
                profile.Should().BeEquivalentTo(_stored.Single(s => s.Id == profile.Id));
            }
        }

        [Test]
        public void changing_profiles_returned_by_all_should_not_change_later_results()
        {
            var first = Subject.All();
            first.ForEach(Mutate);

            var second = Subject.All();

            second.Should().HaveCount(3);
            ShouldMatchStored(second);
        }

        [Test]
        public void changing_profiles_returned_for_a_release_should_not_change_the_next_decision()
        {
            // EnabledForTags is what ReleaseRestrictionsSpecification reads for every release.
            var tags = new HashSet<int> { 7 };

            var first = Subject.EnabledForTags(tags, 3);
            first.Select(p => p.Id).Should().Equal(1, 2);
            first.ForEach(Mutate);

            var second = Subject.EnabledForTags(tags, 3);

            second.Select(p => p.Id).Should().Equal(1, 2);
            ShouldMatchStored(second);

            Subject.AllForTag(7).Select(p => p.Id).Should().Equal(2, 3);
            Subject.AllForTags(new HashSet<int> { 8 }).Select(p => p.Id).Should().Equal(1, 3);
        }

        [Test]
        public void changing_profiles_returned_by_tag_lookups_should_not_change_later_results()
        {
            Subject.AllForTag(7).ForEach(Mutate);
            Subject.AllForTags(new HashSet<int> { 8 }).ForEach(Mutate);

            ShouldMatchStored(Subject.All());
        }

        [Test]
        public void two_calls_should_not_share_profiles_or_their_collections()
        {
            var first = Subject.All();
            var second = Subject.All();

            for (var i = 0; i < first.Count; i++)
            {
                second[i].Should().NotBeSameAs(first[i]);
                second[i].Required.Should().NotBeSameAs(first[i].Required);
                second[i].Ignored.Should().NotBeSameAs(first[i].Ignored);
                second[i].Tags.Should().NotBeSameAs(first[i].Tags);
            }
        }

        [Test]
        public void returned_profiles_should_carry_every_property()
        {
            // Fails when upstream adds a ReleaseProfile property: add it to ReleaseProfileService.Copy and here.
            typeof(ReleaseProfile).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)
                .Should().Equal("Enabled", "Id", "Ignored", "IndexerId", "Name", "Required", "Tags");

            var returned = Subject.All();

            returned.Should().BeEquivalentTo(_stored, o => o.WithStrictOrdering());
        }

        [Test]
        public void concurrent_callers_changing_their_profiles_should_not_affect_each_other()
        {
            var tags = new HashSet<int> { 7 };
            var failures = 0;

            Parallel.For(0, 2000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                var profiles = Subject.EnabledForTags(tags, 3);

                if (profiles.Count != 2 ||
                    !profiles[0].Required.SequenceEqual(new[] { "x264" }) ||
                    !profiles[1].Ignored.SequenceEqual(new[] { "ts" }) ||
                    profiles.Any(p => p.Tags.Contains(42)))
                {
                    Interlocked.Increment(ref failures);
                }

                try
                {
                    profiles.ForEach(Mutate);
                }
                catch (Exception)
                {
                    // A HashSet or List changed by two threads at once (only possible when the objects are shared).
                    Interlocked.Increment(ref failures);
                }
            });

            failures.Should().Be(0);
        }
    }
}
