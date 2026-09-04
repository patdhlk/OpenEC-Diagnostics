using System.Buffers.Binary;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Synthesis;

/// <summary>Generates a synthetic INIT→OP bringup for a two-slave bus, so learning mode is
/// testable without hardware. Bringup happens once on a real bus and is awkward to capture
/// on demand, which makes this the load-bearing test asset for the whole feature.
///
/// The bus is two 8-bit digital input terminals sharing the identity of the EL1008 ESI test
/// fixture (vendor 2, product 0x03F03052, revision 0x00120000). Each contributes one byte of
/// inputs, mapped through FMMU 0 into logical address 0x00010000.</summary>
public static class BringupCapture
{
    private const uint VendorId = 2;
    private const uint ProductCode = 0x03F03052;
    private const uint Revision = 0x00120000;   // must match EL1008.xml's RevisionNo
    private const uint SerialNumber = 0;

    private static readonly ushort[] Stations = [1001, 1002];

    /// <summary>How many slaves sit on the ring. Every auto-increment and broadcast datagram has
    /// its ADP incremented once per slave it passes, so a returning copy's ADP is offset by exactly
    /// this — the single fact the old fixture left out, and the reason a decoder that read the
    /// returning ADP as a ring position agreed with the fixture and disagreed with hardware.</summary>
    private static ushort RingLength => (ushort)Stations.Length;

    /// <summary>DL status per ring position: the first slave forwards on port 1, the last is a line
    /// end. Matches what the reconstruction needs to draw a two-device line.</summary>
    private static ushort DlStatusAt(int position) => position == 0 ? (ushort)0x0030 : (ushort)0x0010;

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

        // --- The master counts the ring before it does anything else. Every slave increments a
        // broadcast's ADP as it passes, so the returning copy's ADP is the slave count. This is
        // both how a real master sizes the ring and how a passive observer must size it, because
        // it is the only thing that makes a returning auto-increment ADP interpretable. ---
        for (var poll = 0; poll < 2; poll++)
        {
            Emit(new EtherCatFrameBuilder()
                    .AddPhysical(EtherCatCommand.Brd, idx, 0, 0x0130, new byte[2], 0),
                new EtherCatFrameBuilder().AsReturning()
                    .AddPhysical(EtherCatCommand.Brd, idx, RingLength, 0x0130, [0x01, 0x00],
                        RingLength));
            idx++;
        }

        // --- INIT: broadcast-clear every station address, so the scan starts from a known state.
        // A real master does this before assigning; it is why address zero briefly names every
        // slave on the bus and must never be treated as a device. ---
        Emit(new EtherCatFrameBuilder()
                .AddPhysical(EtherCatCommand.Bwr, idx, 0, 0x0010, new byte[2], 0),
            new EtherCatFrameBuilder().AsReturning()
                .AddPhysical(EtherCatCommand.Bwr, idx, RingLength, 0x0010, new byte[2], RingLength));
        idx++;

        // --- INIT: the scan reads DL status and identity by AUTO-INCREMENT, because configured
        // station addresses do not exist yet — they are assigned at the end of this sequence, from
        // what the scan finds. Every fact below therefore arrives before any slave has a name. ---
        for (var position = 0; position < Stations.Length; position++)
            EmitAutoRead(position, 0x0110, BitConverter.GetBytes(DlStatusAt(position)));

        for (var position = 0; position < Stations.Length; position++)
        {
            foreach (var (word, value) in new (uint, uint)[]
                     {
                         (0x0008, VendorId), (0x000A, ProductCode),
                         (0x000C, Revision), (0x000E, SerialNumber),
                     })
            {
                var request = new byte[6];
                BitConverter.GetBytes((ushort)0x0100).CopyTo(request, 0);   // read command
                BitConverter.GetBytes(word).CopyTo(request, 2);
                EmitAutoWrite(position, 0x0502, request);
                EmitAutoRead(position, 0x0508, BitConverter.GetBytes(value));
            }
        }

        // --- INIT: only now are configured station addresses assigned, naming what the scan
        // already described. An observer that dropped the scan has nothing left to attach. ---
        for (var position = 0; position < Stations.Length; position++)
            EmitAutoWrite(position, 0x0010, BitConverter.GetBytes(Stations[position]));

        // --- INIT: error counters, polled by configured address once the slaves have one ---
        foreach (var station in Stations)
        {
            EmitRead(station, 0x0300, new byte[14]);   // 0x0300-0x030D, all counters clear
            EmitRead(station, 0x0310, new byte[4]);    // lost link per port
        }

        // --- INIT→PREOP: mailbox SyncManagers (SM0 out, SM1 in) ---
        foreach (var station in Stations)
        {
            var block = new byte[16];
            WriteSyncManager(block.AsSpan(0, 8), start: 0x1000, length: 128, control: 0x26);
            WriteSyncManager(block.AsSpan(8, 8), start: 0x1080, length: 128, control: 0x22);
            EmitWrite(station, 0x0800, block);
        }

        // --- PREOP→SAFEOP: PDO assignment and mapping over CoE ---
        foreach (var station in Stations)
        {
            EmitSdo(station, 0x1C13, 0, 0);
            EmitSdo(station, 0x1A00, 0, 0);
            for (byte bit = 1; bit <= 8; bit++)
                EmitSdo(station, 0x1A00, bit, (uint)(0x60000000 | ((uint)bit << 8) | 0x01));
            EmitSdo(station, 0x1A00, 0, 8);
            EmitSdo(station, 0x1C13, 1, 0x1A00);
            EmitSdo(station, 0x1C13, 0, 1);
        }

