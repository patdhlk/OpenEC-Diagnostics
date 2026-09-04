using System.Buffers.Binary;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Topology;

namespace OpenEC.Monitor.Learning;

/// <summary>Pure decoders turning one observed datagram into zero or more learned facts.
/// Register offsets are per ETG.1000.4; phase attribution is documented in the design spec.</summary>
public static class RegisterDecoders
{
    public const ushort StationAddressRegister = 0x0010;
    public const ushort SiiControlRegister = 0x0502;
    public const ushort SiiDataRegister = 0x0508;
    public const ushort SyncManagerBase = 0x0800;
    public const ushort FmmuBase = 0x0600;

    internal static bool IsWrite(EtherCatCommand cmd) =>
        cmd is EtherCatCommand.Fpwr or EtherCatCommand.Apwr or EtherCatCommand.Bwr;

    internal static bool IsRead(EtherCatCommand cmd) =>
        cmd is EtherCatCommand.Fprd or EtherCatCommand.Aprd or EtherCatCommand.Brd;

    /// <summary>APWR to 0x0010 — the master assigning a configured station address to the
    /// slave at an auto-increment position. The single richest datagram on the bus: it
    /// yields ring position and station address together.</summary>
    public static StationAddressFact? TryStationAddress(EtherCatDatagram d, FrameDirection dir)
    {
        if (dir != FrameDirection.Outbound) return null;
        if (d.Command != EtherCatCommand.Apwr) return null;
        if (d.Ado != StationAddressRegister || d.Payload.Length < 2) return null;
        return new StationAddressFact(d.Adp,
            BinaryPrimitives.ReadUInt16LittleEndian(d.Payload.Span));
    }

    /// <summary>Write to 0x0502 — SII control (2 bytes) followed by the SII word address
    /// (4 bytes). Bit 8 of control requests a read.</summary>
    public static SiiAddressFact? TrySiiAddress(EtherCatDatagram d, FrameDirection dir)
    {
        if (dir != FrameDirection.Outbound || !IsWrite(d.Command)) return null;
        if (d.Ado != SiiControlRegister || d.Payload.Length < 6) return null;
        var span = d.Payload.Span;
        var control = BinaryPrimitives.ReadUInt16LittleEndian(span);
        return new SiiAddressFact(SlaveRef.From(d, dir),
            BinaryPrimitives.ReadUInt32LittleEndian(span[2..]), (control & 0x0100) != 0);
    }

    /// <summary>Returning read of 0x0508 — the EEPROM data answering the preceding address
    /// write. A zero working counter means no slave answered, so the payload is meaningless.</summary>
    public static SiiDataFact? TrySiiData(EtherCatDatagram d, FrameDirection dir)
    {
        if (dir != FrameDirection.Returning || !IsRead(d.Command)) return null;
        if (d.Ado != SiiDataRegister || d.Payload.Length < 2 || d.WorkingCounter == 0) return null;
        return new SiiDataFact(SlaveRef.From(d, dir), d.Payload.ToArray());
    }

    /// <summary>ETG.1000.4 defines 16 SyncManagers and 16 FMMUs per slave, numbered 0-15.</summary>
    private const int MaxRegisterBlocks = 16;

    /// <summary>Writes to 0x0800 + 8n. Layout per block: physical start (2), length (2),
    /// control (1), status (1), activate (1), PDI control (1). Bit 0 of activate enables.
    /// Masters configure several SyncManagers in one datagram, so this returns a list.
    /// The loop is bounded by block number as well as payload length: a write starting near
    /// the top of the window with a long payload must not fabricate a block 16 that no slave
    /// has, since a bogus SyncManager can later match a real FMMU by physical address.</summary>
    public static IReadOnlyList<SyncManagerFact> TrySyncManagers(EtherCatDatagram d, FrameDirection dir)
    {
        if (dir != FrameDirection.Outbound || !IsWrite(d.Command)) return [];
        if (d.Ado < SyncManagerBase || d.Ado >= SyncManagerBase + 8 * MaxRegisterBlocks) return [];
        var offset = d.Ado - SyncManagerBase;
        if (offset % 8 != 0) return [];
        var first = offset / 8;
        var span = d.Payload.Span;
        var facts = new List<SyncManagerFact>();
        for (var i = 0; i + 8 <= span.Length && first + i / 8 < MaxRegisterBlocks; i += 8)
        {
            var b = span.Slice(i, 8);
            facts.Add(new SyncManagerFact(SlaveRef.From(d, dir), (byte)(first + i / 8),
                BinaryPrimitives.ReadUInt16LittleEndian(b),
                BinaryPrimitives.ReadUInt16LittleEndian(b[2..]),
                b[4], (b[6] & 0x01) != 0));
        }
        return facts;
    }

