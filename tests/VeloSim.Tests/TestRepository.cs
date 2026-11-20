using System.Runtime.CompilerServices;

namespace VeloSim.Tests;

/// <summary>Locates source fixtures independently of the relocated test binaries.</summary>
internal static class TestRepository
{
    internal static string Root => Resolve(Environment.GetEnvironmentVariable("VELOSIM_SOURCE_ROOT"), SourceDirectory());

    // CallerFilePath is compiled into this test helper, not inferred from E: output
    // locations. Another machine must rebuild or explicitly select its checkout.
    private static string SourceDirectory([CallerFilePath] string source = "") => Path.GetDirectoryName(source)!;

    internal static string Resolve(string? configured, string compiledSource)
    {
        static bool IsCheckout(string path) => Directory.Exists(path) &&
            File.Exists(Path.Combine(path, "examples", "counter.normalized.json")) &&
            File.Exists(Path.Combine(path, "native", "rtl", "runtime.h"));
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured) || !IsCheckout(configured))
                throw new IOException("VELOSIM_SOURCE_ROOT must select an existing VeloSim checkout.");
            return Path.GetFullPath(configured);
        }
        for (var current = new DirectoryInfo(compiledSource); current is not null; current = current.Parent)
            if (IsCheckout(current.FullName)) return current.FullName;
        throw new IOException("Source fixtures unavailable; rebuild tests or set VELOSIM_SOURCE_ROOT.");
    }
}
