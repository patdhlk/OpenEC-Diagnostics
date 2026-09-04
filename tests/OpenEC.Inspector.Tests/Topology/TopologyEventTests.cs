using OpenEC.Inspector.ViewModels;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Topology;

namespace OpenEC.Inspector.Tests.Topology;

public class TopologyEventTests
{
    private static readonly MonitorEvent.TopologyChanged LinkLost =
        new(DateTimeOffset.UnixEpoch, Address: 1013, Port: 1,
            PortLinkState.Active, PortLinkState.Dangling);

    [Fact]
    public void A_topology_change_is_its_own_category()
    {
        Assert.Equal("Topology", EventFormatter.Category(LinkLost));
    }

    /// <summary>The category must exist in the filter list, or the event is only reachable as
    /// "Other" — the exact half-wiring this pairing exists to prevent.</summary>
    [Fact]
    public async Task The_messages_panel_offers_a_topology_filter()
    {
        var events = new EventsViewModel(await TestSessions.BranchedAsync());

        Assert.Contains(events.Categories, c => c.Name == "Topology");
    }

    [Fact]
    public void A_link_loss_names_the_device_the_port_and_both_states()
    {
        var text = EventFormatter.Describe(LinkLost);

        Assert.Contains("1013", text);
        Assert.Contains("port 1", text);
        Assert.Contains("Active", text);
        Assert.Contains("Dangling", text);
    }

    [Fact]
    public void A_topology_config_mismatch_reads_as_a_disagreement()
    {
        var text = EventFormatter.Describe(new MonitorEvent.ConfigMismatch(
            DateTimeOffset.UnixEpoch, ConfigMismatchKind.Topology, 1002,
            Declared: "1001 port 2", Observed: "1001 port 1"));

        Assert.Contains("Topology", text);
        Assert.Contains("1001 port 2", text);
        Assert.Contains("1001 port 1", text);
    }

    [Fact]
    public async Task Every_formatter_category_is_a_filterable_category()
    {
        var events = new EventsViewModel(await TestSessions.BranchedAsync());
        MonitorEvent[] samples =
        [
            LinkLost,
            new MonitorEvent.ConfigMismatch(DateTimeOffset.UnixEpoch,
                ConfigMismatchKind.Topology, 1002, "a", "b"),
        ];

        foreach (var sample in samples)
            Assert.Contains(events.Categories, c => c.Name == EventFormatter.Category(sample));
    }
}
