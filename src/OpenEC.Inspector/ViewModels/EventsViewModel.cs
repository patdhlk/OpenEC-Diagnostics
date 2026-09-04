using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenEC.Inspector.Session;
using OpenEC.Monitor.Observation;

namespace OpenEC.Inspector.ViewModels;

public sealed record EventRow(string Time, string Category, string Description);

public sealed partial class CategoryFilter : ObservableObject
{
    private readonly Action _onChanged;

    public CategoryFilter(string name, Action onChanged)
    {
        Name = name;
        _onChanged = onChanged;
    }

    public string Name { get; }

    [ObservableProperty] private bool _isEnabled = true;

    partial void OnIsEnabledChanged(bool value) => _onChanged();
}

public sealed partial class EventsViewModel : ObservableObject, IRefreshable
{
    private static readonly string[] CategoryNames =
        ["State", "State request", "WKC", "Emergency", "SoE", "Config", "Health", "Learning", "Topology", "Other"];

    private readonly MonitorSession _session;
    private readonly int _maxRows;
    private int _lastCount = -1;
    private MonitorEvent? _lastTail;

    public EventsViewModel(MonitorSession session, int maxRows = 500)
    {
        _session = session;
        _maxRows = maxRows;
        Categories = CategoryNames.Select(n => new CategoryFilter(n, OnFilterChanged)).ToList();
    }

    public IReadOnlyList<CategoryFilter> Categories { get; }
    public ObservableCollection<EventRow> Rows { get; } = [];

    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private bool _isCollapsed;

    public void Refresh()
    {
        if (IsCollapsed) return;

        var events = _session.Observer.SnapshotEvents(_maxRows);
        if (events.Count == _lastCount && ReferenceEquals(events.Count > 0 ? events[^1] : null, _lastTail))
            return;

        var tailIndex = IndexOfTail(events, _lastTail);
        _lastCount = events.Count;
        _lastTail = events.Count > 0 ? events[^1] : null;

        if (tailIndex < 0)
        {
            Rebuild(events);
            return;
        }

        for (var i = tailIndex + 1; i < events.Count; i++) AppendIfEnabled(events[i]);
        while (Rows.Count > _maxRows) Rows.RemoveAt(0);
    }

    private static int IndexOfTail(IReadOnlyList<MonitorEvent> events, MonitorEvent? tail)
    {
        if (tail is null) return -1;
        for (var i = events.Count - 1; i >= 0; i--)
            if (ReferenceEquals(events[i], tail))
                return i;
        return -1;
    }

    partial void OnIsCollapsedChanged(bool value)
    {
        if (!value) Refresh();
    }

    private void OnFilterChanged()
    {
        _lastCount = -1;
        _lastTail = null;
        Refresh();
    }

    private void Rebuild(IReadOnlyList<MonitorEvent> events)
    {
        Rows.Clear();
        foreach (var e in events) AppendIfEnabled(e);
    }

    private void AppendIfEnabled(MonitorEvent e)
    {
        var category = EventFormatter.Category(e);
        var enabled = Categories.FirstOrDefault(c => c.Name == category)?.IsEnabled ?? true;
        if (!enabled) return;
        Rows.Add(new EventRow(
            e.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            category,
            EventFormatter.Describe(e)));
    }
}
