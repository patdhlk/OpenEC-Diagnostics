namespace OpenEC.Monitor.Observation;

public abstract record MonitorEvent(DateTimeOffset Timestamp)
{
    public sealed record SlaveStateChanged(DateTimeOffset Timestamp, ushort Address,
        SlaveAlState OldState, SlaveAlState NewState, bool ErrorFlag) : MonitorEvent(Timestamp);

    public sealed record StateChangeRequested(DateTimeOffset Timestamp, ushort Address,
        SlaveAlState RequestedState) : MonitorEvent(Timestamp);

    public sealed record WkcMismatchDetected(DateTimeOffset Timestamp, Protocol.EtherCatCommand Command,
        uint Address, ushort Expected, ushort Actual) : MonitorEvent(Timestamp);

    public sealed record EmergencyReceived(DateTimeOffset Timestamp, ushort StationAddress,
        ushort ErrorCode, byte ErrorRegister) : MonitorEvent(Timestamp);

    public sealed record SoeErrorReceived(DateTimeOffset Timestamp, ushort StationAddress,
        Protocol.SoeOpCode OpCode, ushort Idn, ushort ErrorCode) : MonitorEvent(Timestamp)
    {
        public string IdnLabel => Protocol.SoeMessage.FormatIdn(Idn);
    }

    /// <summary>The declared ENI and the passively learned configuration disagree.</summary>
    public sealed record ConfigMismatch(DateTimeOffset Timestamp, ConfigMismatchKind Kind,
        ushort? Address, string Declared, string Observed) : MonitorEvent(Timestamp);

    /// <summary>A learned configuration was published. Spec §7 puts this on the event stream so a
    /// session's log shows when the picture of the bus changed, and by how much.</summary>
    public sealed record ConfigurationLearned(DateTimeOffset Timestamp, int Revision, string Summary)
        : MonitorEvent(Timestamp);

    /// <summary>A port's link state changed mid-session — a cable pulled or plugged, or a loop
    /// opening or closing. The map shows where; this says when.</summary>
    public sealed record TopologyChanged(DateTimeOffset Timestamp, ushort Address, byte Port,
        Topology.PortLinkState OldState, Topology.PortLinkState NewState) : MonitorEvent(Timestamp);

    /// <summary>A slave in OP stopped changing its process-data inputs for longer than the
    /// configured threshold. Deliberately an observation rather than a verdict: a device can hold an
    /// input steady for a long time perfectly legitimately. It is the one signal that catches an
    /// application hung behind a healthy EtherCAT chip, which every other health measure reports as
    /// fine.</summary>
    public sealed record ProcessDataStalled(DateTimeOffset Timestamp, ushort Address,
        TimeSpan StaleFor) : MonitorEvent(Timestamp);

    /// <summary>A slave previously reported stalled started changing its inputs again.</summary>
    public sealed record ProcessDataResumed(DateTimeOffset Timestamp, ushort Address)
        : MonitorEvent(Timestamp);

    /// <summary>The aggregate bus health changed. Includes AL state, device-count match, and DC sync.</summary>
    public sealed record BusHealthChanged(DateTimeOffset Timestamp, BusHealth Health) : MonitorEvent(Timestamp);
}

public enum ConfigMismatchKind { SlaveMissing, SlaveUnexpected, Identity, ProcessImage, Topology }
