namespace OpenEC.Monitor.Protocol;

public sealed record EtherCatDatagram(
    EtherCatCommand Command,
    byte Index,
    uint RawAddress,
    bool Circulating,
    bool MoreFollows,
    ushort Irq,
    ReadOnlyMemory<byte> Payload,
    ushort WorkingCounter)
{
    /// <summary>Position/fixed station address (low 16 bits) for physical commands.</summary>
    public ushort Adp => (ushort)(RawAddress & 0xFFFF);

    /// <summary>Register offset (high 16 bits) for physical commands.</summary>
    public ushort Ado => (ushort)(RawAddress >> 16);

    public bool IsLogical => Command is EtherCatCommand.Lrd or EtherCatCommand.Lwr or EtherCatCommand.Lrw;

    public uint LogicalAddress => RawAddress;
}
