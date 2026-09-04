using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Synthesis;

/// <summary>Composes valid EtherCAT wire images — for tests, demos, and generated sample captures.</summary>
public sealed class EtherCatFrameBuilder
{
    private sealed record PendingDatagram(EtherCatCommand Command, byte Index, uint Address,
        byte[] Payload, ushort Wkc, ushort Irq);

    private readonly List<PendingDatagram> _datagrams = new();
    private readonly byte[] _dst = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
    private readonly byte[] _src = { 0x00, 0x01, 0x05, 0x10, 0x00, 0x01 };

    public EtherCatFrameBuilder AsReturning()
    {
        _src[0] |= 0x02;
        return this;
    }

    public EtherCatFrameBuilder AddDatagram(EtherCatCommand cmd, byte idx, uint address,
        byte[] payload, ushort wkc, ushort irq = 0)
    {
        if (payload.Length > 0x07FF)
            throw new ArgumentException($"payload length {payload.Length} exceeds 11-bit limit (2047)", nameof(payload));
        _datagrams.Add(new PendingDatagram(cmd, idx, address, payload, wkc, irq));
        return this;
    }

    public EtherCatFrameBuilder AddPhysical(EtherCatCommand cmd, byte idx, ushort adp, ushort ado,
        byte[] payload, ushort wkc)
        => AddDatagram(cmd, idx, ((uint)ado << 16) | adp, payload, wkc);

    public byte[] Build()
    {
        if (_datagrams.Count == 0)
            throw new InvalidOperationException("at least one datagram required");
        var area = new List<byte>();
        for (var i = 0; i < _datagrams.Count; i++)
        {
            var d = _datagrams[i];
            var lenField = (ushort)d.Payload.Length;
            if (i < _datagrams.Count - 1) lenField |= 0x8000;
            area.Add((byte)d.Command);
            area.Add(d.Index);
            area.AddRange(BitConverter.GetBytes(d.Address));
            area.AddRange(BitConverter.GetBytes(lenField));
            area.AddRange(BitConverter.GetBytes(d.Irq));
            area.AddRange(d.Payload);
            area.AddRange(BitConverter.GetBytes(d.Wkc));
        }
        if (area.Count > 0x07FF)
            throw new InvalidOperationException($"datagram area size {area.Count} exceeds 11-bit limit (2047)");
        var frame = new List<byte>();
        frame.AddRange(_dst);
        frame.AddRange(_src);
        frame.Add(0x88); frame.Add(0xA4);
        var header = (ushort)(area.Count | (1 << 12));
        frame.Add((byte)(header & 0xFF));
        frame.Add((byte)(header >> 8));
        frame.AddRange(area);
        return frame.ToArray();
    }
}