    /// <summary>Writes to 0x0600 + 16n. Layout per block: logical start address (4),
    /// length (2), logical start bit (1), logical stop bit (1), physical start address (2),
    /// physical start bit (1), type (1), activate (1), then 3 reserved bytes.
    /// Bounded by block number as well as payload length, for the same reason as <see cref="TrySyncManagers"/>.</summary>
    public static IReadOnlyList<FmmuFact> TryFmmus(EtherCatDatagram d, FrameDirection dir)
    {
        if (dir != FrameDirection.Outbound || !IsWrite(d.Command)) return [];
        if (d.Ado < FmmuBase || d.Ado >= FmmuBase + 16 * MaxRegisterBlocks) return [];
        var offset = d.Ado - FmmuBase;
        if (offset % 16 != 0) return [];
        var first = offset / 16;
        var span = d.Payload.Span;
        var facts = new List<FmmuFact>();
        for (var i = 0; i + 16 <= span.Length && first + i / 16 < MaxRegisterBlocks; i += 16)
        {
            var b = span.Slice(i, 16);
            facts.Add(new FmmuFact(SlaveRef.From(d, dir), (byte)(first + i / 16),
                BinaryPrimitives.ReadUInt32LittleEndian(b),
                BinaryPrimitives.ReadUInt16LittleEndian(b[4..]),
                b[6], b[7],
                BinaryPrimitives.ReadUInt16LittleEndian(b[8..]),
                b[10], (FmmuType)b[11], (b[12] & 0x01) != 0));
        }
        return facts;
    }

    public const ushort DlStatusRegister = 0x0110;

    /// <summary>Returning read of 0x0110 — DL status, the register a master polls to notice a
    /// topology change. A zero working counter means no slave answered, so the payload is
    /// meaningless, exactly as for <see cref="TrySiiData"/>.</summary>
    public static DlStatusFact? TryDlStatus(EtherCatDatagram d, FrameDirection dir)
    {
        if (dir != FrameDirection.Returning || !IsRead(d.Command)) return null;
        if (d.Ado != DlStatusRegister || d.Payload.Length < 2 || d.WorkingCounter == 0) return null;
        return new DlStatusFact(SlaveRef.From(d, dir),
            BinaryPrimitives.ReadUInt16LittleEndian(d.Payload.Span));
    }

    public const ushort ErrorCounterBase = 0x0300;      // 0x0300 + 2n invalid frame, +1 RX error
    public const ushort ForwardedErrorBase = 0x0308;    // 0x0308 + n
    public const ushort ProcessingUnitErrorRegister = 0x030C;
    public const ushort PdiErrorRegister = 0x030D;
    public const ushort LostLinkBase = 0x0310;          // 0x0310 + n

    /// <summary>Returning read of the ESC error-counter registers (ETG.1000.4). Masters read
    /// these in blocks, so the payload is walked byte by byte from whatever offset the datagram
    /// started at and each byte is attributed to the register it actually lands on. A register
    /// the read did not cover is left absent — never defaulted to zero, which would claim a
    /// healthy port on a bus whose master never polls these at all.</summary>
    public static PortCountersFact? TryPortCounters(EtherCatDatagram d, FrameDirection dir)
    {
        if (dir != FrameDirection.Returning || !IsRead(d.Command)) return null;
        if (d.Payload.Length == 0 || d.WorkingCounter == 0) return null;
        var start = d.Ado;
        var end = start + d.Payload.Length;
        if (end <= ErrorCounterBase || start > LostLinkBase + 3) return null;

        var span = d.Payload.Span;
        var ports = new Dictionary<byte, PortCounters>();
        byte? processingUnit = null;
        byte? pdi = null;

        PortCounters For(byte port) => ports.TryGetValue(port, out var existing)
            ? existing : PortCounters.Unknown;

        for (var i = 0; i < span.Length; i++)
        {
            var register = start + i;
            var value = span[i];
            switch (register)
            {
                case >= ErrorCounterBase and < ForwardedErrorBase:
                    {
                        var offset = register - ErrorCounterBase;
                        var port = (byte)(offset / 2);
                        ports[port] = offset % 2 == 0
                            ? For(port) with { InvalidFrame = value }
                            : For(port) with { RxError = value };
                        break;
                    }
                case >= ForwardedErrorBase and < ProcessingUnitErrorRegister:
                    {
                        var port = (byte)(register - ForwardedErrorBase);
                        ports[port] = For(port) with { ForwardedRxError = value };
                        break;
                    }
                case ProcessingUnitErrorRegister:
                    processingUnit = value;
                    break;
                case PdiErrorRegister:
                    pdi = value;
                    break;
                case >= LostLinkBase and <= LostLinkBase + 3:
                    {
                        var port = (byte)(register - LostLinkBase);
                        ports[port] = For(port) with { LostLink = value };
                        break;
                    }
            }
        }

        return ports.Count == 0 && processingUnit is null && pdi is null
            ? null
            : new PortCountersFact(SlaveRef.From(d, dir), ports, processingUnit, pdi);
    }
}
