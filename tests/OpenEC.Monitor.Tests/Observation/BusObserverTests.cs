using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Observation;

public class BusObserverTests
{
    private static EniConfiguration Fixture() =>
        EniConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml"));

    private static void Feed(BusObserver observer, DateTimeOffset ts, byte[] raw) =>
        observer.Process(ts, EtherCatFrameParser.Parse(raw));

    private static byte[] MailboxPayload(byte type, byte counter, byte[] body, ushort station = 1004)
    {
        var mailbox = new byte[6 + body.Length];
        BitConverter.GetBytes((ushort)body.Length).CopyTo(mailbox, 0);
        BitConverter.GetBytes(station).CopyTo(mailbox, 2);
        mailbox[5] = (byte)((counter << 4) | type);
        body.CopyTo(mailbox, 6);
        return mailbox;
    }

    private static (byte[] Outbound, byte[] Returning) CyclePair(byte idx,
        byte[] outputs, byte[] inputs, ushort lrwWkc = 6, byte brdState = 0x08)
    {
        var outbound = new EtherCatFrameBuilder()
            .AddDatagram(EtherCatCommand.Lrw, idx, 0x01000000, outputs, 0)
            .AddPhysical(EtherCatCommand.Brd, (byte)(idx + 1), 0, 0x0130, new byte[] { 0, 0 }, 0)
            .Build();
        var returning = new EtherCatFrameBuilder().AsReturning()
            .AddDatagram(EtherCatCommand.Lrw, idx, 0x01000000, inputs, lrwWkc)
            .AddPhysical(EtherCatCommand.Brd, (byte)(idx + 1), 0, 0x0130, new byte[] { brdState, 0 }, 4)
            .Build();
        return (outbound, returning);
    }

    [Fact]
    public void Full_cycles_update_statistics_process_image_and_bus_state()
    {
        var observer = new BusObserver(Fixture());
        var t = DateTimeOffset.UnixEpoch;
        for (byte i = 0; i < 20; i += 2)
        {
            var (outbound, returning) = CyclePair(i,
                outputs: new byte[] { 0x01, 0x00, 0x0F, 0x00 },
                inputs: new byte[] { 0x01, 0x00, 0x37, 0x06 });
            Feed(observer, t.AddMilliseconds(i), outbound);
            Feed(observer, t.AddMilliseconds(i + 0.1), returning);
        }

        Assert.Equal(20, observer.Statistics.EtherCatFrames);
        Assert.Equal(0, observer.Statistics.WkcMismatches);
        Assert.Equal(SlaveAlState.Op, observer.Bus.BusState);

        var sw = observer.ProcessImage.Current["Drive 4 (AX5101).Inputs.Statusword"];
        Assert.Equal((ushort)0x0637, sw.Value);
        Assert.NotNull(sw.Cia402Description); // "Operation enabled ..."
        var cw = observer.ProcessImage.Current["Drive 4 (AX5101).Outputs.Controlword"];
        Assert.Equal((ushort)0x000F, cw.Value);
        Assert.NotNull(observer.Statistics.EstimatedCycleTime);
    }

    [Fact]
    public void Wkc_mismatch_is_counted_and_logged()
    {
        var observer = new BusObserver(Fixture());
        var t = DateTimeOffset.UnixEpoch;
        var good = CyclePair(0, new byte[4], new byte[4]);
        var bad = CyclePair(2, new byte[4], new byte[4], lrwWkc: 5);
        Feed(observer, t, good.Outbound);
        Feed(observer, t.AddMilliseconds(0.1), good.Returning);
        Feed(observer, t.AddMilliseconds(1), bad.Outbound);
        Feed(observer, t.AddMilliseconds(1.1), bad.Returning);

        Assert.Equal(1, observer.Statistics.WkcMismatches);
        Assert.Contains(observer.EventLog, e => e is MonitorEvent.WkcMismatchDetected m && m.Actual == 5);
    }

