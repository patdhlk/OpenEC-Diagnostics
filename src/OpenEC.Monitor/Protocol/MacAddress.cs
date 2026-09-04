namespace OpenEC.Monitor.Protocol;

public readonly record struct MacAddress(ulong Value)
{
    public static MacAddress FromBytes(ReadOnlySpan<byte> bytes)
    {
        ulong v = 0;
        for (var i = 0; i < 6; i++) v = (v << 8) | bytes[i];
        return new MacAddress(v);
    }

    /// <summary>Bit 0x02 of the first octet — set by EtherCAT slaves on frames returning to the master.</summary>
    public bool IsLocallyAdministered => ((Value >> 40) & 0x02) != 0;

    public override string ToString()
    {
        var v = Value;
        return string.Join(":",
            Enumerable.Range(0, 6).Select(i => ((v >> (8 * (5 - i))) & 0xFF).ToString("x2")));
    }
}
