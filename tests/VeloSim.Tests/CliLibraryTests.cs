using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using NUnit.Framework;

namespace VeloSim.Tests;

public class CliLibraryTests
{
    [Test]
    public void UniqueWorkflowAcceptsOneValueAndUnsignedSeedBounds()
    {
        var values = CliOptions.Parse(["fifo-differential", "design.sv", "--seed", "4294967295"], 2);
        Assert.That(values.UInt("--seed", 1), Is.EqualTo(uint.MaxValue));
        Assert.That(CliOptions.Parse(["fifo-differential", "design.sv"], 2).UInt("--seed", 42), Is.EqualTo(42));
        foreach (string text in new[] { "-1", "4294967296", "1.5" })
            Assert.That(() => CliOptions.Parse(["fifo-differential", "design.sv", "--seed", text], 2).UInt("--seed", 1), Throws.ArgumentException);
    }
    [TestCase("simulate", "--release-reset"), TestCase("word-generate", "--four-state")]
    public void BooleanFlagsAreExplicitAndDoNotConsumeValues(string command, string flag)
    {
        Assert.That(CliOptions.Parse([command, "fixture.json"], 2).Flag(flag), Is.False);
        Assert.That(CliOptions.Parse([command, "fixture.json", flag], 2).Flag(flag), Is.True);
        Assert.That(() => CliOptions.Parse([command, "fixture.json", flag, "false"], 2), Throws.ArgumentException);
    }
    [Test]
    public void WorkerListsAndOmittedNumbersPreserveTheirContracts()
    {
        Assert.That(CliOptions.Parse(["simulate", "fixture.json"], 2).Int("--cycles", 5), Is.EqualTo(5));
        Assert.That(CliOptions.Parse(["word-conformance"], 1).Workers([1, 4]), Is.EqualTo(new[] { 1, 4 }));
        Assert.That(CliOptions.Parse(["word-conformance", "--workers", "1,4,8"], 1).Workers([]), Is.EqualTo(new[] { 1, 4, 8 }));
        Assert.That(() => CliOptions.Parse(["word-conformance", "--workers", "1,x"], 1).Workers([]), Throws.ArgumentException);
        Assert.That(() => CliOptions.Parse(["simulate", "fixture.json", "--version"], 2), Throws.ArgumentException);
    }
    [Test]
    public void HelpDetectionDoesNotExecuteOrAcceptUnknownWorkflows()
    {
        using var output = new StringWriter();
        Assert.That(CliOptions.TryHelp([], output), Is.False);
        Assert.That(CliOptions.TryHelp(["compile", "a.sv"], output), Is.False);
        Assert.That(CliOptions.TryHelp(["unknown-command", "--help"], output), Is.False);
        Assert.That(output.ToString(), Is.Empty);
    }
    [TestCase("fifo-differential"), TestCase("word-seeded-conformance")]
    public void HistoricallyUniqueOptionsStillRejectDuplicates(string command)
    {
        var args = command == "fifo-differential" ? new[] { command, "design.sv", "--seed", "1", "--seed", "2" } : [command, "--seed", "1", "--seed", "2"];
        Assert.That(() => CliOptions.Parse(args, command == "fifo-differential" ? 2 : 1), Throws.ArgumentException.With.Message.Contains("duplicate"));
    }
    [Test]
    public void RepeatedOrdinaryOptionsKeepLastValueAndSourcesStaySeparate()
    {
        var options = CliOptions.Parse(["compile", "a file.sv", "--threads", "1", "--threads", "4", "--", "--literal.sv"], 1);
        Assert.That(options.Int("--threads", 1), Is.EqualTo(4));
        Assert.That(options.Sources, Is.EqualTo(new[] { "a file.sv", "--literal.sv" }));
    }
    [Test]
    public void AnOptionConsumesExactlyOneValueBeforeTheNextSource()
    {
        var options=CliOptions.Parse(["compile","first.sv","--threads","4","second.sv"],1);
        Assert.That(options.Int("--threads",1),Is.EqualTo(4));
        Assert.That(options.Sources,Is.EqualTo(new[]{"first.sv","second.sv"}));
        Assert.That(CliOptions.Parse(["compile"],1).Sources,Is.Empty);
    }
    [TestCase("simulate","--release-reset"),TestCase("word-generate","--four-state")]
    public async Task GeneratedHelpIncludesPublishedFlags(string command,string flag)
    {
        using var output=new StringWriter();using var error=new StringWriter();
        Assert.That(await ToolCli.RunAsync([command,"--help"],output,error),Is.Zero);
        Assert.That(output.ToString(),Does.Contain(flag));Assert.That(error.ToString(),Is.Empty);
    }
    [Test]
    public async Task HelpOutputFailureDoesNotReportSuccess()
    {
        using var output=new BrokenWriter();using var error=new StringWriter();
        Assert.That(await ToolCli.RunAsync(["compile","--help"],output,error),Is.EqualTo(1));
        Assert.That(error.ToString(),Does.Contain("output unavailable"));
    }
    private sealed class BrokenWriter:TextWriter
    {
        public override System.Text.Encoding Encoding=>System.Text.Encoding.UTF8;
        public override void Write(char value)=>throw new IOException("output unavailable");
        public override void Write(string? value)=>throw new IOException("output unavailable");
    }
    [TestCase("--bogus"), TestCase("--cycles")]
    public void UnknownAndMissingOptionValuesFailBeforeExecution(string option)
        => Assert.That(() => CliOptions.Parse(["simulate", "fixture.json", option], 2), Throws.ArgumentException);
    [TestCase("-1"), TestCase("1,000"), TestCase("2147483648"), TestCase(""), TestCase("NaN")]
    public void IntegerSyntaxRemainsInvariantAndBounded(string value)
    {
        var options = CliOptions.Parse(["simulate", "fixture.json", "--cycles", value], 2);
        Assert.That(() => options.Int("--cycles", 5), Throws.ArgumentException);
    }
    [Test]
    public void ResponseFileContentsAreNeverExpanded()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "--threads 256");
            var options = CliOptions.Parse(["compile", "@" + file], 1);
            Assert.That(options.Sources, Is.EqualTo(new[] { "@" + file }));
            Assert.That(options.Int("--threads", 1), Is.EqualTo(1));
        }
        finally { File.Delete(file); }
    }
    [Test]
    public void ArgumentBoundariesSurviveUnicodeQuotesAndWhitespace()
    {
        var property = Prop.ForAll<int>(seed => {
            string value = "folder with spaces/雪\"\\" + seed.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".sv";
            var parsed = CliOptions.Parse(["compile", value, "--output-dir", value], 1);
            return parsed.Sources.SequenceEqual(new[] { value }) && parsed.String("--output-dir") == value;
        });
        Check.One(Config.QuickThrowOnFailure.WithMaxTest(100).WithReplay(20261027, 112359), property);
    }
    [TestCase("--help"), TestCase("compile"), TestCase("rtl-export")]
    public async Task LibraryGeneratedHelpListsRealCommandsAndOptions(string command)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.That(await ToolCli.RunAsync(command == "--help" ? [command] : [command, "--help"], output, error), Is.Zero);
        Assert.That(output.ToString(), Does.Contain(command == "--help" ? "compile" : "--output-dir"));
        Assert.That(error.ToString(), Is.Empty);
    }
    [Test]
    public async Task GeneratedErrorContractPreservesSchemaAndEscapesText()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        int code = await ToolCli.RunAsync(["--json-errors", "fifo-differential", "ignored.sv", "--invalid\"\n雪"], output, error);
        using var doc = JsonDocument.Parse(error.ToString());
        Assert.That(code, Is.EqualTo(1));
        Assert.That(doc.RootElement.GetProperty("schema").GetString(), Is.EqualTo("velosim.cli.error.v1"));
        Assert.That(doc.RootElement.GetProperty("exit_code").GetInt32(), Is.EqualTo(code));
        Assert.That(doc.RootElement.GetProperty("command").GetString(), Is.EqualTo("fifo-differential"));
        Assert.That(doc.RootElement.GetProperty("message").GetString(), Does.Contain("invalid"));
    }
}
