namespace OpenEC.Monitor.Protocol;

/// <summary>EtherCAT datagram command per ETG.1000.4.</summary>
public enum EtherCatCommand : byte
{
    Nop = 0, Aprd = 1, Apwr = 2, Aprw = 3, Fprd = 4, Fpwr = 5, Fprw = 6,
    Brd = 7, Bwr = 8, Brw = 9, Lrd = 10, Lwr = 11, Lrw = 12, Armw = 13, Frmw = 14,
}
