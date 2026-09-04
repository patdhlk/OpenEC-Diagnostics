namespace OpenEC.Monitor.Topology;

/// <summary>One port's ESC error counters. Every field is nullable and null means the register
/// was never read: a passive observer sees these only if the master polls them, and rendering an
/// unread counter as 0 would claim a healthy port we know nothing about.</summary>
public sealed record PortCounters(
    byte? InvalidFrame,
    byte? RxError,
    byte? ForwardedRxError,
    byte? LostLink)
{
    public static readonly PortCounters Unknown = new(null, null, null, null);

    public bool AnyKnown =>
        InvalidFrame is not null || RxError is not null
        || ForwardedRxError is not null || LostLink is not null;

    /// <summary>True when any known counter is non-zero. Null counters do not make a port look
    /// healthy — they make it look unknown, which <see cref="AnyKnown"/> distinguishes.</summary>
    public bool AnyError =>
        InvalidFrame > 0 || RxError > 0 || ForwardedRxError > 0 || LostLink > 0;

    /// <summary>Folds a newer partial read over this one. A field the newer read did not cover
    /// keeps its previous value rather than being erased, because masters read these registers
    /// in blocks that do not all cover the same fields.</summary>
    public PortCounters Merge(PortCounters newer) => new(
        newer.InvalidFrame ?? InvalidFrame,
        newer.RxError ?? RxError,
        newer.ForwardedRxError ?? ForwardedRxError,
        newer.LostLink ?? LostLink);
}
