using System.Runtime.CompilerServices;
using System.Threading.Channels;
using OpenEC.Inspector.ViewModels;
using OpenEC.Monitor.Capture;

namespace OpenEC.Inspector.Tests;

/// <summary>Parks until cancelled, then honors the cancellation. Simulates a quiet live NIC.</summary>
internal sealed class BlockingCaptureSource : ICaptureSource
{
    public async IAsyncEnumerable<RawFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var _ = ct.Register(() => parked.TrySetResult());
        await parked.Task;
        ct.ThrowIfCancellationRequested();
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Waits for <see cref="Trigger"/>, then throws. Simulates a mid-session capture fault.</summary>
internal sealed class TriggeredFaultSource : ICaptureSource
{
    public TaskCompletionSource Trigger { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async IAsyncEnumerable<RawFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Trigger.Task;
        // The condition is always true; it exists so the compiler keeps the iterator shape
        // without flagging the yield below as unreachable.
        if (Trigger.Task.IsCompleted) throw new IOException("boom");
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Testable file picker that returns a predetermined result or null on cancel.</summary>
internal sealed class FakeFilePicker(string? result = null, string? saveResult = null) : IFilePicker
{
    public Task<string?> PickFileAsync(string title, params string[] extensions) =>
        Task.FromResult(result);

    public Task<string?> PickSaveFileAsync(string title, string defaultName, string extension) =>
        Task.FromResult(saveResult);
}

/// <summary>Frames are pushed by the test and flow to the pump immediately —
/// lets a test grow the event log between two Refresh() calls.</summary>
internal sealed class PushCaptureSource : ICaptureSource
{
    private readonly Channel<RawFrame> _channel = Channel.CreateUnbounded<RawFrame>();

    public void Push(RawFrame frame) => _channel.Writer.TryWrite(frame);
    public void Complete() => _channel.Writer.TryComplete();

    public IAsyncEnumerable<RawFrame> CaptureAsync(CancellationToken ct = default) =>
        _channel.Reader.ReadAllAsync(ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
