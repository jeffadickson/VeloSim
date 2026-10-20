using NUnit.Framework;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace VeloSim.Tests;
public class NativeBundleTests
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), "velosim-bundle-" + Guid.NewGuid().ToString("N"));
    private static string Create(string root)
    {
        Directory.CreateDirectory(root); var source = Path.Combine(root, "source"); File.WriteAllText(source, "fixture");
        var files = new[] { "VeloSim.Tools", "VeloSim.Build", "VeloSim.Bench", "libvelosim_ir.so", "libvelosim_scheduler.so", "include/velosim.h", "examples/counter.ir.json", "README.txt" }.ToDictionary(p => p, _ => source);
        var bundle = Path.Combine(root, "bundle"); NativeBundle.Create(bundle, files, "test-revision", "jobs=4\n"); return bundle;
    }
    [Test] public void BundleManifestVerifiesCopiedFilesAndTrailingDirectorySeparator()
    {
        var root = Temp(); try { var bundle = Create(root); NativeBundle.Validate(bundle + Path.DirectorySeparatorChar); Assert.That(File.Exists(Path.Combine(bundle, "include", "velosim.h")), Is.True); }
        finally { Directory.Delete(root, true); }
    }
    [Test] public void BundleDetectsChangedAndUnexpectedFiles()
    {
        var root = Temp();
        try
        {
            var bundle = Create(root); File.WriteAllText(Path.Combine(bundle, "README.txt"), "changed"); Assert.Throws<IOException>(() => NativeBundle.Validate(bundle));
            File.WriteAllText(Path.Combine(bundle, "README.txt"), "fixture"); File.WriteAllText(Path.Combine(bundle, "extra"), "extra"); Assert.Throws<IOException>(() => NativeBundle.Validate(bundle));
        }
        finally { Directory.Delete(root, true); }
    }
    [Test] public void BundleRejectsTraversalAndDuplicateManifestPaths()
    {
        var root = Temp();
        try
        {
            var bundle = Create(root); var file = Path.Combine(bundle, "manifest.json"); var original = File.ReadAllText(file); var json = JsonNode.Parse(original)!;
            json["files"]![0]!["path"] = "../source"; File.WriteAllText(file, json.ToJsonString()); Assert.Throws<IOException>(() => NativeBundle.Validate(bundle));
            json = JsonNode.Parse(original)!; json["files"]!.AsArray().Add(json["files"]![0]!.DeepClone()); File.WriteAllText(file, json.ToJsonString()); Assert.Throws<IOException>(() => NativeBundle.Validate(bundle));
        }
        finally { Directory.Delete(root, true); }
    }
    [Test] public void BundleRejectsSymbolicLinksWithoutFollowingThem()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Symlink creation requires Windows developer mode.");
        var root = Temp();
        try { var bundle = Create(root); File.CreateSymbolicLink(Path.Combine(bundle, "linked"), Path.Combine(root, "source")); Assert.Throws<IOException>(() => NativeBundle.Validate(bundle)); }
        finally { Directory.Delete(root, true); }
    }
    [Test] public void BundleArchiveContainsManifestAndCannotArchiveIntoItself()
    {
        var root = Temp();
        try
        {
            var bundle = Create(root); Assert.Throws<IOException>(() => NativeBundle.Archive(bundle, Path.Combine(bundle, "loop.tar.gz")));
            var archive = Path.Combine(root, "bundle.tar.gz"); NativeBundle.Archive(bundle, archive);
            using var input = File.OpenRead(archive); using var gzip = new GZipStream(input, CompressionMode.Decompress); using var reader = new TarReader(gzip);
            bool found = false; while (reader.GetNextEntry() is { } entry) if (entry.Name.EndsWith("/manifest.json", StringComparison.Ordinal)) found = true;
            Assert.That(found, Is.True);
        }
        finally { Directory.Delete(root, true); }
    }
}
