using System.Security.Cryptography;
using System.Text.Json;
using NUnit.Framework;

namespace VeloSim.Tests;

public class BuildDiagnosticsTests
{
    private const string Coverage = """
        <coverage><packages>
        <package name="VeloSim"><classes><class filename="src/VeloSim.Core/A.cs"><lines><line number="1" hits="1"/><line number="2" hits="0"/></lines></class></classes></package>
        <package name="VeloSim.Build"><classes><class filename="tools/VeloSim.Build/A.cs"><lines><line number="1" hits="1"/><line number="2" hits="0"/></lines></class></classes></package>
        <package name="Default"><classes><class filename="native/ir/engine.cpp"><lines><line number="1" hits="2"/></lines></class>
        <class filename="native/scheduler.cpp"><lines><line number="1" hits="2"/></lines></class>
        <class filename="native/uhdm/json_escape.h"><lines><line number="1" hits="2"/></lines></class></classes></package>
        </packages></coverage>
        """;
    [Test] public void ToolResolutionPreservesAbsolutePathsAndDiagnosesMissingTools()
    {
        var file = Path.GetTempFileName();
        try { Assert.That(BuildDiagnostics.ResolveTool(file), Is.EqualTo(Path.GetFullPath(file))); }
        finally { File.Delete(file); }
        Assert.Throws<FileNotFoundException>(() => BuildDiagnostics.ResolveTool("velosim-absent-tool-678f53"));
    }
    [Test] public async Task VersionProbeHasAnEnforcedDeadline()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("Linux process deadline probe.");
        Assert.ThrowsAsync<TimeoutException>(async () => await BuildDiagnostics.ProbeVersionAsync("/bin/sleep", "3", TimeSpan.FromMilliseconds(30)));
        Assert.That(await BuildDiagnostics.ProbeVersionAsync("/bin/echo", "version-marker", TimeSpan.FromSeconds(2)), Is.EqualTo("version-marker"));
    }
    [Test] public void CoverageSeparatesManagedAndNativeDenominators()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, Coverage); var values = BuildDiagnostics.ReadCoverage(file);
            Assert.That(values[0].Percent, Is.EqualTo(50)); Assert.That(values[1].Percent, Is.EqualTo(100));
            File.WriteAllText(file, Coverage.Replace("name=\"VeloSim\"", "name=\"Other\""));
            Assert.Throws<IOException>(() => BuildDiagnostics.ReadCoverage(file));
            File.WriteAllText(file, Coverage.Replace("native/scheduler.cpp", "other.cpp")); Assert.Throws<IOException>(() => BuildDiagnostics.ReadCoverage(file));
            File.WriteAllText(file, Coverage.Replace("name=\"VeloSim\"", "name=\"VeloSim.Tools\"")); Assert.Throws<IOException>(() => BuildDiagnostics.ReadCoverage(file));
            File.WriteAllText(file, Coverage.Replace("hits=\"0\"", "hits=\"-1\""));
            Assert.Throws<IOException>(() => BuildDiagnostics.ReadCoverage(file));
        }
        finally { File.Delete(file); }
    }
    [Test] public void CoverageThresholdsRejectInvalidNumbers()
    {
        Assert.That(BuildDiagnostics.Threshold("85.5"), Is.EqualTo(85.5));
        foreach (var text in new[] { "NaN", "Infinity", "-1", "100.01", "", "1,5" }) Assert.Throws<ArgumentException>(() => BuildDiagnostics.Threshold(text));
    }
    [Test] public void CoverageGateFingerprintsItsExactMeasuredInputAndReportsFailure()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, Coverage);
            using var passed = JsonDocument.Parse(BuildDiagnostics.CheckCoverage(file, 50, 100));
            Assert.That(passed.RootElement.GetProperty("status").GetString(), Is.EqualTo("pass"));
            Assert.That(passed.RootElement.GetProperty("sha256").GetString(), Is.EqualTo(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant()));
            using var failed = JsonDocument.Parse(BuildDiagnostics.CheckCoverage(file, 51, 100)); Assert.That(failed.RootElement.GetProperty("status").GetString(), Is.EqualTo("fail"));
        }
        finally { File.Delete(file); }
    }
}
