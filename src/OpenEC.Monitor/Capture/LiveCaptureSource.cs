using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SharpPcap;
using SharpPcap.LibPcap;

namespace OpenEC.Monitor.Capture;

/// <summary>Captures EtherCAT frames from a live interface (e.g. the TAP monitor port NIC).</summary>
public sealed class LiveCaptureSource(string interfaceName) : ICaptureSource
{
    // Plain EtherCAT (ethertype 0x88a4, optionally VLAN-tagged) plus CU2508 prefix-ESL, whose
    // gigabit frames carry the Beckhoff ESL cookie in the destination-MAC slot (0x88a4 sits buried
    // 16 bytes in, so the ethertype term alone never matches them). Postfix-ESL (ET2000) still
    // matches the ethertype term. The fourth cookie octet is 0x10 or 0x11.
    public const string BpfFilter =
        "ether proto 0x88a4 or (vlan and ether proto 0x88a4) "
        + "or ether dst 01:01:05:10:00:00 or ether dst 01:01:05:11:00:00";

    private LibPcapLiveDevice? _device;

    public async IAsyncEnumerable<RawFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _device = PcapNative.Guard(() => LibPcapLiveDeviceList.Instance
                .FirstOrDefault(d => d.Name == interfaceName))
            ?? throw new ArgumentException($"capture interface '{interfaceName}' not found", nameof(interfaceName));
        var channel = Channel.CreateBounded<RawFrame>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        _device.OnPacketArrival += (_, e) =>
        {
            var raw = e.GetPacket();
            var utc = DateTime.SpecifyKind(raw.Timeval.Date, DateTimeKind.Utc);
            channel.Writer.TryWrite(new RawFrame(new DateTimeOffset(utc), raw.Data));
        };
        _device.Open(new DeviceConfiguration
        {
            Mode = DeviceModes.Promiscuous,
            ReadTimeout = 250,
            Snaplen = 65536,
            Immediate = true,
        });
        _device.Filter = BpfFilter;
        _device.StartCapture();
        await foreach (var frame in channel.Reader.ReadAllAsync(ct))
            yield return frame;
    }

    public ValueTask DisposeAsync()
    {
        if (_device is { } device)
        {
            if (device.Started) device.StopCapture();
            device.Dispose();
            _device = null;
        }
        return ValueTask.CompletedTask;
    }
}
