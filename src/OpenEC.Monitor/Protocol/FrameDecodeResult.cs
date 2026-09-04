namespace OpenEC.Monitor.Protocol;

public abstract record FrameDecodeResult
{
    public sealed record Success(EtherCatFrame Frame) : FrameDecodeResult;
    public sealed record NotEtherCat(ushort EtherType) : FrameDecodeResult;
    public sealed record Malformed(string Reason) : FrameDecodeResult;
}
