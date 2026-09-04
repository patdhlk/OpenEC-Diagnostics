using System.Buffers.Binary;

namespace OpenEC.Monitor.Eni;

public static class ProcessValueDecoder
{
    /// <summary>Decodes an IEC 61131 typed value out of a payload at a bit offset.
    /// Multi-byte types must be byte-aligned; otherwise (or for unknown types) the raw
    /// bytes are returned as a lowercase hex string.</summary>
    public static object Decode(string dataType, int bitSize, ReadOnlySpan<byte> payload, int bitOffset)
    {
        var type = dataType.ToUpperInvariant();
        if (type is "BOOL" or "BIT")
            return ((payload[bitOffset / 8] >> (bitOffset % 8)) & 1) == 1;
        if (bitOffset % 8 != 0)
            return Hex(payload, bitOffset, bitSize);
        var b = payload[(bitOffset / 8)..];
        return type switch
        {
            "BYTE" or "USINT" => b[0],
            "SINT" => (sbyte)b[0],
            "UINT" or "WORD" => BinaryPrimitives.ReadUInt16LittleEndian(b),
            "INT" => BinaryPrimitives.ReadInt16LittleEndian(b),
            "UDINT" or "DWORD" => BinaryPrimitives.ReadUInt32LittleEndian(b),
            "DINT" => BinaryPrimitives.ReadInt32LittleEndian(b),
            "ULINT" or "LWORD" => BinaryPrimitives.ReadUInt64LittleEndian(b),
            "LINT" => BinaryPrimitives.ReadInt64LittleEndian(b),
            "REAL" => BinaryPrimitives.ReadSingleLittleEndian(b),
            "LREAL" => BinaryPrimitives.ReadDoubleLittleEndian(b),
            _ => Hex(payload, bitOffset, bitSize),
        };
    }

    private static string Hex(ReadOnlySpan<byte> payload, int bitOffset, int bitSize)
    {
        var byteStart = bitOffset / 8;
        var byteCount = Math.Max(1, (bitSize + 7) / 8);
        var end = Math.Min(payload.Length, byteStart + byteCount);
        return Convert.ToHexString(payload[byteStart..end]).ToLowerInvariant();
    }
}
