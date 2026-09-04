// src/OpenEC.Monitor/Eni/EniModels.cs
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Eni;

public sealed record MailboxRange(ushort Start, ushort Length)
{
    public bool Contains(ushort ado) => ado >= Start && ado < Start + Length;
}

/// <summary>An ENI-declared topology edge: the device upstream of this one, and the upstream
/// device's port it hangs off. ENI writes the port as a letter.</summary>
public sealed record EniPreviousPort(ushort PhysAddr, byte Port)
{
    /// <summary>Maps an ENI port designation to a port index. The letter mapping
    /// (A=0, B=1, C=2, D=3) is marked unverified in the topology design spec §10 and lives only
    /// here. An unrecognised value yields null rather than a defaulted port 0, which would place
    /// a branch on the upstream port and silently corrupt the tree.</summary>
    public static byte? ParsePort(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "A" => 0,
        "B" => 1,
        "C" => 2,
        "D" => 3,
        "0" => 0,
        "1" => 1,
        "2" => 2,
        "3" => 3,
        _ => null,
    };
}

public sealed record EniSlave(string Name, ushort PhysAddr, ushort AutoIncAddr,
    uint VendorId, uint ProductCode, uint RevisionNo,
    MailboxRange? MailboxOut, MailboxRange? MailboxIn,
    EniPreviousPort? PreviousPort = null);

/// <summary>One command of the master's cyclic frame table. RawAddress matches
/// <see cref="EtherCatDatagram.RawAddress"/> (logical address, or ado&lt;&lt;16|adp).</summary>
public sealed record EniCyclicCommand(EtherCatCommand Command, uint RawAddress,
    int DataLength, int ExpectedWkc, int? InputOffs, int? OutputOffs);

/// <summary>A process-image variable. BitOffs is relative to the whole input or output image.</summary>
public sealed record EniVariable(string Name, string DataType, int BitSize, int BitOffs, bool IsInput);
