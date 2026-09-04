namespace OpenEC.Monitor.Protocol;

public sealed class MalformedFrameException(string message) : Exception(message);