        // --- PREOP→SAFEOP: process-data SyncManager and FMMU ---
        for (var position = 0; position < Stations.Length; position++)
        {
            var block = new byte[8];
            WriteSyncManager(block, start: 0x1100, length: 1, control: 0x00);
            EmitWrite(Stations[position], 0x0818, block);   // SM3 = 0x0800 + 3*8

            var fmmu = new byte[16];
            BitConverter.GetBytes(0x00010000u + (uint)position).CopyTo(fmmu, 0);
            BitConverter.GetBytes((ushort)1).CopyTo(fmmu, 4);
            fmmu[6] = 0;                                     // logical start bit
            fmmu[7] = 7;                                     // logical stop bit
            BitConverter.GetBytes((ushort)0x1100).CopyTo(fmmu, 8);
            fmmu[10] = 0;                                    // physical start bit
            fmmu[11] = (byte)1;                              // inputs
            fmmu[12] = 1;                                    // activate
            EmitWrite(Stations[position], 0x0600, fmmu);
        }

        // --- SAFEOP→OP: DC drift compensation. The master polls each slave's System Time
        // Difference (0x092C) to confirm the distributed clocks are locked before cyclic
        // operation starts. Emitted here rather than in the cyclic loop so the trailing frames
        // stay the pure cyclic process-data stream other fixtures rely on.
        foreach (var station in Stations)
        {
            // Locked DC: +5 µs, well within the 10 µs sync tolerance.
            var dcBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(dcBytes, 5000);
            EmitRead(station, 0x092C, dcBytes);
        }

        // --- OP: cyclic input read plus the broadcast AL status poll ---
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var inputs = new byte[] { (byte)(cycle & 0xFF), (byte)(~cycle & 0xFF) };
            frames.Add((t, new EtherCatFrameBuilder()
                .AddDatagram(EtherCatCommand.Lrd, idx, 0x00010000, new byte[2], 0)
                .AddPhysical(EtherCatCommand.Brd, (byte)(idx + 1), 0, 0x0130, new byte[2], 0)
                .Build()));
            frames.Add((t.AddMicroseconds(60), new EtherCatFrameBuilder().AsReturning()
                .AddDatagram(EtherCatCommand.Lrd, idx, 0x00010000, inputs, 2)
                .AddPhysical(EtherCatCommand.Brd, (byte)(idx + 1), RingLength, 0x0130, [0x08, 0x00], 2)
                .Build()));
            idx += 2;
            t = t.AddMilliseconds(1);
        }

        return frames;

        void EmitWrite(ushort station, ushort register, byte[] payload)
        {
            Emit(new EtherCatFrameBuilder()
                    .AddPhysical(EtherCatCommand.Fpwr, idx, station, register, payload, 0),
                new EtherCatFrameBuilder().AsReturning()
                    .AddPhysical(EtherCatCommand.Fpwr, idx, station, register, payload, 1));
            idx++;
        }

        void EmitSdo(ushort station, ushort index, byte subIndex, uint value)
        {
            var mailbox = CoeDownload(station, index, subIndex, value);
            Emit(new EtherCatFrameBuilder()
                    .AddPhysical(EtherCatCommand.Fpwr, idx, station, 0x1000, mailbox, 0),
                new EtherCatFrameBuilder().AsReturning()
                    .AddPhysical(EtherCatCommand.Fpwr, idx, station, 0x1000, mailbox, 1));
            idx++;
        }

        // Auto-increment addressing. The outbound ADP counts down from zero; the returning copy
        // carries that ADP plus RingLength, because every slave on the ring incremented it.
        void EmitAutoRead(int position, ushort register, byte[] answer)
        {
            var outbound = (ushort)(0 - position);
            Emit(new EtherCatFrameBuilder()
                    .AddPhysical(EtherCatCommand.Aprd, idx, outbound, register,
                        new byte[answer.Length], 0),
                new EtherCatFrameBuilder().AsReturning()
                    .AddPhysical(EtherCatCommand.Aprd, idx, (ushort)(outbound + RingLength),
                        register, answer, 1));
            idx++;
        }

        void EmitAutoWrite(int position, ushort register, byte[] payload)
        {
            var outbound = (ushort)(0 - position);
            Emit(new EtherCatFrameBuilder()
                    .AddPhysical(EtherCatCommand.Apwr, idx, outbound, register, payload, 0),
                new EtherCatFrameBuilder().AsReturning()
                    .AddPhysical(EtherCatCommand.Apwr, idx, (ushort)(outbound + RingLength),
                        register, payload, 1));
            idx++;
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
    }

    private static void WriteSyncManager(Span<byte> block, ushort start, ushort length, byte control)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(block, start);
        BinaryPrimitives.WriteUInt16LittleEndian(block[2..], length);
        block[4] = control;
        block[6] = 0x01;    // activate
    }

    /// <summary>An expedited, size-indicated CoE SDO download wrapped in a mailbox header.</summary>
    private static byte[] CoeDownload(ushort station, ushort index, byte subIndex, uint value)
    {
        var body = new byte[10];
        BitConverter.GetBytes((ushort)((ushort)CoeService.SdoRequest << 12)).CopyTo(body, 0);
        body[2] = 0x23;     // ccs 1, expedited, size indicated, 4 bytes used
        BitConverter.GetBytes(index).CopyTo(body, 3);
        body[5] = subIndex;
        BitConverter.GetBytes(value).CopyTo(body, 6);

        var mailbox = new byte[6 + body.Length];
        BitConverter.GetBytes((ushort)body.Length).CopyTo(mailbox, 0);
        BitConverter.GetBytes(station).CopyTo(mailbox, 2);
        mailbox[5] = (byte)MailboxType.Coe;
        body.CopyTo(mailbox, 6);
        return mailbox;
    }
}
