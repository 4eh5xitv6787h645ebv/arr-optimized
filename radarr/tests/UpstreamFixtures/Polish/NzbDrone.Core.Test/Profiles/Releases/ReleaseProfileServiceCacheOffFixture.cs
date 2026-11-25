using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Optimize;
using NzbDrone.Core.Profiles.Releases;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Profiles.Releases
{
    // With the release profile cache off (Optimize page or ARR_RELEASE_PROFILE_CACHE) every read goes to the database,
    // as on stock: not only All() but also the tag and indexer lookups the decision engine uses.
    [TestFixture]
    [NonParallelizable]
    public class ReleaseProfileServiceCacheOffFixture : CoreTest<ReleaseProfileService>
    {
        private List<ReleaseProfile> _stored;

        [SetUp]
        public void Setup()
        {
            OptimizeSettings.Reset();

            _stored = new List<ReleaseProfile>
            {
                new ReleaseProfile { Id = 1, Name = "Any", Tags = new HashSet<int>() },
                new ReleaseProfile { Id = 2, Name = "Tagged", IndexerId = 3, Tags = new HashSet<int> { 7 } }
            };

            Mocker.GetMock<IReleaseProfileRepository>()
                  .Setup(s => s.All())
                  .Returns(() => _stored.Select(p => new ReleaseProfile { Id = p.Id, Name = p.Name, IndexerId = p.IndexerId, Tags = new HashSet<int>(p.Tags) }).ToList());
        }

        [TearDown]
        public void TearDown()
        {
            OptimizeSettings.Reset();
        }

        private void GivenCache(bool on)
        {
            OptimizeSettings.Current = OptimizeSettings.Current.With(OptimizeTunables.ReleaseProfileCache, on);
        }

        [Test]
        public void cache_off_should_read_the_repository_for_every_lookup()
        {
            GivenCache(false);

            Subject.AllForTag(7).Select(p => p.Id).Should().Equal(2);
            Subject.AllForTags(new HashSet<int> { 7 }).Select(p => p.Id).Should().Equal(1, 2);
            Subject.EnabledForTags(new HashSet<int> { 7 }, 3).Select(p => p.Id).Should().Equal(1, 2);
            Subject.EnabledForTags(new HashSet<int>(), 4).Select(p => p.Id).Should().Equal(1);

            Mocker.GetMock<IReleaseProfileRepository>().Verify(s => s.All(), Times.Exactly(4));
        }

        [Test]
        public void cache_off_should_see_a_change_made_behind_the_service()
        {
            GivenCache(true);
            Subject.EnabledForTags(new HashSet<int> { 7 }, 3).Should().HaveCount(2);

            GivenCache(false);
            _stored.RemoveAt(1);

            Subject.EnabledForTags(new HashSet<int> { 7 }, 3).Select(p => p.Id).Should().Equal(1);
            Subject.AllForTag(7).Should().BeEmpty();
        }

        [Test]
        public void cache_on_should_read_the_repository_once()
        {
            GivenCache(true);

            Subject.AllForTag(7);
            Subject.AllForTags(new HashSet<int> { 7 });
            Subject.EnabledForTags(new HashSet<int> { 7 }, 3);

            Mocker.GetMock<IReleaseProfileRepository>().Verify(s => s.All(), Times.Once());
        }
    }
}
