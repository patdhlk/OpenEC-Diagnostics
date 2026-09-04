using OpenEC.Monitor.Eni;

namespace OpenEC.Monitor.Tests.Eni;

public class ProcessVariableAssignmentTests
{
    private static EniSlave Slave(string name, ushort addr) =>
        new(name, addr, 0, 2, 0, 0, null, null);

    private static EniVariable Var(string name) => new(name, "BOOL", 1, 0, true);

    private static EniConfiguration Eni(IReadOnlyList<EniSlave> slaves, IReadOnlyList<EniVariable> vars) =>
        new() { Slaves = slaves, CyclicCommands = [], Variables = vars };

    [Fact]
    public void Assigns_variables_to_slaves_by_name_prefix()
    {
        var eni = Eni(
            [Slave("Term 2 (EL1008)", 1002), Slave("Term 3 (EL2008)", 1003)],
            [Var("Term 2 (EL1008).Channel 1.Input"),
                Var("Term 2 (EL1008).Channel 2.Input"),
                Var("Term 3 (EL2008).Channel 1.Output")]);

        var a = ProcessVariableAssignment.Build(eni);

        Assert.Equal(2, a.BySlave[1002].Count);
        Assert.Single(a.BySlave[1003]);
        Assert.Empty(a.Unmatched);
    }

    [Fact]
    public void Longest_slave_name_wins_for_nested_names()
    {
        var eni = Eni(
            [Slave("Rack 1", 1001), Slave("Rack 1.Module 2", 1002)],
            [Var("Rack 1.Module 2.Temp"), Var("Rack 1.Status")]);

        var a = ProcessVariableAssignment.Build(eni);

        Assert.Equal("Rack 1.Module 2.Temp", Assert.Single(a.BySlave[1002]).Name);
        Assert.Equal("Rack 1.Status", Assert.Single(a.BySlave[1001]).Name);
    }

    [Fact]
    public void Variables_matching_no_slave_are_reported_unmatched()
    {
        var eni = Eni([Slave("Term 2 (EL1008)", 1002)], [Var("Ghost.Value")]);

        var a = ProcessVariableAssignment.Build(eni);

        Assert.Equal("Ghost.Value", Assert.Single(a.Unmatched).Name);
        Assert.Empty(a.BySlave[1002]);
    }

    [Fact]
    public void Duplicate_slave_names_assign_to_the_lowest_address()
    {
        var eni = Eni(
            [Slave("Term (EL1008)", 1005), Slave("Term (EL1008)", 1002)],
            [Var("Term (EL1008).Channel 1.Input")]);

        var a = ProcessVariableAssignment.Build(eni);

        Assert.Single(a.BySlave[1002]);
        Assert.Empty(a.BySlave[1005]);
    }

    [Fact]
    public void Every_eni_slave_gets_a_key_even_without_variables()
    {
        var eni = Eni([Slave("Term 1 (EK1100)", 1001)], []);

        var a = ProcessVariableAssignment.Build(eni);

        Assert.Empty(a.BySlave[1001]);
        Assert.Empty(a.Unmatched);
    }

    [Fact]
    public void The_fixture_eni_assigns_all_five_variables()
    {
        var eni = EniConfiguration.Load(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml"));

        var a = ProcessVariableAssignment.Build(eni);

        Assert.Empty(a.Unmatched);
        Assert.Equal(2, a.BySlave[1004].Count); // Drive 4: Statusword + Controlword
        Assert.Equal(2, a.BySlave[1002].Count); // Term 2: Channel 1 + 2
        Assert.Single(a.BySlave[1003]);          // Term 3: Channel 1.Output
        Assert.Empty(a.BySlave[1001]);           // EK1100 coupler: none
    }
}
