namespace OpenEC.Monitor.Protocol;

public enum MailboxType : byte { Error = 0, Aoe = 1, Eoe = 2, Coe = 3, Foe = 4, Soe = 5, Voe = 15 }

public enum CoeService : byte
{
    Emergency = 1, SdoRequest = 2, SdoResponse = 3, TxPdo = 4, RxPdo = 5,
    TxPdoRemoteRequest = 6, RxPdoRemoteRequest = 7, SdoInfo = 8,
}

public enum FoeOpCode : byte { ReadRequest = 1, WriteRequest = 2, Data = 3, Ack = 4, Error = 5, Busy = 6 }

public enum SoeOpCode : byte
{
    ReadRequest = 1, ReadResponse = 2, WriteRequest = 3, WriteResponse = 4,
    Notification = 5, Emergency = 6,
}

[Flags]
public enum SoeElements : byte
{
    None = 0, DataState = 0x01, Name = 0x02, Attribute = 0x04, Unit = 0x08,
    Min = 0x10, Max = 0x20, Value = 0x40, Default = 0x80,
}

public sealed record SdoTransfer(byte CommandSpecifier, bool Expedited, bool SizeIndicated,
    ushort Index, byte SubIndex, ReadOnlyMemory<byte> Data);

public sealed record CoeEmergency(ushort ErrorCode, byte ErrorRegister, ReadOnlyMemory<byte> Data);

public sealed record CoeMessage(ushort Number, CoeService Service, SdoTransfer? Sdo, CoeEmergency? Emergency);

/// <param name="PacketNumber">For <see cref="FoeOpCode.Data"/>/<see cref="FoeOpCode.Ack"/> this is the
/// FoE packet number as named. For <see cref="FoeOpCode.ReadRequest"/>/<see cref="FoeOpCode.WriteRequest"/>
/// (RRQ/WRQ) this same 4-byte field instead carries the password per the ETG FoE specification; it is
/// named PacketNumber here for the DATA/ACK case, its more common use.</param>
public sealed record FoeMessage(FoeOpCode OpCode, uint PacketNumber, string? FileName,
    string? ErrorText, ReadOnlyMemory<byte> Data);

/// <param name="IdnOrFragmentsLeft">The SoE header's 2-byte field carries the IDN
/// (identification number, see IEC 61800-7-204) for ordinary telegrams; for follow-up
/// fragments of a segmented transfer the same field instead carries the number of
/// fragments still outstanding. A passive observer cannot distinguish the two without
/// tracking transfer state, so the raw value is exposed under this dual-purpose name.</param>
/// <param name="ErrorCode">SoE error code from the last two data bytes when
/// <paramref name="Error"/> is set; null otherwise.</param>
public sealed record SoeMessage(SoeOpCode OpCode, bool Incomplete, bool Error, byte DriveNumber,
    SoeElements Elements, ushort IdnOrFragmentsLeft, ushort? ErrorCode, ReadOnlyMemory<byte> Data)
{
    /// <summary>The IDN in IEC 61800-7-204 notation, e.g. "S-0-0017" (standard) or
    /// "P-1-0017" (product-specific, parameter set 1). Meaningless for follow-up
    /// fragments, where the field carries the fragments-left count instead.</summary>
    public string IdnLabel => FormatIdn(IdnOrFragmentsLeft);

    public static string FormatIdn(ushort idn) =>
        $"{((idn & 0x8000) != 0 ? 'P' : 'S')}-{(idn >> 12) & 0x07}-{idn & 0x0FFF:D4}";
}

public sealed record EoeFragment(byte FrameType, byte Port, bool LastFragment, bool TimeAppended,
    ushort FragmentNumber, ushort OffsetOrBufferSize, byte FrameNumber);

public sealed record MailboxMessage(ushort Length, ushort StationAddress, byte Channel, byte Priority,
    MailboxType Type, byte Counter, ReadOnlyMemory<byte> Body,
    CoeMessage? Coe, FoeMessage? Foe, EoeFragment? Eoe, SoeMessage? Soe);
