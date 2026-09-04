using System.Runtime.CompilerServices;
using OpenEC.Monitor.Learning;

namespace OpenEC.Monitor.Tests;

/// <summary>Points <see cref="LearnedBusCache.DefaultDirectory"/> at a throwaway directory for the
/// whole test process. `analyze`, `live` and the Inspector's session all cache by default now, so
/// without this every run that exercises them would deposit ENI files in the developer's real
/// profile — a test that pollutes the machine it runs on is a defect in its own right.
///
/// A module initializer rather than a fixture because the CLI commands read the directory through a
/// static, with no seam a fixture could reach, and because it must be in force before the first test
/// class constructs anything.</summary>
internal static class CacheRedirect
{
    [ModuleInitializer]
    internal static void Redirect()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"openec-test-cache-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(LearnedBusCache.DirectoryVariable, directory);
        // A fresh directory per run, deleted on exit: a reused one would let an entry saved by an
        // earlier run satisfy a later run's lookup, which is how a cache-miss test starts passing
        // for the wrong reason.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        };
    }
}
