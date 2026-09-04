namespace OpenEC.Monitor.Observation;

public sealed class SlaveStatus
{
    public required ushort Address { get; init; }
    public string? ConfiguredName { get; set; }
    public string? ResolvedDeviceName { get; set; }
    public uint? VendorId { get; set; }
    public uint? ProductCode { get; set; }
    public uint? Revision { get; set; }
    public SlaveAlState AlState { get; set; }
    public bool ErrorFlag { get; set; }
    public ushort? AlStatusCode { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
    public int? DcSystemTimeDiffNs { get; set; }

    public string DisplayName => ConfiguredName ?? ResolvedDeviceName ?? $"Slave {Address}";
}
