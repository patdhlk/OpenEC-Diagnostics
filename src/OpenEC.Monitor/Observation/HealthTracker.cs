using System.Buffers.Binary;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Observation;

/// <summary>Aggregates bus AL state, device count, and DC sync into a single health snapshot,
/// deriving DC sync from observed 0x092C (System Time Difference) register traffic.</summary>
public sealed class HealthTracker
{
    private const ushort AlStatus = 0x0130;
    private const ushort DcSystemTimeDiff = 0x092C;
    public const int DcSyncToleranceNs = 10_000;
    private readonly BusModel _model;
    private int? _configuredDevices;
    private BusHealth? _lastEmitted;

    public HealthTracker(BusModel model, EniConfiguration? eni = null)
    {
        _model = model;
        Rebind(eni);
    }

    public void Rebind(EniConfiguration? eni)
    {
        _configuredDevices = eni?.Slaves?.Count;
    }

    public BusHealth Compute()
    {
        var foundDevices = _model.Slaves.Count(s => s.LastSeen is not null);

        // DC sync: Unknown when no observed slave has a value; OutOfSync when any exceeds tolerance;
        // else Synced. MaxDcDeviationNs = max magnitude among observed.
        var dcValues = _model.Slaves
            .Where(s => s.DcSystemTimeDiffNs is not null)
            .Select(s => s.DcSystemTimeDiffNs!.Value)
            .ToList();

        var dcSync = DcSyncState.Unknown;
        int? maxDeviation = null;

        if (dcValues.Count > 0)
        {
            maxDeviation = dcValues.Max(Math.Abs);
            dcSync = dcValues.Any(v => Math.Abs(v) > DcSyncToleranceNs)
                ? DcSyncState.OutOfSync
                : DcSyncState.Synced;
        }

        return new BusHealth(
            _model.BusState,
            _model.BusStateUniform,
            foundDevices,
            _configuredDevices,
            dcSync,
            maxDeviation);
    }

    public IEnumerable<MonitorEvent> Observe(DateTimeOffset ts, EtherCatDatagram d, FrameDirection dir)
    {
        if (d.IsLogical) yield break;
        if (dir != FrameDirection.Returning || d.WorkingCounter == 0) yield break;

        var shouldRecompute = false;

        // Decode returning reads of 0x092C (System Time Difference)
        if (d.Ado == DcSystemTimeDiff && d.Payload.Length >= 4)
        {
            if (d.Command is not (EtherCatCommand.Fprd or EtherCatCommand.Aprd)) yield break;

            var address = d.Adp;
            if (d.Command == EtherCatCommand.Aprd && !_model.TryMapAutoInc(d.Adp, out address))
                yield break;

            // DC register 0x092C is 32-bit signed-magnitude:
            // magnitude = raw & 0x7FFFFFFF, sign = (raw & 0x8000_0000) != 0 (local ahead of reference)
            var raw = BinaryPrimitives.ReadInt32LittleEndian(d.Payload.Span);
            var magnitude = raw & 0x7FFFFFFF;
            var isNegative = (raw & unchecked((int)0x8000_0000)) != 0;
            var signedDiff = isNegative ? -magnitude : magnitude;

            var slave = _model.GetOrAdd(address);
            slave.DcSystemTimeDiffNs = signedDiff;
            slave.LastSeen = ts;
            shouldRecompute = true;
        }
        // Also recompute when AL status is read (0x0130), as BusState/BusStateUniform may have changed
        else if (d.Ado == AlStatus && d.Payload.Length >= 1)
        {
            shouldRecompute = true;
        }

        if (!shouldRecompute) yield break;

        // Emit BusHealthChanged only when aggregate changes
        var current = Compute();
        if (!Equals(_lastEmitted, current))
        {
            _lastEmitted = current;
            yield return new MonitorEvent.BusHealthChanged(ts, current);
        }
    }
}
