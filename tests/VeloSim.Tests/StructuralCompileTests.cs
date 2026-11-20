using NUnit.Framework;
using System.Security.Cryptography;
using System.Text.Json;

namespace VeloSim.Tests;

public class StructuralCompileTests
{
    private static string Root
    {
        get
        {
            DirectoryInfo? dir = new(TestContext.CurrentContext.TestDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VeloSim.slnx"))) dir = dir.Parent;
            return dir!.FullName;
        }
    }
    private static string Source => Path.Combine(Root, "tests/fixtures/structural/velosim-bit-register.sv");
    private static string Fixture => File.ReadAllText(Path.Combine(Root, "tests/fixtures/structural/velosim-bit-register.rtl.json"));
    private sealed class Runner(string? json, int exitCode = 0, Action? afterExport = null) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> args, string cwd, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (executable == "surelog-test") File.WriteAllText(Path.Combine(cwd, "surelog.uhdm"), "uhdm");
            else
            {
                if (json is not null) File.WriteAllText(args[1], json);
                afterExport?.Invoke();
                return Task.FromResult(new ProcessResult(exitCode, "", exitCode == 0 ? "" : "unsupported delay"));
            }
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }
    private static Task<int> Compile(string dir, TextWriter output, TextWriter error, IProcessRunner runner, string? source = null, params string[] extra) =>
        ToolCli.RunAsync(["compile-rtl", source ?? Source, "--top", "velosim_bit_register", "--output-dir", dir, "--surelog", "surelog-test", "--exporter", "rtl-test", .. extra], output, error, runner);

