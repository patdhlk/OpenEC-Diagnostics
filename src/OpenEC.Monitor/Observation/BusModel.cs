using OpenEC.Monitor.Eni;

namespace OpenEC.Monitor.Observation;

public sealed class BusModel
{
    private readonly Dictionary<ushort, SlaveStatus> _slaves = new();
    private readonly Dictionary<ushort, ushort> _autoIncToPhys = new();

    public IReadOnlyCollection<SlaveStatus> Slaves => _slaves.Values;
    public SlaveAlState BusState { get; internal set; }
    public bool BusStateUniform { get; internal set; }

    public SlaveStatus GetOrAdd(ushort address)
    {
        if (!_slaves.TryGetValue(address, out var slave))
            _slaves[address] = slave = new SlaveStatus { Address = address };
        return slave;
    }

    public bool TryGet(ushort address, out SlaveStatus? slave) =>
        _slaves.TryGetValue(address, out slave);

    public bool TryMapAutoInc(ushort autoIncAdp, out ushort configuredAddress) =>
        _autoIncToPhys.TryGetValue(autoIncAdp, out configuredAddress);

    public void Seed(EniConfiguration eni)
    {
        foreach (var s in eni.Slaves)
        {
            var slave = GetOrAdd(s.PhysAddr);
            slave.ConfiguredName = s.Name;
            slave.VendorId = s.VendorId;
            slave.ProductCode = s.ProductCode;
            slave.Revision = s.RevisionNo;
            _autoIncToPhys[s.AutoIncAddr] = s.PhysAddr;
        }
    }
}
