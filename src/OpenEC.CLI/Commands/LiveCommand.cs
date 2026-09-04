using System.ComponentModel;
using OpenEC.CLI.Reporting;
using OpenEC.Monitor;
using OpenEC.Monitor.Ads;
using OpenEC.Monitor.Eni;
using OpenEC.Monitor.Learning;
using OpenEC.Monitor.Observation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenEC.CLI.Commands;

public sealed class LiveCommand : AsyncCommand<LiveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--interface <name>")]
        [Description("Capture interface connected to the TAP monitor port")]
        public string? Interface { get; init; }

        [CommandOption("--eni")]
        public string? Eni { get; init; }

        [CommandOption("--esi-dir")]
        public string? EsiDirectory { get; init; }

        [CommandOption("--ads")]
        [Description("AMS NetId of a TwinCAT target for active enrichment")]
        public string? AdsNetId { get; init; }

        [CommandOption("--duration")]
        [Description("Stop after this many seconds (default: until Ctrl-C)")]
        public int? DurationSeconds { get; init; }

        [CommandOption("--learn-out")]
        [Description("Write the learned bus configuration to this ENI XML path when the session ends")]
        public string? LearnOut { get; init; }

        public override ValidationResult Validate() =>
            string.IsNullOrWhiteSpace(Interface)
                ? ValidationResult.Error("--interface is required")
                : ValidationResult.Success();
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        EniConfiguration? eni = null;
        if (settings.Eni is not null)
        {
            if (!File.Exists(settings.Eni))
            {
                AnsiConsole.MarkupLineInterpolated($"[red]error:[/] ENI not found: {settings.Eni}");
                return 2;
            }
            try
            {
                eni = EniConfiguration.Load(settings.Eni);
            }
            catch (Exception ex)
            {
                // CLI boundary: a corrupt ENI XML must map to exit 2 (usage/IO failure), not the
                // default unhandled-exception exit 255.
                AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
                return 2;
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler onCancelKeyPress = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += onCancelKeyPress;
        try
        {
            if (settings.DurationSeconds is { } seconds)
                cts.CancelAfter(TimeSpan.FromSeconds(seconds));

            EtherCatMonitor monitor;
            try
            {
                monitor = EtherCatMonitor.OpenLive(settings.Interface!, new EtherCatMonitorOptions
                {
                    Eni = eni,
                    EsiDirectory = settings.EsiDirectory,
                    // The case the cache exists for: attaching to a machine already in OP, whose
                    // startup this session will never see. A bus analysed or watched from INIT once
                    // is recognised here and its process data decodes from the first frame.
                    LearnedCache = LearnedBusCache.Default(),
                });
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
                return 2;
            }

            await using (monitor)
            {
                AdsBusSnapshot? adsSnapshot = null;
                IAsyncDisposable? adsHandle = null;
                Task? adsLoop = null;
                var pump = Task.Run(async () =>
                {
                    try { await monitor.RunAsync(cts.Token); }
                    catch (OperationCanceledException) { }
                });
                // The live source can also fail on first use (bad interface): surface that as exit 2.
                var early = await Task.WhenAny(pump, Task.Delay(500, CancellationToken.None));
                if (early == pump && pump.Exception is not null)
                {
                    AnsiConsole.MarkupLineInterpolated(
                        $"[red]error:[/] {pump.Exception.GetBaseException().Message}");
                    return 2;
                }

                if (settings.AdsNetId is not null)
                {
                    try
                    {
                        var (client, handle) = await AdsClientFactory.ConnectAsync(settings.AdsNetId, cts.Token);
                        adsHandle = handle;
                        var enrichment = new AdsEnrichment(client);
                        adsLoop = Task.Run(async () =>
                        {
                            try
                            {
                                while (!cts.Token.IsCancellationRequested)
                                {
                                    var polled = await enrichment.PollAsync(settings.AdsNetId, cts.Token);
                                    adsSnapshot = polled;
                                    // The ADS identity tier (spec §6/§9a): where startup checking is
                                    // disabled the wire never carries identity, and the master's own
                                    // scan is the only source there is. Identity observed on the wire
                                    // always wins — the learner skips any slave it already knows — so
                                    // a disagreement between master and bus stays a finding rather
                                    // than being overwritten. Safe at 1 Hz: a poll that changes
                                    // nothing changes no fact, so the learner does not republish.
                                    // A poll that did not answer is a null snapshot, not an empty
                                    // one: feeding it as "nothing scanned" would be indistinguishable
                                    // from a master reporting an empty bus.
                                    if (polled is not null)
                                        monitor.ApplyAdsIdentity(polled.ScannedIdentities());
                                    await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
                                }
                            }
                            catch (OperationCanceledException) { }
                        });
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLineInterpolated($"[yellow]ads disabled:[/] {ex.Message}");
                    }
                }

                if (AnsiConsole.Profile.Capabilities.Interactive)
                {
                    await AnsiConsole.Live(new Table()).StartAsync(async ctx =>
                    {
                        while (!cts.Token.IsCancellationRequested && !pump.IsCompleted)
                        {
                            ctx.UpdateTarget(BuildDashboard(monitor.Observer, adsSnapshot));
                            try { await Task.Delay(250, cts.Token); }
                            catch (OperationCanceledException) { }
                        }
                    });
                }
                else
                {
                    // Spectre's LiveDisplay is only supported on interactive consoles; with
                    // redirected output its renderer crashes (and a 4-per-second table dump
                    // would be wrong for a pipe anyway). Capture headless instead.
                    AnsiConsole.MarkupLine("[grey]non-interactive console: live dashboard disabled[/]");
                    while (!cts.Token.IsCancellationRequested && !pump.IsCompleted)
                    {
                        try { await Task.Delay(250, cts.Token); }
                        catch (OperationCanceledException) { }
                    }
                }

                cts.Cancel();
                Exception? pumpFailure = null;
                try
                {
                    try { await pump; }
                    catch (Exception ex) { pumpFailure = ex; }

                    if (adsLoop is not null)
                    {
                        try { await adsLoop; }
                        catch (Exception) { /* a mid-session ADS failure shouldn't affect the capture-session exit code */ }
                    }
                }
                finally
                {
                    if (adsHandle is not null)
                    {
                        try { await adsHandle.DisposeAsync(); }
                        catch (Exception) { /* a dispose throw here shouldn't mask the pump/session outcome */ }
                    }
                }

                if (pumpFailure is not null)
                {
                    AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {pumpFailure.Message}");
                    return 2;
                }

                var report = AnalysisReport.Build(settings.Interface!, monitor);

                if (settings.LearnOut is { } learnOut)
                {
                    if (monitor.Learned is { } learned)
                    {
                        EniXmlWriter.Write(learned.Configuration, learnOut);
                        AnsiConsole.MarkupLineInterpolated($"Wrote learned ENI → [green]{learnOut}[/]");
                    }
                    else
                    {
                        // Asking for an export and getting no file and no word is the same silence the
                        // Inspector's Save button had. Say why, and say what would fix it.
                        AnsiConsole.MarkupLineInterpolated(
                            $"[yellow]nothing learned:[/] no ENI written to {learnOut}. The reconstruction is built from the master bringing the bus up, so it needs a session that includes startup.");
                    }
                }

                AnsiConsole.WriteLine();
                AnsiConsole.WriteLine($"Session summary: {report.EtherCatFrames} frames, "
                    + $"{report.WkcMismatches} WKC mismatches, {report.Emergencies} emergencies.");
                return report.HasBusErrors ? 1 : 0;
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancelKeyPress;
        }
    }

    internal static Table BuildDashboard(BusObserver observer, AdsBusSnapshot? ads)
    {
        var stats = observer.Statistics;
        var table = new Table().Title("OpenEC live")
            .AddColumn("Metric").AddColumn("Value");
        table.AddRow("Frames", stats.EtherCatFrames.ToString());
        table.AddRow("Rate (fps)", stats.FramesPerSecond?.ToString("F0") ?? "-");
        table.AddRow("Out fps (cyclic+queued)",
            stats.OutboundFramesPerSecond is null ? "-"
                : $"{stats.OutboundCyclicFramesPerSecond?.ToString("F0") ?? "-"} + {stats.OutboundQueuedFramesPerSecond?.ToString("F0") ?? "-"}");
        table.AddRow("Ret fps", stats.ReturningFramesPerSecond?.ToString("F0") ?? "-");
        table.AddRow("Cycle (us)", stats.EstimatedCycleTime?.TotalMicroseconds.ToString("F0") ?? "-");
        table.AddRow("WKC mismatches", stats.WkcMismatches.ToString());
        table.AddRow("Ring lost frames", stats.RingLostFrames.ToString());
        table.AddRow("Bus state", observer.Bus.BusState.ToString());
        var health = observer.SnapshotHealth();
        table.AddRow("Bus health", HealthFormat.Level(health.Level.ToString()));
        table.AddRow("Devices (found/configured)",
            HealthFormat.Devices(health.FoundDevices, health.ConfiguredDevices));
        table.AddRow("DC sync", HealthFormat.Dc(health.DcSync.ToString()));
        foreach (var s in observer.SnapshotSlaves().OrderBy(s => s.Address).Take(32))
            table.AddRow($"slave {s.Address}",
                $"{s.DisplayName.EscapeMarkup()} {s.AlState}{(s.ErrorFlag ? " [red]ERR[/]" : "")}");
        if (ads is not null)
        {
            table.AddRow("[bold]ADS master[/]",
                $"{ads.MasterState.CurrentState} ({ads.ConfiguredSlaves.Count} slaves)");
            if (ads.FrameStatistics is { } fs)
            {
                // Cyclic + queued, mirroring the TwinCAT System Manager counter panel.
                // Tx/Rx errors are omitted: IG 0x0C does not carry them (DTO is always 0).
                var lost = fs.CyclicLostFrames + fs.QueuedLostFrames;
                table.AddRow("ads frames sent", $"{fs.CyclicSendFrames} + {fs.QueuedSendFrames}");
                table.AddRow("ads frames/s",
                    $"{fs.CyclicFramesPerSecond?.ToString("F0") ?? "-"} + {fs.QueuedFramesPerSecond?.ToString("F0") ?? "-"}");
                table.AddRow("ads lost frames", lost > 0
                    ? $"[red]{fs.CyclicLostFrames} + {fs.QueuedLostFrames}[/]"
                    : $"{fs.CyclicLostFrames} + {fs.QueuedLostFrames}");
            }
            foreach (var (addr, counters) in ads.ErrorCounters.OrderBy(kv => kv.Key).Take(16))
            {
                var crc = string.Join(" ", counters.Ports.Select(p => $"p{p.Port}:{p.CrcErrors}"));
                table.AddRow($"crc {addr}", crc.EscapeMarkup());
            }
        }
        foreach (var evt in observer.SnapshotEvents(lastN: 8))
            table.AddRow("event", evt.ToString().EscapeMarkup());
        return table;
    }
}
