using NUnit.Framework;
using System.Text.Json;
namespace VeloSim.Tests;
public class CoverageEvidenceTests
{
    [Test] public void SourceEvidenceIncludesDependencyBuildAndHdlInputs()
    {
        var root = Temp(); try
        {
            foreach (var relative in new[] { "NuGet.config", "src/packages.lock.json", "native/uhdm/CMakeLists.txt", "native/config.cmake", "examples/header.svh" })
            {
                var file = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "before");
                var manifest = Path.Combine(root, "sources.json"); SourceEvidence.Save(manifest, SourceEvidence.Capture(root)); File.WriteAllText(file, "after");
                Assert.Throws<IOException>(() => SourceEvidence.VerifyCurrent(root, manifest), relative);
            }
        } finally { Directory.Delete(root, true); }
    }
    private static string Temp() { var path = Path.Combine(Path.GetTempPath(), "velosim-evidence-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(path, "src")); return path; }
    [Test] public void SourceEvidenceDetectsChangedAndNewCompiledInputs()
    {
        var root = Temp(); try
        {
            var source = Path.Combine(root, "src", "A.cs"); File.WriteAllText(source, "one"); var manifest = Path.Combine(root, "sources.json"); SourceEvidence.Save(manifest, SourceEvidence.Capture(root)); SourceEvidence.VerifyCurrent(root, manifest);
            File.WriteAllText(source, "two"); Assert.Throws<IOException>(() => SourceEvidence.VerifyCurrent(root, manifest));
            File.WriteAllText(source, "one"); File.WriteAllText(Path.Combine(root, "src", "New.cs"), "new"); Assert.Throws<IOException>(() => SourceEvidence.VerifyCurrent(root, manifest));
        } finally { Directory.Delete(root, true); }
    }
    [Test] public void SourceEvidenceExcludesGeneratedBuildDirectories()
    {
        var root = Temp(); try
        {
            File.WriteAllText(Path.Combine(root, "src", "A.cs"), "code"); var before = SourceEvidence.Digest(SourceEvidence.Capture(root)); Directory.CreateDirectory(Path.Combine(root, "src", "obj")); File.WriteAllText(Path.Combine(root, "src", "obj", "Generated.cs"), "generated"); Assert.That(SourceEvidence.Digest(SourceEvidence.Capture(root)), Is.EqualTo(before));
        } finally { Directory.Delete(root, true); }
    }
    [Test] public void SourceEvidenceRejectsContradictoryManifestHashes()
    {
        var root = Temp(); try
        {
            File.WriteAllText(Path.Combine(root, "src", "A.cs"), "code"); var file = Path.Combine(root, "sources.json"); SourceEvidence.Save(file, SourceEvidence.Capture(root));
            var content = File.ReadAllText(file); using var json = JsonDocument.Parse(content); var hash = json.RootElement.GetProperty("sources")[0].GetProperty("sha256").GetString()!;
            File.WriteAllText(file, content.Replace(hash, new string('0', 64), StringComparison.Ordinal)); Assert.Throws<IOException>(() => SourceEvidence.VerifyCurrent(root, file));
        } finally { Directory.Delete(root, true); }
    }
    private const string Report = """
      <coverage><packages>
      <package name="VeloSim"><classes><class filename="src/A.cs"><lines><line number="1" hits="1" branch="true" condition-coverage="50% (1/2)"/></lines></class></classes></package>
      <package name="VeloSim.Build"><classes><class filename="tools/B.cs"><lines><line number="1" hits="1" branch="true" condition-coverage="100% (1/1)"/></lines></class></classes></package>
      <package name="Default"><classes>
      <class filename="native/ir/engine.cpp"><lines><line number="1" hits="1" branch="true" condition-coverage="100% (2/2)"/></lines></class>
      <class filename="native/scheduler.cpp"><lines><line number="1" hits="1" branch="true" condition-coverage="100% (2/2)"/></lines></class>
      <class filename="native/uhdm/json_escape.h"><lines><line number="1" hits="1" branch="true" condition-coverage="100% (2/2)"/></lines></class>
      </classes></package></packages></coverage>
      """;
    [Test] public void BranchGatesUseSeparateManagedNativeCounters()
    {
        var file = Path.GetTempFileName(); try
        {
            File.WriteAllText(file, Report); var values = BuildDiagnostics.ReadCoverage(file); Assert.That(values[0].BranchCovered, Is.EqualTo(2)); Assert.That(values[0].BranchTotal, Is.EqualTo(3)); Assert.That(values[1].BranchPercent, Is.EqualTo(100));
            using var pass = JsonDocument.Parse(BuildDiagnostics.CheckCoverage(file, 100, 100, 66, 100)); Assert.That(pass.RootElement.GetProperty("status").GetString(), Is.EqualTo("pass"));
            using var fail = JsonDocument.Parse(BuildDiagnostics.CheckCoverage(file, 100, 100, 67, 100)); Assert.That(fail.RootElement.GetProperty("status").GetString(), Is.EqualTo("fail"));
        } finally { File.Delete(file); }
    }
    [Test] public void CoverageRejectsInvalidBranchCountsAndMissingHelperInstrumentation()
    {
        var file = Path.GetTempFileName(); try
        {
            File.WriteAllText(file, Report.Replace("50% (1/2)", "invalid")); Assert.Throws<IOException>(() => BuildDiagnostics.ReadCoverage(file));
            File.WriteAllText(file, Report.Replace("name=\"VeloSim.Build\"", "name=\"Other\"")); Assert.Throws<IOException>(() => BuildDiagnostics.ReadCoverage(file));
        } finally { File.Delete(file); }
    }
}
