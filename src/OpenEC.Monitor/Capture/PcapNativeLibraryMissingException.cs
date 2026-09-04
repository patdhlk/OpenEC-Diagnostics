namespace OpenEC.Monitor.Capture;

/// <summary>Thrown when the native packet-capture library cannot be loaded: Npcap on Windows,
/// libpcap on Linux/macOS. It is needed even to open a capture file offline, so the raw
/// <see cref="DllNotFoundException"/> SharpPcap surfaces is translated into a message that names
/// what to install.</summary>
public sealed class PcapNativeLibraryMissingException : Exception
{
    public PcapNativeLibraryMissingException(DllNotFoundException inner)
        : base(BuildMessage(), inner)
    {
    }

    private static string BuildMessage() =>
        OperatingSystem.IsWindows()
            ? "the packet-capture library Npcap is not installed. Install it from https://npcap.com/ "
              + "in WinPcap API-compatible mode; it is required even to open a capture file offline."
            : "the packet-capture library libpcap is not installed. On Debian/Ubuntu install "
              + "'libpcap0.8'; it is required even to open a capture file offline.";
}

/// <summary>Runs a native SharpPcap call and translates a missing-native-library
/// <see cref="DllNotFoundException"/> into <see cref="PcapNativeLibraryMissingException"/>.</summary>
internal static class PcapNative
{
    public static T Guard<T>(Func<T> access)
    {
        try
        {
            return access();
        }
        catch (DllNotFoundException ex)
        {
            throw new PcapNativeLibraryMissingException(ex);
        }
    }
}
