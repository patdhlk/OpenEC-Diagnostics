using Dahlke.EtherCAT.Diagnostics;
using Dahlke.TwinCAT.Ads;
using Microsoft.Extensions.DependencyInjection;

namespace OpenEC.Monitor.Ads;

/// <summary>Builds a started ADS connection pool (no generic host) and resolves the
/// EtherCAT diagnostics client from it. Dispose the returned handle to tear down.</summary>
public static class AdsClientFactory
{
    public static async Task<(IEtherCatClient Client, IAsyncDisposable Handle)> ConnectAsync(
        string amsNetId, CancellationToken ct)
    {
        var handle = await AdsConnectionPoolBuilder.Create()
            .AddTarget("target", o => o.AmsNetId = amsNetId)
            .ConfigureServices(s => s.AddEtherCatDiagnostics(startMonitor: false))
            .BuildAndStartAsync(ct);
        return (handle.Services.GetRequiredService<IEtherCatClient>(), handle);
    }
}
