using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Learning;

/// <summary>How a datagram addressed the slave it targeted. During INIT the master uses
/// auto-increment addressing before configured station addresses exist, so a fact cannot
/// name its slave until <see cref="LearnedBus"/> has seen the assignment that maps the
/// two. Carrying the addressing mode keeps the decoders pure.
///
/// A broadcast targets every slave at once: its ADP field is a responder count rather than an
/// address, and a broadcast read returns the bitwise OR of what every slave held. So it names no
/// single slave, and the per-slave facts decoded from one belong to nobody. Attributing them to the
/// ADP verbatim lands them on address zero — which is <see cref="Topology.BusTopology.MasterAddress"/>,
/// the one address that must never name a device.</summary>
public readonly record struct SlaveRef(ushort Address, bool IsAutoIncrement,
    bool IsBroadcast = false, bool IsReturning = false)
{
    public static SlaveRef From(EtherCatDatagram d, FrameDirection direction) => new(d.Adp,
        d.Command is EtherCatCommand.Aprd or EtherCatCommand.Apwr
            or EtherCatCommand.Aprw or EtherCatCommand.Armw,
        d.Command is EtherCatCommand.Brd or EtherCatCommand.Bwr or EtherCatCommand.Brw,
        direction == FrameDirection.Returning);

    /// <summary>The same reference with its address restated as the master sent it.
    ///
    /// Every slave increments an auto-increment datagram's ADP as it forwards the frame, so a
    /// returning copy carries the outbound ADP plus the number of slaves that saw it. Subtracting
    /// the ring length undoes that. This matters because the facts worth having — DL status, SII
    /// identity, error counters — are only readable on the RETURN leg, where the answer is: read
    /// the returning ADP as a ring position and every one of them lands on the wrong slave, or on
    /// no slave at all.</summary>
    public SlaveRef Normalized(ushort ringLength) =>
        IsAutoIncrement && IsReturning
            ? this with { Address = (ushort)(Address - ringLength), IsReturning = false }
            // Fixed addressing carries a station address, which no slave touches — but the flag is
            // still cleared, because a normalized reference is used as a dictionary key that has to
            // match across the two legs of one exchange. An SII read is a write on the way out and
            // a read on the way back; if those two hash differently the answer never finds its
            // question, and the identity it carried is silently lost.
            : this with { IsReturning = false };

    /// <summary>Zero-based ring position, or -1 when this reference uses fixed addressing.
    /// Auto-increment addresses count down from zero, so the position is the two's
    /// complement of the address. Only meaningful once <see cref="Normalized"/> has been
    /// applied — on a raw returning reference the arithmetic is off by the ring length.</summary>
    public int RingPosition => IsAutoIncrement ? (ushort)(0 - Address) : -1;
}

/// <summary>FMMU direction, per the type byte at offset 11 of an FMMU register block.</summary>
public enum FmmuType : byte { None = 0, Inputs = 1, Outputs = 2 }

/// <summary>The master assigning a configured station address to a ring position (APWR 0x0010).</summary>
public sealed record StationAddressFact(ushort AutoIncAddress, ushort StationAddress)
{
    /// <summary>Delegates to <see cref="SlaveRef.RingPosition"/> rather than repeating the
    /// two's-complement arithmetic. This is the property <see cref="LearnedBus"/> actually
    /// consumes to key every slave, so it must not drift from the tested implementation.</summary>
    public int RingPosition => new SlaveRef(AutoIncAddress, IsAutoIncrement: true).RingPosition;
}

/// <summary>An SII/EEPROM address+command write (register 0x0502). The data arrives separately.</summary>
public sealed record SiiAddressFact(SlaveRef Slave, uint WordAddress, bool IsRead);

/// <summary>SII/EEPROM data returned at register 0x0508, answering the preceding address write.</summary>
public sealed record SiiDataFact(SlaveRef Slave, byte[] Data);

/// <summary>One SyncManager register block (8 bytes at 0x0800 + 8n).</summary>
public sealed record SyncManagerFact(SlaveRef Slave, byte Number, ushort PhysicalStart,
    ushort Length, byte Control, bool Enabled);

/// <summary>One FMMU register block (16 bytes at 0x0600 + 16n).</summary>
public sealed record FmmuFact(SlaveRef Slave, byte Number, uint LogicalStart, ushort Length,
    byte LogicalStartBit, byte LogicalStopBit, ushort PhysicalStart, byte PhysicalStartBit,
    FmmuType Type, bool Enabled);

/// <summary>One CoE SDO value, from a master download or a slave upload response.
/// Only expedited transfers are decoded; segmented transfers are ignored (spec §9).</summary>
public sealed record SdoValueFact(SlaveRef Slave, ushort Index, byte SubIndex, uint Value);

/// <summary>One entry of a PDO mapping object (0x16xx/0x1Axx), decoded from its 32-bit value.</summary>
public sealed record PdoMappingEntry(ushort Index, byte SubIndex, byte BitLength)
{
    public static PdoMappingEntry FromRaw(uint raw) =>
        new((ushort)(raw >> 16), (byte)((raw >> 8) & 0xFF), (byte)(raw & 0xFF));

    /// <summary>ESI writes padding as index 0 with a bit length and no sub-index. Padding
    /// advances the offset but is not a variable.</summary>
    public bool IsPadding => Index == 0;
}

/// <summary>DL status (register 0x0110) as returned by a slave. One 16-bit word describes all
/// four ports, so the fact exposes them decoded rather than making callers re-shift the raw
/// value. Per ETG.1000.4: bits 4-7 physical link per port 0-3, bits 8/10/12/14 loop closed,
/// bits 9/11/13/15 signal detected.</summary>
public sealed record DlStatusFact(SlaveRef Slave, ushort Raw)
{
    public IReadOnlyDictionary<byte, Topology.PortState> Ports { get; } =
        Enumerable.Range(0, 4).ToDictionary(
            port => (byte)port,
            port => new Topology.PortState(
                (byte)port,
                HasLink: (Raw & (1 << (4 + port))) != 0,
                LoopClosed: (Raw & (1 << (8 + port * 2))) != 0,
                SignalDetected: (Raw & (1 << (9 + port * 2))) != 0));
}

/// <summary>ESC error counters read out of the 0x0300-0x030D and 0x0310-0x0313 blocks. Only the
/// registers the read actually covered are present; the rest stay absent rather than zero.</summary>
public sealed record PortCountersFact(SlaveRef Slave,
    IReadOnlyDictionary<byte, Topology.PortCounters> Ports,
    byte? ProcessingUnitErrors, byte? PdiErrors);
