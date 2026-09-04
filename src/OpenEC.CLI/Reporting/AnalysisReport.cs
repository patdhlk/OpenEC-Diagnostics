using System.Globalization;
using OpenEC.Monitor;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;

namespace OpenEC.CLI.Reporting;

public sealed record SlaveReport(ushort Address, string Name, string State, bool Error, string? AlStatusCode);

public sealed record LearningReport(
    bool SawStartup,
    int SlavesComplete,
    int SlavesTotal,
    string Summary,
    IReadOnlyList<string> Mismatches,
    IReadOnlyDictionary<string, string> Provenance,
    /// <summary>Where the configuration actually decoding this capture came from: <c>"cache"</c> when a
    /// previously learned bus was recognised by fingerprint, <c>"observed"</c> when this capture's own
    /// traffic supplied it. Omitted when nothing was rebound at all — with an ENI supplied the ENI is
    /// the authority, and with nothing learned there is no configuration to attribute.
    ///
    /// Deliberately separate from <see cref="Provenance"/>, which reports the LEARNER's view of this
    /// capture and must keep doing so: on a cache hit the learner still knows only what the wire
    /// showed, and overwriting that would discard what the capture actually proved. Without this field
    /// a consumer could only tell a cache hit from a fresh learn by string-matching the events list.</summary>
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? ConfigurationSource);

/// <summary>Bus-health snapshot at end of capture: master/bus AL state, found-vs-configured device
/// count, and DC sync. <c>ConfiguredDevices</c>/<c>MaxDcDeviationNs</c> are omitted from JSON when
/// null — no ENI/learned config, and no DC register seen on the wire, respectively.</summary>
public sealed record HealthReport(
    string Level,
    string BusState,
    bool BusStateUniform,
    int FoundDevices,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    int? ConfiguredDevices,
    string DcSync,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    int? MaxDcDeviationNs,
    /// <summary>Slaves in OP whose process-data inputs stopped changing. Empty when none, so the
    /// JSON always carries the field and a reader can tell "checked, nothing" from "not checked".</summary>
    IReadOnlyList<ushort> StaleProcessData);