    [Fact]
    public void Coe_emergency_in_mailbox_read_raises_event()
    {
        var observer = new BusObserver(Fixture());
        var events = new List<MonitorEvent>();
        observer.EventRaised += events.Add;

        // Establish direction context first (one outbound frame with clear MAC bit).
        var (outbound, _) = CyclePair(0, new byte[4], new byte[4]);
        Feed(observer, DateTimeOffset.UnixEpoch, outbound);

        // Slave 1004's ENI mailbox 'Recv' (slave->master, read by the master via FPRD)
        // starts at 4224 (0x1080): returning FPRD with WKC 1 carries the mailbox content.
        var mailbox = MailboxPayload(type: 3, counter: 1,
            new byte[] { 0x00, 0x10, 0x30, 0x81, 0x81, 0, 0, 0, 0, 0 });
        var frame = new EtherCatFrameBuilder().AsReturning()
            .AddPhysical(EtherCatCommand.Fprd, 9, 1004, 4224, mailbox, 1)
            .Build();
        Feed(observer, DateTimeOffset.UnixEpoch.AddMilliseconds(1), frame);

        var emcy = Assert.IsType<MonitorEvent.EmergencyReceived>(
            Assert.Single(events, e => e is MonitorEvent.EmergencyReceived));
        Assert.Equal((ushort)0x8130, emcy.ErrorCode);
        Assert.Equal((ushort)1004, emcy.StationAddress);
    }

    /// <summary>A learned configuration derives each mailbox window independently from SM0 and SM1
    /// (<c>LearnedSlave.MailboxRange</c>), so it can legitimately carry one without the other — unlike
    /// a supplied ENI, which always declares both. The observer used to drop the 0x1000–0x2000
    /// fallback as soon as EITHER window was known and then match only the known one, so a
    /// configuration that knew SM0 but not SM1 stopped recognising the MBoxIn window entirely and
    /// silently swallowed every CoE emergency arriving there. Emergencies were raised with no
    /// configuration at all and with the full one, and lost with the partial one in between: partial
    /// knowledge was strictly worse than none.</summary>
    [Theory]
    [InlineData(null, null)]                 // nothing learned yet — pure fallback
    [InlineData(4096, null)]                 // SM0 seen, SM1 not: the regression case
    [InlineData(null, 4224)]                 // SM1 seen, SM0 not
    [InlineData(4096, 4224)]                 // both seen — the declared-ENI shape
    public void Coe_emergency_is_raised_whatever_share_of_the_mailbox_map_is_known(
        int? mailboxOutStart, int? mailboxInStart)
    {
        var observer = new BusObserver(WithMailbox(mailboxOutStart, mailboxInStart));
        var events = new List<MonitorEvent>();
        observer.EventRaised += events.Add;
        Feed(observer, DateTimeOffset.UnixEpoch, CyclePair(0, new byte[4], new byte[4]).Outbound);

        // An emergency in slave 1001's MBoxIn window (0x1080 = 4224), read by the master via FPRD.
        var mailbox = MailboxPayload(type: 3, counter: 1,
            new byte[] { 0x00, 0x10, 0x30, 0x81, 0x81, 0, 0, 0, 0, 0 }, station: 1001);
        var frame = new EtherCatFrameBuilder().AsReturning()
            .AddPhysical(EtherCatCommand.Fprd, 9, 1001, 4224, mailbox, 1)
            .Build();
        Feed(observer, DateTimeOffset.UnixEpoch.AddMilliseconds(1), frame);

        var emcy = Assert.IsType<MonitorEvent.EmergencyReceived>(
            Assert.Single(events, e => e is MonitorEvent.EmergencyReceived));
        Assert.Equal((ushort)0x8130, emcy.ErrorCode);
        Assert.Equal((ushort)1001, emcy.StationAddress);
    }

    /// <summary>The narrowing the fallback replaces is still worth having once BOTH windows are
    /// known: a datagram inside the generic 0x1000–0x2000 guess but outside every declared window is
    /// not mailbox traffic, and parsing it as such is how a register write becomes a phantom
    /// emergency. Keeping this pinned is what stops the fix above turning into "always fall back".</summary>
    [Fact]
    public void A_fully_known_mailbox_map_still_excludes_addresses_outside_its_windows()
    {
        var observer = new BusObserver(WithMailbox(4096, 4224));
        var events = new List<MonitorEvent>();
        observer.EventRaised += events.Add;
        Feed(observer, DateTimeOffset.UnixEpoch, CyclePair(0, new byte[4], new byte[4]).Outbound);

        // 0x1800 = 6144: inside the fallback range, outside both declared 128-byte windows.
        var mailbox = MailboxPayload(type: 3, counter: 1,
            new byte[] { 0x00, 0x10, 0x30, 0x81, 0x81, 0, 0, 0, 0, 0 }, station: 1001);
        var frame = new EtherCatFrameBuilder().AsReturning()
            .AddPhysical(EtherCatCommand.Fprd, 9, 1001, 6144, mailbox, 1)
            .Build();
        Feed(observer, DateTimeOffset.UnixEpoch.AddMilliseconds(1), frame);

        Assert.DoesNotContain(events, e => e is MonitorEvent.EmergencyReceived);
    }

