using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenEC.Inspector.Session;
using OpenEC.Monitor.Eni;

namespace OpenEC.Inspector.ViewModels;

public abstract partial class ExplorerNode : ObservableObject
{
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private StatusDot _dot;
}

public sealed partial class NetworkNode : ExplorerNode
{
    public ObservableCollection<ExplorerNode> Children { get; } = [];
}

public sealed partial class SlaveNode : ExplorerNode
{
    public required ushort Address { get; init; }
}

public sealed partial class ProcessImageNode : ExplorerNode { }

/// <summary>Builds the explorer tree: the session root, its slaves in address order
/// (row-reuse keeps existing node instances stable so expansion/selection survive ticks), and a
/// single cached process-image node that surfaces whenever process-variable coverage is
/// incomplete (spec-§4 visibility rule).</summary>
public sealed partial class ExplorerViewModel : ObservableObject
{
    private readonly MonitorSession _session;
    private readonly ProcessVariableAssignment? _assignment;
    private readonly Action<ExplorerNode?> _onSelected;
    private readonly ProcessImageNode _processImage = new() { Label = "Process Image", Dot = StatusDot.Idle };

    public ExplorerViewModel(MonitorSession session, ProcessVariableAssignment? assignment,
        Action<ExplorerNode?> onSelected)
    {
        _session = session;
        _assignment = assignment;
        _onSelected = onSelected;
        Root = new NetworkNode();
        RootItems = new List<ExplorerNode> { Root };
        Topology = new TopologyViewModel(session, ResolveNode, node => SelectedNode = node);
    }

    public NetworkNode Root { get; }

    public TopologyViewModel Topology { get; }

    /// <summary>Which explorer view is showing: 0 = Classic View, 1 = Topology View.</summary>
    [ObservableProperty] private int _selectedViewIndex;

    /// <summary>Maps a topology address to the node the tree already holds — the master's
    /// stand-in address resolves to the root. Returning the tree's instance rather than a new node
    /// is what makes selection identity-based across both views.</summary>
    private ExplorerNode? ResolveNode(ushort address) =>
        address == OpenEC.Monitor.Topology.BusTopology.MasterAddress
            ? Root
            : Root.Children.OfType<SlaveNode>().FirstOrDefault(s => s.Address == address);

    /// <summary>The tree's single top-level row. Typed to the node base and held in a plain list on
    /// purpose: to resolve a clicked row's container the TreeView probes this list via
    /// <c>IList.IndexOf</c> with the clicked node, which for a nested slave or process-image node is
    /// not the element type. A collection expression's synthesized single-element list casts there
    /// instead of type-checking and threw on every row below the root; <c>List&lt;T&gt;</c> answers
    /// -1 and lets the search descend into <see cref="NetworkNode.Children"/>.</summary>
    public IReadOnlyList<ExplorerNode> RootItems { get; }

    [ObservableProperty] private ExplorerNode? _selectedNode;

    partial void OnSelectedNodeChanged(ExplorerNode? value)
    {
        Topology.SyncSelection(value);
        _onSelected(value);
    }

    public void Refresh()
    {
        Root.Label = _session.SourceDescription;
        Root.Dot = StatusDotMap.ForSession(_session.State);

        var snapshot = _session.Observer.SnapshotSlaves().OrderBy(s => s.Address).ToList();
        foreach (var status in snapshot)
        {
            var node = Root.Children.OfType<SlaveNode>().FirstOrDefault(s => s.Address == status.Address);
            if (node is null)
            {
                node = new SlaveNode { Address = status.Address };
                Root.Children.Insert(
                    Root.Children.OfType<SlaveNode>().TakeWhile(s => s.Address < status.Address).Count(),
                    node);
            }
            node.Label = $"{status.DisplayName} ({status.Address})";
            node.Dot = StatusDotMap.ForSlave(status);
        }

        var showProcessImage = _assignment is null || _assignment.Unmatched.Count > 0;
        var hasProcessImage = Root.Children.Contains(_processImage);
        if (showProcessImage && !hasProcessImage)
            Root.Children.Add(_processImage);
        else if (!showProcessImage && hasProcessImage)
            Root.Children.Remove(_processImage);

        Topology.Refresh();
    }
}
