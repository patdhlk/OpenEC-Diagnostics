namespace OpenEC.Monitor.Capture;

public readonly record struct RawFrame(DateTimeOffset Timestamp, ReadOnlyMemory<byte> Data);