    /// <summary>One slave at 1001 with whichever mailbox windows the caller says are known, plus the
    /// cyclic command the CyclePair fixture needs so direction classification behaves as elsewhere.</summary>
    private static EniConfiguration WithMailbox(int? mailboxOutStart, int? mailboxInStart) => new()
    {
        Slaves =
        [
            new EniSlave("Slave 1001", 1001, 0x0000, 2, 0x03F03052, 0x00120000,
                mailboxOutStart is { } o ? new MailboxRange((ushort)o, 128) : null,
                mailboxInStart is { } i ? new MailboxRange((ushort)i, 128) : null),
        ],
        CyclicCommands = [new EniCyclicCommand(EtherCatCommand.Lrw, 0x01000000, 4, 6, 0, 0)],
        Variables = [],
    };

    [Fact]
    public void Soe_error_response_in_mailbox_read_raises_event()
    {
        var observer = new BusObserver(Fixture());
        var events = new List<MonitorEvent>();
        observer.EventRaised += events.Add;

        var (outbound, _) = CyclePair(0, new byte[4], new byte[4]);
        Feed(observer, DateTimeOffset.UnixEpoch, outbound);

        // SoE read response for S-0-0017 with the error bit set, error code 0x7009.
        var mailbox = MailboxPayload(type: 5, counter: 1,
            new byte[] { 0x12, 0x40, 0x11, 0x00, 0x09, 0x70 });
        var frame = new EtherCatFrameBuilder().AsReturning()
            .AddPhysical(EtherCatCommand.Fprd, 9, 1004, 4224, mailbox, 1)
            .Build();
        Feed(observer, DateTimeOffset.UnixEpoch.AddMilliseconds(1), frame);

        var soe = Assert.IsType<MonitorEvent.SoeErrorReceived>(
            Assert.Single(events, e => e is MonitorEvent.SoeErrorReceived));
        Assert.Equal((ushort)1004, soe.StationAddress);
        Assert.Equal(SoeOpCode.ReadResponse, soe.OpCode);
        Assert.Equal("S-0-0017", soe.IdnLabel);
        Assert.Equal((ushort)0x7009, soe.ErrorCode);
    }

    [Fact]
    public void Counts_frames_per_direction_and_idx_pool()
    {
        var observer = new BusObserver(Fixture());
        var t = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < 3; i++)
        {
            var (outbound, returning) = CyclePair(0, new byte[4], new byte[4]);
            Feed(observer, t.AddMilliseconds(i), outbound);
            Feed(observer, t.AddMilliseconds(i + 0.1), returning);
        }

