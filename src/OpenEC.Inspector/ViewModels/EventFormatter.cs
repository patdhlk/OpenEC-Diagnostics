using OpenEC.Monitor.Observation;

namespace OpenEC.Inspector.ViewModels;

public static class EventFormatter
{
    public static string Category(MonitorEvent e) => e switch
    {
        MonitorEvent.SlaveStateChanged => "State",
        MonitorEvent.StateChangeRequested => "State request",
        MonitorEvent.WkcMismatchDetected => "WKC",
        MonitorEvent.EmergencyReceived => "Emergency",
        MonitorEvent.SoeErrorReceived => "SoE",
        MonitorEvent.ConfigMismatch => "Config",
        MonitorEvent.ConfigurationLearned => "Learning",
        MonitorEvent.TopologyChanged => "Topology",
        MonitorEvent.BusHealthChanged => "Health",
        _ => "Other",
    };

    public static string Describe(MonitorEvent e) => e switch
    {
        MonitorEvent.SlaveStateChanged s =>
            $"Slave {s.Address}: {s.OldState} → {s.NewState}{(s.ErrorFlag ? " (error)" : "")}",
        MonitorEvent.StateChangeRequested r => $"Slave {r.Address}: requested {r.RequestedState}",
        MonitorEvent.WkcMismatchDetected w =>
            $"{w.Command} @0x{w.Address:X8}: WKC {w.Actual} (expected {w.Expected})",
        MonitorEvent.EmergencyReceived em =>
            $"Slave {em.StationAddress}: CoE emergency 0x{em.ErrorCode:X4} (register 0x{em.ErrorRegister:X2})",
        MonitorEvent.SoeErrorReceived so =>
            $"Slave {so.StationAddress}: SoE error 0x{so.ErrorCode:X4} on {so.IdnLabel} ({so.OpCode})",
        MonitorEvent.ConfigMismatch c =>
            c.Address is { } address
                ? $"Slave {address}: {c.Kind} — ENI says {c.Declared}, bus shows {c.Observed}"
                : $"{c.Kind} — ENI says {c.Declared}, bus shows {c.Observed}",
        MonitorEvent.ConfigurationLearned l =>
            $"Configuration revision {l.Revision}: {l.Summary}",
        MonitorEvent.TopologyChanged t =>
            $"Slave {t.Address} port {t.Port}: {t.OldState} → {t.NewState}",
        MonitorEvent.BusHealthChanged h => Health(h.Health),
        _ => e.ToString()!,
    };

    private static string Health(BusHealth h)
    {
        var devices = h.ConfiguredDevices is { } cfg
            ? $"{h.FoundDevices}/{cfg} devices"
            : $"{h.FoundDevices} devices";
        var dc = h.DcSync switch
        {
            DcSyncState.Synced => "DC synced",
            DcSyncState.OutOfSync => "DC out of sync",
            _ => "DC unmonitored",
        };
        return $"Bus {h.Level.ToString().ToLowerInvariant()} — {devices}, {dc}";
    }
}
