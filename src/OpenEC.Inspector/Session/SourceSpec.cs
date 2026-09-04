namespace OpenEC.Inspector.Session;

/// <summary>Where a session captures from. One spec → one fresh ICaptureSource.</summary>
public abstract record SourceSpec
{
    /// <summary>When set, every captured frame is also recorded to this pcap file.</summary>
    public string? RecordPath { get; init; }

    public sealed record Live(string InterfaceName) : SourceSpec;
    public sealed record File(string Path) : SourceSpec;

    public string Description => this switch
    {
        Live l => l.InterfaceName,
        File f => System.IO.Path.GetFileName(f.Path),
        _ => ToString()!,
    };
}
