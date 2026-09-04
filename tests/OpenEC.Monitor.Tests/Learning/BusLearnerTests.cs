using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Protocol;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Monitor.Tests.Learning;

public class BusLearnerTests
{
    private static BusLearner Learn(string? esiDirectory = null)
    {
        var learner = new BusLearner(esiDirectory);
        foreach (var (timestamp, frame) in BringupCapture.Frames(cycles: 5))
            learner.Observe(timestamp, EtherCatFrameParser.Parse(frame));
        return learner;
    }

    [Fact]
    public void Observing_a_bringup_produces_a_configuration()
    {
        var learner = Learn();

        Assert.NotNull(learner.Current);
        Assert.Equal(2, learner.Current!.Configuration.Slaves.Count);
        Assert.Equal(16, learner.Current.Configuration.Variables.Count);
    }

    [Fact]
    public void Configuration_revision_increments_only_when_the_picture_changes()
    {
        var learner = Learn();
        var revision = learner.Current!.Revision;

        // Replaying identical cyclic traffic must not churn the revision.
        foreach (var (timestamp, frame) in BringupCapture.Frames(cycles: 5).TakeLast(4))
            learner.Observe(timestamp, EtherCatFrameParser.Parse(frame));

        Assert.Equal(revision, learner.Current!.Revision);
    }

    [Fact]
    public void Subscribers_are_notified_when_a_revision_lands()
    {
        var learner = new BusLearner();
        var seen = new List<int>();
        learner.ConfigurationLearned += c => seen.Add(c.Revision);

        foreach (var (timestamp, frame) in BringupCapture.Frames(cycles: 5))
            learner.Observe(timestamp, EtherCatFrameParser.Parse(frame));

        Assert.NotEmpty(seen);
        Assert.Equal(seen.OrderBy(r => r), seen);
    }

    /// <summary>The fingerprint digested identity, cyclic commands and variables but not the mailbox
    /// windows, so "SM1 just became known" changed nothing it looked at and no revision was published.
    /// The observer's mailbox map is rebound only by a republish, so the second half of the map
    /// reached it only if some unrelated change happened to carry it — and until then every CoE
    /// emergency in that window was dropped (see BusObserverTests' mailbox theory). A learned fact a
    /// consumer would act on has to move the fingerprint.</summary>
    [Fact]
    public void Learning_the_second_mailbox_window_publishes_a_revision()
    {
        var learner = new BusLearner();
        Observe(learner, SyncManagerWrite(number: 0, physicalStart: 0x1000));
        var afterSm0 = learner.Current!;
        Assert.NotNull(afterSm0.Configuration.Slaves.Single().MailboxOut);
        Assert.Null(afterSm0.Configuration.Slaves.Single().MailboxIn);

        Observe(learner, SyncManagerWrite(number: 1, physicalStart: 0x1080));

        Assert.NotEqual(afterSm0.Revision, learner.Current!.Revision);
        var slave = learner.Current!.Configuration.Slaves.Single();
        Assert.Equal(new MailboxRange(0x1000, 128), slave.MailboxOut);
        Assert.Equal(new MailboxRange(0x1080, 128), slave.MailboxIn);
    }

    private static void Observe(BusLearner learner, byte[] frame) =>
        learner.Observe(DateTimeOffset.UnixEpoch, EtherCatFrameParser.Parse(frame));

    /// <summary>An outbound FPWR configuring one SyncManager on station 1001: physical start (2),
    /// length (2), control, status, activate (bit 0 enables), PDI control.</summary>
    private static byte[] SyncManagerWrite(byte number, ushort physicalStart)
    {
        var block = new byte[8];
        BitConverter.GetBytes(physicalStart).CopyTo(block, 0);
        BitConverter.GetBytes((ushort)128).CopyTo(block, 2);
        block[6] = 0x01; // activate
        return new EtherCatFrameBuilder()
            .AddPhysical(EtherCatCommand.Fpwr, number, 1001,
                (ushort)(RegisterDecoders.SyncManagerBase + 8 * number), block, 0)
            .Build();
    }