public sealed record AnalysisReport(
    string File,
    long TotalFrames,
    long EtherCatFrames,
    long NonEtherCatFrames,
    long MalformedFrames,
    double? FramesPerSecond,
    double? CycleTimeMicroseconds,
    long SuspectedLostFrames,
    long RingLostFrames,
    long WkcMismatches,
    long Emergencies,
    long SoeErrors,
    string BusState,
    IReadOnlyList<SlaveReport> Slaves,
    IReadOnlyList<string> Events,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    LearningReport? Learning,
    HealthReport Health)
{
    public bool HasBusErrors =>
        WkcMismatches > 0 || Emergencies > 0 || SoeErrors > 0 || Slaves.Any(s => s.Error);

    public static AnalysisReport Build(string file, EtherCatMonitor monitor)
    {
        var stats = monitor.Observer.Statistics;
        var log = monitor.Observer.EventLog;
        var learning = monitor.Learned is { } learned
            ? new LearningReport(
                learned.Completeness.SawStartup,
                learned.Completeness.Slaves.Count(s => s.IsComplete),
                learned.Completeness.Slaves.Count,
                learned.Completeness.Summary,
                log.OfType<MonitorEvent.ConfigMismatch>()
                    .Select(m => $"{m.Kind}: declared {m.Declared}, observed {m.Observed}")
                    .ToList(),
                learned.Provenance.ToDictionary(
                    kv => kv.Key.ToString(CultureInfo.InvariantCulture),
                    kv => $"identity={kv.Value.Identity}, names={kv.Value.Names}, mapping={kv.Value.Mapping}"),
                ConfigurationSourceOf(monitor))
            : null;
        var health = monitor.SnapshotHealth();
        return new AnalysisReport(
            file,
            stats.TotalFrames,
            stats.EtherCatFrames,
            stats.NonEtherCatFrames,
            stats.MalformedFrames,
            stats.FramesPerSecond,
            stats.EstimatedCycleTime?.TotalMicroseconds,
            stats.SuspectedLostFrames,
            stats.RingLostFrames,
            stats.WkcMismatches,
            log.Count(e => e is MonitorEvent.EmergencyReceived),
            log.Count(e => e is MonitorEvent.SoeErrorReceived),
            monitor.Observer.Bus.BusState.ToString(),
            monitor.Observer.Bus.Slaves
                .OrderBy(s => s.Address)
                .Select(s => new SlaveReport(s.Address, s.DisplayName, s.AlState.ToString(),
                    s.ErrorFlag, s.AlStatusCode?.ToString("X4")))
                .ToList(),
            log.Select(Describe).ToList(),
            learning,
            new HealthReport(
                health.Level.ToString(),
                health.BusState.ToString(),
                health.BusStateUniform,
                health.FoundDevices,
                health.ConfiguredDevices,
                health.DcSync.ToString(),
                health.MaxDcDeviationNs,
                health.Stale));
    }

    /// <summary>Reads the applied configuration's own provenance, which is where a cache hit is
    /// recorded: the hit path stamps <see cref="FactSource.Cache"/> on every fact it puts in force, so
    /// an all-cache provenance is the cache's signature rather than an inference about it. Null when
    /// nothing was applied — an ENI-driven session never rebinds — so the JSON simply omits the field.
    ///
    /// A later revision that this capture learned in full does replace a cache hit, and then the
    /// provenance is the learner's again and this reports "observed". That is the honest answer: the
    /// configuration in force at the end is the one the capture itself produced.</summary>
    private static string? ConfigurationSourceOf(EtherCatMonitor monitor) =>
        monitor.Observer.Applied is not { } applied
            ? null
            : applied.Provenance.Count > 0
              && applied.Provenance.Values.All(p =>
                  p is { Identity: FactSource.Cache, Names: FactSource.Cache, Mapping: FactSource.Cache })
                ? "cache"
                : "observed";

    private static string Describe(MonitorEvent e) => e switch
    {
        MonitorEvent.SlaveStateChanged s =>
            $"{s.Timestamp:HH:mm:ss.fff} slave {s.Address}: {s.OldState} -> {s.NewState}{(s.ErrorFlag ? " [ERROR]" : "")}",
        MonitorEvent.StateChangeRequested r =>
            $"{r.Timestamp:HH:mm:ss.fff} master requested {r.RequestedState} for slave {r.Address}",
        MonitorEvent.WkcMismatchDetected w =>
            $"{w.Timestamp:HH:mm:ss.fff} WKC mismatch on {w.Command} 0x{w.Address:X8}: expected {w.Expected}, got {w.Actual}",
        MonitorEvent.EmergencyReceived m =>
            $"{m.Timestamp:HH:mm:ss.fff} EMERGENCY from slave {m.StationAddress}: code 0x{m.ErrorCode:X4} register 0x{m.ErrorRegister:X2}",
        MonitorEvent.SoeErrorReceived s =>
            $"{s.Timestamp:HH:mm:ss.fff} SoE error from slave {s.StationAddress}: {s.IdnLabel} {s.OpCode} code 0x{s.ErrorCode:X4}",
        MonitorEvent.ConfigMismatch c =>
            c.Address is { } address
                ? $"{c.Timestamp:HH:mm:ss.fff} slave {address}: {c.Kind} - ENI says {c.Declared}, bus shows {c.Observed}"
                : $"{c.Timestamp:HH:mm:ss.fff} {c.Kind} - ENI says {c.Declared}, bus shows {c.Observed}",
        MonitorEvent.ConfigurationLearned l =>
            $"{l.Timestamp:HH:mm:ss.fff} configuration revision {l.Revision}: {l.Summary}",
        MonitorEvent.ProcessDataStalled p =>
            $"{p.Timestamp:HH:mm:ss.fff} slave {p.Address}: process data unchanged for "
            + $"{p.StaleFor.TotalSeconds:F0}s while in Op",
        MonitorEvent.ProcessDataResumed r =>
            $"{r.Timestamp:HH:mm:ss.fff} slave {r.Address}: process data changing again",
        MonitorEvent.BusHealthChanged h =>
            $"{h.Timestamp:HH:mm:ss.fff} bus health {h.Health.Level}: {h.Health.FoundDevices}"
            + (h.Health.ConfiguredDevices is { } cfg ? $"/{cfg}" : "") + " devices, DC "
            + (h.Health.DcSync switch
            {
                DcSyncState.Synced => "synced",
                DcSyncState.OutOfSync => "out of sync",
                _ => "unmonitored",
            }),
        _ => e.ToString() ?? "",
    };
}
