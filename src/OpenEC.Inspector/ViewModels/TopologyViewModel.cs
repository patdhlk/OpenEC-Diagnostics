using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenEC.Inspector.Session;
using OpenEC.Inspector.Topology;
using OpenEC.Monitor.Observation;
using OpenEC.Monitor.Topology;

namespace OpenEC.Inspector.ViewModels;

/// <summary>One box on the map. <see cref="Node"/> is the SAME instance the device tree holds, so
/// selecting a box and selecting its tree row are the same act.</summary>
public sealed partial class TopologyBoxViewModel : ObservableObject
{
    public TopologyBoxViewModel(ushort address, ExplorerNode node)
    {
        Address = address;
        Node = node;
    }

    public ushort Address { get; }
    public ExplorerNode Node { get; }

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _width;
    [ObservableProperty] private double _height;
    [ObservableProperty] private TopologyBoxKind _kind;
    [ObservableProperty] private bool _isWide;
    [ObservableProperty] private bool _edgeInferred;
    [ObservableProperty] private bool _hasConflict;
    [ObservableProperty] private StatusDot _dot;
    [ObservableProperty] private string _tooltip = "";
    [ObservableProperty] private IReadOnlyList<TopologyPortMark> _ports = [];
}

public sealed partial class TopologyWireViewModel : ObservableObject
{
    /// <summary>Typed as the interface <c>Polyline.Points</c> exposes, not a concrete collection:
    /// there is no `Avalonia.Points` type, and binding a list the shape cannot accept fails
    /// silently at runtime rather than at build time.</summary>
    [ObservableProperty] private IList<Point> _points = new List<Point>();
    [ObservableProperty] private bool _isInferred;
    [ObservableProperty] private bool _hasConflict;
}

/// <summary>The Topology View's model. Geometry is recomputed only when the topology's shape
/// changes; a tick that changes only AL state or counters mutates the existing box view models,
/// which is what keeps selection and instance identity stable (spec §5).</summary>
public sealed partial class TopologyViewModel : ObservableObject, IRefreshable
{
    /// <summary>States what has not been seen, not what the master did. The earlier wording
    /// asserted the master "never read DL status", which was a claim about someone else's behaviour
    /// that this code could not support: the reads were happening and were being discarded, and the
    /// screen accused the master of the monitor's own gap.</summary>
    private const string NoPortDataNotice =
        "Port topology not observed — devices are shown in ring order. "
        + "No DL status (0x0110) has been read since this session started; "
        + "a master restart or bus scan fills it in.";

    private readonly MonitorSession _session;
    private readonly Func<ushort, ExplorerNode?> _resolveNode;
    private readonly Action<ExplorerNode?> _select;
    private string? _shape;   // fingerprint of the last laid-out topology

    public TopologyViewModel(MonitorSession session, Func<ushort, ExplorerNode?> resolveNode,
        Action<ExplorerNode?> select)
    {
        _session = session;
        _resolveNode = resolveNode;
        _select = select;
    }

    public ObservableCollection<TopologyBoxViewModel> Boxes { get; } = [];
    public ObservableCollection<TopologyWireViewModel> Wires { get; } = [];

    [ObservableProperty] private double _zoom = 1.0;
    [ObservableProperty] private double _canvasWidth;
    [ObservableProperty] private double _canvasHeight;
    [ObservableProperty] private string? _notice;

    /// <summary>A separate bool rather than binding IsVisible to <c>Unplaced.Count</c>: Avalonia
    /// does not convert an int to a bool, so the count binding would silently never show the
    /// panel — and the unplaced strip is exactly the surface that must not fail quietly.</summary>
    [ObservableProperty] private bool _hasUnplaced;
    [ObservableProperty] private IReadOnlyList<string> _unplaced = [];
    [ObservableProperty] private ExplorerNode? _selectedNode;

    partial void OnSelectedNodeChanged(ExplorerNode? value) => _select(value);

