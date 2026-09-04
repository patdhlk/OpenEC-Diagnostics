using System.Runtime.CompilerServices;
using OpenEC.Monitor.Learning;

namespace OpenEC.Inspector.Tests;

/// <summary>Points <see cref="LearnedBusCache.DefaultDirectory"/> at a throwaway directory for the
/// whole test process. <see cref="OpenEC.Inspector.Session.MonitorSession"/> caches by default now,
/// so without this every session test would deposit ENI files in the developer's real profile — a
/// test that pollutes the machine it runs on is a defect in its own right.
///
/// A module initializer rather than a fixture because the session reads the directory through a
/// static, with no seam a fixture could reach, and because it must be in force before the first test
/// class constructs anything. Duplicated rather than shared with the OpenEC.Monitor.Tests copy: the
/// two assemblies have no common project, and each needs its own initializer regardless.</summary>
internal static class CacheRedirect
{
    [ModuleInitializer]
    internal static void Redirect()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"openec-test-cache-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(LearnedBusCache.DirectoryVariable, directory);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        };
    }
}
