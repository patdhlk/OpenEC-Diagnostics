using OpenEC.Inspector.Session;
using OpenEC.Monitor;
using OpenEC.Monitor.Learning;

namespace OpenEC.Inspector.Tests.Session;

public class MonitorSessionTests
{
    [Fact]
    public async Task File_session_pumps_to_completion()
    {
        await using var session = await TestSessions.RunFileSessionAsync();

        Assert.Equal(SessionState.Completed, session.State);
        Assert.Null(session.Fault);
        Assert.Equal(103, session.FramesSeen);
        Assert.Equal(0, session.MalformedFrames);
        Assert.NotEmpty(session.Observer.SnapshotSlaves());
    }

    /// <summary>The Inspector caches with no toggle, so a machine whose startup it watched once is
    /// recognised on a later mid-run attach. The session supplied no cache at all until this was
    /// wired, which left the README's "cached by fingerprint" claim unreachable from the GUI.
    /// <c>CacheRedirect</c> keeps this off the developer's real profile.</summary>
    [Fact]
    public async Task A_completed_bringup_session_caches_the_learned_bus()
    {
        await using var session = await TestSessions.BringupAsync();

        var learned = session.Learned!.Configuration;
        var cache = new LearnedBusCache(LearnedBusCache.DefaultDirectory);
        Assert.True(cache.TryLoad(LearnedBusCache.Fingerprint(learned), out var cached));
        Assert.Equal(learned.Variables.Count, cached!.Variables.Count);
    }

    [Fact]
    public async Task Source_description_is_the_file_name()
    {
        var path = TestSessions.WriteDemoPcap();
        await using var session = new MonitorSession(new SourceSpec.File(path));

        Assert.Equal(Path.GetFileName(path), session.SourceDescription);
        Assert.Equal(new SourceSpec.File(path), session.Source);
    }

    [Fact]
    public void Live_source_description_is_the_interface_name() =>
        Assert.Equal("en11", new SourceSpec.Live("en11").Description);

    [Fact]
    public async Task Start_twice_throws()
    {
        await using var session = await TestSessions.RunFileSessionAsync();

        Assert.Throws<InvalidOperationException>(session.Start);
    }

    [Fact]
    public async Task State_changes_fire_the_event()
    {
        var states = new List<SessionState>();
        var path = TestSessions.WriteDemoPcap();
        await using var session = new MonitorSession(new SourceSpec.File(path));
        session.StateChanged += states.Add;

        session.Start();
        await session.Completion;

        Assert.Equal([SessionState.Running, SessionState.Completed], states);
    }

    [Fact]
    public async Task Throwing_subscriber_does_not_block_completion()
    {
        var path = TestSessions.WriteDemoPcap();
        await using var session = new MonitorSession(new SourceSpec.File(path));
        session.StateChanged += state =>
        {
            if (state == SessionState.Completed) throw new InvalidOperationException("boom");
        };

        session.Start();
        var finished = await Task.WhenAny(session.Completion, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.Same(session.Completion, finished);
        Assert.Equal(SessionState.Completed, session.State);
    }

    [Fact]
    public async Task Stop_during_a_running_session_yields_stopped()
    {
        await using var session = new MonitorSession(
            EtherCatMonitor.FromSource(new BlockingCaptureSource()), "test");
        session.Start();

        await session.StopAsync();

        Assert.Equal(SessionState.Stopped, session.State);
        Assert.Null(session.Fault);
    }

    [Fact]
    public async Task Capture_fault_yields_faulted_with_the_exception()
    {
        var source = new TriggeredFaultSource();
        await using var session = new MonitorSession(EtherCatMonitor.FromSource(source), "test");
        var states = new List<SessionState>();
        session.StateChanged += states.Add;
        session.Start();

        source.Trigger.SetResult();
        await session.Completion;

        Assert.Equal(SessionState.Faulted, session.State);
        Assert.IsType<IOException>(session.Fault);
        Assert.Equal("boom", session.Fault!.Message);
        Assert.Equal([SessionState.Running, SessionState.Faulted], states);
    }

    [Fact]
    public async Task Nonexistent_file_faults_instead_of_throwing()
    {
        await using var session = new MonitorSession(
            new SourceSpec.File("/nonexistent/no-such-capture.pcap"));
        session.Start();

        await session.Completion;

        Assert.Equal(SessionState.Faulted, session.State);
        Assert.NotNull(session.Fault);
    }

    [Fact]
    public async Task Dispose_while_running_cancels_and_stops()
    {
        var session = new MonitorSession(
            EtherCatMonitor.FromSource(new BlockingCaptureSource()), "test");
        session.Start();

        await session.DisposeAsync();

        Assert.Equal(SessionState.Stopped, session.State);
        Assert.True(session.Completion.IsCompleted);
    }

    [Fact]
    public async Task Stop_before_start_completes_immediately_as_stopped()
    {
        await using var session = new MonitorSession(
            EtherCatMonitor.FromSource(new BlockingCaptureSource()), "test");

        await session.StopAsync();

        Assert.Equal(SessionState.Stopped, session.State);
    }
}
