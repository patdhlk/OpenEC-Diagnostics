using OpenEC.Inspector.Session;
using OpenEC.Inspector.ViewModels;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Synthesis;

namespace OpenEC.Inspector.Tests;

internal static class TestSessions
{
    public static string WriteDemoPcap()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-inspector-{Guid.NewGuid():N}.pcap");
        return SampleCapture.WriteDemo(path);
    }

    public static EniConfiguration LoadFixtureEni() =>
        EniConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml"));

    public static async Task<MonitorSession> RunFileSessionAsync(EniConfiguration? eni = null)
    {
        var session = new MonitorSession(new SourceSpec.File(WriteDemoPcap()), eni);
        session.Start();
        await session.Completion;
        return session;
    }

    public static string WriteBringupPcap()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-bringup-{Guid.NewGuid():N}.pcap");
        return BringupCapture.Write(path, cycles: 5);
    }

    /// <summary>A completed session over a synthetic INIT→OP bringup, so the learner has published a
    /// full configuration and the observer has had it applied.</summary>
    public static async Task<MonitorSession> BringupAsync()
    {
        var session = new MonitorSession(new SourceSpec.File(WriteBringupPcap()));
        session.Start();
        await session.Completion;
        return session;
    }

    /// <summary>A completed session over the branched synthetic bus, so the observer has a real
    /// port-level topology to draw.</summary>
    public static async Task<MonitorSession> BranchedAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-branched-{Guid.NewGuid():N}.pcap");
        BranchedBusCapture.Write(path, cycles: 5);
        var session = new MonitorSession(new SourceSpec.File(path));
        session.Start();
        await session.Completion;
        return session;
    }

    /// <summary>A completed session over a capture with no EtherCAT frames at all, so the learner
    /// never publishes and `Observer.Applied` stays null.</summary>
    public static async Task<MonitorSession> EmptyAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-empty-{Guid.NewGuid():N}.pcap");
        PcapFileWriter.Write(path, []);
        var session = new MonitorSession(new SourceSpec.File(path));
        session.Start();
        await session.Completion;
        return session;
    }

    /// <summary>A MainWindowViewModel with a completed bringup session, for exercising the
    /// session-level commands. Marshals inline so command execution is synchronous in tests.</summary>
    public static async Task<MainWindowViewModel> ShellWithBringupAsync(
        IFilePicker picker, bool withEni = false)
    {
        var vm = Shell(picker);
        vm.Start.PcapPath = WriteBringupPcap();
        if (withEni)
            vm.Start.EniPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.eni.xml");
        await vm.Start.StartFileCommand.ExecuteAsync(null);
        return vm;
    }

    /// <summary>A shell over a capture with no EtherCAT frames at all, so nothing is ever learned —
    /// the state in which the session-level commands have nothing to act on.</summary>
    public static async Task<MainWindowViewModel> ShellWithNothingLearnedAsync(IFilePicker picker)
    {
        var path = Path.Combine(Path.GetTempPath(), $"openec-shell-empty-{Guid.NewGuid():N}.pcap");
        PcapFileWriter.Write(path, []);
        var vm = Shell(picker);
        vm.Start.PcapPath = path;
        await vm.Start.StartFileCommand.ExecuteAsync(null);
        return vm;
    }

    private static MainWindowViewModel Shell(IFilePicker picker) =>
        new(() => [],
            (spec, eni) => new MonitorSession(spec, eni),
            picker,
            marshal: action => action());
}
