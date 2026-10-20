using NUnit.Framework;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
namespace VeloSim.Tests;

public class CoverageIntegrityTests
{
    [Test] public void EvidenceRejectsLinkedManifestsAndSourceInputs()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Symlinks require Windows elevation.");
        Scenario((root, native, managed, json) =>
        {
            Save(root, json); var manifest = Path.Combine(root, "evidence.json"); var actual = Path.Combine(root, "actual.json"); File.Move(manifest, actual); File.CreateSymbolicLink(manifest, actual);
            Assert.Throws<IOException>(() => CoverageEvidence.Validate(Path.Combine(root, "Cobertura.xml"), [native], [managed]));
            Directory.CreateDirectory(Path.Combine(root, "src")); File.CreateSymbolicLink(Path.Combine(root, "src", "linked.cs"), managed);
            Assert.Throws<IOException>(() => SourceEvidence.Capture(root));
        });
    }
    private static void Scenario(Action<string, string, string, JsonObject> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "velosim-coverage-integrity-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            JsonObject Entry(string path, string physical) { File.WriteAllText(physical, "data"); return new() { ["path"] = path, ["sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(physical))).ToLowerInvariant() }; }
            var reports = new JsonArray(); foreach (var name in new[] { "Cobertura.xml", "gate.json", "sources.json", "doctor.json" }) reports.Add(Entry(name, Path.Combine(root, name)));
            var native = Path.Combine(root, "native.so"); var managed = Path.Combine(root, "managed.dll");
            var json = new JsonObject { ["format"] = "velosim.coverage.evidence.v1", ["run_id"] = "test", ["jobs"] = 4, ["llvm_version"] = "LLVM18", ["reports"] = reports, ["native_objects"] = new JsonArray(Entry(native, native)), ["managed_objects"] = new JsonArray(Entry(managed, managed)) };
            action(root, native, managed, json);
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Save(string root, JsonObject json) => File.WriteAllText(Path.Combine(root, "evidence.json"), json.ToJsonString());
    [Test] public void CompleteEvidenceAcceptsOnlyBoundReportAndBinaryContents() => Scenario((root, native, managed, json) =>
    {
        Save(root, json); CoverageEvidence.Validate(Path.Combine(root, "Cobertura.xml"), [native], [managed]);
        File.AppendAllText(managed, "changed"); Assert.Throws<IOException>(() => CoverageEvidence.Validate(Path.Combine(root, "Cobertura.xml"), [native], [managed]));
        File.WriteAllText(managed, "data"); File.AppendAllText(Path.Combine(root, "gate.json"), "changed"); Assert.Throws<IOException>(() => CoverageEvidence.Validate(Path.Combine(root, "Cobertura.xml"), [native], [managed]));
    });
    [Test] public void EvidenceRejectsMissingAndDuplicateModuleEntries() => Scenario((root, native, managed, json) =>
    {
        var modules = json["native_objects"]!.AsArray(); var entry = modules[0]!.DeepClone(); modules.Clear(); Save(root, json);
        Assert.Throws<IOException>(() => CoverageEvidence.Validate(Path.Combine(root, "Cobertura.xml"), [native], [managed]));
        modules.Add(entry); modules.Add(entry.DeepClone()); Save(root, json); Assert.Throws<IOException>(() => CoverageEvidence.Validate(Path.Combine(root, "Cobertura.xml"), [native], [managed]));
    });
    [Test] public void EvidenceRejectsAmbiguousFieldsAndInvalidJobMetadata() => Scenario((root, native, managed, json) =>
    {
        json["jobs"] = 257; Save(root, json); Assert.Throws<IOException>(() => CoverageEvidence.Validate(Path.Combine(root, "Cobertura.xml"), [native], [managed]));
        json["jobs"] = 4; File.WriteAllText(Path.Combine(root, "evidence.json"), json.ToJsonString().Replace("\"jobs\":4", "\"jobs\":4,\"jobs\":4"));
        Assert.Throws<IOException>(() => CoverageEvidence.Validate(Path.Combine(root, "Cobertura.xml"), [native], [managed]));
        json["unknown"] = true; Save(root, json); Assert.Throws<IOException>(() => CoverageEvidence.Validate(Path.Combine(root, "Cobertura.xml"), [native], [managed]));
    });
    [Test] public void EvidenceRejectsOversizedManifestAndWrongReportName() => Scenario((root, native, managed, json) =>
    {
        Save(root, json); Assert.Throws<IOException>(() => CoverageEvidence.Validate(Path.Combine(root, "other.xml"), [native], [managed]));
        File.WriteAllText(Path.Combine(root, "evidence.json"), new string(' ', 1024 * 1024 + 1)); Assert.Throws<IOException>(() => CoverageEvidence.Validate(Path.Combine(root, "Cobertura.xml"), [native], [managed]));
    });
    [Test] public void SourceManifestRejectsDuplicateJsonProperties()
    {
        var root = Path.Combine(Path.GetTempPath(), "velosim-source-shape-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { var file = Path.Combine(root, "sources.json"); SourceEvidence.Save(file, SourceEvidence.Capture(root)); File.WriteAllText(file, File.ReadAllText(file).Replace("\"sources\":", "\"sources\": [], \"sources\":")); Assert.Throws<IOException>(() => SourceEvidence.VerifyCurrent(root, file)); }
        finally { Directory.Delete(root, true); }
    }
}
