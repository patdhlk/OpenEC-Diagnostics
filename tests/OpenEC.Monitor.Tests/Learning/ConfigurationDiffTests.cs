using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Tests.Learning;

public class ConfigurationDiffTests
{
    private static EniConfiguration Config(
        IReadOnlyList<EniSlave>? slaves = null, IReadOnlyList<EniVariable>? variables = null) => new()
        {
            Slaves = slaves ?? [new EniSlave("Term 1 (EL1008)", 1001, 0x0000, 2, 0x03F03052, 0x00120000, null, null)],
            CyclicCommands = [new EniCyclicCommand(EtherCatCommand.Lrd, 0x00010000, 1, 1, 0, null)],
            Variables = variables ?? [new EniVariable("Term 1 (EL1008).Channel 1.Input", "BOOL", 1, 0, true)],
        };

    [Fact]
    public void Identical_configurations_produce_no_mismatches()
    {
        Assert.Empty(ConfigurationDiff.Compare(Config(), Config()));
    }

    [Fact]
    public void A_different_product_code_at_the_same_address_is_an_identity_mismatch()
    {
        var learned = Config([
            new EniSlave("Term 1 (EL2008)", 1001, 0x0000, 2, 0x07D83052, 0x00110000, null, null)]);

        var mismatch = Assert.Single(ConfigurationDiff.Compare(Config(), learned));

        Assert.Equal(ConfigMismatchKind.Identity, mismatch.Kind);
        Assert.Equal((ushort?)1001, mismatch.Address);
    }

    [Fact]
    public void A_slave_the_bus_never_showed_is_reported_missing()
    {
        var learned = Config(slaves: []);

        var mismatch = Assert.Single(ConfigurationDiff.Compare(Config(), learned));

        Assert.Equal(ConfigMismatchKind.SlaveMissing, mismatch.Kind);
    }

    [Fact]
    public void A_slave_the_eni_never_declared_is_reported_unexpected()
    {
        var declared = Config(slaves: []);

        var mismatch = Assert.Single(ConfigurationDiff.Compare(declared, Config()));

        Assert.Equal(ConfigMismatchKind.SlaveUnexpected, mismatch.Kind);
    }

    /// <summary>TwinCAT's "Add WC state bit(s)" injects WcState and InputToggle into the ENI's
    /// process image; the master computes them and they never appear on the wire. A learned
    /// configuration therefore cannot contain them, and reporting their absence would raise a
    /// mismatch on every real TwinCAT configuration.</summary>
    [Fact]
    public void Master_synthesised_variables_are_not_mismatches()
    {
        var declared = Config(variables: [
            new EniVariable("Term 1 (EL1008).Channel 1.Input", "BOOL", 1, 0, true),
            new EniVariable("Term 1 (EL1008).WcState", "BOOL", 1, 8, true),
            new EniVariable("Term 1 (EL1008).InputToggle", "BOOL", 1, 9, true),
            new EniVariable("Term 1 (EL1008).InfoData.State", "UINT", 16, 16, true),
            new EniVariable("Term 1 (EL1008).InfoData.AdsAddr", "UDINT", 32, 32, true),
        ]);

        Assert.Empty(ConfigurationDiff.Compare(declared, Config()));
    }

    /// <summary>TxPdoState is a genuine PDO entry on many drives, not a master-computed bit.
    /// Excluding it would hide a real remapping — the failure that makes a cross-check worthless.</summary>
    [Fact]
    public void A_missing_real_pdo_entry_is_still_a_mismatch()
    {
        var declared = Config(variables: [
            new EniVariable("Term 1 (EL1008).Channel 1.Input", "BOOL", 1, 0, true),
            new EniVariable("Drive 2 (AX5101).Inputs.TxPdoState", "BOOL", 1, 8, true),
        ]);

        var mismatch = Assert.Single(ConfigurationDiff.Compare(declared, Config()));

        Assert.Equal(ConfigMismatchKind.ProcessImage, mismatch.Kind);
        Assert.Contains("TxPdoState", mismatch.Declared);
    }

    /// <summary>One learned variable of this shape, so "where did it go" has exactly one answer and
    /// the report may name it. Asserting the offset itself, not just the kind: the previous version of
    /// this test asserted only <c>Kind</c> against a single-variable configuration, which could not
    /// tell a correct offset from an arbitrary one.</summary>
    [Fact]
    public void A_variable_at_a_different_offset_reports_the_offset_it_moved_to()
    {
        var learned = Config(variables: [
            new EniVariable("Term 1 (EL1008).Channel 1.Input", "BOOL", 1, 8, true)]);

        var mismatch = Assert.Single(ConfigurationDiff.Compare(Config(), learned));

        Assert.Equal(ConfigMismatchKind.ProcessImage, mismatch.Kind);
        Assert.Equal("Term 1 (EL1008).Channel 1.Input @bit 0", mismatch.Declared);
        Assert.Equal("@bit 8", mismatch.Observed);
    }

    /// <summary>Spec §5's flagship finding — "the PDO was remapped at runtime" — on the bus shape that
    /// is nearly every real bus: more than one entry of the same size and direction. The old code
    /// grouped learned variables by (BitSize, IsInput) and reported `g.First().BitOffs`, an arbitrary
    /// representative: a declared variable displaced to bit 500 was reported as "observed @bit 0",
    /// where bit 0 held a different entry on a different slave. Nothing had looked for where the
    /// variable actually went. With the answer ambiguous the report must not invent one.</summary>
    [Fact]
    public void A_displaced_variable_with_several_same_shape_counterparts_names_no_offset()
    {
        var declared = Config(variables: [
            new EniVariable("Term 2 (EL1008).Channel 8.Input", "BOOL", 1, 500, true)]);
        var learned = Config(variables: [
            new EniVariable("Slave 1001.0x6000:01", "BOOL", 1, 0, true),
            new EniVariable("Slave 1002.0x6000:08", "BOOL", 1, 15, true)]);

        var mismatch = Assert.Single(ConfigurationDiff.Compare(declared, learned));

        Assert.Equal(ConfigMismatchKind.ProcessImage, mismatch.Kind);
        Assert.Equal("Term 2 (EL1008).Channel 8.Input @bit 500", mismatch.Declared);
        Assert.DoesNotContain("@bit", mismatch.Observed);
        // Same wording as the no-counterpart branch, because that is what is actually known.
        Assert.Equal("not in the learned image", mismatch.Observed);
    }

    /// <summary>A learned name is synthesised ("Slave 1002 (EL1008)…") while a declared ENI carries
    /// the master's own label ("Term 2 (EL1008)…"), so the two sides never share a name even when
    /// the wire matches the declaration exactly. The comparison must key on placement — offset,
    /// size, direction — not on name, or every declared variable reports as absent.</summary>
    [Fact]
    public void A_variable_present_at_the_same_placement_under_a_different_name_is_not_a_mismatch()
    {
        var declared = Config(variables: [
            new EniVariable("Term 1 (EL1008).Channel 1.Input", "BOOL", 1, 0, true)]);
        var learned = Config(variables: [
            new EniVariable("Slave 1001 (EL1008).Channel 1.Input", "BOOL", 1, 0, true)]);

        Assert.Empty(ConfigurationDiff.Compare(declared, learned));
    }
}
