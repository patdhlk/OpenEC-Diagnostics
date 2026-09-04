// src/OpenEC.Monitor/Observation/CycleEstimator.cs
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Observation;

/// <summary>Estimates the bus cycle time from the cadence of the most frequent
/// outbound logical (cyclic) datagram.</summary>
public sealed class CycleEstimator
{
    private const int WindowSize = 128;
    private const int MinSamples = 8;

    private readonly Dictionary<(EtherCatCommand, uint), Queue<DateTimeOffset>> _timestamps = new();
    private readonly Dictionary<(EtherCatCommand, uint), long> _counts = new();

    public void Observe(DateTimeOffset ts, EtherCatDatagram d, FrameDirection dir)
    {
        if (dir != FrameDirection.Outbound || !d.IsLogical) return;
        var key = (d.Command, d.RawAddress);
        _counts[key] = _counts.GetValueOrDefault(key) + 1;
        if (!_timestamps.TryGetValue(key, out var queue))
            _timestamps[key] = queue = new Queue<DateTimeOffset>();
        queue.Enqueue(ts);
        while (queue.Count > WindowSize) queue.Dequeue();
    }

    public TimeSpan? EstimatedCycleTime
    {
        get
        {
            if (_counts.Count == 0) return null;
            var top = _counts.MaxBy(kv => kv.Value).Key;
            var samples = _timestamps[top].ToArray();
            if (samples.Length < MinSamples) return null;
            var deltas = new List<double>(samples.Length - 1);
            for (var i = 1; i < samples.Length; i++)
                deltas.Add((samples[i] - samples[i - 1]).TotalMilliseconds);
            deltas.Sort();
            return TimeSpan.FromMilliseconds(deltas[deltas.Count / 2]);
        }
    }
}
