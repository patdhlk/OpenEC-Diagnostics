using Microsoft.Extensions.Logging;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Learning;

namespace OpenEC.Monitor;

/// <summary>Whether the passive learner runs alongside the observer. `Auto` is the default: with
/// no ENI it supplies the configuration, and with an ENI it cross-checks against it.</summary>
public enum LearningMode { Auto, Off }

public sealed class EtherCatMonitorOptions
{
    public EniConfiguration? Eni { get; set; }
    public string? EsiDirectory { get; set; }
    public ILoggerFactory? LoggerFactory { get; set; }
    public LearningMode Learning { get; set; } = LearningMode.Auto;

    /// <summary>Where learned configurations are cached, so a bus whose startup was observed once is
    /// recognised on later mid-run attaches. Null disables caching — which is what tests want, and
    /// why this is not defaulted: a default would have every test write into the real user profile.
    /// Callers that want caching pass `new LearnedBusCache(LearnedBusCache.DefaultDirectory)`.</summary>
    public LearnedBusCache? LearnedCache { get; set; }

    /// <summary>How long a slave in OP may hold its process-data inputs unchanged before the
    /// observer reports it. Null disables the check; the per-slave activity is still tracked and
    /// still readable through <c>BusObserver.SnapshotProcessData</c>, only the verdict goes away.
    ///
    /// The default is generous on purpose. At a 10 ms cycle a minute is six thousand unchanged
    /// exchanges, which is long enough that slow physical processes and idle digital inputs do not
    /// trip it, and short enough to catch an application that has stopped answering.</summary>
    public TimeSpan? StaleProcessDataAfter { get; set; } = TimeSpan.FromSeconds(60);
}