    [Fact]
    public void Malformed_and_non_ethercat_frames_are_ignored()
    {
        var learner = new BusLearner();

        learner.Observe(DateTimeOffset.UnixEpoch, new FrameDecodeResult.NotEtherCat(0x0800));
        learner.Observe(DateTimeOffset.UnixEpoch, new FrameDecodeResult.Malformed("bad"));

        Assert.Null(learner.Current);
    }

    /// <summary>Resolution folds the ESI device name into the slave name rather than replacing it:
    /// the device name is a TYPE name every identical terminal shares, so the station address has
    /// to stay to keep the name — and the process variables qualified by it — unique.</summary>
    [Fact]
    public async Task Esi_resolution_folds_the_device_name_into_the_slave_name()
    {
        var learner = Learn(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Esi"));
        Assert.Equal("Slave 1001", learner.Current!.Configuration.Slaves[0].Name);

        await learner.ResolveSchemasAsync(CancellationToken.None);

        Assert.Equal("Slave 1001 (EL1008 8Ch. Dig. Input 24V, 3ms)",
            learner.Current!.Configuration.Slaves[0].Name);
        Assert.True(learner.Current.Completeness.Slaves[0].NamesFromEsi);
    }

    /// <summary>The headline capability of the whole milestone: with no ENI at all, a resolved ESI
    /// schema turns bare offsets into named, typed process variables. Asserting both sides of the
    /// transformation matters — without this the feature could ship with every variable still
    /// called "Slave 1001.0x6000:01" and every other test in the plan would still pass.</summary>
    [Fact]
    public async Task Esi_resolution_names_process_variables()
    {
        var learner = Learn(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Esi"));

        var before = learner.Current!.Configuration.Variables[0];
        Assert.Equal("Slave 1001.0x6000:01", before.Name);
        Assert.Equal("BOOL", before.DataType);

        await learner.ResolveSchemasAsync(CancellationToken.None);

        var after = learner.Current!.Configuration.Variables[0];
        Assert.Equal("Slave 1001 (EL1008 8Ch. Dig. Input 24V, 3ms).Channel 1.Input 1", after.Name);
        Assert.Equal("BOOL", after.DataType);
        Assert.Equal(0, after.BitOffs);
        Assert.Equal(1, after.BitSize);
    }

    /// <summary>Provenance exists so each surface can state where a fact came from. The bringup
    /// fixture reads identity out of SII and learns its PDO mapping from CoE downloads, so those
    /// are the sources it must report — not a generic or defaulted value.</summary>
    [Fact]
    public void Provenance_reports_where_each_fact_came_from()
    {
        var learner = Learn();

        var provenance = learner.Current!.Provenance[1001];

        Assert.Equal(FactSource.Sii, provenance.Identity);
        Assert.Equal(FactSource.Coe, provenance.Mapping);
        Assert.Equal(FactSource.Inferred, provenance.Names);
    }

    /// <summary>Provenance had no "nothing known" outcome for mapping: it was `Coe` when a PDO
    /// assignment had been observed and `EsiDefault` otherwise, so a cyclic-only capture with no ESI
    /// directory at all reported `mapping=EsiDefault` while `learn` on the same capture reported zero
    /// process variables. The one type whose job is reporting sources accurately named a source that
    /// produced nothing — and it sat between two correct `Inferred`s, which made it look considered.
    /// `EsiDefault` is now claimed only when a schema genuinely exists for that slave.</summary>
    [Fact]
    public void Mapping_provenance_is_inferred_when_no_schema_exists_for_the_slave()
    {
        var learner = new BusLearner();
        // Enough traffic to list a slave and nothing more: no station-address assignment, no SII, no
        // CoE, no ESI directory. Exactly a mid-run attach with no --esi-dir. Both halves of the AL
        // poll, because a returning frame with no outbound counterpart falls back to pairing and is
        // classified outbound — which would list no slave at all.
        Observe(learner, new EtherCatFrameBuilder()
            .AddPhysical(EtherCatCommand.Fprd, 0, 1004, 0x0130, new byte[2], 0).Build());
        Observe(learner, new EtherCatFrameBuilder().AsReturning()
            .AddPhysical(EtherCatCommand.Fprd, 0, 1004, 0x0130, [0x08, 0x00], 1).Build());

        var provenance = learner.Current!.Provenance[1004];

        Assert.Equal(FactSource.Inferred, provenance.Identity);
        Assert.Equal(FactSource.Inferred, provenance.Names);
        Assert.Equal(FactSource.Inferred, provenance.Mapping);
        // And the claim it used to make was false in the only way that matters: nothing was mapped.
        Assert.Empty(learner.Current!.Configuration.Variables);
    }

    /// <summary>The other side of the same rule: a resolved schema really is where an unobserved PDO
    /// mapping would come from, so `EsiDefault` must still be claimed when one exists.</summary>
    [Fact]
    public async Task Mapping_provenance_is_esi_default_when_a_schema_did_resolve()
    {
        var learner = Learn(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Esi"));
        await learner.ResolveSchemasAsync(CancellationToken.None);
        Assert.True(learner.Current!.Completeness.Slaves[0].NamesFromEsi);

        // The bringup fixture observes its assignment over CoE, so force the ESI-default path by
        // asking a slave whose schema resolved but whose assignment was never on the wire.
        var withoutObservedAssignment = new BusLearner(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Esi"));
        foreach (var frame in IdentityOnlyFrames()) Observe(withoutObservedAssignment, frame);
        await withoutObservedAssignment.ResolveSchemasAsync(CancellationToken.None);
        Assert.Equal(0x03F03052u,
            withoutObservedAssignment.Current!.Configuration.Slaves.Single().ProductCode);

        Assert.Equal(FactSource.EsiDefault,
            withoutObservedAssignment.Current!.Provenance[1001].Mapping);
    }

    /// <summary>Returning CoE uploads of 0x1018:01–03 — identity over the mailbox, with no PDO
    /// assignment or mapping objects, so the slave's ESI schema resolves while its mapping stays
    /// unobserved. Reuses <see cref="MailboxDecoderTests"/>'s encoders rather than restating the
    /// CoE byte layout, so a change to either stays in one place.</summary>
    private static IEnumerable<byte[]> IdentityOnlyFrames()
    {
        byte idx = 0;
        foreach (var (subIndex, value) in
                 new (byte SubIndex, uint Value)[] { (1, 2u), (2, 0x03F03052u), (3, 0x00120000u) })
        {
            var mailbox = MailboxDecoderTests.CoeMailbox(1001,
                MailboxDecoderTests.ExpeditedSdo(3, 0x43, 0x1018, subIndex, value));
            // The request half first, as a master really emits it. Also what establishes the
            // "MAC bit clear" baseline DirectionTracker needs — a returning-only frame with no
            // outbound counterpart falls back to pairing and is classified OUTBOUND, which would
            // route these through the download decoder instead of the upload one.
            yield return new EtherCatFrameBuilder()
                .AddPhysical(EtherCatCommand.Fprd, idx, 1001, 0x1080, new byte[mailbox.Length], 0)
                .Build();
            yield return new EtherCatFrameBuilder().AsReturning()
                .AddPhysical(EtherCatCommand.Fprd, idx, 1001, 0x1080, mailbox, 1)
                .Build();
            idx++;
        }
    }

    [Fact]
    public async Task Esi_resolution_without_a_directory_is_a_no_op()
    {
        var learner = Learn();
        var before = learner.Current!.Configuration.Slaves[0].Name;

        await learner.ResolveSchemasAsync(CancellationToken.None);

        Assert.Equal(before, learner.Current!.Configuration.Slaves[0].Name);
    }

    /// <summary>Plan 2 drives schema resolution from a timer, so a call that resolves nothing new
    /// must not publish a revision. Otherwise a converged live session emits a fresh, identical
    /// configuration on every tick.</summary>
    [Fact]
    public async Task Repeated_schema_resolution_does_not_churn_revisions()
    {
        var learner = Learn(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Esi"));
        await learner.ResolveSchemasAsync(CancellationToken.None);
        var revision = learner.Current!.Revision;
        var published = 0;
        learner.ConfigurationLearned += _ => published++;

        await learner.ResolveSchemasAsync(CancellationToken.None);

        Assert.Equal(revision, learner.Current!.Revision);
        Assert.Equal(0, published);
    }
}
