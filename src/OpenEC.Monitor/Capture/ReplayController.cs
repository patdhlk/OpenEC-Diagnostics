namespace OpenEC.Monitor.Capture;

/// <summary>Thread-safe playback state for paced replay.</summary>
public sealed class ReplayController
{
    public const double MinSpeed = 0.1;
    public const double MaxSpeed = 10.0;

    private readonly object _gate = new();
    private bool _paused;
    private double _speed;
    private int _stepCredits;
    private TaskCompletionSource _wake;

    public ReplayController(bool startPaused = true, double speed = 1.0)
    {
        _paused = startPaused;
        _speed = double.IsNaN(speed) ? 1.0 : Math.Clamp(speed, MinSpeed, MaxSpeed);
        _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public double Speed
    {
        get
        {
            lock (_gate)
            {
                return _speed;
            }
        }
        set
        {
            lock (_gate)
            {
                _speed = double.IsNaN(value) ? 1.0 : Math.Clamp(value, MinSpeed, MaxSpeed);
            }
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!_paused)
            {
                _paused = true;
            }
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_paused)
            {
                _paused = false;
                _stepCredits = 0;
                _wake.TrySetResult();
            }
        }
    }

    /// <summary>Advance the paused replay by one frame (debugger-style single step). A no-op while
    /// running; grants one credit that the emit gate consumes to release exactly one frame, then the
    /// replay stays paused. Rapid calls queue.</summary>
    public void Step()
    {
        lock (_gate)
        {
            if (!_paused) return;
            _stepCredits++;
            _wake.TrySetResult();
        }
    }

    /// <summary>Gate awaited once per frame before it is emitted. <paramref name="gap"/> is the
    /// capture-time delta from the previous frame (Zero for the first). Returns when the frame may be
    /// emitted: immediately on a pending step credit (skipping the wait, staying paused), after pacing
    /// <paramref name="gap"/>/Speed when running, or once Resume()/Step() releases it when paused.</summary>
    internal async Task WaitToEmitAsync(TimeSpan gap, CancellationToken ct)
    {
        var remaining = gap > TimeSpan.Zero ? gap : TimeSpan.Zero;
        var quantum = TimeSpan.FromMilliseconds(20); // bounds how fast a mid-gap Speed/pause change is felt
        while (true)
        {
            Task? wake = null;
            var speed = 1.0;
            var pace = false;
            lock (_gate)
            {
                if (_paused)
                {
                    if (_stepCredits > 0)
                    {
                        _stepCredits--;
                        return; // release one frame, remain paused, skip the remaining gap
                    }
                    if (_wake.Task.IsCompleted)
                        _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    wake = _wake.Task;
                }
                else
                {
                    if (remaining <= TimeSpan.Zero) return;
                    speed = _speed;
                    pace = true;
                }
            }
            if (pace)
            {
                var stepCapture = remaining < quantum * speed ? remaining : quantum * speed;
                var wall = stepCapture / speed;
                await Task.Delay(wall, ct);
                remaining -= stepCapture;
            }
            else
            {
                await wake!.WaitAsync(ct);
            }
        }
    }
}