    [Test] public async Task StructuralCommandRetainsExactProducerBytesAndDeterministicSourceProvenance()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            string? manifest = null;
            foreach (string run in new[] { "one", "two" })
            {
                var output = new StringWriter(); var error = new StringWriter(); string dir = Path.Combine(root, run);
                Assert.That(await Compile(dir, output, error, new Runner(Fixture)), Is.Zero, error.ToString());
                string path = output.ToString().Trim();
                Assert.That(path, Is.EqualTo(Path.Combine(dir, "design.rtl.json")));
                Assert.That(File.ReadAllText(path), Is.EqualTo(Fixture));
                Assert.That(File.Exists(Path.Combine(dir, "design.ir.json")), Is.False);
                string text = File.ReadAllText(Path.Combine(dir, "compile-rtl.json"));
                using var document = JsonDocument.Parse(text); var p = document.RootElement;
                Assert.That(p.GetProperty("schema").GetString(), Is.EqualTo("velosim.compile-rtl.v1"));
                Assert.That(p.GetProperty("format").GetString(), Is.EqualTo("velosim.rtl.v1"));
                Assert.That(p.GetProperty("top").GetString(), Is.EqualTo("velosim_bit_register"));
                Assert.That(p.GetProperty("sources")[0].GetProperty("path").GetString(), Is.EqualTo(Source));
                Assert.That(p.GetProperty("sources")[0].GetProperty("sha256").GetString(), Is.EqualTo(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Source))).ToLowerInvariant()));
                Assert.That(p.GetProperty("artifact_sha256").GetString(), Is.EqualTo(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()));
                if (manifest is not null) Assert.That(text, Is.EqualTo(manifest));
                manifest = text;
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string ChangeField(string field, string value)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(Fixture)!;
        root[field] = System.Text.Json.Nodes.JsonNode.Parse(value);
        return root.ToJsonString();
    }

    [TestCase("schema", "velosim.rtl.v1")]
    [TestCase("duplicate", "duplicate")]
    [TestCase("malformed", "structural")]
    [TestCase("missing", "output")]
    [TestCase("unknown", "Unexpected")]
    [TestCase("signals", "signals")]
    [TestCase("nested-duplicate", "duplicate")]
    [TestCase("initialization", "envelope")]
    [TestCase("body", "envelope")]
    [TestCase("failure", "unsupported delay")]
    [TestCase("oversize", "4 MiB")]
    public async Task StructuralCommandRejectsBadProducerResultsWithoutPublishingArtifacts(string kind, string diagnostic)
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string? json = kind switch
        {
            "schema" => Fixture.Replace("velosim.rtl.v1", "velosim.normalized.v1"),
            "duplicate" => Fixture.Replace("\"format\":", "\"format\":\"velosim.rtl.v1\",\"format\":"),
            "malformed" => Fixture.Replace("\"signals\":[", "\"signals\":{"),
            "missing" => null,
            "unknown" => Fixture.Replace("\"clock\":", "\"extra\":0,\"clock\":"),
            "signals" => ChangeField("signals", "null"),
            "nested-duplicate" => Fixture.Replace("\"name\":\"clk\"", "\"name\":\"clk\",\"name\":\"clk\""),
            "initialization" => Fixture.Replace("\"initialization\":{\"kind\":\"block\",\"items\":[]}", "\"initialization\":[]"),
            "body" => ChangeField("body", "null"),
            "oversize" => new string(' ', 4 * 1024 * 1024 + 1),
            _ => Fixture
        };
        try
        {
            var error = new StringWriter();
            Assert.That(await Compile(dir, new StringWriter(), error, new Runner(json, kind == "failure" ? 7 : 0)), Is.EqualTo(1));
            Assert.That(error.ToString(), Does.Contain(diagnostic).IgnoreCase);
            Assert.That(File.Exists(Path.Combine(dir, "design.rtl.json")), Is.False);
            Assert.That(File.Exists(Path.Combine(dir, "compile-rtl.json")), Is.False);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Test] public async Task StructuralCommandRejectsStaleDirectoryDuplicateOptionsAndSourceMutation()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "input.sv"); File.Copy(Source, source);
            string dir = Path.Combine(root, "run"); var error = new StringWriter();
            Assert.That(await Compile(dir, new StringWriter(), error, new Runner(Fixture, afterExport: () => File.AppendAllText(source, "// changed")), source), Is.EqualTo(1));
            Assert.That(error.ToString(), Does.Contain("Source changed"));
            Assert.That(File.Exists(Path.Combine(dir, "design.rtl.json")), Is.False);
            error.GetStringBuilder().Clear();
            Assert.That(await Compile(dir, new StringWriter(), error, new Runner(Fixture)), Is.EqualTo(1));
            Assert.That(error.ToString(), Does.Contain("empty"));
            error.GetStringBuilder().Clear();
            Assert.That(await Compile(Path.Combine(root, "duplicate"), new StringWriter(), error, new Runner(Fixture), extra: ["--top", "other"]), Is.EqualTo(1));
            Assert.That(error.ToString(), Does.Contain("duplicate").IgnoreCase);
        }
        finally { Directory.Delete(root, true); }
    }

    [Test] public async Task StructuralProvenanceCoversLiteralIncludesAndRejectsIncludeMutation()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "input.sv"), include = Path.Combine(root, "types.svh");
            File.WriteAllText(source, "/* `include \"ignored.svh\" */\n// `include \"ignored.svh\"\n/* header */ `include /* operand */ \"types.svh\" /* trailing\n`include \"ignored.svh\"\n*/\n" + File.ReadAllText(Source));
            File.WriteAllText(include, "// initial");
            var error = new StringWriter(); string dir = Path.Combine(root, "good");
            Assert.That(await Compile(dir, new StringWriter(), error, new Runner(Fixture), source), Is.Zero, error.ToString());
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "compile-rtl.json")));
            Assert.That(manifest.RootElement.GetProperty("sources").GetArrayLength(), Is.EqualTo(2));
            Assert.That(manifest.RootElement.GetProperty("sources")[1].GetProperty("path").GetString(), Is.EqualTo(include));
            dir = Path.Combine(root, "changed");
            Assert.That(await Compile(dir, new StringWriter(), error, new Runner(Fixture, afterExport: () => File.AppendAllText(include, "// changed")), source), Is.EqualTo(1));
            Assert.That(error.ToString(), Does.Contain("Source changed"));
            Assert.That(File.Exists(Path.Combine(dir, "design.rtl.json")), Is.False);
        }
        finally { Directory.Delete(root, true); }
    }

    [Test] public async Task LegacyCommandExplainsStructuralExporterMismatch()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var error = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["compile", Source, "--top", "velosim_bit_register", "--output-dir", dir, "--surelog", "surelog-test", "--normalizer", "rtl-test"], new StringWriter(), error, new Runner(Fixture)), Is.EqualTo(1));
            Assert.That(error.ToString(), Does.Contain("compile-rtl").And.Contain("--exporter"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Test, Category("RealFifo")] public async Task ActualSurelogStructuralCompileIsDeterministicAndRejectsUnsupportedDelay()
    {
        if (Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO") != "1") Assert.Ignore("Requires pinned Surelog and UHDM RTL exporter.");
        string root = Path.Combine(Root, "artifacts", "structural-compile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string exporter = Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL") ?? Path.Combine(Root, "artifacts/with-uhdm/bin/velosim-uhdm-rtl");
        string? previous = null, previousManifest = null;
        foreach (string run in new[] { "one", "two" })
        {
            var output = new StringWriter(); var error = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["compile-rtl", Source, "--top", "velosim_bit_register", "--output-dir", Path.Combine(root, run), "--exporter", exporter, "--threads", "1"], output, error), Is.Zero, error.ToString());
            string json = File.ReadAllText(output.ToString().Trim());
            using var document = JsonDocument.Parse(json);
            Assert.That(document.RootElement.GetProperty("format").GetString(), Is.EqualTo("velosim.rtl.v1"));
            if (previous is not null) Assert.That(json, Is.EqualTo(previous));
            previous = json;
            string manifestPath = Path.Combine(root, run, "compile-rtl.json");
            string manifestText = File.ReadAllText(manifestPath);
            using var manifest = JsonDocument.Parse(manifestText);
            Assert.That(manifest.RootElement.GetProperty("sources")[0].GetProperty("sha256").GetString(), Is.EqualTo(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Source))).ToLowerInvariant()));
            if (previousManifest is not null) Assert.That(manifestText, Is.EqualTo(previousManifest));
            previousManifest = manifestText;
        }
        string bad = Path.Combine(root, "delay.sv");
        File.WriteAllText(bad, File.ReadAllText(Source).Replace("q <= data;", "q <= #1 data;"));
        var badError = new StringWriter();
        Assert.That(await ToolCli.RunAsync(["compile-rtl", bad, "--top", "velosim_bit_register", "--output-dir", Path.Combine(root, "bad"), "--exporter", exporter], new StringWriter(), badError), Is.EqualTo(1));
        Assert.That(badError.ToString(), Does.Contain("exporter failed"));
        Assert.That(File.Exists(Path.Combine(root, "bad", "design.rtl.json")), Is.False);
    }
}
