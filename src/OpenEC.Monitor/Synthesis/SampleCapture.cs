using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Synthesis;

/// <summary>Generates a demo capture so the tooling can be exercised without hardware.</summary>
public static class SampleCapture
{
    public static string WriteDemo(string path, int cycles = 50)
    {
        var frames = new List<(DateTimeOffset Timestamp, byte[] Frame)>();
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var idx = (byte)((cycle * 2) % 256);
            ushort wkc = cycle == cycles / 2 ? (ushort)5 : (ushort)6;
            frames.Add((t, new EtherCatFrameBuilder()
                .AddDatagram(EtherCatCommand.Lrw, idx, 0x01000000, new byte[] { 0x01, 0x00, 0x0F, 0x00 }, 0)
                .AddPhysical(EtherCatCommand.Brd, (byte)(idx + 1), 0, 0x0130, new byte[] { 0, 0 }, 0)
                .Build()));
            frames.Add((t.AddMicroseconds(120), new EtherCatFrameBuilder().AsReturning()
                .AddDatagram(EtherCatCommand.Lrw, idx, 0x01000000, new byte[] { 0x01, 0x00, 0x37, 0x06 }, wkc)
                .AddPhysical(EtherCatCommand.Brd, (byte)(idx + 1), 0, 0x0130, new byte[] { 0x08, 0x00 }, 4)
                .Build()));
            if (cycle == cycles / 3)
                frames.Add((t.AddMicroseconds(200), new EtherCatFrameBuilder().AsReturning()
                    .AddPhysical(EtherCatCommand.Fprd, 200, 1004, 0x0130, new byte[] { 0x14, 0x00 }, 1)
                    .Build()));
            if (cycle == 2 * cycles / 3)
            {
                var body = new byte[] { 0x00, 0x10, 0x30, 0x81, 0x81, 0, 0, 0, 0, 0 };
                var mailbox = new byte[6 + body.Length];
                BitConverter.GetBytes((ushort)body.Length).CopyTo(mailbox, 0);
                BitConverter.GetBytes((ushort)1004).CopyTo(mailbox, 2);
                mailbox[5] = 0x13;
                body.CopyTo(mailbox, 6);
                frames.Add((t.AddMicroseconds(250), new EtherCatFrameBuilder().AsReturning()
                    .AddPhysical(EtherCatCommand.Fprd, 201, 1004, 0x1080, mailbox, 1)
                    .Build()));
            }
            if (cycle == 5 * cycles / 6)
            {
                // SoE read response for S-0-0017 with the error bit set, code 0x7009.
                var body = new byte[] { 0x12, 0x40, 0x11, 0x00, 0x09, 0x70 };
                var mailbox = new byte[6 + body.Length];
                BitConverter.GetBytes((ushort)body.Length).CopyTo(mailbox, 0);
                BitConverter.GetBytes((ushort)1004).CopyTo(mailbox, 2);
                mailbox[5] = 0x25;
                body.CopyTo(mailbox, 6);
                frames.Add((t.AddMicroseconds(250), new EtherCatFrameBuilder().AsReturning()
                    .AddPhysical(EtherCatCommand.Fprd, 202, 1004, 0x1080, mailbox, 1)
                    .Build()));
            }
            t = t.AddMilliseconds(1);
        }
        PcapFileWriter.Write(path, frames);
        return path;
    }
}
