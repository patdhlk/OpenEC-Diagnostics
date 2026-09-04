using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenEC.Inspector.Session;

namespace OpenEC.Inspector.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject, IRefreshable
{
    private const string Placeholder = "—"; // em dash

    private readonly MonitorSession _session;

    public DashboardViewModel(MonitorSession session) => _session = session;

    [ObservableProperty] private string _cyclicTxRate = Placeholder;
    [ObservableProperty] private string _queuedTxRate = Placeholder;
    [ObservableProperty] private string _rxRate = Placeholder;
    [ObservableProperty] private string _cycleTime = Placeholder;
    [ObservableProperty] private string _wkcMismatches = "0";
    [ObservableProperty] private string _lostFrames = "0";
    [ObservableProperty] private string _ringLostFrames = "0";
    [ObservableProperty] private string _frameTotals = "0";
    [ObservableProperty] private string _malformed = "0";

    public void Refresh()
    {
        var s = _session.Statistics;
        CyclicTxRate = FormatRate(s.OutboundCyclicFramesPerSecond);
        QueuedTxRate = FormatRate(s.OutboundQueuedFramesPerSecond);
        RxRate = FormatRate(s.ReturningFramesPerSecond);
        CycleTime = s.EstimatedCycleTime is { } cycle
            ? cycle.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture) + " ms"
            : Placeholder;
        WkcMismatches = s.WkcMismatches.ToString("N0", CultureInfo.InvariantCulture);
        LostFrames = s.SuspectedLostFrames.ToString("N0", CultureInfo.InvariantCulture);
        RingLostFrames = s.RingLostFrames.ToString("N0", CultureInfo.InvariantCulture);
        FrameTotals = string.Create(CultureInfo.InvariantCulture,
            $"{s.EtherCatFrames:N0} EtherCAT / {s.TotalFrames:N0} total");
        Malformed = s.MalformedFrames.ToString("N0", CultureInfo.InvariantCulture);
    }

    private static string FormatRate(double? rate) =>
        rate is { } r ? r.ToString("N0", CultureInfo.InvariantCulture) + " /s" : Placeholder;
}
