using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Learning;

/// <summary>Pure decoders for the CoE traffic that configures process data: PDO assignment
/// (0x1C1x), PDO mapping (0x16xx/0x1Axx) and the identity object (0x1018).
/// Only expedited SDO transfers are decoded — every value learning mode needs fits in four
/// bytes, and segmented transfers are out of scope per the design spec.</summary>
public static class MailboxDecoders
{
    private const byte DownloadRequest = 1;   // client command specifier: initiate download
    private const byte UploadResponse = 2;    // server command specifier: initiate upload

    /// <summary>A master-to-slave SDO write. Carries PDO assignment and mapping.</summary>
    public static SdoValueFact? TrySdoDownload(EtherCatDatagram d, FrameDirection dir)
    {
        if (dir != FrameDirection.Outbound) return null;
        if (!RegisterDecoders.IsWrite(d.Command)) return null;
        return TryValue(d, dir, CoeService.SdoRequest, DownloadRequest);
    }

    /// <summary>A slave-to-master SDO read answer. Carries identity when the master polls
    /// 0x1018 instead of reading SII.</summary>
    public static SdoValueFact? TrySdoUploadResponse(EtherCatDatagram d, FrameDirection dir)
    {
        if (dir != FrameDirection.Returning) return null;
        if (!RegisterDecoders.IsRead(d.Command) || d.WorkingCounter == 0) return null;
        return TryValue(d, dir, CoeService.SdoResponse, UploadResponse);
    }

    private static SdoValueFact? TryValue(EtherCatDatagram d, FrameDirection dir,
        CoeService service, byte specifier)
    {
        var mailbox = MailboxParser.TryParse(d.Payload);
        if (mailbox?.Coe is not { } coe || coe.Service != service) return null;
        if (coe.Sdo is not { Expedited: true, SizeIndicated: true } sdo) return null;
        // Load-bearing on real traffic: an SDO upload REQUEST also carries service SdoRequest
        // (with ccs 2), which is how masters read an object such as 0x1018. Without this check a
        // read request would be recorded as a written value.
        if ((sdo.CommandSpecifier >> 5) != specifier) return null;
        if (TryExpeditedValue(sdo) is not { } value) return null;
        return new SdoValueFact(SlaveRef.From(d, dir), sdo.Index, sdo.SubIndex, value);
    }

    /// <summary>Reads the expedited payload as a little-endian unsigned value. Bits 2-3 of
    /// the command specifier count the UNUSED bytes of the four-byte field. Returns null when
    /// the mailbox body carries fewer bytes than the specifier declares — a truncated capture
    /// must be rejected, not zero-filled into a plausible-looking value, since a fabricated
    /// `0x1C13:00 = 0` would read downstream as "no PDOs assigned".</summary>
    private static uint? TryExpeditedValue(SdoTransfer sdo)
    {
        var span = sdo.Data.Span;
        var used = 4 - ((sdo.CommandSpecifier >> 2) & 0x03);
        if (span.Length < used) return null;
        uint value = 0;
        for (var i = 0; i < used; i++)
            value |= (uint)span[i] << (8 * i);
        return value;
    }
}
