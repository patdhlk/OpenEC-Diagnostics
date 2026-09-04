using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenEC.Inspector.Session;
using OpenEC.Monitor.Eni;

namespace OpenEC.Inspector.ViewModels;

public sealed partial class StartViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<(string Name, string? Description)>> _listDevices;
    private readonly Func<SourceSpec, EniConfiguration?, MonitorSession> _createSession;
    private readonly IFilePicker _filePicker;
    private readonly Action<MonitorSession> _onStarted;
    private readonly TimeSpan _earlyFaultProbe;

    public StartViewModel(
        Func<IReadOnlyList<(string Name, string? Description)>> listDevices,
        Func<SourceSpec, EniConfiguration?, MonitorSession> createSession,
        IFilePicker filePicker,
        Action<MonitorSession> onStarted,
        TimeSpan? earlyFaultProbe = null)
    {
        _listDevices = listDevices;
        _createSession = createSession;
        _filePicker = filePicker;
        _onStarted = onStarted;
        _earlyFaultProbe = earlyFaultProbe ?? TimeSpan.FromMilliseconds(500);
        RefreshDevices();
    }

    public ObservableCollection<string> Devices { get; } = [];

    [ObservableProperty] private string? _selectedDevice;
    [ObservableProperty] private string? _pcapPath;
    [ObservableProperty] private string? _eniPath;
    [ObservableProperty] private string? _recordPath;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isStarting;

    [RelayCommand]
    private void RefreshDevices()
    {
        Devices.Clear();
        ErrorMessage = null;
        try
        {
            foreach (var (name, _) in _listDevices()) Devices.Add(name);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task BrowsePcapAsync() =>
        PcapPath = await _filePicker.PickFileAsync("Open capture", "pcap", "pcapng") ?? PcapPath;

    [RelayCommand]
    private async Task BrowseEniAsync() =>
        EniPath = await _filePicker.PickFileAsync("Load ENI", "xml") ?? EniPath;

    [RelayCommand]
    private async Task BrowseRecordAsync() =>
        RecordPath = await _filePicker.PickSaveFileAsync("Record capture", "capture.pcap", "pcap") ?? RecordPath;

    [RelayCommand]
    private Task StartLiveAsync() =>
        SelectedDevice is null
            ? SetError("Select a capture interface first.")
            : StartAsync(new SourceSpec.Live(SelectedDevice)
            {
                RecordPath = string.IsNullOrWhiteSpace(RecordPath) ? null : RecordPath,
            });

    [RelayCommand]
    private Task StartFileAsync() =>
        string.IsNullOrWhiteSpace(PcapPath) || !System.IO.File.Exists(PcapPath)
            ? SetError("Choose an existing .pcap/.pcapng file first.")
            : StartAsync(new SourceSpec.File(PcapPath));

    private Task SetError(string message)
    {
        ErrorMessage = message;
        return Task.CompletedTask;
    }

    private async Task StartAsync(SourceSpec spec)
    {
        ErrorMessage = null;
        EniConfiguration? eni = null;
        if (!string.IsNullOrWhiteSpace(EniPath))
        {
            try
            {
                eni = EniConfiguration.Load(EniPath);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"ENI could not be loaded: {ex.Message}";
                return;
            }
        }

        IsStarting = true;
        try
        {
            var session = _createSession(spec, eni);
            session.Start();
            // Early-fault probe (mirrors the CLI's live command): a bad interface or file
            // faults within moments — don't switch to the shell just to show a dead session.
            await Task.WhenAny(session.Completion, Task.Delay(_earlyFaultProbe));
            if (session.State == SessionState.Faulted)
            {
                ErrorMessage = FormatFault(session.Fault!);
                await session.DisposeAsync();
                return;
            }
            _onStarted(session);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsStarting = false;
        }
    }

    internal static string FormatFault(Exception ex) =>
        ex.Message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("BPF", StringComparison.OrdinalIgnoreCase)
            ? $"{ex.Message}\nOn macOS, capture needs BPF access — see docs/tap-setup.md (ChmodBPF)."
            : ex.Message;
}
