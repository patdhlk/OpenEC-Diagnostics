namespace OpenEC.CLI.Reporting;

/// <summary>Spectre markup for the bus-health fields, shared by the live dashboard and the analyze
/// overview so both render Ok/Warning/Fault and DC state identically. Takes the enum <c>ToString()</c>
/// names rather than the enums themselves, so both a <c>BusHealth</c> and a <c>HealthReport</c> feed it.</summary>
public static class HealthFormat
{
    public static string Level(string level) => level switch
    {
        "Ok" => "[green]Ok[/]",
        "Warning" => "[yellow]Warning[/]",
        "Fault" => "[red]Fault[/]",
        _ => level,
    };

    public static string Dc(string dcSync) => dcSync switch
    {
        "Synced" => "[green]synced[/]",
        "OutOfSync" => "[red]out of sync[/]",
        _ => "[grey]unmonitored[/]",
    };

    /// <summary>The slaves whose process data stopped moving. Yellow rather than red: a device can
    /// hold an input steady legitimately, so this points somewhere to look rather than declaring a
    /// fault.</summary>
    public static string StaleProcessData(IReadOnlyList<ushort> stale) =>
        stale.Count == 0
            ? "[grey]none[/]"
            : $"[yellow]{string.Join(", ", stale)}[/]";

    /// <summary>Found/configured device count, red when they disagree. Just the found count when no
    /// configuration is in force (nothing to compare against).</summary>
    public static string Devices(int found, int? configured) =>
        configured is { } cfg
            ? found == cfg ? $"{found}/{cfg}" : $"[red]{found}/{cfg}[/]"
            : found.ToString();
}
