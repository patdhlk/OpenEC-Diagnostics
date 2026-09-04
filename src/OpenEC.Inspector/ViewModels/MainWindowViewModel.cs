using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenEC.Inspector.Session;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;

namespace OpenEC.Inspector.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<(string Name, string? Description)>> _listDevices;
    private readonly Func<SourceSpec, EniConfiguration?, MonitorSession> _createSession;
    private readonly IFilePicker _filePicker;
    private readonly Action<Action> _marshal;
    private readonly TimeSpan? _earlyFaultProbe;
    private readonly Dictionary<ushort, DeviceEditorViewModel> _editorCache = new();
    private MonitorSession? _subscribedSession;
    private Action<SessionState>? _stateChangedHandler;
    private ProcessVariableAssignment? _assignment;
    private VariableWatchViewModel? _processImagePage;
    private int? _assignmentRevision; // learned revision the current _assignment was built from

    public MainWindowViewModel(
        Func<IReadOnlyList<(string Name, string? Description)>> listDevices,
        Func<SourceSpec, EniConfiguration?, MonitorSession> createSession,
        IFilePicker filePicker,
        Action<Action>? marshal = null,
        TimeSpan? earlyFaultProbe = null)
    {
        _listDevices = listDevices;
        _createSession = createSession;
        _filePicker = filePicker;
        _marshal = marshal ?? (action => action());
        _earlyFaultProbe = earlyFaultProbe;
        Start = NewStartViewModel();
        _currentPage = Start;
    }

    public StartViewModel Start { get; private set; }
    public MonitorSession? Session { get; private set; }
    public DashboardViewModel? Dashboard { get; private set; }
    public ExplorerViewModel? Explorer { get; private set; }
    public EventsViewModel? Events { get; private set; }

    [ObservableProperty] private object _currentPage;
    [ObservableProperty] private bool _hasSession;
    [ObservableProperty] private string _statusText = "No session";
    [ObservableProperty] private string? _faultMessage;
    private const string StopLabel = "Stop session";
    private const string CloseLabel = "Close session";

    /// <summary>What the session control offers. A capture that has run to its end — every offline
    /// file, and a live session after a fault — has nothing left to stop, but the control is still
    /// the only way back to the start screen, so it is relabelled rather than hidden.</summary>
    [ObservableProperty] private string _sessionActionLabel = StopLabel;

    [ObservableProperty] private StatusDot _sessionDot;
    [ObservableProperty] private StatusDot _healthDot;
    [ObservableProperty] private string _healthText = "";

    private const double ClassicPaneWidth = 280;
    private const double TopologyPaneWidth = 620;

    /// <summary>Remembered pane width per explorer view. The tree wants a narrow pane and the map
    /// wants a wide one, so a single width would make one of the two views useless on every switch.
    /// </summary>
    private readonly double[] _paneWidths = [ClassicPaneWidth, TopologyPaneWidth];
    private int _explorerView;

    [ObservableProperty] private double _explorerWidth = ClassicPaneWidth;

    partial void OnExplorerWidthChanged(double value) => _paneWidths[_explorerView] = value;

    private void OnExplorerViewChanged(int viewIndex)
    {
        if (viewIndex < 0 || viewIndex >= _paneWidths.Length) return;
        _explorerView = viewIndex;
        ExplorerWidth = _paneWidths[viewIndex];
    }

    private StartViewModel NewStartViewModel() =>
        new(_listDevices, _createSession, _filePicker, OnSessionStarted, _earlyFaultProbe);

    private void OnSessionStarted(MonitorSession session)
    {
        Session = session;
        OnPropertyChanged(nameof(Session));
        _assignment = session.Eni is { } eni ? ProcessVariableAssignment.Build(eni) : null;
        _assignmentRevision = null;
        Explorer = new ExplorerViewModel(session, _assignment, OnNodeSelected);
        Explorer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExplorerViewModel.SelectedViewIndex))
                OnExplorerViewChanged(Explorer!.SelectedViewIndex);
        };
        OnPropertyChanged(nameof(Explorer));
        Events = new EventsViewModel(session);
        OnPropertyChanged(nameof(Events));
        Dashboard = new DashboardViewModel(session);
        _editorCache.Clear();
        _processImagePage = null;
        _stateChangedHandler = state => _marshal(() =>
        {
            if (!ReferenceEquals(Session, session)) return;
            if (state == SessionState.Faulted) FaultMessage = session.Fault?.Message;
            UpdateStatus();
        });
        _subscribedSession = session;
        session.StateChanged += _stateChangedHandler;
        HasSession = true;
        FaultMessage = null;
        // A fault can land between StartViewModel's probe and this subscription; catch up so the banner isn't lost.
        if (session.State == SessionState.Faulted) FaultMessage = session.Fault?.Message;
        Explorer.SelectedNode = Explorer.Root; // drives CurrentPage = Dashboard through the callback
        Tick();
    }

    private void OnNodeSelected(ExplorerNode? node)
    {
        if (Dashboard is null) return; // teardown/rebind race: no session to show a page for
        CurrentPage = node switch
        {
            SlaveNode s => GetOrCreateEditor(s.Address),
            ProcessImageNode => GetOrCreateProcessImagePage(),
            _ => (object)Dashboard!,
        };
        (CurrentPage as IRefreshable)?.Refresh();
    }

    private DeviceEditorViewModel GetOrCreateEditor(ushort address)
    {
        if (_editorCache.TryGetValue(address, out var cached)) return cached;
        var slave = Session!.Eni?.Slaves.FirstOrDefault(s => s.PhysAddr == address);
        var variables = _assignment is not null && _assignment.BySlave.TryGetValue(address, out var assigned)
            ? assigned
            : (IReadOnlyList<EniVariable>)[];
        var watch = VariableWatchViewModel.ForSlave(Session!, RestartWithEniAsync, slave, variables);
        var editor = new DeviceEditorViewModel(Session!, address, watch);
        _editorCache[address] = editor;
        return editor;
    }

    private VariableWatchViewModel GetOrCreateProcessImagePage() =>
        _processImagePage ??= VariableWatchViewModel.ForUnmatched(
            Session!, RestartWithEniAsync, _assignment?.Unmatched ?? []);

    /// <summary>An ENI is one source of a process image; a learned configuration is another, and the
    /// Variables tab cannot tell them apart (spec §7). Rebuilt when learning publishes a new revision,
    /// so a bus that converges mid-session stops showing an empty tab.</summary>
    private void RefreshAssignmentIfLearned()
    {
        if (Session is not { } session || session.Eni is not null) return;
        if (session.Observer.Applied is not { } learned) return;
        if (_assignmentRevision == learned.Revision) return;
        _assignmentRevision = learned.Revision;
        _assignment = ProcessVariableAssignment.Build(learned.Configuration);
        // Editors cache the variable list they were built with, so they must be rebuilt too.
        _editorCache.Clear();
        _processImagePage = null;
        if (Explorer?.SelectedNode is { } selected) OnNodeSelected(selected);
    }

    /// <summary>Called by the view's DispatcherTimer every 250 ms (4 Hz).</summary>
    public void Tick()
    {
        RefreshAssignmentIfLearned();
        RefreshSaveLearnedEniAvailability();
        Explorer?.Refresh();
        (CurrentPage as IRefreshable)?.Refresh();
        Events?.Refresh();
        UpdateStatus();
        SessionDot = Session is null ? StatusDot.Idle : StatusDotMap.ForSession(Session.State);
    }

    private void UpdateStatus()
    {
        SessionActionLabel = Session?.State == SessionState.Running ? StopLabel : CloseLabel;
        if (Session is null)
        {
            StatusText = "No session";
            HealthText = "";
            HealthDot = StatusDot.Idle;
            return;
        }
        var recording = Session.RecordPath is { } recordPath
            ? $" · rec → {Path.GetFileName(recordPath)}"
            : "";
        StatusText = string.Create(CultureInfo.InvariantCulture,
            $"{Session.SourceDescription} · {StateLabel(Session.State)} · " +
            $"{Session.FramesSeen:N0} frames · {Session.MalformedFrames:N0} malformed{recording}");

        var health = Session.Observer.SnapshotHealth();
        HealthDot = StatusDotMap.ForHealth(health.Level);
        HealthText = FormatHealth(health);
    }

    private static string FormatHealth(BusHealth h)
    {
        var devices = h.ConfiguredDevices is { } cfg
            ? $"{h.FoundDevices}/{cfg} devices"
            : $"{h.FoundDevices} devices";
        var dc = h.DcSync switch
        {
            DcSyncState.Synced => "DC synced",
            DcSyncState.OutOfSync => "DC out of sync",
            _ => "DC unmonitored",
        };
        // Named, not just counted. A warning that says "1 stale" sends the reader hunting; the one
        // that says which slave is the whole finding, and it is the only place this fault surfaces.
        var stale = h.Stale.Count > 0
            ? $" · stale process data: {string.Join(", ", h.Stale)}"
            : "";
        return $"health {h.Level.ToString().ToLowerInvariant()} · {devices} · {dc}{stale}";
    }

    private static string StateLabel(SessionState state) => state switch
    {
        SessionState.Running => "capturing",
        SessionState.Completed => "completed",
        SessionState.Stopped => "stopped",
        SessionState.Faulted => "faulted",
        _ => "idle",
    };

    [RelayCommand]
    private void DismissFault() => FaultMessage = null;

    private const string NothingLearnedHint =
        "Nothing has been learned from this capture yet. The reconstruction is built from the "
        + "master bringing the bus up, so it needs a capture that includes startup.";

    private const string SaveLearnedEniTooltip =
        "Export the bus configuration reconstructed from observed traffic as ENI XML.";

    /// <summary>Tooltip for the Save control. The view puts it on an always-enabled wrapper rather
    /// than on the button itself: a disabled Avalonia control takes no pointer input, so a tooltip
    /// set directly on it would never appear — and a disabled button with no visible reason is the
    /// same silence this replaces.</summary>
    [ObservableProperty] private string _saveLearnedEniHint = NothingLearnedHint;

    /// <summary>Sourced from the LEARNED configuration, not the applied one. With an ENI loaded the
    /// ENI stays the authority and <see cref="BusObserver.Applied"/> is null for the whole session by
    /// design, so guarding on it made the command a no-op for every ENI-loaded session — the button
    /// was enabled (bound to HasSession alone) and did nothing: no dialog, no file, no message.</summary>
    private bool CanSaveLearnedEni => Session?.Learned is not null;

    [RelayCommand(CanExecute = nameof(CanSaveLearnedEni))]
    private async Task SaveLearnedEniAsync()
    {
        if (Session?.Learned is not { } learned) return;
        var path = await _filePicker.PickSaveFileAsync("Save learned ENI", "bus.eni.xml", "xml");
        if (path is null) return;
        try
        {
            EniXmlWriter.Write(learned.Configuration, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FaultMessage = $"Learned ENI could not be saved: {ex.Message}";
        }
    }

    /// <summary>Re-evaluates the Save control from the 4 Hz tick, since a live bus can start with
    /// nothing learned and converge mid-session. Guarded on a change so a converged session does not
    /// raise CanExecuteChanged four times a second for nothing.</summary>
    private void RefreshSaveLearnedEniAvailability()
    {
        var available = CanSaveLearnedEni;
        if (available == _saveLearnedEniAvailable) return;
        _saveLearnedEniAvailable = available;
        SaveLearnedEniHint = available ? SaveLearnedEniTooltip : NothingLearnedHint;
        SaveLearnedEniCommand.NotifyCanExecuteChanged();
    }

    private bool _saveLearnedEniAvailable;

    [RelayCommand]
    private async Task StopSessionAsync()
    {
        if (Session is null) return;
        var session = DetachSession();
        await session.StopAsync();
        await session.DisposeAsync();
        Start = NewStartViewModel();
        OnPropertyChanged(nameof(Start));
        CurrentPage = Start;
        StatusText = "No session";
    }

    /// <summary>File-menu entry point: pick a capture file and analyze it. Mirrors the start
    /// screen's file flow, and is usable while a session is live — the current session is closed
    /// first so analysis starts from the same clean state the Close control would leave.</summary>
    [RelayCommand]
    private async Task OpenCaptureFileAsync()
    {
        var path = await _filePicker.PickFileAsync("Open capture", "pcap", "pcapng");
        if (path is null) return; // dialog cancelled
        if (Session is not null) await StopSessionAsync();
        Start.PcapPath = path;
        await Start.StartFileCommand.ExecuteAsync(null);
    }

    /// <summary>Load an ENI onto the running session: it restarts the same source with the
    /// configuration applied, so the process-variable watch and slave names come alive. Reached
    /// from the File menu and from a device editor's Variables tab.</summary>
    [RelayCommand]
    private async Task RestartWithEniAsync()
    {
        if (Session?.Source is not { } spec) return;
        var path = await _filePicker.PickFileAsync("Load ENI", "xml");
        if (path is null) return;
        EniConfiguration eni;
        try
        {
            eni = EniConfiguration.Load(path);
        }
        catch (Exception ex)
        {
            FaultMessage = $"ENI could not be loaded: {ex.Message}";
            return;
        }
        var selectedAddress = (Explorer?.SelectedNode as SlaveNode)?.Address;
        var old = DetachSession();
        await old.StopAsync();
        await old.DisposeAsync();
        var next = _createSession(spec, eni);
        next.Start();
        OnSessionStarted(next);
        Explorer!.SelectedNode = Explorer.Root.Children.OfType<SlaveNode>()
            .FirstOrDefault(n => n.Address == selectedAddress) ?? (ExplorerNode)Explorer.Root;
    }

    private MonitorSession DetachSession()
    {
        var session = Session!;
        if (_subscribedSession is not null && _stateChangedHandler is not null)
            _subscribedSession.StateChanged -= _stateChangedHandler;
        _subscribedSession = null;
        _stateChangedHandler = null;
        Session = null;
        OnPropertyChanged(nameof(Session));
        HasSession = false;
        Dashboard = null;
        Explorer = null;
        OnPropertyChanged(nameof(Explorer));
        Events = null;
        OnPropertyChanged(nameof(Events));
        _assignment = null;
        _assignmentRevision = null;
        _editorCache.Clear();
        _processImagePage = null;
        FaultMessage = null;
        SessionDot = StatusDot.Idle;
        HealthDot = StatusDot.Idle;
        HealthText = "";
        // With no session there is nothing to export, and the tick that would normally notice has
        // stopped mattering — the shell is back on the start screen.
        RefreshSaveLearnedEniAvailability();
        return session;
    }
}
