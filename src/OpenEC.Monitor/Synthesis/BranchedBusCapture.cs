using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Synthesis;

/// <summary>A synthetic bringup for a BRANCHED four-slave bus, so the topology reconstruction is
/// testable end to end without hardware. Deliberately separate from
/// <see cref="BringupCapture"/>: that fixture's two-slave line is asserted by a dozen existing
/// tests, and widening it would change what they mean.
///
/// The shape, which exercises every path in the reconstruction:
/// <code>
///   master ── 1001 ── 1002 ── 1003        1001 is a junction: ports 1 and 2 both active
///               └──── 1004                1004 hangs off its port 2
/// </code>
/// Identity is the EL1008 ESI test fixture's, as in <see cref="BringupCapture"/>.</summary>
public static class BranchedBusCapture
{
    private const uint VendorId = 2;
    private const uint ProductCode = 0x03F03052;
    private const uint Revision = 0x00120000;

    private static readonly ushort[] Stations = [1001, 1002, 1003, 1004];

    /// <summary>DL status per station: link plus open loop on each active port.
    /// 1001: ports 0, 1, 2 → bits 4,5,6 = 0x0070. 1002: ports 0, 1 → 0x0030.
    /// 1003 and 1004 end their lines: port 0 only → 0x0010.</summary>
    private static readonly ushort[] DlStatus = [0x0070, 0x0030, 0x0010, 0x0010];

    public static string Write(string path, int cycles = 20)
    {
        PcapFileWriter.Write(path, Frames(cycles));
        return path;
    }

    public static IReadOnlyList<(DateTimeOffset Timestamp, byte[] Frame)> Frames(int cycles = 20)
    {
        var frames = new List<(DateTimeOffset, byte[])>();
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        byte idx = 0;

        void Emit(EtherCatFrameBuilder outbound, EtherCatFrameBuilder returning)
        {
            frames.Add((t, outbound.Build()));
            frames.Add((t.AddMicroseconds(60), returning.Build()));
            t = t.AddMicroseconds(250);
        }

        void EmitRead(ushort station, ushort register, byte[] answer)
        {
            Emit(new EtherCatFrameBuilder()
                    .AddPhysical(EtherCatCommand.Fprd, idx, station, register,
                        new byte[answer.Length], 0),
                new EtherCatFrameBuilder().AsReturning()
                    .AddPhysical(EtherCatCommand.Fprd, idx, station, register, answer, 1));
            idx++;
        }

        // --- INIT: assign station addresses by ring position ---
        for (var position = 0; position < Stations.Length; position++)
        {
            var autoInc = (ushort)(0 - position);
            var payload = BitConverter.GetBytes(Stations[position]);
            Emit(new EtherCatFrameBuilder()
                    .AddPhysical(EtherCatCommand.Apwr, idx, autoInc, 0x0010, payload, 0),
                new EtherCatFrameBuilder().AsReturning()
                    .AddPhysical(EtherCatCommand.Apwr, idx, autoInc, 0x0010, payload, 1));
            idx++;
        }

        // --- INIT: identity out of SII ---
        foreach (var station in Stations)
        {
            foreach (var (word, value) in new (uint, uint)[]
                     { (0x0008, VendorId), (0x000A, ProductCode), (0x000C, Revision), (0x000E, 0) })
            {
                var request = new byte[6];
                BitConverter.GetBytes((ushort)0x0100).CopyTo(request, 0);
                BitConverter.GetBytes(word).CopyTo(request, 2);
                Emit(new EtherCatFrameBuilder()
                        .AddPhysical(EtherCatCommand.Fpwr, idx, station, 0x0502, request, 0),
                    new EtherCatFrameBuilder().AsReturning()
                        .AddPhysical(EtherCatCommand.Fpwr, idx, station, 0x0502, request, 1));
                idx++;
                EmitRead(station, 0x0508, BitConverter.GetBytes(value));
            }
        }

        // --- INIT: DL status and error counters ---
        for (var position = 0; position < Stations.Length; position++)
        {
            EmitRead(Stations[position], 0x0110, BitConverter.GetBytes(DlStatus[position]));
            EmitRead(Stations[position], 0x0300, new byte[14]);
            EmitRead(Stations[position], 0x0310, new byte[4]);
        }

        // --- OP: the broadcast AL status poll, so the capture has cyclic traffic ---
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            frames.Add((t, new EtherCatFrameBuilder()
                .AddPhysical(EtherCatCommand.Brd, idx, 0, 0x0130, new byte[2], 0)
                .Build()));
            frames.Add((t.AddMicroseconds(60), new EtherCatFrameBuilder().AsReturning()
                .AddPhysical(EtherCatCommand.Brd, idx, 0, 0x0130, [0x08, 0x00], 4)
                .Build()));
            t = t.AddMicroseconds(250);
            idx++;
        }

        return frames;
    }
}