        Assert.Equal(3, observer.Statistics.OutboundFrames);
        Assert.Equal(3, observer.Statistics.ReturningFrames);
        Assert.Equal(3, observer.Statistics.OutboundCyclicFrames); // Lrw idx 0 = cyclic pool
        Assert.Equal(0, observer.Statistics.OutboundQueuedFrames);
    }

    [Fact]
    public void Complete_cycles_count_no_ring_loss()
    {
        // TwinCAT-style cyclic traffic reuses a fixed idx every cycle.
        var observer = new BusObserver(Fixture());
        var t = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < 3; i++)
        {
            var (outbound, returning) = CyclePair(0, new byte[4], new byte[4]);
            Feed(observer, t.AddMilliseconds(i), outbound);
            Feed(observer, t.AddMilliseconds(i + 0.1), returning);
        }

        Assert.Equal(0, observer.Statistics.RingLostFrames);
    }

    [Fact]
    public void Outbound_frame_without_return_counts_as_ring_loss_on_reuse()
    {
        // A frame is "ring lost" when the master re-sends its key while the previous
        // send is still unanswered — the passive equivalent of TwinCAT's Lost Frames.
        var observer = new BusObserver(Fixture());
        var t = DateTimeOffset.UnixEpoch;
        var (out1, ret1) = CyclePair(0, new byte[4], new byte[4]);
        Feed(observer, t, out1);
        Feed(observer, t.AddMilliseconds(0.1), ret1);

        var (out2, _) = CyclePair(0, new byte[4], new byte[4]);
        Feed(observer, t.AddMilliseconds(1), out2); // return of cycle 2 never arrives

        var (out3, ret3) = CyclePair(0, new byte[4], new byte[4]);
        Feed(observer, t.AddMilliseconds(2), out3);
        Feed(observer, t.AddMilliseconds(2.1), ret3);

        Assert.Equal(1, observer.Statistics.RingLostFrames);
    }

    [Fact]
    public void Malformed_and_foreign_frames_only_touch_statistics()
    {
        var observer = new BusObserver();
        observer.Process(DateTimeOffset.UnixEpoch, new FrameDecodeResult.Malformed("x"));
        observer.Process(DateTimeOffset.UnixEpoch, new FrameDecodeResult.NotEtherCat(0x0800));

        Assert.Equal(1, observer.Statistics.MalformedFrames);
        Assert.Equal(1, observer.Statistics.NonEtherCatFrames);
        Assert.Empty(observer.EventLog);
    }

    // Regression for the live-dashboard crash: BuildDashboard (a render task on its own thread)
    // used to enumerate Bus.Slaves and EventLog directly while Process (the pump task) mutated
    // them concurrently, which threw InvalidOperationException ("Collection was modified") and
    // crashed the whole `live` command with exit 255. SnapshotSlaves/SnapshotEvents must be safe
    // to call from a reader thread throughout a sustained burst of Process calls - and, per the
    // review that caught EnrichNamesAsync as a second unguarded writer, SetResolvedDeviceName
    // must be safe to interleave with Process from a separate thread too (live --eni --esi-dir
    // runs name enrichment on the pump task while the render task is already reading snapshots).
    [Fact]
    public async Task Concurrent_process_and_setresolveddevicename_and_snapshot_reads_do_not_throw()
    {
        var observer = new BusObserver();
        const int frameCount = 5000;
        var start = DateTimeOffset.UnixEpoch;

        // DirectionTracker classifies frames from the source-MAC "locally administered" bit once
        // it has seen both values; until then it falls back to pairing (idx, cmd, address), which
        // would misclassify our all-AsReturning() burst below (each address is used exactly once).
        // One plain outbound frame establishes the "bit clear" baseline first.
        var warmup = new EtherCatFrameBuilder()
            .AddPhysical(EtherCatCommand.Brd, 0, 0, 0x0130, new byte[] { 0, 0 }, 0)
            .Build();
        observer.Process(start, EtherCatFrameParser.Parse(warmup));

        var pump = Task.Run(() =>
        {
            for (var i = 0; i < frameCount; i++)
            {
                // A distinct station address per frame keeps BusModel.GetOrAdd inserting new
                // dictionary entries for the whole run, maximizing overlap with the reader.
                var address = (ushort)(1000 + i);
                var stateNibble = (byte)(1 << (i % 4)); // cycles Init/PreOp/SafeOp/Op-shaped nibbles
                var frame = new EtherCatFrameBuilder().AsReturning()
                    .AddPhysical(EtherCatCommand.Fprd, (byte)(i % 256), address, 0x0130,
                        new byte[] { stateNibble, 0x00 }, 1)
                    .Build();
                observer.Process(start.AddMicroseconds(i), EtherCatFrameParser.Parse(frame));
            }
        });

        // Mirrors EtherCatMonitor.EnrichNamesAsync: resolves a device name per slave address,
        // overlapping the same address range Process is concurrently inserting via GetOrAdd.
        var enrich = Task.Run(() =>
        {
            for (var i = 0; i < frameCount; i++)
            {
                var address = (ushort)(1000 + i);
                observer.SetResolvedDeviceName(address, $"Resolved-{address}");
            }
        });

        Exception? readerException = null;
        using var readerCts = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            try
            {
                while (!readerCts.IsCancellationRequested)
                {
                    _ = observer.SnapshotSlaves().Count;
                    _ = observer.SnapshotEvents(8).Count;
                    // A brief yield keeps this a genuine race against the writer threads without
                    // the growing-snapshot copy dominating wall-clock time (SnapshotSlaves is
                    // O(current slave count) and would otherwise be called hundreds of thousands
                    // of times over the run).
                    Thread.Sleep(0);
                }
            }
            catch (Exception ex)
            {
                readerException = ex;
            }
        });

        await Task.WhenAll(pump, enrich);
        readerCts.Cancel();
        await reader;

        Assert.Null(readerException);
        Assert.Equal(frameCount + 1, observer.Statistics.EtherCatFrames); // +1 for the warmup frame
        var finalSlaves = observer.SnapshotSlaves();
        Assert.Equal(frameCount, finalSlaves.Count);
        Assert.All(finalSlaves, s => Assert.False(string.IsNullOrEmpty(s.ResolvedDeviceName)));
    }
}
