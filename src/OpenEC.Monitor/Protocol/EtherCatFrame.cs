namespace OpenEC.Monitor.Protocol;

public sealed record EtherCatFrame(
    MacAddress Destination,
    MacAddress Source,
    ushort? VlanId,
    IReadOnlyList<EtherCatDatagram> Datagrams);