    /// <summary>Set by the explorer when the selection changed elsewhere. Distinct from the
    /// property setter so echoing a selection back does not re-enter the callback.</summary>
    internal void SyncSelection(ExplorerNode? node)
    {
        if (ReferenceEquals(SelectedNode, node)) return;
        // Deliberate backing-field write: raises SelectedNode's PropertyChanged for the map binding
        // while bypassing the generated setter's OnSelectedNodeChanged hook, so echoing the tree's
        // selection into the map does not re-enter _select. The ReferenceEquals guard above already
        // prevents a real loop; MVVMTK0034 only flags the field access, which is intentional here.
#pragma warning disable MVVMTK0034
        SetProperty(ref _selectedNode, node, nameof(SelectedNode));
#pragma warning restore MVVMTK0034
    }

    public void Refresh()
    {
        var topology = _session.Observer.SnapshotTopology();
        var layout = TopologyLayoutEngine.Layout(topology);

        // The fingerprint covers everything the geometry depends on. When it is unchanged the
        // boxes are updated in place, so instances — and therefore selection — survive.
        var shape = string.Join('|', layout.Boxes.Select(b =>
            $"{b.Address}:{b.Row}:{b.X}:{b.Y}:{b.Width}:{b.Kind}:{b.Ports.Count}:{b.HasConflict}:"
            + string.Join(",", b.Ports.Select(p => $"{p.Port}{p.State}{p.HasError}"))));
        if (shape != _shape)
        {
            _shape = shape;
            Rebuild(layout);
        }

        var slaves = _session.Observer.SnapshotSlaves();
        foreach (var box in Boxes) UpdateLive(box, slaves);
        Notice = layout.PortDataObserved ? null : NoPortDataNotice;
        Unplaced = layout.Unplaced.Select(a => $"Slave {a}").ToList();
        HasUnplaced = Unplaced.Count > 0;
        CanvasWidth = layout.Width;
        CanvasHeight = layout.Height;
    }

    private void Rebuild(TopologyLayout layout)
    {
        var existing = Boxes.ToDictionary(b => b.Address);
        Boxes.Clear();
        foreach (var geometry in layout.Boxes)
        {
            if (_resolveNode(geometry.Address) is not { } node) continue;
            var box = existing.TryGetValue(geometry.Address, out var reused) && ReferenceEquals(reused.Node, node)
                ? reused
                : new TopologyBoxViewModel(geometry.Address, node);
            box.X = geometry.X;
            box.Y = geometry.Y;
            box.Width = geometry.Width;
            box.Height = geometry.Height;
            box.Kind = geometry.Kind;
            box.IsWide = geometry.IsWide;
            box.EdgeInferred = geometry.EdgeInferred;
            box.HasConflict = geometry.HasConflict;
            box.Ports = geometry.Ports;
            Boxes.Add(box);
        }

        Wires.Clear();
        foreach (var wire in layout.Wires)
            Wires.Add(new TopologyWireViewModel
            {
                Points = wire.Points.Select(p => new Point(p.X, p.Y)).ToList(),
                IsInferred = wire.IsInferred,
                HasConflict = wire.HasConflict,
            });
    }

    /// <summary>Per-tick state: label, status dot and tooltip. No geometry is touched here. The
    /// slaves snapshot is taken once per tick by the caller and shared across every box.</summary>
    private void UpdateLive(TopologyBoxViewModel box, IReadOnlyList<SlaveStatus> slaves)
    {
        if (box.Address == BusTopology.MasterAddress)
        {
            box.Label = "M1";
            box.Dot = StatusDotMap.ForSession(_session.State);
            box.Tooltip = _session.SourceDescription;
            return;
        }

        var status = slaves.FirstOrDefault(s => s.Address == box.Address);
        box.Label = box.Address.ToString();
        box.Dot = status is null ? StatusDot.Idle : StatusDotMap.ForSlave(status);
        box.Tooltip = Tooltip(box, status?.DisplayName);
    }

    private static string Tooltip(TopologyBoxViewModel box, string? name)
    {
        var lines = new List<string> { name ?? $"Slave {box.Address}" };
        foreach (var port in box.Ports)
        {
            var errors = port.HasError ? " · errors" : "";
            lines.Add($"Port {port.Port}: {port.State}{errors}");
        }
        if (box.EdgeInferred) lines.Add("Connection inferred from ring order, not observed");
        if (box.HasConflict) lines.Add("The loaded ENI declares a different connection for this device");
        return string.Join('\n', lines);
    }
}
