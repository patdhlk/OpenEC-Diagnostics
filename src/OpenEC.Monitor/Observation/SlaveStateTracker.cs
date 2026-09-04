using OpenEC.Monitor.Protocol;

namespace OpenEC.Monitor.Observation;

/// <summary>Derives per-slave AL states from observed register traffic
/// (AL Control 0x0120, AL Status 0x0130, AL Status Code 0x0134).</summary>
public sealed class SlaveStateTracker(BusModel model)
{
    private const ushort AlControl = 0x0120;
    private const ushort AlStatus = 0x0130;
    private const ushort AlStatusCode = 0x0134;

    public IEnumerable<MonitorEvent> Observe(DateTimeOffset ts, EtherCatDatagram d, FrameDirection dir)
    {
        if (d.IsLogical) yield break;

        if (dir == FrameDirection.Outbound
            && d.Command is EtherCatCommand.Fpwr
            && d.Ado == AlControl
            && d.Payload.Length >= 1)
        {
            yield return new MonitorEvent.StateChangeRequested(ts, d.Adp,
                ToState((byte)(d.Payload.Span[0] & 0x0F)));
            yield break;
        }

        if (dir != FrameDirection.Returning || d.WorkingCounter == 0 || d.Payload.Length < 1)
            yield break;

        if (d.Command == EtherCatCommand.Brd && d.Ado == AlStatus)
        {
            var raw = d.Payload.Span[0];
            model.BusState = ToState((byte)(raw & 0x0F));
            model.BusStateUniform = System.Numerics.BitOperations.PopCount((uint)(raw & 0x0F)) == 1;
            yield break;
        }

        if (d.Command is not (EtherCatCommand.Fprd or EtherCatCommand.Aprd)) yield break;
        var address = d.Adp;
        if (d.Command == EtherCatCommand.Aprd && !model.TryMapAutoInc(d.Adp, out address))
            yield break;

        if (d.Ado == AlStatus)
        {
            var raw = d.Payload.Span[0];
            var newState = ToState((byte)(raw & 0x0F));
            var error = (raw & 0x10) != 0;
            var slave = model.GetOrAdd(address);
            slave.LastSeen = ts;
            if (slave.AlState != newState || slave.ErrorFlag != error)
            {
                var old = slave.AlState;
                slave.AlState = newState;
                slave.ErrorFlag = error;
                yield return new MonitorEvent.SlaveStateChanged(ts, address, old, newState, error);
            }
        }
        else if (d.Ado == AlStatusCode && d.Payload.Length >= 2)
        {
            var slave = model.GetOrAdd(address);
            slave.LastSeen = ts;
            slave.AlStatusCode = System.Buffers.Binary.BinaryPrimitives
                .ReadUInt16LittleEndian(d.Payload.Span);
        }
        else if (d.Command == EtherCatCommand.Fprd)
        {
            model.GetOrAdd(address).LastSeen = ts;
        }
    }

    private static SlaveAlState ToState(byte nibble) =>
        nibble is 1 or 2 or 3 or 4 or 8 ? (SlaveAlState)nibble : SlaveAlState.Unknown;
}
