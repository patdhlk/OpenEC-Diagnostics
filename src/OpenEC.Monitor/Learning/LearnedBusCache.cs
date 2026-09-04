using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenEC.Monitor.Eni;

namespace OpenEC.Monitor.Learning;

/// <summary>Persists learned configurations so a bus whose startup was observed once is recognised
/// on every later mid-run attach. Entries are real ENI XML, which means the cache, the `--out`
/// export and the test fixtures are all the same artifact — one writer, one reader, one format.</summary>
public sealed class LearnedBusCache(string directory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>The environment variable that relocates <see cref="DefaultDirectory"/>. Every
    /// production construction site now caches by default, so without a redirect a test run — which
    /// drives those same sites — would write into the developer's real profile. Also the supported
    /// way for a user on a shared or read-only profile to put the cache somewhere else.</summary>
    public const string DirectoryVariable = "OPENEC_CACHE_DIR";

    /// <summary>Under the per-user application-data folder the runtime nominates: `%APPDATA%` on
    /// Windows, `~/Library/Application Support` on macOS, `~/.config` on Linux — the cross-platform
    /// guarantee holds without a per-OS branch here. <see cref="DirectoryVariable"/> overrides it.</summary>
    public static string DefaultDirectory =>
        Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } redirected
            ? redirected
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "openec", "learned");

    /// <summary>A cache at <see cref="DefaultDirectory"/> — what the CLI and the Inspector pass so
    /// that a bus whose startup was observed once is recognised on later mid-run attaches. Kept as
    /// a factory rather than a static instance so the directory is re-read per session, which is
    /// what makes <see cref="DirectoryVariable"/> effective.</summary>
    public static LearnedBusCache Default() => new(DefaultDirectory);

    /// <summary>Slave count, then each slave's ring position and identity in ring order, then the
    /// logical address layout. Serial numbers are deliberately excluded so replacing a terminal
    /// with an identical one still hits the cache.</summary>
    public static string Fingerprint(EniConfiguration configuration) => Digest(
        $"v1|{configuration.Slaves.Count}|"
        + string.Join(';', configuration.Slaves
            .OrderBy(s => (ushort)(0 - s.AutoIncAddr)).ThenBy(s => s.PhysAddr)
            .Select(s => string.Create(CultureInfo.InvariantCulture,
                $"{s.VendorId:X}:{s.ProductCode:X}:{s.RevisionNo:X}")))
        + "|" + CyclicShape(configuration));

    /// <summary>Used when identity was never read from the wire — startup checking disabled, or a
    /// capture that began after INIT. Keys only on what is observable in OP: how many slaves
    /// answered, at which station addresses, and the shape of the cyclic frame table. Weaker, so a
    /// hit is not guaranteed and the completeness surface says so.</summary>
    public static string FallbackFingerprint(EniConfiguration configuration) => Digest(
        $"v1-fallback|{configuration.Slaves.Count}|"
        + string.Join(';', configuration.Slaves.Select(s => s.PhysAddr).OrderBy(a => a))
        + "|" + CyclicShape(configuration));

    public void Save(LearnedConfiguration learned)
    {
        Directory.CreateDirectory(directory);
        var fingerprint = Fingerprint(learned.Configuration);
        WriteEntry(learned, fingerprint);

        // Also index under the weaker fingerprint. It is the ONLY key a mid-run attach can compute —
        // such a capture never observes identity, so its primary fingerprint is derived from zeroes
        // and can never match a saved bus. Without this second entry the fallback lookup reads a file
        // nothing ever writes, and the whole mid-run case is dead.
        //
        // Caveat, inherent to a weaker key: two different buses that share slave count, station
        // addresses and cyclic shape collide here, and the last save wins. Spec §5 already states a
        // fallback hit is not guaranteed, and the completeness surface says so to the user.
        var fallback = FallbackFingerprint(learned.Configuration);
        if (fallback != fingerprint) WriteEntry(learned, fallback);
    }

    private void WriteEntry(LearnedConfiguration learned, string key)
    {
        EniXmlWriter.Write(learned.Configuration, Path.Combine(directory, $"{key}.eni.xml"));
        File.WriteAllText(Path.Combine(directory, $"{key}.meta.json"),
            JsonSerializer.Serialize(new
            {
                learned.Revision,
                learned.Completeness.SawStartup,
                learned.Completeness.IsComplete,
                Summary = learned.Completeness.Summary,
                Slaves = learned.Completeness.Slaves,
                Provenance = learned.Provenance.ToDictionary(
                    kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value),
            }, JsonOptions));
    }

    public bool TryLoad(string fingerprint, out EniConfiguration? configuration)
    {
        configuration = null;
        var path = Path.Combine(directory, $"{fingerprint}.eni.xml");
        if (!File.Exists(path)) return false;
        try
        {
            configuration = EniConfiguration.Load(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Xml.XmlException)
        {
            // A corrupt or half-written cache entry must never break a session; treat it as a miss.
            return false;
        }
    }

    private static string CyclicShape(EniConfiguration configuration) =>
        string.Join(';', configuration.CyclicCommands
            .OrderBy(c => c.RawAddress).ThenBy(c => (int)c.Command)
            .Select(c => string.Create(CultureInfo.InvariantCulture,
                $"{(int)c.Command}:{c.RawAddress:X}:{c.DataLength}")));

    private static string Digest(string material) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..16].ToLowerInvariant();
}
