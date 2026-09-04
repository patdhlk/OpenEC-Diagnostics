using OpenEC.Monitor.Eni;

namespace OpenEC.Monitor.Tests.Topology;

public class EniPreviousPortTests
{
    private static EniConfiguration Load(string fixture) =>
        EniConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));

    [Fact]
    public void Previous_port_edges_are_parsed_with_their_port_letters()
    {
        var eni = Load("branched.eni.xml");

        Assert.Equal(new EniPreviousPort(1001, 1), Slave(eni, 1002).PreviousPort);
        Assert.Equal(new EniPreviousPort(1002, 1), Slave(eni, 1003).PreviousPort);
        Assert.Equal(new EniPreviousPort(1002, 2), Slave(eni, 1004).PreviousPort);
    }

    /// <summary>The first slave has no PreviousPort — it hangs off the master. A null must stay
    /// null rather than defaulting to a parent the file never declared.</summary>
    [Fact]
    public void A_slave_without_a_previous_port_element_has_none()
    {
        Assert.Null(Slave(Load("branched.eni.xml"), 1001).PreviousPort);
    }

    /// <summary>The existing sample fixture declares no topology at all, and must keep loading.</summary>
    [Fact]
    public void An_eni_with_no_topology_still_loads()
    {
        var eni = Load("sample.eni.xml");

        Assert.NotEmpty(eni.Slaves);
        Assert.All(eni.Slaves, s => Assert.Null(s.PreviousPort));
    }

    [Theory]
    [InlineData("A", 0)]
    [InlineData("B", 1)]
    [InlineData("C", 2)]
    [InlineData("D", 3)]
    [InlineData("a", 0)]
    [InlineData("2", 2)]
    public void Port_letters_and_numbers_both_map_to_a_port_index(string text, byte expected)
    {
        Assert.Equal(expected, EniPreviousPort.ParsePort(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Z")]
    [InlineData("9")]
    [InlineData(null)]
    public void An_unrecognised_port_is_null_rather_than_a_guess(string? text)
    {
        Assert.Null(EniPreviousPort.ParsePort(text));
    }

    private static EniSlave Slave(EniConfiguration eni, ushort address) =>
        eni.Slaves.Single(s => s.PhysAddr == address);
}
