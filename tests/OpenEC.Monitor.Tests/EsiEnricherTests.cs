using Dahlke.EtherCAT.Esi;

namespace OpenEC.Monitor.Tests;

public class EsiEnricherTests
{
    private static string FixtureDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Esi");

    [Fact]
    public void Extracts_type_hint_from_twincat_names()
    {
        Assert.Equal("EL1008", EsiEnricher.TypeHintFromName("Term 2 (EL1008)"));
        Assert.Equal("AX5101", EsiEnricher.TypeHintFromName("Drive 4 (AX5101)"));
        Assert.Null(EsiEnricher.TypeHintFromName("NoParens"));
        Assert.Null(EsiEnricher.TypeHintFromName(null));
    }

    [Fact]
    public async Task Empty_esi_directory_resolves_to_null_without_throwing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"esi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var enricher = new EsiEnricher(dir);
            Assert.Null(await enricher.ResolveNameAsync(2, 0x03f03052, 0x00120000, "EL1008"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Resolves_a_known_identity_from_the_esi_directory()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Esi");
        using var enricher = new EsiEnricher(dir);
        Assert.Equal("EL1008 8Ch. Dig. Input 24V, 3ms",
            await enricher.ResolveNameAsync(2, 0x03f03052, 0x00120000, "EL1008"));
    }

    [Fact]
    public async Task Resolves_the_full_device_including_process_data()
    {
        using var enricher = new EsiEnricher(FixtureDirectory);

        var device = await enricher.ResolveDeviceAsync(2, 0x03F03052, 0x00120000, "EL1008");

        Assert.NotNull(device);
        Assert.Equal("EL1008 8Ch. Dig. Input 24V, 3ms", device!.NameEn);
        var pdo = Assert.Single(device.ProcessData!.Pdos);
        Assert.Equal(0x1A00, pdo.Index);
        Assert.Equal(EsiPdoDirection.Transmit, pdo.Direction);
        Assert.Equal(8, pdo.Entries.Count);
        Assert.Equal("Input 1", pdo.Entries[0].Name);
        Assert.Equal("BOOL", pdo.Entries[0].DataType);
        Assert.Equal(1, pdo.Entries[0].BitLength);
    }

    [Fact]
    public async Task Unknown_identities_resolve_to_null()
    {
        using var enricher = new EsiEnricher(FixtureDirectory);

        Assert.Null(await enricher.ResolveDeviceAsync(0xDEAD, 0xBEEF, 1));
    }
}
