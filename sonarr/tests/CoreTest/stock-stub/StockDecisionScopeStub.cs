// Stock has no DecisionScope: this stub runs every loader directly, i.e. stock behaviour.
using System;
namespace NzbDrone.Core.Test.SonarrNext
{
    internal sealed class DecisionScope : IDisposable
    {
        public static DecisionScope Begin() => new DecisionScope();
        public static T Memo<T>(string kind, int id, Func<T> load) where T : class => load();
        public void Dispose() { }
    }
}
