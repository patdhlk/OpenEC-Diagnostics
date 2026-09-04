namespace OpenEC.Monitor.Capture;

public interface ICaptureSource : IAsyncDisposable
{
    IAsyncEnumerable<RawFrame> CaptureAsync(CancellationToken ct = default);

    /// <summary>True when <see cref="CaptureAsync"/> can be enumerated more than once and yields
    /// the same frames each time. Only then can learning run a cheap discovery pass before the
    /// decode pass, so that process data arriving before the configuration converged is still
    /// mapped. False for live interfaces, and false for the recording decorator — re-enumerating
    /// that would write the capture twice.</summary>
    bool SupportsMultiplePasses => false;
}
