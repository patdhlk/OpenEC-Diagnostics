using System.Text.RegularExpressions;
using Dahlke.EtherCAT.Esi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenEC.Monitor;

/// <summary>Resolves slave identities to vendor device names via an ESI XML directory
/// (e.g. C:/TwinCAT/3.1/Config/Io/EtherCAT). Unresolvable identities yield null.</summary>
public sealed partial class EsiEnricher : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IEsiCatalog _catalog;

    public EsiEnricher(string directory, ILoggerFactory? loggerFactory = null)
    {
        // Dahlke.EtherCAT.Esi.EsiCatalog is `internal` to its assembly; the only supported way to
        // obtain an IEsiCatalog is via EsiServiceCollectionExtensions.AddEsiCatalog, which wants an
        // IConfiguration and a DI container. A tiny throwaway ServiceProvider stands in for the
        // caller's own host here; it is owned by this instance and disposed alongside it.
        var services = new ServiceCollection();
        services.AddSingleton(loggerFactory ?? NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Directory"] = directory,
                ["LookupBudgetMs"] = "5000",
            })
            .Build();
        services.AddEsiCatalog(config);
        _provider = services.BuildServiceProvider();
        _catalog = _provider.GetRequiredService<IEsiCatalog>();
    }

    /// <summary>Resolves a slave identity to its full ESI device description — name, declared
    /// process data and object dictionary. Learning mode needs the process data, not just the
    /// name: ESI supplies the schema (PDO entry names, datatypes, bit lengths) that the wire's
    /// FMMU and assignment traffic then binds to concrete offsets.</summary>
    public async Task<EsiDevice?> ResolveDeviceAsync(uint vendorId, uint productCode, uint revision,
        string? typeHint = null)
    {
        var result = await _catalog.LookupAsync(new EsiKey(vendorId, productCode, revision),
            typeHint ?? string.Empty);
        return result.Status == EsiStatus.Resolved ? result.Device : null;
    }

    public async Task<string?> ResolveNameAsync(uint vendorId, uint productCode, uint revision,
        string? typeHint = null) =>
        (await ResolveDeviceAsync(vendorId, productCode, revision, typeHint))?.NameEn;

    [GeneratedRegex(@"\(([^)]+)\)")]
    private static partial Regex ParentheticalRegex();

    public static string? TypeHintFromName(string? name)
    {
        if (name is null) return null;
        var match = ParentheticalRegex().Match(name);
        return match.Success ? match.Groups[1].Value : null;
    }

    public void Dispose() => _provider.Dispose();
}
