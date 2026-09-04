using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenEC.Inspector.Session;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Observation;

namespace OpenEC.Inspector.ViewModels;

public sealed partial class VariableRowViewModel : ObservableObject
{
    public required string FullName { get; init; }   // ProcessImage lookup key
    public required string Name { get; init; }       // display name (prefix-stripped)
    public required string DataType { get; init; }
    public required string Direction { get; init; }  // "IN" / "OUT"

    [ObservableProperty] private string _value = "—";
    [ObservableProperty] private string _updated = "—";
}

public static class VariableValueFormat
{
    public static string Describe(VariableValue v)
    {
        var text = v.Value switch
        {
            bool b => b ? "TRUE" : "FALSE",
            ushort word => string.Create(CultureInfo.InvariantCulture, $"0x{word:X4} ({word})"),
            _ => Convert.ToString(v.Value, CultureInfo.InvariantCulture) ?? "",
        };
        return v.Cia402Description is { } description ? $"{text} — {description}" : text;
    }
}

/// <summary>Scoped process-variable watch: either the variables assigned to one slave (or an
/// observed-only station without a slave) or the ENI's unmatched variables. Rows are seeded from
/// the assigned variable list, not just observed values, so a mapped-but-quiet variable still
/// shows a placeholder row.</summary>
public sealed partial class VariableWatchViewModel : ObservableObject, IRefreshable
{
    private readonly MonitorSession _session;
    private readonly Func<Task> _requestLoadEni;
    private readonly string _namePrefix;
    private readonly IReadOnlyList<EniVariable> _variables;

    private VariableWatchViewModel(MonitorSession session, Func<Task> requestLoadEni,
        string namePrefix, IReadOnlyList<EniVariable> variables)
    {
        _session = session;
        _requestLoadEni = requestLoadEni;
        _namePrefix = namePrefix;
        _variables = variables;
    }

    public static VariableWatchViewModel ForSlave(MonitorSession session, Func<Task> requestLoadEni,
        EniSlave? slave, IReadOnlyList<EniVariable> variables) =>
        new(session, requestLoadEni, slave is null ? "" : slave.Name + ".", variables);

    public static VariableWatchViewModel ForUnmatched(MonitorSession session, Func<Task> requestLoadEni,
        IReadOnlyList<EniVariable> unmatched) =>
        new(session, requestLoadEni, "", unmatched);

    // An explicit ENI is one way to have a configuration; a learned one that already produced a
    // non-empty variable list for this scope is another — the Variables tab cannot tell the two
    // apart (spec §7), so neither can this gate. Named for what it answers, not for one of its
    // two sources: a learned bus with no ENI file still has variables to show.
    //
    // Also reads true for the process-image scope when an ENI is loaded but Unmatched is empty:
    // a fully matched process image is a known, complete answer — zero unassigned variables — not
    // missing data, so it gets the same empty content list a variable-less slave gets, rather than
    // the "go get a configuration" prompt. ExplorerViewModel already hides the Process Image node
    // whenever Unmatched is empty, so this combination is unreachable from the live UI today; the
    // property stays correct on its own terms rather than leaning on that.
    public bool HasVariables => _session.Eni is not null || _variables.Count > 0;
    public ObservableCollection<VariableRowViewModel> Rows { get; } = [];

    [ObservableProperty] private string _filterText = "";

    partial void OnFilterTextChanged(string value) => Refresh();

    [RelayCommand]
    private Task LoadEniAsync() => _requestLoadEni();

    public void Refresh()
    {
        if (!HasVariables)
        {
            Rows.Clear();
            return;
        }

        var wanted = _variables
            .Select(v => (Variable: v, DisplayName: StripPrefix(v.Name)))
            .Where(r => r.DisplayName.Contains(FilterText, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Variable.Name, StringComparer.Ordinal)
            .ToList();

        if (wanted.Count != Rows.Count ||
            !wanted.Select(r => r.Variable.Name).SequenceEqual(Rows.Select(r => r.FullName)))
        {
            Rows.Clear();
            foreach (var (variable, displayName) in wanted)
                Rows.Add(new VariableRowViewModel
                {
                    FullName = variable.Name,
                    Name = displayName,
                    DataType = variable.DataType,
                    Direction = variable.IsInput ? "IN" : "OUT",
                });
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            var row = Rows[i];
            if (_session.ProcessImage.Current.TryGetValue(row.FullName, out var value))
            {
                row.Value = VariableValueFormat.Describe(value);
                row.Updated = value.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            }
            else
            {
                row.Value = "—";
                row.Updated = "—";
            }
        }
    }

    private string StripPrefix(string fullName) =>
        _namePrefix.Length > 0 && fullName.StartsWith(_namePrefix, StringComparison.Ordinal)
            ? fullName[_namePrefix.Length..]
            : fullName;
}
