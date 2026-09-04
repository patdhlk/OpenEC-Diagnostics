using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenEC.Inspector.Session;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;

namespace OpenEC.Inspector.ViewModels;

public sealed record SlaveDetailViewModel(
    string Title,
    string Identity,
    IReadOnlyList<string> StateHistory,
    IReadOnlyList<string> MailboxActivity)
{
    public static SlaveDetailViewModel Build(SlaveStatus? status, IReadOnlyList<MonitorEvent> events)
    {
        var identity = status is { VendorId: { } vendor, ProductCode: { } product }
            ? string.Create(CultureInfo.InvariantCulture,
                $"Vendor 0x{vendor:X8} · Product 0x{product:X8} · Rev 0x{status.Revision ?? 0:X8}")
            : "Identity not observed";
        return new SlaveDetailViewModel(
            status?.DisplayName ?? "Unknown slave",
            identity,
            events.OfType<MonitorEvent.SlaveStateChanged>().Select(EventFormatter.Describe).ToList(),
            events.Where(e => e is MonitorEvent.EmergencyReceived or MonitorEvent.SoeErrorReceived)
                .Select(EventFormatter.Describe).ToList());
    }
}

/// <summary>Tabbed device editor for one slave: a General tab (state badge + detail, cheap to
/// rebuild every tick) and a Variables tab (only scanned while visible — spec-§4 cost rule).</summary>
public sealed partial class DeviceEditorViewModel : ObservableObject, IRefreshable
{
    private readonly MonitorSession _session;

    public DeviceEditorViewModel(MonitorSession session, ushort address, VariableWatchViewModel variables)
    {
        _session = session;
        Address = address;
        Variables = variables;
    }

    public ushort Address { get; }
    public VariableWatchViewModel Variables { get; }

    [ObservableProperty] private SlaveDetailViewModel? _detail;
    [ObservableProperty] private StatusDot _stateDot;
    [ObservableProperty] private string _stateLabel = "Unknown";
    [ObservableProperty] private string _lastSeen = "—";
    [ObservableProperty] private int _selectedTabIndex; // 0 = General, 1 = Variables
    [ObservableProperty] private string _completeness = "";

    public void Refresh()
    {
        var status = _session.Observer.SnapshotSlaves().FirstOrDefault(s => s.Address == Address);
        StateDot = status is null ? StatusDot.Idle : StatusDotMap.ForSlave(status);
        StateLabel = status?.AlState.ToString() ?? "Unknown";
        LastSeen = status?.LastSeen?.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "—";

        var events = _session.Observer.SnapshotEvents()
            .Where(e => AddressOf(e) == Address).ToList();
        Detail = SlaveDetailViewModel.Build(status, events);

        // Sourced from the LEARNED configuration, not the applied one. With an ENI loaded the ENI
        // stays the authority and `Observer.Applied` is null for the whole session by design, so
        // reading it there left the strip blank for exactly the sessions whose Save button now
        // exports a reconstruction — offering an artifact while hiding its quality. Where both
        // exist they agree: a cache hit copies the capture's own completeness rather than the
        // cached file's, and with no ENI the applied configuration IS the learned one. Where they
        // differ — a revision published after a cache hit but deliberately not applied — the
        // learner's is the fresher assessment, and it describes precisely what Save would write.
        //
        // Empty string rather than a placeholder: with nothing learned there is nothing honest to
        // say, and the view collapses the strip when it is empty.
        Completeness = _session.Learned?.Completeness.Slaves
            .FirstOrDefault(s => s.StationAddress == Address) is { } slaveCompleteness
            ? Describe(slaveCompleteness)
            : "";

        if (SelectedTabIndex == 1)
            Variables.Refresh();
    }

    private static ushort? AddressOf(MonitorEvent e) => e switch
    {
        MonitorEvent.SlaveStateChanged s => s.Address,
        MonitorEvent.StateChangeRequested r => r.Address,
        MonitorEvent.EmergencyReceived em => em.StationAddress,
        MonitorEvent.SoeErrorReceived so => so.StationAddress,
        _ => null,
    };

    /// <summary>States what is known and what a master restart would recover, rather than
    /// presenting a partial configuration as a complete one. Internal so the degraded sentence can be
    /// pinned per gap directly: a session cannot be steered into leaving exactly one flag false, and
    /// LearningCompletenessTests already covers which flags a given capture produces.</summary>
    internal static string Describe(SlaveCompleteness c)
    {
        if (c.IsComplete) return "Fully learned from observed traffic.";
        var missing = new List<string>();
        if (!c.IdentityKnown) missing.Add("identity");
        if (!c.SyncManagersKnown) missing.Add("sync managers");
        if (!c.FmmusKnown) missing.Add("FMMUs");
        if (!c.PdoMappingKnown) missing.Add("PDO mapping");
        if (!c.ProcessDataPlaceable) missing.Add("process-data placement");
        return $"Partially learned — missing {string.Join(", ", missing)}. "
             + "Restarting the master with the capture running would recover it.";
    }
}
