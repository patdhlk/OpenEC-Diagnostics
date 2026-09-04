using SharpPcap.LibPcap;

namespace OpenEC.Monitor.Capture;

public static class CaptureDevices
{
    public static IReadOnlyList<(string Name, string? Description)> List() =>
        PcapNative.Guard(() =>
            LibPcapLiveDeviceList.Instance.Select(d => (d.Name, (string?)d.Description)).ToList());
}
