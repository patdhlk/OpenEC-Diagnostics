using OpenEC.Monitor;
using OpenEC.Monitor.Capture;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;

namespace OpenEC.Inspector.Session;

public enum SessionState { Idle, Running, Completed, Stopped, Faulted }

/// <summary>Lifecycle wrapper around EtherCatMonitor: one capture source, one pump task,
/// one state machine. UI code must read the observer via snapshots only.</summary>
public sealed class MonitorSession : IAsyncDisposable
{
    private readonly EtherCatMonitor _monitor;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _pump = Task.CompletedTask;

    public MonitorSession(SourceSpec source, EniConfiguration? eni = null)
        : this(CreateMonitor(source, eni), source.Description, eni) =>
        Source = source;

    /// <summary>Composition/test seam mirroring EtherCatMonitor.FromSource.</summary>
    public MonitorSession(EtherCatMonitor monitor, string sourceDescription, EniConfiguration? eni = null)
    {
        _monitor = monitor;
        SourceDescription = sourceDescription;
        Eni = eni;
    }

    private static EtherCatMonitor CreateMonitor(SourceSpec source, EniConfiguration? eni)
    {
        ICaptureSource capture = source switch
        {
            SourceSpec.Live l => new LiveCaptureSource(l.InterfaceName),
            SourceSpec.File f => new PcapFileSource(f.Path),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
        if (source.RecordPath is { } recordPath)
            capture = new RecordingCaptureSource(capture, recordPath);
        return EtherCatMonitor.FromSource(capture, new EtherCatMonitorOptions
        {
            Eni = eni,
            // No toggle: caching is how the Inspector decodes a machine it attaches to mid-run, and
            // the surfaces that report a learned configuration already say where every fact came
            // from — provenance names the cache, so a cached fact is never passed off as observed.
            LearnedCache = LearnedBusCache.Default(),
        });
    }

    public BusObserver Observer => _monitor.Observer;

    /// <summary>What the learner has derived from this capture, independent of whether the observer
    /// was rebound to it. With an ENI loaded the ENI stays the authority and
    /// <see cref="BusObserver.Applied"/> is null all session by design — but the learner still ran,
    /// and this is where its result lives. Surfaces that export or describe the reconstruction must
    /// read it here, or they go silent for every ENI-loaded session.</summary>
    public LearnedConfiguration? Learned => _monitor.Learned;

    public TrafficStatistics Statistics => _monitor.Statistics;
    public ProcessImage ProcessImage => _monitor.ProcessImage;
    public EniConfiguration? Eni { get; }
    public SourceSpec? Source { get; }
    public string? RecordPath => Source?.RecordPath;
    public string SourceDescription { get; }
    public SessionState State { get; private set; } = SessionState.Idle;
    public Exception? Fault { get; private set; }
    public Task Completion => _done.Task;
    public long FramesSeen => Statistics.TotalFrames;
    public long MalformedFrames => Statistics.MalformedFrames;

    /// <summary>Raised from the pump thread, synchronously, before <see cref="Completion"/> resolves —
    /// subscribers always observe the terminal state first. UI subscribers must marshal to their own
    /// thread rather than block synchronously on this event: a handler that synchronously awaits or
    /// blocks on <see cref="Completion"/> will deadlock, since Completion only resolves once this event
    /// has finished dispatching to every subscriber.</summary>
    public event Action<SessionState>? StateChanged;

    public void Start()
    {
        lock (_gate)
        {
            if (State != SessionState.Idle)
                throw new InvalidOperationException($"Session already started (state: {State}).");
            State = SessionState.Running;
            // Assigned under the same lock as the state transition so DisposeAsync can never
            // observe the stale Task.CompletedTask default while the real pump is running.
            _pump = Task.Run(RunPumpAsync);
        }
        StateChanged?.Invoke(SessionState.Running);
    }

    private async Task RunPumpAsync()
    {
        try
        {
            await _monitor.RunAsync(_cts.Token);
            CompleteWith(_cts.IsCancellationRequested ? SessionState.Stopped : SessionState.Completed);
        }
        catch (OperationCanceledException)
        {
            CompleteWith(SessionState.Stopped);
        }
        catch (Exception ex)
        {
            Fault = ex;
            CompleteWith(SessionState.Faulted);
        }
    }

    public async Task StopAsync()
    {
        var idleStop = false;
        lock (_gate)
        {
            if (State == SessionState.Idle)
            {
                State = SessionState.Stopped;
                idleStop = true;
            }
        }
        if (idleStop)
        {
            // Subscribers must observe the terminal state before Completion resolves; TrySetResult
            // in `finally` keeps that unskippable even if a subscriber throws.
            try
            {
                StateChanged?.Invoke(SessionState.Stopped);
            }
            finally
            {
                _done.TrySetResult();
            }
            return;
        }
        _cts.Cancel();
        await Completion.ConfigureAwait(false);
    }

    private void CompleteWith(SessionState terminal)
    {
        var changed = false;
        lock (_gate)
        {
            if (State == SessionState.Running)
            {
                State = terminal;
                changed = true;
            }
        }
        // Subscribers must observe the terminal state before Completion resolves; TrySetResult
        // in `finally` keeps that unskippable even if a subscriber throws.
        try
        {
            if (changed) StateChanged?.Invoke(terminal);
        }
        finally
        {
            _done.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        Task pump;
        lock (_gate) { pump = _pump; }
        try { await pump.ConfigureAwait(false); } catch { /* terminal state already captured */ }
        await _monitor.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        var changed = false;
        lock (_gate)
        {
            if (State is SessionState.Idle or SessionState.Running)
            {
                State = SessionState.Stopped;
                changed = true;
            }
        }
        try
        {
            if (changed) StateChanged?.Invoke(SessionState.Stopped);
        }
        finally
        {
            _done.TrySetResult();
        }
    }
}
